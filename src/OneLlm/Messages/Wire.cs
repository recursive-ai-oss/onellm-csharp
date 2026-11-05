using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OneLlm.Messages;

// The on-disk and on-the-wire shapes of the message model. The public types are richer than the files (one ordered part
// sequence, parts as a closed union); these classes are the files' shapes, and the converters map between the two.
// Every class keeps unknown keys in ExtensionData so nothing a reader did not understand is lost.

/// <summary>A message as written to a file: the definition format's lists, plus the order index in extension keys.</summary>
internal sealed class MessageWire
{
    public string Id { get; set; } = "";

    public List<Part>? User { get; set; }

    public List<Part>? Assistant { get; set; }

    public List<ToolCallWire>? ToolCalls { get; set; }

    public List<ReasoningWire>? Reasoning { get; set; }

    [JsonConverter(typeof(Rfc3339JsonConverter))]
    [OneYaml.YamlConverter(typeof(Rfc3339YamlConverter))]
    public DateTimeOffset? DateTime { get; set; }

    public MessageSource? Source { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cache { get; set; }

    public Usage? Usage { get; set; }

    public string? When { get; set; }

    // OneLLM keys. A reader that knows only the lists above keeps them as unknown keys.
    public List<string>? PartOrder { get; set; }

    public List<Part>? ExtraParts { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A tool call as written to a file.</summary>
internal sealed class ToolCallWire
{
    public string Id { get; set; } = "";

    public string Tool { get; set; } = "";

    public string? Routine { get; set; }

    public JsonObject? Inputs { get; set; }

    public List<Part>? Result { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal Cost { get; set; }

    public string? When { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Injected { get; set; }

    public string? InjectedBy { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }

    public List<CostLine>? CostLines { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A reasoning block as written to the <c>reasoning</c> list.</summary>
internal sealed class ReasoningWire
{
    public string Provider { get; set; } = "";

    public string? Display { get; set; }

    public JsonObject? Data { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>
/// A content item as written to a file: one object whose keys say the kind. The kinds without a counterpart in the definition format
/// (<c>refusal</c>, <c>json</c>, <c>fileRef</c>, <c>hostedToolCall</c>, <c>providerPart</c>, <c>reasoning</c>) are flat: the key that names the
/// kind holds its main identifier, and its other fields sit beside it.
/// </summary>
internal sealed class PartWire
{
    public string? Text { get; set; }

    public List<Annotation>? Annotations { get; set; }

    public AssetRef? Asset { get; set; }

    public string? PublicUrl { get; set; }

    [JsonConverter(typeof(Rfc3339JsonConverter))]
    [OneYaml.YamlConverter(typeof(Rfc3339YamlConverter))]
    public DateTimeOffset? PublicUrlExpires { get; set; }

    public ResizedImage? ResizedImage { get; set; }

    public string? Refusal { get; set; }

    public JsonNode? Json { get; set; }

    /// <summary>The provider's file id.</summary>
    public string? FileRef { get; set; }

    /// <summary>The tool name.</summary>
    public string? HostedToolCall { get; set; }

    /// <summary>The provider that produced the raw block.</summary>
    public string? ProviderPart { get; set; }

    /// <summary>The provider that owns the reasoning block.</summary>
    public string? Reasoning { get; set; }

    // Fields shared by the flat kinds.
    public string? Provider { get; set; }

    public string? Id { get; set; }

    public string? Status { get; set; }

    public JsonObject? Inputs { get; set; }

    public JsonNode? Result { get; set; }

    public decimal? Cost { get; set; }

    public JsonObject? Data { get; set; }

    public string? ContentType { get; set; }

    public string? Name { get; set; }

    public string? Uri { get; set; }

    public string? Type { get; set; }

    public JsonNode? Raw { get; set; }

    public string? Display { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
