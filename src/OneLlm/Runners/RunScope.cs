using OneLlm.Agents;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Pricing;
using OneLlm.Records;

namespace OneLlm.Runners;

/// <summary>
/// One live run of an instance: holds the store lease, keeps the instance saved as messages are added, writes the run's records and
/// settles how the run ended (finished, failed or cancelled). Disposing releases the lease.
/// </summary>
internal sealed class RunScope : IAsyncDisposable
{
    private readonly IInstanceLease _lease;

    private RunScope(Runtime runtime, Instance instance, IInstanceLease lease, SpendGuard guard, HookSet hooks)
    {
        Hooks = hooks;
        Runtime = runtime;
        Instance = instance;
        _lease = lease;
        Guard = guard;
    }

    public Runtime Runtime { get; }

    /// <summary>
    /// The hooks of this run. Empty for agent runs, whose loop dispatches its own set; simple, chat, scripted and decider runs get the
    /// runtime's hooks here, so the same hook points fire with the same rules on every run kind.
    /// </summary>
    public HookSet Hooks { get; }

    /// <summary>The number of model calls this run has started. Hook contexts report it as the turn number.</summary>
    public int Iteration { get; private set; }

    public Instance Instance { get; }

    /// <summary>The instance's spend limits, starting from what its earlier turns cost.</summary>
    public SpendGuard Guard { get; }

    /// <summary>When set, a failed or cancelled run that produced nothing removes the user message it was answering (chats).</summary>
    public bool DropUnansweredUser { get; set; }

