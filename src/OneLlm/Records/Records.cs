using OneLlm.Instances;
using OneLlm.Messages;

namespace OneLlm.Records;

/// <summary>One entry of the audit trail a run leaves. Instance state is the truth; records are the trail of how it got there.</summary>
public abstract class AuditRecord
{
    /// <summary>The kind name written to files: <c>run</c>, <c>message</c>, <c>inference</c>, <c>toolCall</c>, <c>taskCompletion</c>, <c>approval</c>, <c>compaction</c>, <c>hook</c>, <c>error</c> or <c>rawTurn</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>The instance the entry belongs to.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>When it happened, by the runtime's clock.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>The message it belongs to, when there is one.</summary>
    public string? MessageId { get; init; }
}

/// <summary>A run started, stopped, resumed or was cancelled.</summary>
public sealed class RunRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "run";

    /// <summary>What happened: <c>started</c>, <c>stopped</c>, <c>resumed</c>, <c>cancelled</c> or <c>stateChanged</c>.</summary>
    public string Event { get; init; } = "";

    /// <summary>The state after the event, or null.</summary>
    public string? State { get; init; }

    /// <summary>The model the run uses, or null.</summary>
    public string? Model { get; init; }
}

/// <summary>A message was added to the conversation. Carries the message with its <see cref="Message.Source"/>.</summary>
public sealed class MessageRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "message";

    /// <summary>The message.</summary>
    public required Message Message { get; init; }
}

/// <summary>One model call: what it used and what it cost, as line items.</summary>
public sealed class InferenceRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "inference";

    /// <summary>The provider name, for example <c>anthropic</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>The operation: <c>chat</c>, <c>embeddings</c>, ...</summary>
    public string Operation { get; init; } = "chat";

    /// <summary>The usage with its cost lines, or null for a call that failed before any usage.</summary>
    public Usage? Usage { get; init; }

    /// <summary>How long the call took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Why the model stopped (<c>stop</c>, <c>tool_calls</c>, <c>length</c>, ...), or null.</summary>
    public string? FinishReason { get; init; }
}

/// <summary>One tool call and its result, with its cost.</summary>
public sealed class ToolCallRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "toolCall";

    /// <summary>The call, with its inputs, result parts and cost.</summary>
    public required ToolCall Call { get; init; }

    /// <summary>How long the tool ran.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>The agent finished with a loop-ending tool such as <c>task_complete</c>.</summary>
public sealed class TaskCompletionRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "taskCompletion";

    /// <summary>The tool that ended the loop.</summary>
    public string Tool { get; init; } = "";

    /// <summary>The result text it gave.</summary>
    public string? Result { get; init; }
}

/// <summary>An approval was requested, granted or denied.</summary>
public sealed class ApprovalRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "approval";

    /// <summary>The tool call that needs a decision.</summary>
    public string ToolCallId { get; init; } = "";

    /// <summary>The tool's name.</summary>
    public string Tool { get; init; } = "";

    /// <summary><c>requested</c>, <c>approved</c> or <c>denied</c>.</summary>
    public string Decision { get; init; } = "requested";

    /// <summary>Who decided, when a person or a hook did.</summary>
    public string? By { get; init; }
}

/// <summary>Context was compacted.</summary>
public sealed class CompactionRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "compaction";

    /// <summary>What was replaced and what it cost.</summary>
    public required Compaction Compaction { get; init; }
}

/// <summary>A hook changed, blocked or answered for a step. Observers that only watch leave no record.</summary>
public sealed class HookRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "hook";

    /// <summary>The hook point, for example <c>beforeToolCall</c>.</summary>
    public string Point { get; init; } = "";

    /// <summary>The hook's name or file.</summary>
    public string Hook { get; init; } = "";

    /// <summary><c>modify</c>, <c>block</c>, <c>requireApproval</c> or <c>respond</c>.</summary>
    public string Decision { get; init; } = "";

    /// <summary>The reason the hook gave, or null.</summary>
    public string? Reason { get; init; }
}

/// <summary>Something failed.</summary>
public sealed class ErrorRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "error";

    /// <summary>The exception type name.</summary>
    public string ErrorType { get; init; } = "";

    /// <summary>The message. OneLLM's own errors never contain a secret.</summary>
    public string Message { get; init; } = "";

    /// <summary>Where it happened (<c>inference</c>, <c>tool:name</c>, <c>hook</c>, ...), or null.</summary>
    public string? Where { get; init; }
}

/// <summary>The raw request and response of one turn, for debugging. Off by default; secrets are removed before it exists.</summary>
public sealed class RawTurnRecord : AuditRecord
{
    /// <inheritdoc />
    public override string Kind => "rawTurn";

    /// <summary>The request body with secrets masked.</summary>
    public string Request { get; init; } = "";

    /// <summary>The response body with secrets masked.</summary>
    public string Response { get; init; } = "";

    /// <summary>Builds a raw turn record with every given secret masked out of both bodies.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <param name="time">When the turn happened.</param>
    /// <param name="request">The request body.</param>
    /// <param name="response">The response body.</param>
    /// <param name="secrets">Secrets to mask, usually the API keys in use.</param>
    /// <returns>The record.</returns>
    public static RawTurnRecord Create(string instanceId, DateTimeOffset time, string request, string response, IEnumerable<Secret> secrets) => new()
    {
        InstanceId = instanceId,
        Time = time,
        Request = Secret.ScrubAll(request, secrets),
        Response = Secret.ScrubAll(response, secrets),
    };
}
