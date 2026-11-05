using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Tools;

namespace OneLlm.Hooks;

/// <summary>
/// The hooks registered on a <see cref="Runtime"/> (the host layer). Add an <see cref="IHook"/> or a lambda for one point. Hooks run in
/// the order they were added. Changes apply to runs that start afterwards.
/// </summary>
public sealed class HookRegistry
{
    private readonly object _gate = new();
    private IHook[] _hooks = [];

    /// <summary>The number of hooks registered.</summary>
    public int Count => Volatile.Read(ref _hooks).Length;

    internal IHook[] Snapshot => Volatile.Read(ref _hooks);

    /// <summary>Adds a hook.</summary>
    /// <param name="hook">The hook.</param>
    public void Add(IHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_gate)
        {
            _hooks = [.. _hooks, hook];
        }
    }

    /// <summary>Removes a hook added earlier.</summary>
    /// <param name="hook">The hook.</param>
    /// <returns>True when it was registered.</returns>
    public bool Remove(IHook hook)
    {
        lock (_gate)
        {
            int at = Array.IndexOf(_hooks, hook);
            if (at < 0)
            {
                return false;
            }

            _hooks = [.. _hooks.Where((_, i) => i != at)];
            return true;
        }
    }

    /// <summary>Adds an interceptor for <see cref="HookPoints.BeforeInference"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records; defaults to <c>beforeInference</c>.</param>
    /// <returns>The registered hook, for <see cref="Remove"/>.</returns>
    public IHook BeforeInference(Func<BeforeInferenceContext, CancellationToken, ValueTask<HookDecision>> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "beforeInference", HookPoints.BeforeInference) { BeforeInference = hook });

    /// <summary>Adds an interceptor for <see cref="HookPoints.AfterInference"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook AfterInference(Func<AfterInferenceContext, CancellationToken, ValueTask<HookDecision>> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "afterInference", HookPoints.AfterInference) { AfterInference = hook });

    /// <summary>Adds an interceptor for <see cref="HookPoints.BeforeToolCall"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook BeforeToolCall(Func<BeforeToolCallContext, CancellationToken, ValueTask<HookDecision>> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "beforeToolCall", HookPoints.BeforeToolCall) { BeforeToolCall = hook });

    /// <summary>Adds an interceptor for <see cref="HookPoints.AfterToolCall"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook AfterToolCall(Func<AfterToolCallContext, CancellationToken, ValueTask<HookDecision>> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "afterToolCall", HookPoints.AfterToolCall) { AfterToolCall = hook });

    /// <summary>Adds an interceptor for <see cref="HookPoints.BeforeCompaction"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook BeforeCompaction(Func<BeforeCompactionHookContext, CancellationToken, ValueTask<HookDecision>> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "beforeCompaction", HookPoints.BeforeCompaction) { BeforeCompaction = hook });

    /// <summary>Adds an observer for <see cref="HookPoints.OnMessage"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook OnMessage(Func<MessageHookContext, CancellationToken, ValueTask> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "onMessage", HookPoints.OnMessage) { OnMessage = hook });

    /// <summary>Adds an observer for <see cref="HookPoints.OnStateChange"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook OnStateChange(Func<StateChangeHookContext, CancellationToken, ValueTask> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "onStateChange", HookPoints.OnStateChange) { OnStateChange = hook });

    /// <summary>Adds an observer for <see cref="HookPoints.OnCost"/>.</summary>
    /// <param name="hook">The function.</param>
    /// <param name="name">The name used in records.</param>
    /// <returns>The registered hook.</returns>
    public IHook OnCost(Func<CostHookContext, CancellationToken, ValueTask> hook, string? name = null) =>
        AddDelegate(new DelegateHook(name ?? "onCost", HookPoints.OnCost) { OnCost = hook });

    private DelegateHook AddDelegate(DelegateHook hook)
    {
        Add(hook);
        return hook;
    }

    private sealed class DelegateHook(string name, HookPoints points) : IHook
    {
        public string Name { get; } = name;

        public HookPoints Points { get; } = points;

        public Func<BeforeInferenceContext, CancellationToken, ValueTask<HookDecision>>? BeforeInference { get; init; }

        public Func<AfterInferenceContext, CancellationToken, ValueTask<HookDecision>>? AfterInference { get; init; }

        public Func<BeforeToolCallContext, CancellationToken, ValueTask<HookDecision>>? BeforeToolCall { get; init; }

        public Func<AfterToolCallContext, CancellationToken, ValueTask<HookDecision>>? AfterToolCall { get; init; }

        public Func<BeforeCompactionHookContext, CancellationToken, ValueTask<HookDecision>>? BeforeCompaction { get; init; }

        public Func<MessageHookContext, CancellationToken, ValueTask>? OnMessage { get; init; }

        public Func<StateChangeHookContext, CancellationToken, ValueTask>? OnStateChange { get; init; }

        public Func<CostHookContext, CancellationToken, ValueTask>? OnCost { get; init; }

        ValueTask<HookDecision> IHook.BeforeInferenceAsync(BeforeInferenceContext c, CancellationToken t) => BeforeInference is { } f ? f(c, t) : ValueTask.FromResult(HookDecision.Continue);

        ValueTask<HookDecision> IHook.AfterInferenceAsync(AfterInferenceContext c, CancellationToken t) => AfterInference is { } f ? f(c, t) : ValueTask.FromResult(HookDecision.Continue);

        ValueTask<HookDecision> IHook.BeforeToolCallAsync(BeforeToolCallContext c, CancellationToken t) => BeforeToolCall is { } f ? f(c, t) : ValueTask.FromResult(HookDecision.Continue);

        ValueTask<HookDecision> IHook.AfterToolCallAsync(AfterToolCallContext c, CancellationToken t) => AfterToolCall is { } f ? f(c, t) : ValueTask.FromResult(HookDecision.Continue);

        ValueTask<HookDecision> IHook.BeforeCompactionAsync(BeforeCompactionHookContext c, CancellationToken t) => BeforeCompaction is { } f ? f(c, t) : ValueTask.FromResult(HookDecision.Continue);

        ValueTask IHook.OnMessageAsync(MessageHookContext c, CancellationToken t) => OnMessage is { } f ? f(c, t) : ValueTask.CompletedTask;

        ValueTask IHook.OnStateChangeAsync(StateChangeHookContext c, CancellationToken t) => OnStateChange is { } f ? f(c, t) : ValueTask.CompletedTask;

        ValueTask IHook.OnCostAsync(CostHookContext c, CancellationToken t) => OnCost is { } f ? f(c, t) : ValueTask.CompletedTask;
    }
}

