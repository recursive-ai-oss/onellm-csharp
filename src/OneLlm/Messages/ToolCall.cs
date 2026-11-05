using System.Text.Json.Nodes;

namespace OneLlm.Messages;

/// <summary>A call to a client-side tool, with its result once the tool has run.</summary>
public sealed class ToolCall
{
    /// <summary>Creates a tool call.</summary>
    /// <param name="id">The call id the provider gave. Results are matched to calls by it.</param>
    /// <param name="tool">The tool's name.</param>
    public ToolCall(string id, string tool)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }

    /// <summary>The call id.</summary>
    public string Id { get; }

    /// <summary>The tool's name.</summary>
    public string Tool { get; }

    /// <summary>The routine URI the tool resolves to, for tools defined by one.</summary>
    public string? Routine { get; init; }

    /// <summary>The arguments, as a JSON object in the order the model wrote them. Null when the call has none.</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>
    /// The arguments text exactly as received, set only when it was not a valid JSON object so <see cref="Inputs"/> could not be built.
    /// Not written to disk.
    /// </summary>
    public string? InvalidInputs { get; init; }

    /// <summary>What the tool returned. Empty until the tool has run.</summary>
    public IReadOnlyList<Part> Result { get; init; } = [];

    /// <summary>True when the tool failed and <see cref="Result"/> describes the error.</summary>
    public bool IsError { get; init; }

    /// <summary>The cost of running the tool in USD, for costed tools. Unrounded.</summary>
    public decimal Cost { get; init; }

    /// <summary>The cost broken into line items. Null when the tool was not costed.</summary>
    public IReadOnlyList<CostLine>? CostLines { get; init; }

    /// <summary>A template deciding whether the call runs; meaningful only in initial prompts. Empty or absent means always.</summary>
    public string? When { get; init; }

    /// <summary>True when the call was put into the history by the runtime or another agent instead of being emitted by the model.</summary>
    public bool Injected { get; init; }

    /// <summary>For injected calls: who injected it (by default the calling instance's id).</summary>
    public string? InjectedBy { get; init; }

    /// <summary>
    /// How many entries of <see cref="Message.Assistant"/> precede this call in the turn. The default, <see cref="int.MaxValue"/>, puts the call after
    /// all of them. Calls with the same position keep their order in <see cref="Message.ToolCalls"/>. Written to disk as part of the message's order index.
    /// </summary>
    public int After { get; init; } = int.MaxValue;

    /// <summary>Fields that were present when the call was read and that OneLLM does not model.</summary>
    public Dictionary<string, object?>? ExtensionData { get; init; }
}
