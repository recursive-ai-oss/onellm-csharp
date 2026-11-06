using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Agents;

internal readonly record struct ToolPhase(bool Pending, bool EndsLoop, bool Asks = false);

internal sealed partial class LiveAgent
{
    private const string ShellToolName = "execute_terminal_command";

    /// <summary>One tool call on its way through hooks, approval and the tool itself.</summary>
    private sealed class CallState
    {
        public required ToolCall Call { get; init; }

        public ITool? Tool { get; set; }

        private JsonObject? _inputs;

        /// <summary>The arguments the tool will get. Made only when something asks, since most calls replace it with a copy straight away.</summary>
        public JsonObject Inputs
        {
            get => _inputs ??= new JsonObject();
            set => _inputs = value;
        }

        public ToolResult? Result { get; set; }

        public bool Pending { get; set; }

        public string? PendingReason { get; set; }

        public bool Ran { get; set; }

        public bool Cancelled { get; set; }

        /// <summary>A person approved this call through the saved approval gate.</summary>
        public bool Approved { get; set; }

        /// <summary>The call was answered before this pass (an earlier part of a turn held for approval); it is kept as it is.</summary>
        public bool Prior { get; init; }

        public TimeSpan Duration { get; set; }

        public bool EndsLoop => Result is { IsError: false } r && (r.EndsLoop || Tool?.Definition.EndsLoop == true);
    }

    /// <summary>
    /// Runs the tool calls of one assistant turn and commits the turn. Calls are checked and passed through the before-hooks in call
    /// order; the ones that may run then run together (when every one is parallel-safe) or one after another, and their results are
    /// put back in call order. A call that needs approval is held: the turn is saved with that call unanswered and the agent rests in
    /// <c>waiting_for_approval</c>; the other calls of the turn still run.
    /// </summary>
    private async ValueTask<ToolPhase> ExecuteToolsAsync(Message message, CancellationToken token)
    {
        // Indexed loops over the interface lists on this path: enumerating them would allocate on every turn.
        IReadOnlyList<ToolCall> toolCalls = message.ToolCalls;
        var states = new List<CallState>(toolCalls.Count);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            CallState st = await PrepareAsync(toolCalls[i], token).ConfigureAwait(false);
            states.Add(st);
        }