/// <summary>Supplies the hooks a definition's <c>hooks:</c> list names (for example JavaScript files). The core package ships none; an adapter package sets <see cref="Runtime.DefinitionHooks"/>.</summary>
public interface IDefinitionHookSource
{
    /// <summary>Loads the hooks a definition names.</summary>
    /// <param name="definition">The definition (or instance) whose <see cref="Definitions.AgentDefinition.Hooks"/> to load.</param>
    /// <param name="capabilities">What the run is granted; the hooks run under it.</param>
    /// <returns>The hooks, in the order the definition lists them.</returns>
    IReadOnlyList<IHook> Load(Definitions.AgentDefinition definition, ICapabilities capabilities);
}

/// <summary>What an interceptor chain decided.</summary>
internal readonly record struct HookOutcome(HookAction Action, string? Reason, object? Payload, string? Hook)
{
    public static HookOutcome Continue => default;
}

/// <summary>
/// The hooks of one run, ordered: host hooks wrap definition hooks, so before-points run host then definition and after-points run
/// definition then host. Within a layer, registration order. Interceptors fail closed, observers fail open. Callers check
/// <see cref="Has"/> first, so a run with no hooks never builds a context.
/// </summary>
internal sealed class HookSet
{
    public static readonly HookSet Empty = new(null, [], []);

    private readonly Runtime? _runtime;
    private readonly IHook[] _beforeInference;
    private readonly IHook[] _afterInference;
    private readonly IHook[] _beforeToolCall;
    private readonly IHook[] _afterToolCall;
    private readonly IHook[] _beforeCompaction;
    private readonly IHook[] _onMessage;
    private readonly IHook[] _onStateChange;
    private readonly IHook[] _onCost;
    private readonly IHook[] _onStream;
    private readonly HookPoints _present;

    public HookSet(Runtime? runtime, IReadOnlyList<IHook> host, IReadOnlyList<IHook> definition)
    {
        _runtime = runtime;
        _beforeInference = Order(host, definition, HookPoints.BeforeInference, true);
        _afterInference = Order(host, definition, HookPoints.AfterInference, false);
        _beforeToolCall = Order(host, definition, HookPoints.BeforeToolCall, true);
        _afterToolCall = Order(host, definition, HookPoints.AfterToolCall, false);
        _beforeCompaction = Order(host, definition, HookPoints.BeforeCompaction, true);
        _onMessage = Order(host, definition, HookPoints.OnMessage, true);
        _onStateChange = Order(host, definition, HookPoints.OnStateChange, true);
        _onCost = Order(host, definition, HookPoints.OnCost, true);
        _onStream = Order(host, definition, HookPoints.StreamEvent, true);
        _present = (_beforeInference.Length > 0 ? HookPoints.BeforeInference : 0)
            | (_afterInference.Length > 0 ? HookPoints.AfterInference : 0)
            | (_beforeToolCall.Length > 0 ? HookPoints.BeforeToolCall : 0)
            | (_afterToolCall.Length > 0 ? HookPoints.AfterToolCall : 0)
            | (_beforeCompaction.Length > 0 ? HookPoints.BeforeCompaction : 0)
            | (_onMessage.Length > 0 ? HookPoints.OnMessage : 0)
            | (_onStateChange.Length > 0 ? HookPoints.OnStateChange : 0)
            | (_onCost.Length > 0 ? HookPoints.OnCost : 0)
            | (_onStream.Length > 0 ? HookPoints.StreamEvent : 0);
    }

