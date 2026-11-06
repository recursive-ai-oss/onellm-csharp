using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.Agents;

/// <summary>The kinds of event a running agent reports.</summary>
public enum AgentEventKind
{
    /// <summary>The instance changed state. <see cref="AgentEvent.State"/> holds the new one.</summary>
    StateChanged = 0,

    /// <summary>A message was added to the conversation. <see cref="AgentEvent.Message"/> holds it.</summary>
    MessageAdded = 1,

    /// <summary>A tool call is about to run. <see cref="AgentEvent.ToolCall"/> holds it.</summary>
    ToolCallStarted = 2,

    /// <summary>A tool call finished. <see cref="AgentEvent.ToolCall"/> holds it with its result.</summary>
    ToolCallFinished = 3,

    /// <summary>A tool call is waiting for a person. <see cref="AgentEvent.ToolCall"/> holds it.</summary>
    ApprovalRequested = 4,

    /// <summary>A pending call was approved or denied. <see cref="AgentEvent.Detail"/> says which.</summary>
    ApprovalDecided = 5,

    /// <summary>Context was compacted. <see cref="AgentEvent.Detail"/> names the strategy.</summary>
    Compacted = 6,

    /// <summary>A monitor fired. <see cref="AgentEvent.Detail"/> holds the monitor id and the condition.</summary>
    MonitorFired = 7,

    /// <summary>The run segment ended; the instance is resting in <see cref="AgentEvent.State"/>.</summary>
    Finished = 8,
}

/// <summary>Something that happened in a running agent, in the order it happened.</summary>
public sealed class AgentEvent
{
    internal AgentEvent(AgentEventKind kind, string instanceId, DateTimeOffset time)
    {
        Kind = kind;
        InstanceId = instanceId;
        Time = time;
    }

    /// <summary>What happened.</summary>
    public AgentEventKind Kind { get; }

    /// <summary>The instance it happened in.</summary>
    public string InstanceId { get; }

    /// <summary>When, by the runtime's clock.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>The state, for state changes and the end of a run.</summary>
    public string? State { get; internal init; }

    /// <summary>The message, for <see cref="AgentEventKind.MessageAdded"/>.</summary>
    public Message? Message { get; internal init; }

    /// <summary>The tool call, for tool and approval events.</summary>
    public ToolCall? ToolCall { get; internal init; }

    /// <summary>Extra detail: a decision, a strategy name, a monitor id and condition.</summary>
    public string? Detail { get; internal init; }
}

/// <summary>Why an agent run stopped.</summary>
public enum AgentStopReason
{
    /// <summary>A loop-ending tool (such as <c>task_complete</c>) ran. The instance is <c>stopped</c>.</summary>
    Completed = 0,

    /// <summary>The model answered without calling a tool. The instance is <c>waiting</c> for input.</summary>
    Waiting = 1,

    /// <summary>A tool call needs a person. The instance is <c>waiting_for_approval</c>; approve or deny it to go on.</summary>
    WaitingForApproval = 2,

    /// <summary>The run used all its turns without finishing. The instance is in <c>error</c>, as the origin format's runtimes leave it.</summary>
    MaxIterations = 3,

    /// <summary>The instance is not running and finished earlier in some other way (a cancelled run, a stopped instance read back).</summary>
    Stopped = 4,
}

/// <summary>The outcome of an agent run, up to the point where it stopped.</summary>
public sealed class AgentResult
{
    internal AgentResult(Instance instance, AgentStopReason reason, string text, JsonNode? output, string? completedBy, int turns, Cost cost, Usage? usage, IReadOnlyList<string> warnings)
    {
        Instance = instance;
        StopReason = reason;
        Text = text;
        Output = output;
        CompletedBy = completedBy;
        Turns = turns;
        Cost = cost;
        Usage = usage;
        Warnings = warnings;
    }

    /// <summary>The instance as saved.</summary>
    public Instance Instance { get; }

    /// <summary>Why the run stopped.</summary>
    public AgentStopReason StopReason { get; }

