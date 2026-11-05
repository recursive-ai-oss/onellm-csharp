using System.Text.Json.Nodes;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;

namespace OneLlm.Hooks;

/// <summary>The places in a run where a hook can act. Flags, so a hook can say which ones it handles.</summary>
[Flags]
public enum HookPoints
{
    /// <summary>No point.</summary>
    None = 0,

    /// <summary>Before a request goes to the model. An interceptor: it may change the request, block the run or answer in the model's place.</summary>
    BeforeInference = 1,

    /// <summary>After the model answered. An interceptor: it may change the answer or block the run.</summary>
    AfterInference = 2,

    /// <summary>Before a tool runs. An interceptor: it may change the inputs, block the call, require approval or answer in the tool's place.</summary>
    BeforeToolCall = 4,

    /// <summary>After a tool ran. An interceptor: it may change the result or block it.</summary>
    AfterToolCall = 8,

    /// <summary>A message was added to the conversation. An observer.</summary>
    OnMessage = 16,

    /// <summary>The instance changed state. An observer.</summary>
    OnStateChange = 32,

    /// <summary>A model call or tool call cost something. An observer.</summary>
    OnCost = 64,

    /// <summary>Before context is compacted. An interceptor: it may block this compaction.</summary>
    BeforeCompaction = 128,

    /// <summary>A stream event arrived from the model. An observer; it sees events only and cannot change them.</summary>
    StreamEvent = 256,

    /// <summary>Every point.</summary>
    All = 511,
}

/// <summary>What a hook tells the run to do.</summary>
public enum HookAction
{
    /// <summary>Go on unchanged.</summary>
    Continue = 0,

    /// <summary>Go on with the changed value the decision carries.</summary>
    Modify = 1,

    /// <summary>Stop this step. At a tool point the model sees an error result with the reason; at an inference point the run fails with <see cref="HookBlockedException"/>.</summary>
    Block = 2,

    /// <summary>Hold the tool call until a person approves it. Only valid at <see cref="HookPoints.BeforeToolCall"/>.</summary>
    RequireApproval = 3,

    /// <summary>Skip the step and use the answer the decision carries: a message in place of the model's, or a result in place of the tool's.</summary>
    Respond = 4,
}

/// <summary>A hook's answer at an interceptor point. Build one with the static members.</summary>
public sealed class HookDecision
{
    private HookDecision(HookAction action, string? reason, object? payload)
    {
        Action = action;
        Reason = reason;
        Payload = payload;
    }

    /// <summary>What to do.</summary>
    public HookAction Action { get; }

    /// <summary>Why, for blocks and approvals; shown to the model or the approver and written to the record. May be null for other actions.</summary>
    public string? Reason { get; }

    internal object? Payload { get; }

    /// <summary>Go on unchanged.</summary>
    public static HookDecision Continue { get; } = new(HookAction.Continue, null, null);

    /// <summary>Blocks the step.</summary>
    /// <param name="reason">Why. Shown to the model at tool points, so write it for the model, and never put a secret in it.</param>
    /// <returns>The decision.</returns>
    public static HookDecision Block(string reason) => new(HookAction.Block, reason ?? throw new ArgumentNullException(nameof(reason)), null);

    /// <summary>Holds a tool call for approval.</summary>
    /// <param name="reason">Why approval is needed; stored with the pending approval.</param>
    /// <returns>The decision.</returns>
    public static HookDecision RequireApproval(string reason) => new(HookAction.RequireApproval, reason ?? throw new ArgumentNullException(nameof(reason)), null);

    /// <summary>At <see cref="HookPoints.BeforeInference"/>: sends a changed request.</summary>
    /// <param name="request">The request to send instead.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision ModifyRequest(ChatRequest request, string? reason = null) =>
        new(HookAction.Modify, reason, request ?? throw new ArgumentNullException(nameof(request)));

    /// <summary>At <see cref="HookPoints.AfterInference"/>: keeps a changed answer.</summary>
    /// <param name="message">The answer to keep instead. Its tool calls are the ones that run.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision ModifyMessage(Message message, string? reason = null) =>
        new(HookAction.Modify, reason, message ?? throw new ArgumentNullException(nameof(message)));

    /// <summary>At <see cref="HookPoints.BeforeToolCall"/>: runs the tool with changed inputs.</summary>
    /// <param name="inputs">The inputs to use instead.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision ModifyInputs(JsonObject inputs, string? reason = null) =>
        new(HookAction.Modify, reason, inputs ?? throw new ArgumentNullException(nameof(inputs)));

    /// <summary>At <see cref="HookPoints.AfterToolCall"/>: gives the model a changed result.</summary>
    /// <param name="result">The result to use instead.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision ModifyResult(ToolResult result, string? reason = null) =>
        new(HookAction.Modify, reason, result ?? throw new ArgumentNullException(nameof(result)));