    /// <summary>Starts a run. A null <paramref name="hooks"/> means no hooks (agent runs); the other run kinds pass <see cref="HookSet.ForRuntime"/>.</summary>
    public static async Task<RunScope> StartAsync(Runtime runtime, Instance instance, CancellationToken cancellationToken, HookSet? hooks = null)
    {
        IInstanceLease lease = await runtime.Store.AcquireAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);
        try
        {
            // A negative total (a bad tool cost written into an older instance) counts as zero: the instance must still load.
            decimal spent = Math.Max(0m, instance.Messages.Sum(static m => m.Usage?.Cost ?? 0m));
            var scope = new RunScope(runtime, instance, lease, new SpendGuard(runtime.MaxCostPerCall, runtime.InstanceSpendCap, spent), hooks ?? HookSet.Empty);
            string? previous = instance.State;
            DateTimeOffset now = runtime.TimeProvider.GetUtcNow();
            instance.State = InstanceStates.Running;
            instance.RunStartedAt = Rfc3339.Format(now);
            instance.ExtensionData?.Remove("cancelled");
            await runtime.Store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            await runtime.WriteRecordAsync(
                new RunRecord { InstanceId = instance.InstanceId, Time = now, Event = "started", State = instance.State, Model = instance.Model },
                cancellationToken).ConfigureAwait(false);
            await scope.StateChangedAsync(previous, cancellationToken).ConfigureAwait(false);
            return scope;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Adds a message to the instance, saves it and records it.</summary>
    public async Task AppendAsync(Message message, CancellationToken cancellationToken)
    {
        Instance.Messages.Add(message);
        await Runtime.Store.SaveAsync(Instance, cancellationToken).ConfigureAwait(false);
        if (!Runtime.Sinks.IsEmpty)
        {
            await Runtime.WriteRecordAsync(
                new MessageRecord { InstanceId = Instance.InstanceId, Time = message.DateTime ?? Runtime.TimeProvider.GetUtcNow(), MessageId = message.Id, Message = message },
                cancellationToken).ConfigureAwait(false);
        }

        if (Hooks.Has(HookPoints.OnMessage))
        {
            await Hooks.EmitMessageAsync(new MessageHookContext(Instance, Iteration, message), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the run in a state (<c>stopped</c> or <c>waiting</c>) and saves.</summary>
    public async Task CompleteAsync(string state, CancellationToken cancellationToken)
    {
        string? previous = Instance.State;
        Instance.State = state;
        await Runtime.Store.SaveAsync(Instance, cancellationToken).ConfigureAwait(false);
        await Runtime.WriteRecordAsync(
            new RunRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), Event = "finished", State = state, Model = Instance.Model },
            cancellationToken).ConfigureAwait(false);
        await StateChangedAsync(previous, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles a run that did not finish and returns the exception to throw. Cancellation keeps the partial answer and leaves the
    /// instance <c>stopped</c> with <c>cancelled: true</c>; any other failure puts the instance in <c>error</c>. Saves ignore the
    /// cancelled token so the record of the stop is not itself cancelled.
    /// </summary>
    public async Task<Exception> FailAsync(Exception error, Message? partial, CancellationToken token)
    {
        DateTimeOffset now = Runtime.TimeProvider.GetUtcNow();
        string? previousState = Instance.State;
        bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
        if (partial is null && DropUnansweredUser && Instance.Messages is [.., { Assistant.Count: 0, ToolCalls.Count: 0, User.Count: > 0 }])
        {
            Instance.Messages.RemoveAt(Instance.Messages.Count - 1);
        }

        if (partial is not null)
        {
            Instance.Messages.Add(new Message
            {
                Id = Runtime.NewId("m"),
                User = partial.User,
                Assistant = partial.Assistant,
                ToolCalls = partial.ToolCalls,
                DateTime = now,
                Usage = partial.Usage,
                ExtensionData = partial.ExtensionData,
            });
        }

        if (cancelled)
        {
            Instance.State = InstanceStates.Stopped;
            (Instance.ExtensionData ??= [])["cancelled"] = true;
        }
        else
        {
            Instance.State = InstanceStates.Error;
        }

        await Runtime.Store.SaveAsync(Instance, CancellationToken.None).ConfigureAwait(false);
        await Runtime.WriteRecordAsync(
            new RunRecord { InstanceId = Instance.InstanceId, Time = now, Event = cancelled ? "cancelled" : "failed", State = Instance.State, Model = Instance.Model },
            CancellationToken.None).ConfigureAwait(false);
        await StateChangedAsync(previousState, CancellationToken.None).ConfigureAwait(false);
        if (!cancelled)
        {
            await Runtime.WriteRecordAsync(
                new ErrorRecord { InstanceId = Instance.InstanceId, Time = now, ErrorType = error.GetType().Name, Message = RecordText.Bound(error.Message), Where = "run" },
                CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        return new RunCancelledException(Instance, partial, token);
    }

    public ValueTask DisposeAsync() => _lease.DisposeAsync();

    private ValueTask StateChangedAsync(string? from, CancellationToken cancellationToken) =>
        Hooks.Has(HookPoints.OnStateChange)
            ? Hooks.EmitStateChangeAsync(new StateChangeHookContext(Instance, from, Instance.State ?? ""), cancellationToken)
            : ValueTask.CompletedTask;

    // ---- inference hooks ----------------------------------------------------------------------------------------------------
    // The same chain the agent loop runs: interceptors fail closed (a failure is a Block), observers fail open, and every Modify,
    // Block and Respond is written as a hook record by the HookSet.

    /// <summary>
    /// Counts the call and runs the before-inference hooks. Returns the request to send, or an answer to use instead of calling the model.
    /// </summary>
    /// <exception cref="HookBlockedException">A hook blocked the call; nothing was sent.</exception>
    public ValueTask<(ChatRequest Request, Message? Answer)> BeforeInferenceAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Iteration++;
        return Hooks.Has(HookPoints.BeforeInference) ? BeforeSlowAsync(request, cancellationToken) : new((request, null));
    }

    private async ValueTask<(ChatRequest Request, Message? Answer)> BeforeSlowAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var ctx = new BeforeInferenceContext(Instance, Iteration, request);
        HookOutcome outcome = await Hooks.BeforeInferenceAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (outcome.Action == HookAction.Block)
        {
            throw new HookBlockedException(outcome.Hook ?? "", HookPoints.BeforeInference, outcome.Reason ?? "blocked");
        }

        if (outcome.Action == HookAction.Respond)
        {
            var given = (Message)outcome.Payload!;
            return (ctx.Request, MessageCopy.WithAnswer(given, new Message { Id = Runtime.NewId("m"), DateTime = Runtime.TimeProvider.GetUtcNow() }));
        }

        return (ctx.Request, null);
    }

    /// <summary>Runs the after-inference hooks on a priced answer and returns the message to keep.</summary>
    /// <exception cref="HookBlockedException">A hook blocked the answer.</exception>
    public ValueTask<Message> AfterInferenceAsync(ChatRequest request, Message stamped, Cost cost, CancellationToken cancellationToken) =>
        Hooks.Has(HookPoints.AfterInference) ? AfterSlowAsync(request, stamped, cost, cancellationToken) : new(stamped);

    private async ValueTask<Message> AfterSlowAsync(ChatRequest request, Message stamped, Cost cost, CancellationToken cancellationToken)
    {
        var ctx = new AfterInferenceContext(Instance, Iteration, request, stamped, cost);
        HookOutcome outcome = await Hooks.AfterInferenceAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (outcome.Action == HookAction.Block)
        {
            throw new HookBlockedException(outcome.Hook ?? "", HookPoints.AfterInference, outcome.Reason ?? "blocked");
        }

        return ReferenceEquals(ctx.Message, stamped) ? stamped : MessageCopy.WithAnswer(ctx.Message, stamped);
    }

    /// <summary>Reports a priced call to the cost hooks (source <c>inference</c>).</summary>
    public ValueTask CostAsync(Cost cost, CancellationToken cancellationToken) =>
        Hooks.Has(HookPoints.OnCost) && cost.Lines.Count > 0
            ? Hooks.EmitCostAsync(new CostHookContext(Instance, Iteration, "inference", cost), cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>Sends a request. When a stream-event hook is registered the call streams and the hook sees each event; the accumulated answer is the same message.</summary>
    public async Task<ChatResponse> SendAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
    {
        if (!Hooks.Has(HookPoints.StreamEvent))
        {
            return await provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var acc = new MessageAccumulator();
        var ctx = new HookContext(HookPoints.StreamEvent, Instance, Iteration);
        await foreach (StreamEvent e in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            Hooks.EmitStreamEvent(ctx, in e);
            acc.Add(e);
        }

        return new ChatResponse { Message = acc.ToMessage(), Id = acc.ResponseId, Model = acc.Model, Finish = acc.Finish, FinishReasonRaw = acc.FinishReasonRaw };
    }
}

internal static class RecordText
{
    /// <summary>Provider errors are scrubbed of secrets before they reach here; this only bounds the length kept in a record.</summary>
    public static string Bound(string text) => text.Length <= 2000 ? text : text[..2000];
}
