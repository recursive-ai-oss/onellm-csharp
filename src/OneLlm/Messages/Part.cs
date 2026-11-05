using System.Text.Json.Nodes;

namespace OneLlm.Messages;

/// <summary>Which kind of content a <see cref="Part"/> holds.</summary>
public enum PartKind
{
    /// <summary>Plain text, optionally with citations: <see cref="TextPart"/>.</summary>
    Text = 0,

    /// <summary>A stored file such as an image, audio clip or document: <see cref="AssetPart"/>.</summary>
    Asset = 1,

    /// <summary>Provider reasoning or thinking kept for replay: <see cref="ReasoningPart"/>.</summary>
    Reasoning = 2,

    /// <summary>A tool the provider ran itself, with its result: <see cref="HostedToolCallPart"/>.</summary>
    HostedToolCall = 3,

    /// <summary>The model declined to answer: <see cref="RefusalPart"/>.</summary>
    Refusal = 4,

    /// <summary>A structured value: <see cref="JsonPart"/>.</summary>
    Json = 5,

    /// <summary>A reference to a file already uploaded to a provider: <see cref="FileRefPart"/>.</summary>
    FileRef = 6,

    /// <summary>Provider-specific content kept as raw JSON: <see cref="ProviderPart"/>.</summary>
    Provider = 7,
}

/// <summary>
/// One piece of content in a message. The set of kinds is closed: the constructor is not accessible outside this assembly,
/// so a <c>switch</c> over <see cref="Kind"/> covers every case.
/// </summary>
/// <remarks>
/// Unknown fields found on a part when it was read are kept in <see cref="ExtensionData"/> and written back unchanged.
/// Parts are serialized as one object whose keys say the kind (<c>text</c>, <c>asset</c>, <c>refusal</c>, ...), see the message format spec.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(PartJsonConverter))]
[OneYaml.YamlConverter(typeof(PartYamlConverter))]
public abstract class Part
{
    private protected Part()
    {
    }

    /// <summary>The kind of content this part holds.</summary>
    public abstract PartKind Kind { get; }

    /// <summary>
    /// Fields that were present when the part was read and that OneLLM does not model. Values are the untyped shapes of the reader
    /// that produced them: a string, number, boolean, <c>null</c>, list or dictionary. Null when there are none.
    /// </summary>
    public Dictionary<string, object?>? ExtensionData { get; init; }
}