    /// <summary>At <see cref="HookPoints.BeforeInference"/>: answers without calling the model.</summary>
    /// <param name="message">The assistant message to use as the model's answer.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision Respond(Message message, string? reason = null) =>
        new(HookAction.Respond, reason, message ?? throw new ArgumentNullException(nameof(message)));

    /// <summary>At <see cref="HookPoints.BeforeToolCall"/>: answers without running the tool.</summary>
    /// <param name="result">The result to give the model.</param>
    /// <param name="reason">An optional note for the record.</param>
    /// <returns>The decision.</returns>
    public static HookDecision Respond(ToolResult result, string? reason = null) =>
        new(HookAction.Respond, reason, result ?? throw new ArgumentNullException(nameof(result)));
}

/// <summary>A hook blocked a step that cannot continue without it (an inference), or an interceptor failed. The instance is left in <c>error</c>.</summary>
public sealed class HookBlockedException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="hook">The hook's name.</param>
    /// <param name="point">The point it blocked at.</param>
    /// <param name="reason">Why.</param>
    public HookBlockedException(string hook, HookPoints point, string reason)
        : base($"Hook '{hook}' blocked {point}: {reason}")
    {
        Hook = hook;
        Point = point;
        Reason = reason;
    }

    /// <summary>The hook's name.</summary>
    public string Hook { get; }

    /// <summary>The point.</summary>
    public HookPoints Point { get; }

    /// <summary>The reason the hook gave.</summary>
    public string Reason { get; }
}

/// <summary>What every hook sees: where in which run it is. Read-only by convention; change things by returning a decision.</summary>
public class HookContext
{
    internal HookContext(HookPoints point, Instance instance, int iteration)
    {
        Point = point;
        Instance = instance;
        Iteration = iteration;
    }

    /// <summary>The point being run.</summary>
    public HookPoints Point { get; }

    /// <summary>The instance. Do not change it from a hook.</summary>
    public Instance Instance { get; }

    /// <summary>The id of the instance.</summary>
    public string InstanceId => Instance.InstanceId;

    /// <summary>The number of the model turn in this run, from 1. Zero outside a turn.</summary>
    public int Iteration { get; }
}

/// <summary>Context for <see cref="HookPoints.BeforeInference"/>.</summary>
public sealed class BeforeInferenceContext : HookContext
{
    internal BeforeInferenceContext(Instance instance, int iteration, ChatRequest request)
        : base(HookPoints.BeforeInference, instance, iteration)
    {
        Request = request;
    }

    /// <summary>The request about to be sent, after any earlier hook changed it.</summary>
    public ChatRequest Request { get; internal set; }
}

/// <summary>Context for <see cref="HookPoints.AfterInference"/>.</summary>
public sealed class AfterInferenceContext : HookContext
{
    internal AfterInferenceContext(Instance instance, int iteration, ChatRequest request, Message message, Cost cost)
        : base(HookPoints.AfterInference, instance, iteration)
    {
        Request = request;
        Message = message;
        Cost = cost;
    }

    /// <summary>The request that was sent.</summary>
    public ChatRequest Request { get; }

    /// <summary>The model's answer, after any earlier hook changed it.</summary>
    public Message Message { get; internal set; }

    /// <summary>What the call cost.</summary>
    public Cost Cost { get; }
}

/// <summary>Context for <see cref="HookPoints.BeforeToolCall"/>.</summary>
public sealed class BeforeToolCallContext : HookContext
{
    internal BeforeToolCallContext(Instance instance, int iteration, ToolCall call, ToolSpec? tool, JsonObject inputs)
        : base(HookPoints.BeforeToolCall, instance, iteration)
    {
        Call = call;
        Tool = tool;
        Inputs = inputs;
    }

    /// <summary>The call the model made.</summary>
    public ToolCall Call { get; }

    /// <summary>The tool's definition, or null when the model named a tool the agent does not have.</summary>
    public ToolSpec? Tool { get; }

    /// <summary>The inputs the tool will get, after any earlier hook changed them.</summary>
    public JsonObject Inputs { get; internal set; }
}

/// <summary>Context for <see cref="HookPoints.AfterToolCall"/>.</summary>
public sealed class AfterToolCallContext : HookContext
{
    internal AfterToolCallContext(Instance instance, int iteration, ToolCall call, ToolSpec? tool, ToolResult result)
        : base(HookPoints.AfterToolCall, instance, iteration)
    {
        Call = call;
        Tool = tool;
        Result = result;
    }

    /// <summary>The call the model made.</summary>
    public ToolCall Call { get; }

    /// <summary>The tool's definition, or null for an unknown tool.</summary>
    public ToolSpec? Tool { get; }

    /// <summary>The result the model will see, after any earlier hook changed it.</summary>
    public ToolResult Result { get; internal set; }
}

