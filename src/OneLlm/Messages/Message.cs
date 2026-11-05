namespace OneLlm.Messages;

/// <summary>
/// One turn of a conversation: what the user sent, or what the assistant answered, together with its tool calls, reasoning and usage.
/// </summary>
/// <remarks>
/// In memory an assistant turn is a single ordered sequence: <see cref="Assistant"/> holds every part in the order the model produced them
/// (reasoning, text, hosted tool calls, ...) and <see cref="ToolCalls"/> hold the client-side calls, each placed by <see cref="ToolCall.After"/>.
/// On disk the turn is written as the definition format's lists (<c>assistant</c>, <c>reasoning</c>, <c>toolCalls</c>) plus an order index
/// in extension keys, so a reader that knows only those lists still reads the turn. See the message format spec.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(MessageJsonConverter))]
[OneYaml.YamlConverter(typeof(MessageYamlConverter))]
public sealed class Message
{
    /// <summary>The message id. Unique within an instance.</summary>
    public string Id { get; init; } = "";

    /// <summary>What the user sent. Empty for an assistant turn.</summary>
    public IReadOnlyList<Part> User { get; init; } = [];

    /// <summary>The assistant's parts in the order the model produced them. Empty for a user turn.</summary>
    public IReadOnlyList<Part> Assistant { get; init; } = [];

    /// <summary>The client-side tool calls of the turn, in the order the model made them.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>When the message was created. Null when unknown.</summary>
    public DateTimeOffset? DateTime { get; init; }

    /// <summary>For user messages: where the message came from.</summary>
    public MessageSource? Source { get; init; }

    /// <summary>True when a cache breakpoint should follow this message.</summary>
    public bool Cache { get; init; }

    /// <summary>What producing this assistant turn consumed and cost.</summary>
    public Usage? Usage { get; init; }

    /// <summary>A template deciding whether this initial prompt runs; meaningful only in initial prompts.</summary>
    public string? When { get; init; }

    /// <summary>Fields that were present when the message was read and that OneLLM does not model.</summary>
    public Dictionary<string, object?>? ExtensionData { get; init; }

    /// <summary>Creates a user message with one text part.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The message id, or null for an empty one.</param>
    /// <returns>The message.</returns>
    public static Message FromUser(string text, string? id = null) =>
        new() { Id = id ?? "", User = [new TextPart(text)] };

    /// <summary>Creates an assistant message with one text part.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The message id, or null for an empty one.</param>
    /// <returns>The message.</returns>
    public static Message FromAssistant(string text, string? id = null) =>
        new() { Id = id ?? "", Assistant = [new TextPart(text)] };

    /// <summary>The concatenated text of the assistant's text parts, in order.</summary>
    /// <returns>The text, or an empty string when there are none.</returns>
    public string GetAssistantText() => JoinText(Assistant);

    /// <summary>The concatenated text of the user's text parts, in order.</summary>
    /// <returns>The text, or an empty string when there are none.</returns>
    public string GetUserText() => JoinText(User);

    private static string JoinText(IReadOnlyList<Part> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0] is TextPart only ? only.Text : "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (Part part in parts)
        {
            if (part is TextPart text)
            {
                sb.Append(text.Text);
            }
        }

        return sb.ToString();
    }
}
