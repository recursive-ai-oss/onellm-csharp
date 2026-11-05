using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Templating;

namespace OneLlm.Agents;

internal sealed partial class LiveAgent
{
    private const int DefaultMaxIterations = 200;

    private RunScope _scope = null!;
    private int _turns;
    private readonly List<Cost> _segmentCosts = [];
    private readonly List<Usage> _segmentUsage = [];
    private Message? _partial;
    private string? _endTool;
    private JsonNode? _endOutput;
    private string? _endText;

    /// <summary>The options the agent was started with (prompt, source, strategy override).</summary>
    public AgentStartOptions Options { get; set; } = new();

    private async Task<AgentResult> RunSegmentAsync(bool initial, CancellationToken token)
    {
        string? previous = Instance.State;
        _turns = 0;
        _segmentCosts.Clear();
        _segmentUsage.Clear();
        _partial = null;
        _endTool = null;
        _endOutput = null;
        _endText = null;
        int maxIterations = Instance.MaxIterations is { } m and > 0 ? m : DefaultMaxIterations;

        _scope = await RunScope.StartAsync(Runtime, Instance, token).ConfigureAwait(false);
        try
        {
            try
            {
                await StateChangedAsync(previous, InstanceStates.Running, token).ConfigureAwait(false);
                if (initial)
                {
                    await ProcessInitialAsync(token).ConfigureAwait(false);
                }

                bool ended = await ResolveApprovalsAsync(token).ConfigureAwait(false);
                AgentStopReason reason = AgentStopReason.Waiting;
                if (ended)
                {
                    reason = AgentStopReason.Completed;
                }
                else
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        await DrainInboxAsync(token).ConfigureAwait(false);
                        if (!NeedsInference())
                        {
                            if (TryRest())
                            {
                                reason = AgentStopReason.Waiting;
                                break;
                            }

                            continue;
                        }

                        if (_turns >= maxIterations)
                        {
                            reason = AgentStopReason.MaxIterations;
                            break;
                        }

                        _turns++;
                        await MaybeCompactAsync(token).ConfigureAwait(false);
                        Message answer = await InferAsync(token).ConfigureAwait(false);
                        if (answer.ToolCalls.Count == 0)
                        {
                            await AppendAsync(answer, token).ConfigureAwait(false);
                            if (TryRest())
                            {
                                reason = AgentStopReason.Waiting;
                                break;
                            }

                            continue;
                        }

                        ToolPhase phase = await ExecuteToolsAsync(answer, injected: false, token).ConfigureAwait(false);
                        if (phase.Pending)
                        {
                            reason = AgentStopReason.WaitingForApproval;
                            break;
                        }

                        if (phase.EndsLoop)
                        {
                            reason = AgentStopReason.Completed;
                            break;
                        }

                        if (phase.Asks)
                        {
                            reason = AgentStopReason.Waiting;
                            break;
                        }
                    }
                }

                return await SettleAsync(reason, maxIterations, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    (Instance.ExtensionData ??= [])["error"] = new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["message"] = RecordText.Bound(ex.Message) };
                }

                string before = Instance.State ?? "";
                Exception toThrow = await _scope.FailAsync(ex, _partial, token).ConfigureAwait(false);
                Emit(AgentEventKind.StateChanged, state: Instance.State);
                Emit(AgentEventKind.Finished, state: Instance.State);
                if (Setup.Hooks.Has(HookPoints.OnStateChange))
                {
                    await Setup.Hooks.EmitStateChangeAsync(new StateChangeHookContext(Instance, before, Instance.State ?? ""), CancellationToken.None).ConfigureAwait(false);
                }