    /// <summary>True when at least one hook handles the point. Costs one mask test.</summary>
    public bool Has(HookPoints point) => (_present & point) != 0;

    private static IHook[] Order(IReadOnlyList<IHook> host, IReadOnlyList<IHook> definition, HookPoints point, bool hostFirst)
    {
        if (host.Count == 0 && definition.Count == 0)
        {
            return [];
        }

        IEnumerable<IHook> first = hostFirst ? host : definition;
        IEnumerable<IHook> second = hostFirst ? definition : host;
        return [.. first.Concat(second).Where(h => (h.Points & point) != 0)];
    }

    public async ValueTask<HookOutcome> BeforeInferenceAsync(BeforeInferenceContext c, CancellationToken ct)
    {
        foreach (IHook h in _beforeInference)
        {
            HookDecision d;
            try
            {
                d = await h.BeforeInferenceAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailClosedAsync(h, c, ex, ct).ConfigureAwait(false);
            }

            switch (d.Action)
            {
                case HookAction.Continue:
                    break;
                case HookAction.Modify when d.Payload is ChatRequest r:
                    c.Request = r;
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    break;
                case HookAction.Block:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Block, d.Reason, null, h.Name);
                case HookAction.Respond when d.Payload is Message m:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Respond, d.Reason, m, h.Name);
                default:
                    return await FailClosedAsync(h, c, Invalid(d, c.Point), ct).ConfigureAwait(false);
            }
        }

        return HookOutcome.Continue;
    }

    public async ValueTask<HookOutcome> AfterInferenceAsync(AfterInferenceContext c, CancellationToken ct)
    {
        foreach (IHook h in _afterInference)
        {
            HookDecision d;
            try
            {
                d = await h.AfterInferenceAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailClosedAsync(h, c, ex, ct).ConfigureAwait(false);
            }

            switch (d.Action)
            {
                case HookAction.Continue:
                    break;
                case HookAction.Modify when d.Payload is Message m:
                    c.Message = m;
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    break;
                case HookAction.Block:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Block, d.Reason, null, h.Name);
                default:
                    return await FailClosedAsync(h, c, Invalid(d, c.Point), ct).ConfigureAwait(false);
            }
        }

        return HookOutcome.Continue;
    }

    public async ValueTask<HookOutcome> BeforeToolCallAsync(BeforeToolCallContext c, CancellationToken ct)
    {
        HookOutcome approval = HookOutcome.Continue;
        foreach (IHook h in _beforeToolCall)
        {
            HookDecision d;
            try
            {
                d = await h.BeforeToolCallAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailClosedAsync(h, c, ex, ct).ConfigureAwait(false);
            }

            switch (d.Action)
            {
                case HookAction.Continue:
                    break;
                case HookAction.Modify when d.Payload is System.Text.Json.Nodes.JsonObject inputs:
                    c.Inputs = inputs;
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    break;
                case HookAction.Block:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Block, d.Reason, null, h.Name);
                case HookAction.Respond when d.Payload is ToolResult result:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Respond, d.Reason, result, h.Name);
                case HookAction.RequireApproval:
                    // Later hooks may still change the inputs or block; the first approval request is the one kept.
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    if (approval.Action == HookAction.Continue)
                    {
                        approval = new HookOutcome(HookAction.RequireApproval, d.Reason, null, h.Name);
                    }

                    break;
                default:
                    return await FailClosedAsync(h, c, Invalid(d, c.Point), ct).ConfigureAwait(false);
            }
        }

        return approval;
    }

    public async ValueTask<HookOutcome> AfterToolCallAsync(AfterToolCallContext c, CancellationToken ct)
    {
        foreach (IHook h in _afterToolCall)
        {
            HookDecision d;
            try
            {
                d = await h.AfterToolCallAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailClosedAsync(h, c, ex, ct).ConfigureAwait(false);
            }

            switch (d.Action)
            {
                case HookAction.Continue:
                    break;
                case HookAction.Modify when d.Payload is ToolResult r:
                    c.Result = r;
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    break;
                case HookAction.Block:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Block, d.Reason, null, h.Name);
                default:
                    return await FailClosedAsync(h, c, Invalid(d, c.Point), ct).ConfigureAwait(false);
            }
        }

        return HookOutcome.Continue;
    }

    public async ValueTask<HookOutcome> BeforeCompactionAsync(BeforeCompactionHookContext c, CancellationToken ct)
    {
        foreach (IHook h in _beforeCompaction)
        {
            HookDecision d;
            try
            {
                d = await h.BeforeCompactionAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailClosedAsync(h, c, ex, ct).ConfigureAwait(false);
            }

            switch (d.Action)
            {
                case HookAction.Continue:
                    break;
                case HookAction.Block:
                    await RecordAsync(c, h, d, ct).ConfigureAwait(false);
                    return new HookOutcome(HookAction.Block, d.Reason, null, h.Name);
                default:
                    return await FailClosedAsync(h, c, Invalid(d, c.Point), ct).ConfigureAwait(false);
            }
        }

        return HookOutcome.Continue;
    }

    public async ValueTask EmitMessageAsync(MessageHookContext c, CancellationToken ct)
    {
        foreach (IHook h in _onMessage)
        {
            try
            {
                await h.OnMessageAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await ObserverFailedAsync(h, c, ex).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask EmitStateChangeAsync(StateChangeHookContext c, CancellationToken ct)
    {
        foreach (IHook h in _onStateChange)
        {
            try
            {
                await h.OnStateChangeAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await ObserverFailedAsync(h, c, ex).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask EmitCostAsync(CostHookContext c, CancellationToken ct)
    {
        foreach (IHook h in _onCost)
        {
            try
            {
                await h.OnCostAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await ObserverFailedAsync(h, c, ex).ConfigureAwait(false);
            }
        }
    }

    public void EmitStreamEvent(HookContext c, in StreamEvent e)
    {
        foreach (IHook h in _onStream)
        {
            try
            {
                h.OnStreamEvent(c, in e);
            }
            catch (Exception)
            {
                // An observer that fails must not stop the stream; it is synchronous, so there is nowhere to await a record.
            }
        }
    }

    private static InvalidOperationException Invalid(HookDecision d, HookPoints point) =>
        new($"The decision {d.Action} is not valid at {point}, or carries the wrong kind of value.");

    private async ValueTask<HookOutcome> FailClosedAsync(IHook hook, HookContext c, Exception ex, CancellationToken ct)
    {
        string reason = $"hook '{hook.Name}' failed: {ex.GetType().Name}";
        if (_runtime is not null)
        {
            DateTimeOffset now = _runtime.TimeProvider.GetUtcNow();
            await _runtime.WriteRecordAsync(new ErrorRecord { InstanceId = c.InstanceId, Time = now, ErrorType = ex.GetType().Name, Message = Runners.RecordText.Bound(ex.Message), Where = "hook:" + hook.Name }, CancellationToken.None).ConfigureAwait(false);
            await _runtime.WriteRecordAsync(new HookRecord { InstanceId = c.InstanceId, Time = now, Point = PointName(c.Point), Hook = hook.Name, Decision = "block", Reason = reason }, CancellationToken.None).ConfigureAwait(false);
        }

        return new HookOutcome(HookAction.Block, reason, null, hook.Name);
    }

    private async ValueTask ObserverFailedAsync(IHook hook, HookContext c, Exception ex)
    {
        if (_runtime is not null)
        {
            await _runtime.WriteRecordAsync(
                new ErrorRecord { InstanceId = c.InstanceId, Time = _runtime.TimeProvider.GetUtcNow(), ErrorType = ex.GetType().Name, Message = Runners.RecordText.Bound(ex.Message), Where = "hook:" + hook.Name },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask RecordAsync(HookContext c, IHook hook, HookDecision d, CancellationToken ct)
    {
        if (_runtime is null)
        {
            return;
        }

        string decision = d.Action switch
        {
            HookAction.Modify => "modify",
            HookAction.Block => "block",
            HookAction.RequireApproval => "requireApproval",
            _ => "respond",
        };
        await _runtime.WriteRecordAsync(
            new HookRecord { InstanceId = c.InstanceId, Time = _runtime.TimeProvider.GetUtcNow(), Point = PointName(c.Point), Hook = hook.Name, Decision = decision, Reason = d.Reason },
            ct).ConfigureAwait(false);
    }

    internal static string PointName(HookPoints p) => p switch
    {
        HookPoints.BeforeInference => "beforeInference",
        HookPoints.AfterInference => "afterInference",
        HookPoints.BeforeToolCall => "beforeToolCall",
        HookPoints.AfterToolCall => "afterToolCall",
        HookPoints.OnMessage => "onMessage",
        HookPoints.OnStateChange => "onStateChange",
        HookPoints.OnCost => "onCost",
        HookPoints.BeforeCompaction => "beforeCompaction",
        HookPoints.StreamEvent => "streamEvent",
        _ => p.ToString(),
    };
}