    /// <summary>
    /// The agent's answer: for a run ended by <c>task_complete</c> its <c>result</c>; for another loop-ending tool the text it returned;
    /// otherwise the text of the last assistant turn. Empty when there is none.
    /// </summary>
    public string Text { get; }

    /// <summary>The typed result of the loop-ending tool (for <c>task_complete</c>: <c>{title?, result, summary}</c>), or null.</summary>
    public JsonNode? Output { get; }

    /// <summary>The name of the tool that ended the loop, or null.</summary>
    public string? CompletedBy { get; }

    /// <summary>The number of model turns this run took.</summary>
    public int Turns { get; }

    /// <summary>What this run cost in model calls and tool calls, as line items. Sub-agents are not included; see <see cref="SubtreeCost"/>.</summary>
    public Cost Cost { get; }

    /// <summary>Token counts summed over this run's model calls; null when it made none.</summary>
    public Usage? Usage { get; }

    /// <summary>The state the instance is in.</summary>
    public string State => Instance.State ?? "";

    /// <summary>The tool calls waiting for a decision.</summary>
    public IReadOnlyList<PendingApproval> PendingApprovals => Instance.PendingApprovals;

    /// <summary>Things the run ignored or changed.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The cost of this instance's whole history plus every sub-agent it started, as a total in USD. Filled by the runtime when the run ends.</summary>
    public decimal SubtreeCost { get; internal set; }

    /// <summary>Reads <see cref="Output"/> as a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/> (source-generated, so the call is trim-safe).</param>
    /// <returns>The value.</returns>
    /// <exception cref="StructuredOutputException">The run has no typed output, or it does not fit the type.</exception>
    public T GetOutput<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (Output is null)
        {
            throw new StructuredOutputException("The run has no typed output.", Text);
        }

        try
        {
            return Output.Deserialize(typeInfo) ?? throw new StructuredOutputException("The typed output is null.", Output.ToJsonString());
        }
        catch (JsonException ex)
        {
            throw new StructuredOutputException("The typed output does not fit the requested type.", Output.ToJsonString(), ex);
        }
    }
}

/// <summary>Options for starting an agent beyond its definition and inputs.</summary>
public sealed class AgentStartOptions
{
    /// <summary>A user message added after the definition's initial prompts, such as the task to do. Null adds none.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// The grants to ask for. The agent gets these intersected with <see cref="Runtime.Capabilities"/> (and with the caller's grants
    /// for a sub-agent), never more. Null asks for everything the runtime grants.
    /// </summary>
    public ICapabilities? Capabilities { get; init; }

    /// <summary>The instance that started this one. Sets <c>callerInstanceId</c> and <c>spawnerInstanceId</c>, and limits the grants to the caller's.</summary>
    public string? SpawnerInstanceId { get; init; }

    /// <summary>The caller's tool call that started this instance.</summary>
    public string? CallerToolCallId { get; init; }

    /// <summary>The prompt the caller gave, stored as <c>delegationPrompt</c>.</summary>
    public string? DelegationPrompt { get; init; }

    /// <summary>Tools to offer in addition to the definition's references, such as tools made in code.</summary>
    public IReadOnlyList<ITool> Tools { get; init; } = [];

    /// <summary>Who the first user message comes from. Defaults to the caller agent when there is one, otherwise <c>api</c>.</summary>
    public MessageSource? Source { get; init; }

    /// <summary>Replaces the definition's context strategy; use <see cref="ContextStrategyOptions.None"/> to switch it off.</summary>
    public ContextStrategyOptions? ContextStrategy { get; init; }

    /// <summary>The working directory template value for <c>.system.workingDir</c>, replacing the definition's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Value for <c>.swarm.uri</c> in templates.</summary>
    public string? SwarmUri { get; init; }

    /// <summary>
    /// When set, the definition's references may only resolve to tools with these names (plus <c>task_complete</c>); anything else fails the
    /// load, or is left out in lenient mode. Used for sub-agents started from files an agent could have written. Saved with the instance.
    /// </summary>
    internal IReadOnlySet<string>? ToolCeiling { get; init; }
}