/// <summary>Text the model wrote or the user sent.</summary>
public sealed class TextPart : Part
{
    /// <summary>Creates a text part.</summary>
    /// <param name="text">The text. An empty string is allowed and is written as an empty object.</param>
    public TextPart(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Text;

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>Citations and other markers over ranges of <see cref="Text"/>, in the order the provider gave them. Null when there are none.</summary>
    public IReadOnlyList<Annotation>? Annotations { get; init; }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>A stored file: image, audio, video or document.</summary>
public sealed class AssetPart : Part
{
    /// <summary>Creates an asset part.</summary>
    /// <param name="asset">The stored file, or null when the part carries only legacy inline fields kept in <see cref="Part.ExtensionData"/>.</param>
    public AssetPart(AssetRef? asset) => Asset = asset;

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Asset;

    /// <summary>The stored file.</summary>
    public AssetRef? Asset { get; }

    /// <summary>A URL a provider can fetch the bytes from, when one was made for this asset.</summary>
    public string? PublicUrl { get; init; }

    /// <summary>When <see cref="PublicUrl"/> stops being trustworthy.</summary>
    public DateTimeOffset? PublicUrlExpires { get; init; }

    /// <summary>A downscaled copy made for an image-size limit, sent to providers instead of the original.</summary>
    public ResizedImage? ResizedImage { get; init; }
}

/// <summary>Provider reasoning (thinking) kept so the next request can replay it.</summary>
public sealed class ReasoningPart : Part
{
    /// <summary>Creates a reasoning part.</summary>
    /// <param name="provider">The provider that owns the block, for example <c>anthropic</c>. Only that provider replays it.</param>
    public ReasoningPart(string provider) => Provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Reasoning;

    /// <summary>The provider that owns the block.</summary>
    public string Provider { get; }

    /// <summary>Reasoning text or summary meant for people to read. Never shown as assistant text.</summary>
    public string? Display { get; init; }

    /// <summary>An opaque provider payload (signatures, item ids, raw blocks) used for replay. OneLLM stores and forwards it without interpreting it.</summary>
    public JsonObject? Data { get; init; }
}

/// <summary>A tool the provider ran on its side, such as web search, with what it returned.</summary>
public sealed class HostedToolCallPart : Part
{
    /// <summary>Creates a hosted tool call.</summary>
    /// <param name="tool">The tool's name in the provider's terms, for example <c>web_search</c>.</param>
    public HostedToolCallPart(string tool) => Tool = tool ?? throw new ArgumentNullException(nameof(tool));

    /// <inheritdoc />
    public override PartKind Kind => PartKind.HostedToolCall;

    /// <summary>The tool's name.</summary>
    public string Tool { get; }

    /// <summary>The provider that ran it.</summary>
    public string? Provider { get; init; }

    /// <summary>The provider's id for the call.</summary>
    public string? Id { get; init; }

    /// <summary>How the call ended in the provider's words, for example <c>completed</c>.</summary>
    public string? Status { get; init; }

    /// <summary>What the model asked the tool to do.</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>What the tool returned, as the provider gave it.</summary>
    public System.Text.Json.Nodes.JsonNode? Result { get; init; }

    /// <summary>The price of this call in USD when the provider charges per call, otherwise null.</summary>
    public decimal? Cost { get; init; }

    /// <summary>An opaque provider payload used for replay.</summary>
    public JsonObject? Data { get; init; }
}

/// <summary>The model declined to answer.</summary>
public sealed class RefusalPart : Part
{
    /// <summary>Creates a refusal.</summary>
    /// <param name="text">The explanation the model gave.</param>
    public RefusalPart(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Refusal;

    /// <summary>The explanation the model gave.</summary>
    public string Text { get; }
}

/// <summary>A structured value, such as the parsed answer to a structured-output request.</summary>
public sealed class JsonPart : Part
{
    /// <summary>Creates a JSON part.</summary>
    /// <param name="value">The value. A JSON <c>null</c> is not representable and reads back as no part.</param>
    public JsonPart(JsonNode value) => Value = value ?? throw new ArgumentNullException(nameof(value));

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Json;

    /// <summary>The value.</summary>
    public JsonNode Value { get; }
}

/// <summary>A reference to a file already uploaded to a provider's file store.</summary>
public sealed class FileRefPart : Part
{
    /// <summary>Creates a file reference.</summary>
    /// <param name="provider">The provider that holds the file.</param>
    /// <param name="fileId">The provider's id for the file.</param>
    public FileRefPart(string provider, string fileId)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        FileId = fileId ?? throw new ArgumentNullException(nameof(fileId));
    }

    /// <inheritdoc />
    public override PartKind Kind => PartKind.FileRef;

    /// <summary>The provider that holds the file.</summary>
    public string Provider { get; }

    /// <summary>The provider's id for the file.</summary>
    public string FileId { get; }

    /// <summary>The file's media type, when known.</summary>
    public string? ContentType { get; init; }

    /// <summary>The file's name, when known.</summary>
    public string? Name { get; init; }

    /// <summary>A URI the provider uses for the file, when it is not just the id (for example a <c>gs://</c> URI).</summary>
    public string? Uri { get; init; }
}

/// <summary>Content only one provider understands, kept as raw JSON so it can be sent back unchanged.</summary>
public sealed class ProviderPart : Part
{
    /// <summary>Creates a provider part.</summary>
    /// <param name="provider">The provider that produced it.</param>
    /// <param name="raw">The provider's JSON for the block, exactly as received.</param>
    public ProviderPart(string provider, JsonNode raw)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
    }

    /// <inheritdoc />
    public override PartKind Kind => PartKind.Provider;

    /// <summary>The provider that produced it.</summary>
    public string Provider { get; }

    /// <summary>The provider's own name for the block type, when it has one.</summary>
    public string? Type { get; init; }

    /// <summary>The provider's JSON for the block.</summary>
    public JsonNode Raw { get; }
}
