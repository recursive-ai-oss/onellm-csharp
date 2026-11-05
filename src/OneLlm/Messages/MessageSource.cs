namespace OneLlm.Messages;

/// <summary>Where a user message came from.</summary>
public sealed class MessageSource
{
    /// <summary>One of <c>user</c>, <c>agent</c>, <c>runtime</c> or <c>api</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>For <c>user</c>: the person's id, when known.</summary>
    public string? UserId { get; init; }

    /// <summary>For <c>user</c>: the person's email, when known.</summary>
    public string? UserEmail { get; init; }

    /// <summary>For <c>agent</c>: the sending instance.</summary>
    public string? InstanceId { get; init; }

    /// <summary>For <c>agent</c>: the sending instance's definition URI.</summary>
    public string? AgentUri { get; init; }

    /// <summary>For <c>agent</c>: the tool call that sent the message.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For <c>runtime</c>: why the runtime wrote the message (<c>monitor</c>, <c>child-finished</c>, ...).</summary>
    public string? Reason { get; init; }

    /// <summary>Fields that were present when the source was read and that OneLLM does not model.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