        await RunAllAsync(message, states, token).ConfigureAwait(false);
        return await CommitAsync(message, states, replaceExisting: false, token).ConfigureAwait(false);
    }

    private async ValueTask<CallState> PrepareAsync(ToolCall call, CancellationToken token)
    {
        var st = new CallState { Call = call };
        if (!Setup.Tools.TryGetValue(call.Tool, out ITool? tool))
        {
            st.Result = ToolResult.Error($"Unknown tool '{call.Tool}'. Available tools: {string.Join(", ", Setup.Tools.Keys.Order(StringComparer.Ordinal))}.");
            return st;
        }

        st.Tool = tool;
        if (call.InvalidInputs is not null)
        {
            st.Result = ToolResult.Error($"Tool '{call.Tool}' was called with arguments that are not a JSON object: {Truncate(call.InvalidInputs, 200)}");
            return st;
        }

        st.Inputs = call.Inputs is null ? new JsonObject() : (JsonObject)call.Inputs.DeepClone();
        if (Setup.Hooks.Has(HookPoints.BeforeToolCall))
        {
            var ctx = new BeforeToolCallContext(Instance, _turns, call, tool.Definition, st.Inputs);
            HookOutcome outcome = await Setup.Hooks.BeforeToolCallAsync(ctx, token).ConfigureAwait(false);
            st.Inputs = ctx.Inputs;
            switch (outcome.Action)
            {
                case HookAction.Block:
                    st.Result = ToolResult.Error($"Blocked by a hook: {outcome.Reason}");
                    return st;
                case HookAction.Respond:
                    st.Result = (ToolResult)outcome.Payload!;
                    return st;
                case HookAction.RequireApproval:
                    st.Pending = true;
                    st.PendingReason = outcome.Reason;
                    return st;
            }
        }

        // Injected calls stop for approval like any other: a definition can be written by a model, so its author is not a trusted approver.
        if (tool.Definition.RequiresApproval)
        {
            st.Pending = true;
            st.PendingReason = $"The tool '{call.Tool}' requires approval.";
        }
        else if (tool.Definition.Name == ShellToolName && Setup.Capabilities.ShellAccess is { RequiresApproval: true, Approver: null })
        {
            // Shell grants that name no approver use this gate, which is saved with the instance and survives a restart.
            st.Pending = true;
            st.PendingReason = "Running a shell command requires approval.";
        }

        return st;
    }

    private async Task RunAllAsync(Message message, List<CallState> states, CancellationToken token)
    {
        List<CallState> runnable = new(states.Count);
        foreach (CallState candidate in states)
        {
            if (candidate.Result is null && !candidate.Pending)
            {
                runnable.Add(candidate);
            }
        }

        if (runnable.Count > 0)
        {
            bool parallel = runnable.Count > 1 && runnable.TrueForAll(static s => s.Tool!.Definition.ParallelSafe);
            if (parallel)
            {
                await Task.WhenAll(runnable.Select(s => RunOneAsync(s, token))).ConfigureAwait(false);
            }
            else
            {
                foreach (CallState s in runnable)
                {
                    await RunOneAsync(s, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }

        if (token.IsCancellationRequested)
        {
            // Keep what finished and say plainly what did not, so the saved turn has a result for every call.
            _partial = BuildMessage(message, states, cancelled: true);
            throw new OperationCanceledException(token);
        }

        if (Setup.Hooks.Has(HookPoints.AfterToolCall))
        {
            foreach (CallState s in states.Where(static s => s.Result is not null && !s.Pending && !s.Prior))
            {
                var ctx = new AfterToolCallContext(Instance, _turns, s.Call, s.Tool?.Definition, s.Result!);
                HookOutcome outcome = await Setup.Hooks.AfterToolCallAsync(ctx, token).ConfigureAwait(false);
                s.Result = outcome.Action == HookAction.Block ? ToolResult.Error($"Blocked by a hook: {outcome.Reason}") : ctx.Result;
            }
        }
    }

    private async Task RunOneAsync(CallState st, CancellationToken token)
    {
        ITool tool = st.Tool!;
        var context = new ToolContext(Instance.InstanceId, st.Call.Id, Runtime.TimeProvider)
        {
            CallerInstanceId = Instance.CallerInstanceId,
            Capabilities = Setup.Capabilities,
            Runtime = Runtime,
            Agent = this,
            LogSink = Runtime.ToolLog,
            Services = Runtime.Services,
            ApprovedByLoop = st.Approved,
        };
        Emit(AgentEventKind.ToolCallStarted, call: st.Call);
        long started = Runtime.TimeProvider.GetTimestamp();
        CurrentTool = tool.Definition.Name;
        CurrentToolStarted = started;
        try
        {
            st.Result = await ToolInvoker.InvokeAsync(tool, st.Inputs, context, token).ConfigureAwait(false);
            st.Ran = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            st.Cancelled = true;
        }
        finally
        {
            st.Duration = Runtime.TimeProvider.GetElapsedTime(started);
            CurrentTool = null;
        }
    }

    private static Message BuildMessage(Message message, List<CallState> states, bool cancelled)
    {
        var calls = new List<ToolCall>(states.Count);
        for (int index = 0; index < states.Count; index++)
        {
            CallState s = states[index];
            if (s.Prior)
            {
                calls.Add(s.Call);
                continue;
            }

            if (s.Pending && !cancelled)
            {
                calls.Add(s.Inputs is { } inp && !JsonNode.DeepEquals(inp, s.Call.Inputs ?? new JsonObject()) ? MessageCopy.WithInputs(s.Call, inp) : s.Call);
                continue;
            }

            ToolResult r = s.Result ?? ToolResult.Error(s.Pending ? "The call was not run: the run was cancelled while it waited for approval." : "The call was cancelled before it finished.");
            decimal cost = 0m;
            IReadOnlyList<CostLine> resultLines = r.CostLines;
            for (int l = 0; l < resultLines.Count; l++)
            {
                cost += resultLines[l].Amount;
            }

            calls.Add(MessageCopy.WithResult(s.Call, r.Parts, r.IsError, cost, r.CostLines));
        }

        return MessageCopy.WithToolCalls(message, calls);
    }

    /// <summary>Saves the turn with its results, records each call, and decides whether the agent rests or goes on.</summary>
    private async ValueTask<ToolPhase> CommitAsync(Message message, List<CallState> states, bool replaceExisting, CancellationToken token)
    {
        Message committed = BuildMessage(message, states, cancelled: false);
        bool anyPending = false;
        foreach (CallState s in states)
        {
            if (!s.Pending)
            {
                continue;
            }

            anyPending = true;
            Instance.PendingApprovals.Add(new PendingApproval
            {
                ToolCallId = s.Call.Id,
                Tool = s.Call.Tool,
                MessageId = message.Id,
                Inputs = ToDictionary(s.Inputs),
                Reason = s.PendingReason,
                RequestedAt = Rfc3339.Format(Runtime.TimeProvider.GetUtcNow()),
            });
        }

        if (replaceExisting)
        {
            int at = Instance.Messages.FindIndex(m => m.Id == message.Id);
            if (at >= 0)
            {
                Instance.Messages[at] = committed;
            }

            await Runtime.Store.SaveAsync(Instance, token).ConfigureAwait(false);
            await Runtime.WriteRecordAsync(
                new MessageRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), MessageId = committed.Id, Message = committed },
                token).ConfigureAwait(false);
        }
        else
        {
            await AppendAsync(committed, token).ConfigureAwait(false);
        }

        bool ends = false;
        bool asks = false;
        for (int i = 0; i < states.Count; i++)
        {
            CallState s = states[i];
            ToolCall final = committed.ToolCalls[i];
            if (s.Prior)
            {
                continue;
            }

            if (s.Pending)
            {
                Emit(AgentEventKind.ApprovalRequested, call: final);
                await Runtime.WriteRecordAsync(
                    new ApprovalRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), ToolCallId = final.Id, Tool = final.Tool, Decision = "requested", MessageId = committed.Id },
                    token).ConfigureAwait(false);
                continue;
            }

            Emit(AgentEventKind.ToolCallFinished, call: final);
            if (!Runtime.Sinks.IsEmpty)
            {
                await Runtime.WriteRecordAsync(
                    new ToolCallRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), MessageId = committed.Id, Call = final, Duration = s.Duration },
                    token).ConfigureAwait(false);
            }
            if (final.CostLines is { Count: > 0 })
            {
                var cost = new Cost(final.CostLines);
                _segmentCosts.Add(cost);
                if (Setup.Hooks.Has(HookPoints.OnCost))
                {
                    await Setup.Hooks.EmitCostAsync(new CostHookContext(Instance, _turns, "tool:" + final.Tool, cost), token).ConfigureAwait(false);
                }
            }

            // ask_user cannot wait itself: its result is the question, and the instance rests until a person answers.
            if (!asks && s.Result is { IsError: false } asked && AskUser.TryGetRequest(asked, out _))
            {
                asks = true;
                (Instance.ExtensionData ??= [])[AskKey] = new Dictionary<string, object?> { ["toolCallId"] = final.Id, ["messageId"] = committed.Id };
            }

            if (s.EndsLoop)
            {
                ends = true;
                _endTool = final.Tool;
                _endOutput = s.Result!.TypedResult?.DeepClone() ?? final.Inputs?.DeepClone();
                _endText = final.Tool == "task_complete" && final.Inputs?["result"] is JsonValue rv && rv.TryGetValue(out string? text)
                    ? text
                    : string.Concat(final.Result.OfType<TextPart>().Select(static p => p.Text));
                await Runtime.WriteRecordAsync(
                    new TaskCompletionRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), MessageId = committed.Id, Tool = final.Tool, Result = _endText },
                    token).ConfigureAwait(false);
            }
        }

        return new ToolPhase(anyPending, ends && !anyPending, asks && !anyPending);
    }

    /// <summary>
    /// At the start of a segment, runs the approved calls and answers the denied ones, then puts the finished turn back in place of
    /// the one saved with unanswered calls. Returns true when one of those calls ended the loop.
    /// </summary>
    private async Task<bool> ResolveApprovalsAsync(CancellationToken token)
    {
        if (Instance.PendingApprovals.Count == 0)
        {
            return false;
        }

        List<PendingApproval> decided = [.. Instance.PendingApprovals.Where(ApprovalDecision.IsDecided)];
        if (decided.Count != Instance.PendingApprovals.Count)
        {
            throw new InvalidOperationException($"Instance '{Instance.InstanceId}' still has tool calls waiting for a decision.");
        }

        bool ends = false;
        foreach (IGrouping<string?, PendingApproval> group in decided.GroupBy(static p => p.MessageId))
        {
            Message? message = Instance.Messages.FirstOrDefault(m => m.Id == group.Key);
            if (message is null)
            {
                continue;
            }

            var states = new List<CallState>(message.ToolCalls.Count);
            foreach (ToolCall call in message.ToolCalls)
            {
                PendingApproval? p = group.FirstOrDefault(x => x.ToolCallId == call.Id);
                var st = new CallState { Call = call, Prior = p is null };
                if (p is null)
                {
                    // Already answered when the turn was first saved.
                    st.Result = new ToolResult(call.Result) { IsError = call.IsError, CostLines = call.CostLines ?? [] };
                }
                else if (!ApprovalDecision.IsApproved(p))
                {
                    st.Result = ToolResult.Error("The call was denied by the approver." + (ApprovalDecision.Reason(p) is { Length: > 0 } why ? " Reason: " + why : ""));
                }
                else if (!Setup.Tools.TryGetValue(call.Tool, out ITool? tool))
                {
                    st.Result = ToolResult.Error($"Unknown tool '{call.Tool}'.");
                }
                else
                {
                    st.Tool = tool;
                    st.Approved = true;
                    st.Inputs = call.Inputs is null ? new JsonObject() : (JsonObject)call.Inputs.DeepClone();
                }

                states.Add(st);
            }

            await RunAllAsync(message, states, token).ConfigureAwait(false);
            foreach (PendingApproval p in group)
            {
                Instance.PendingApprovals.Remove(p);
            }

            ToolPhase phase = await CommitAsync(message, states, replaceExisting: true, token).ConfigureAwait(false);
            ends |= phase.EndsLoop;
        }

        return ends;
    }

    // ---- ask_user -----------------------------------------------------------------------------------------------------------

    internal const string AskKey = "askUser";

    /// <summary>True while a question from <c>ask_user</c> has not been answered.</summary>
    private bool AwaitingAnswer => Instance.ExtensionData is { } d && d.ContainsKey(AskKey);

    /// <summary>Puts a person's message in as the result of the open <c>ask_user</c> call, so the model sees the answer where it asked.</summary>
    private async Task AnswerAskAsync(Message answer, CancellationToken token)
    {
        var ask = (IDictionary<string, object?>)Instance.ExtensionData![AskKey]!;
        string? callId = ask.TryGetValue("toolCallId", out object? c) ? c as string : null;
        string? messageId = ask.TryGetValue("messageId", out object? m) ? m as string : null;
        Instance.ExtensionData.Remove(AskKey);
        int at = Instance.Messages.FindIndex(x => x.Id == messageId);
        if (at >= 0)
        {
            Message turn = Instance.Messages[at];
            ToolCall[] calls = [.. turn.ToolCalls.Select(call => call.Id == callId ? MessageCopy.WithResult(call, [new TextPart(answer.GetUserText())], false, call.Cost, call.CostLines) : call)];
            Instance.Messages[at] = MessageCopy.WithToolCalls(turn, calls);
        }

        await Runtime.Store.SaveAsync(Instance, token).ConfigureAwait(false);
        Emit(AgentEventKind.MessageAdded, message: answer);
    }

    private static Dictionary<string, object?>? ToDictionary(JsonObject inputs)
    {
        using JsonDocument doc = JsonDocument.Parse(inputs.ToJsonString());
        return PlainValues.FromElement(doc.RootElement) as Dictionary<string, object?>;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