                Host.OnSettled(this);
                Runtime.Rethrow(toThrow, ex);
                throw;
            }
        }
        finally
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<AgentResult> SettleAsync(AgentStopReason reason, int maxIterations, CancellationToken token)
    {
        string state = reason switch
        {
            AgentStopReason.Completed => InstanceStates.Stopped,
            AgentStopReason.WaitingForApproval => InstanceStates.WaitingForApproval,
            AgentStopReason.MaxIterations => InstanceStates.Error,
            _ => InstanceStates.Waiting,
        };
        lock (_gate)
        {
            _stopping = true;
        }

        var warnings = new List<string>(Setup.Warnings);
        if (reason == AgentStopReason.MaxIterations)
        {
            string text = $"max iterations ({maxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)}) reached";
            (Instance.ExtensionData ??= [])["error"] = new Dictionary<string, object?> { ["type"] = "MaxIterations", ["message"] = text };
            await Runtime.WriteRecordAsync(
                new ErrorRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), ErrorType = "MaxIterations", Message = text, Where = "run" },
                token).ConfigureAwait(false);
            warnings.Add(text);
        }

        string before = Instance.State ?? "";
        await _scope.CompleteAsync(state, token).ConfigureAwait(false);
        await StateChangedAsync(before, state, token).ConfigureAwait(false);
        Emit(AgentEventKind.Finished, state: state);
        Host.OnSettled(this);

        (string text2, JsonNode? output, string? by) = _endTool is not null ? (_endText ?? "", _endOutput, _endTool) : Outcome.Derive(Instance);
        Cost cost = Cost.Sum(_segmentCosts);
        Usage? usage = SumUsage();
        var result = new AgentResult(Instance, reason, text2, output, by, _turns, cost, usage, warnings);
        result.SubtreeCost = OwnCost + Host.DescendantCost(Instance.InstanceId).Total;
        return result;
    }

    private Usage? SumUsage()
    {
        if (_segmentUsage.Count == 0)
        {
            return null;
        }

        long input = 0, output = 0, write = 0, read = 0;
        decimal cost = 0m;
        foreach (Usage u in _segmentUsage)
        {
            input += u.InputTokens;
            output += u.OutputTokens;
            write += u.CacheCreationInputTokens;
            read += u.CacheReadInputTokens;
            cost += u.Cost;
        }

        return new Usage { Model = Setup.Model.Id, InputTokens = input, OutputTokens = output, CacheCreationInputTokens = write, CacheReadInputTokens = read, Cost = cost };
    }

    private async ValueTask StateChangedAsync(string? from, string to, CancellationToken token)
    {
        Emit(AgentEventKind.StateChanged, state: to);
        if (Setup.Hooks.Has(HookPoints.OnStateChange))
        {
            await Setup.Hooks.EmitStateChangeAsync(new StateChangeHookContext(Instance, from, to), token).ConfigureAwait(false);
        }
    }

    /// <summary>True when nothing is queued, in which case the agent may rest; false when a message arrived and the loop should go on.</summary>
    private bool TryRest()
    {
        lock (_gate)
        {
            if (!_inbox.IsEmpty)
            {
                return false;
            }

            _stopping = true;
            return true;
        }
    }

    /// <summary>The model has something to answer: a user message, or tool calls whose results it has not seen.</summary>
    private bool NeedsInference()
    {
        if (Instance.Messages.Count == 0 || AwaitingAnswer)
        {
            return false;
        }

        Message last = Instance.Messages[^1];
        if (last.User.Count > 0)
        {
            return true;
        }

        return last.ToolCalls.Count > 0 && Instance.PendingApprovals.Count == 0;
    }

    private async Task DrainInboxAsync(CancellationToken token)
    {
        while (_inbox.TryDequeue(out Message? message))
        {
            if (AwaitingAnswer)
            {
                await AnswerAskAsync(message, token).ConfigureAwait(false);
                continue;
            }

            await AppendAsync(message, token).ConfigureAwait(false);
        }
    }

    private async Task AppendAsync(Message message, CancellationToken token)
    {
        await _scope.AppendAsync(message, token).ConfigureAwait(false);
        Emit(AgentEventKind.MessageAdded, message: message);
        if (Setup.Hooks.Has(HookPoints.OnMessage))
        {
            await Setup.Hooks.EmitMessageAsync(new MessageHookContext(Instance, _turns, message), token).ConfigureAwait(false);
        }

        if (message.Usage is not null)
        {
            Host.OnTurn(this);
        }
    }

    // ---- start of a fresh agent ---------------------------------------------------------------------------------------------

    private async Task ProcessInitialAsync(CancellationToken token)
    {
        Dictionary<string, object?> inputs = Instance.Inputs ?? [];
        var turns = new List<object?>();
        Dictionary<string, object?> scope = TemplateScope.Build(Runtime, Instance, inputs, Instance.CallerInstanceId, turns);
        if (Options.SwarmUri is { } swarm && scope["swarm"] is Dictionary<string, object?> swarmMap)
        {
            swarmMap["uri"] = swarm;
        }

        TemplateRenderOptions render = Runtime.RenderOptions(Instance, token);
        string sourceName = Instance.DefinitionPath ?? Instance.Name;

        string Render(string text, string where) =>
            Template.Render(text, scope, new TemplateParseOptions { Name = sourceName + ":" + where, Functions = Runtime.Functions }, render);

        Instance.RenderedSystemPrompt = Render(Instance.SystemPrompt, "systemPrompt");
        await Runtime.Store.SaveAsync(Instance, token).ConfigureAwait(false);

        var prompts = Instance.InitialPrompts.ToList();
        for (int i = 0; i < prompts.Count; i++)
        {
            Message prompt = prompts[i];
            string where = $"initialPrompts[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            if (!Runtime.EvaluateWhen(prompt.When, Render, where + ".when"))
            {
                Setup.Warnings.Add($"{where} was skipped: its 'when' is false.");
                continue;
            }

            Message rendered = Runtime.RenderPrompt(prompt, Render, where);
            if (rendered.User.Count > 0 || rendered.Assistant.Count > 0)
            {
                await AppendAsync(rendered, token).ConfigureAwait(false);
            }

            if (prompt.ToolCalls.Count > 0)
            {
                var calls = new List<ToolCall>();
                foreach (ToolCall c in prompt.ToolCalls)
                {
                    if (!Runtime.EvaluateWhen(c.When, Render, where + ".toolCalls.when"))
                    {
                        continue;
                    }

                    calls.Add(new ToolCall(string.IsNullOrEmpty(c.Id) ? Runtime.NewId("call") : c.Id, c.Tool)
                    {
                        Routine = c.Routine,
                        Inputs = RenderInputs(c.Inputs, Render, where),
                        Injected = true,
                        InjectedBy = c.InjectedBy ?? Instance.CallerInstanceId ?? "runtime",
                        ExtensionData = c.ExtensionData,
                    });
                }

                if (calls.Count > 0)
                {
                    var injected = new Message { Id = Runtime.NewId("m"), ToolCalls = calls, DateTime = Runtime.TimeProvider.GetUtcNow() };
                    ToolPhase phase = await ExecuteToolsAsync(injected, injected: true, token).ConfigureAwait(false);
                    if (phase.Pending)
                    {
                        return;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(Options.Prompt))
        {
            MessageSource source = Options.Source ?? (Instance.SpawnerInstanceId is { } spawner
                ? new MessageSource { Kind = "agent", InstanceId = spawner, ToolCallId = Instance.CallerToolCallId }
                : new MessageSource { Kind = "api" });
            await AppendAsync(
                new Message { Id = Runtime.NewId("m"), User = [new TextPart(Options.Prompt)], DateTime = Runtime.TimeProvider.GetUtcNow(), Source = source },
                token).ConfigureAwait(false);
        }
    }

    // Inputs of an injected call may be templates, like the rest of the initial prompts.
    private static JsonObject? RenderInputs(JsonObject? inputs, Func<string, string, string> render, string where)
    {
        if (inputs is null)
        {
            return null;
        }

        return (JsonObject?)RenderNode(inputs, render, where + ".toolCalls.inputs");
    }

    private static JsonNode? RenderNode(JsonNode? node, Func<string, string, string> render, string where) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, RenderNode(p.Value, render, where + "." + p.Key)))),
        JsonArray a => new JsonArray(a.Select((n, i) => RenderNode(n, render, where + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]")).ToArray()),
        JsonValue v when v.TryGetValue(out string? s) && s.Contains("{{", StringComparison.Ordinal) => JsonValue.Create(render(s, where)),
        _ => node?.DeepClone(),
    };

    // ---- one model call -----------------------------------------------------------------------------------------------------

    private async Task<Message> InferAsync(CancellationToken token)
    {
        ChatRequest request = Runtime.BuildChat(Instance, Setup.Model, Setup.Settings, null) with { Tools = Setup.ToolDefinitions };
        HookSet hooks = Setup.Hooks;
        if (hooks.Has(HookPoints.BeforeInference))
        {
            var ctx = new BeforeInferenceContext(Instance, _turns, request);
            HookOutcome outcome = await hooks.BeforeInferenceAsync(ctx, token).ConfigureAwait(false);
            request = ctx.Request;
            if (outcome.Action == HookAction.Block)
            {
                throw new HookBlockedException(outcome.Hook ?? "", HookPoints.BeforeInference, outcome.Reason ?? "blocked");
            }

            if (outcome.Action == HookAction.Respond)
            {
                var given = (Message)outcome.Payload!;
                return MessageCopy.WithAnswer(given, new Message { Id = Runtime.NewId("m"), DateTime = Runtime.TimeProvider.GetUtcNow() });
            }
        }

        ChatResponse response;
        InferenceCall call;
        for (int attempt = 0; ; attempt++)
        {
            call = InferenceCall.Begin(Runtime, _scope, Setup.Model, Setup.Provider, request);
            try
            {
                response = await SendOnceAsync(call.Request, token).ConfigureAwait(false);
                break;
            }
            catch (ProviderException ex) when (ex.IsTransient && attempt < Runtime.AgentRetry.MaxRetries && !token.IsCancellationRequested)
            {
                call.Fail(ex);
                TimeSpan? retryAfter = ex switch { RateLimitException r => r.RetryAfter, OverloadedException o => o.RetryAfter, _ => null };
                // No jitter here: the HTTP layer already spreads its own retries, and a fixed delay keeps this one testable.
                TimeSpan? delay = Runtime.AgentRetry.GetDelay(attempt, retryAfter, 0d);
                if (delay is null)
                {
                    throw;
                }

                await Task.Delay(delay.Value, Runtime.TimeProvider, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                call.Fail(ex);
                throw;
            }
        }

        (Message stamped, Cost cost, _, IReadOnlyList<ResponseWarning> warnings) = await call
            .CompleteAsync(response.Message, response.Finish, response.FinishReasonRaw, response.Id, token, response.Warnings).ConfigureAwait(false);
        foreach (ResponseWarning w in warnings)
        {
            string line = w.Message;
            if (!Setup.Warnings.Contains(line))
            {
                Setup.Warnings.Add(line);
            }
        }

        _segmentCosts.Add(cost);
        if (stamped.Usage is { } usage)
        {
            _segmentUsage.Add(usage);
        }

        if (hooks.Has(HookPoints.OnCost) && cost.Lines.Count > 0)
        {
            await hooks.EmitCostAsync(new CostHookContext(Instance, _turns, "inference", cost), token).ConfigureAwait(false);
        }

        Message result = stamped;
        if (hooks.Has(HookPoints.AfterInference))
        {
            var ctx = new AfterInferenceContext(Instance, _turns, call.Request, stamped, cost);
            HookOutcome outcome = await hooks.AfterInferenceAsync(ctx, token).ConfigureAwait(false);
            if (outcome.Action == HookAction.Block)
            {
                throw new HookBlockedException(outcome.Hook ?? "", HookPoints.AfterInference, outcome.Reason ?? "blocked");
            }

            result = ReferenceEquals(ctx.Message, stamped) ? stamped : MessageCopy.WithAnswer(ctx.Message, stamped);
        }

        return result;
    }

    private async Task<ChatResponse> SendOnceAsync(ChatRequest request, CancellationToken token)
    {
        if (!Setup.Hooks.Has(HookPoints.StreamEvent))
        {
            return await Setup.Provider.SendAsync(request, token).ConfigureAwait(false);
        }

        // Streaming only when someone watches the events; the accumulator builds the same message a whole answer would.
        var acc = new MessageAccumulator();
        var ctx = new HookContext(HookPoints.StreamEvent, Instance, _turns);
        await foreach (StreamEvent e in Setup.Provider.StreamAsync(request, token).ConfigureAwait(false))
        {
            Setup.Hooks.EmitStreamEvent(ctx, in e);
            acc.Add(e);
        }

        return new ChatResponse { Message = acc.ToMessage(), Id = acc.ResponseId, Model = acc.Model, Finish = acc.Finish, FinishReasonRaw = acc.FinishReasonRaw };
    }
}