/// <summary>Context for <see cref="HookPoints.OnMessage"/>.</summary>
public sealed class MessageHookContext : HookContext
{
    internal MessageHookContext(Instance instance, int iteration, Message message)
        : base(HookPoints.OnMessage, instance, iteration)
    {
        Message = message;
    }

    /// <summary>The message that was added.</summary>
    public Message Message { get; }
}

/// <summary>Context for <see cref="HookPoints.OnStateChange"/>.</summary>
public sealed class StateChangeHookContext : HookContext
{
    internal StateChangeHookContext(Instance instance, string? from, string to)
        : base(HookPoints.OnStateChange, instance, 0)
    {
        From = from;
        To = to;
    }

    /// <summary>The state before, or null at the first change.</summary>
    public string? From { get; }

    /// <summary>The state after.</summary>
    public string To { get; }
}

/// <summary>Context for <see cref="HookPoints.OnCost"/>.</summary>
public sealed class CostHookContext : HookContext
{
    internal CostHookContext(Instance instance, int iteration, string source, Cost cost)
        : base(HookPoints.OnCost, instance, iteration)
    {
        Source = source;
        Cost = cost;
    }

    /// <summary>What was paid for: <c>inference</c>, <c>tool:&lt;name&gt;</c> or <c>compaction</c>.</summary>
    public string Source { get; }

    /// <summary>The cost, as line items.</summary>
    public Cost Cost { get; }
}

/// <summary>Context for <see cref="HookPoints.BeforeCompaction"/>.</summary>
public sealed class BeforeCompactionHookContext : HookContext
{
    internal BeforeCompactionHookContext(Instance instance, int iteration, string strategy, IReadOnlyList<string> messageIds)
        : base(HookPoints.BeforeCompaction, instance, iteration)
    {
        Strategy = strategy;
        MessageIds = messageIds;
    }

    /// <summary>The strategy about to run: <c>clearToolResults</c>, <c>compact</c> or <c>providerNative</c>.</summary>
    public string Strategy { get; }

    /// <summary>The ids of the messages it would replace.</summary>
    public IReadOnlyList<string> MessageIds { get; }
}

/// <summary>
/// A set of hooks. Implement only the points named by <see cref="Points"/>; the other methods keep their defaults, which do nothing.
/// Interceptor methods return a <see cref="HookDecision"/>; an interceptor that throws blocks the step (it fails closed). Observer
/// methods cannot change the run; one that throws is logged and the run goes on (it fails open).
/// </summary>
public interface IHook
{
    /// <summary>The hook's name, for records and errors.</summary>
    string Name { get; }

    /// <summary>The points this hook handles. Methods for other points are never called.</summary>
    HookPoints Points { get; }

    /// <summary>Runs before a request is sent.</summary>
    /// <param name="context">The request and where the run is.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What to do.</returns>
    ValueTask<HookDecision> BeforeInferenceAsync(BeforeInferenceContext context, CancellationToken cancellationToken) => ValueTask.FromResult(HookDecision.Continue);

    /// <summary>Runs after the model answered.</summary>
    /// <param name="context">The answer.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What to do.</returns>
    ValueTask<HookDecision> AfterInferenceAsync(AfterInferenceContext context, CancellationToken cancellationToken) => ValueTask.FromResult(HookDecision.Continue);

    /// <summary>Runs before a tool runs.</summary>
    /// <param name="context">The call and its inputs.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What to do.</returns>
    ValueTask<HookDecision> BeforeToolCallAsync(BeforeToolCallContext context, CancellationToken cancellationToken) => ValueTask.FromResult(HookDecision.Continue);

    /// <summary>Runs after a tool ran.</summary>
    /// <param name="context">The call and its result.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What to do.</returns>
    ValueTask<HookDecision> AfterToolCallAsync(AfterToolCallContext context, CancellationToken cancellationToken) => ValueTask.FromResult(HookDecision.Continue);

    /// <summary>Runs before context is compacted.</summary>
    /// <param name="context">The strategy and the messages it would replace.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Continue, or Block to skip this compaction.</returns>
    ValueTask<HookDecision> BeforeCompactionAsync(BeforeCompactionHookContext context, CancellationToken cancellationToken) => ValueTask.FromResult(HookDecision.Continue);

    /// <summary>Observes a message added to the conversation.</summary>
    /// <param name="context">The message.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask OnMessageAsync(MessageHookContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Observes a state change.</summary>
    /// <param name="context">The old and new state.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask OnStateChangeAsync(StateChangeHookContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Observes a cost.</summary>
    /// <param name="context">The cost and what it paid for.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask OnCostAsync(CostHookContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Observes a stream event. Called synchronously on the stream's path, so keep it fast.</summary>
    /// <param name="context">Where the run is.</param>
    /// <param name="streamEvent">The event. Its byte slices are valid only during the call.</param>
    void OnStreamEvent(HookContext context, in StreamEvent streamEvent)
    {
    }
}
