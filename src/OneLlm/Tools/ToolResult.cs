using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Tools;

/// <summary>What a tool returns to the loop: content for the model, the cost of the call and how the loop should treat it.</summary>
public sealed class ToolResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="parts">The content the model sees, in order. Text, JSON and asset parts are the usual ones.</param>
    public ToolResult(IReadOnlyList<Part> parts) => Parts = parts ?? throw new ArgumentNullException(nameof(parts));

    /// <summary>The content the model sees.</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>The cost of the call as line items. Lines added through <see cref="ToolContext.AddCost"/> are merged in by <see cref="ToolInvoker"/>.</summary>
    public IReadOnlyList<CostLine> CostLines { get; init; } = [];

    /// <summary>True when the call failed and <see cref="Parts"/> says why. The model sees it and can try again.</summary>
    public bool IsError { get; init; }

    /// <summary>True when this result ends the agent loop.</summary>
    public bool EndsLoop { get; init; }

    /// <summary>The loop's typed output when <see cref="EndsLoop"/> is true: a JSON value matching the tool's output schema. Null when there is none.</summary>
    public JsonNode? TypedResult { get; init; }

    /// <summary>A successful result with no content.</summary>
    public static ToolResult Empty => new([]);

    /// <summary>A successful result with one text part.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The result.</returns>
    public static ToolResult FromText(string text) => new([new TextPart(text)]);

    /// <summary>A successful result with one JSON part.</summary>
    /// <param name="value">The value. A null value becomes the text <c>null</c>, because a JSON part cannot hold JSON null.</param>
    /// <returns>The result.</returns>
    public static ToolResult FromJson(JsonNode? value) => value is null ? FromText("null") : new([new JsonPart(value)]);

    /// <summary>A failed result with one text part describing the failure.</summary>
    /// <param name="message">What went wrong, written for the model. Must not contain a secret.</param>
    /// <returns>The result, with <see cref="IsError"/> set.</returns>
    public static ToolResult Error(string message) => new([new TextPart(message)]) { IsError = true };
}
