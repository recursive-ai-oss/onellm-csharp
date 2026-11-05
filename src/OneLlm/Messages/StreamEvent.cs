using System.Text;
using System.Text.Json.Nodes;

namespace OneLlm.Messages;

/// <summary>Why a model stopped producing output.</summary>
public enum FinishReason
{
    /// <summary>The provider did not say, or the stream ended without a finish marker.</summary>
    Unknown = 0,

    /// <summary>The model finished its turn or hit a stop sequence.</summary>
    Stop = 1,

    /// <summary>The output hit the token limit.</summary>
    Length = 2,

    /// <summary>The model stopped to call client-side tools.</summary>
    ToolCalls = 3,

    /// <summary>The provider's content filter ended the output.</summary>
    ContentFilter = 4,

    /// <summary>The model refused.</summary>
    Refusal = 5,

    /// <summary>The provider paused a long turn (for example while its own tools run) and the turn can be continued.</summary>
    PauseTurn = 6,

    /// <summary>A reason that has no neutral name; the provider's word is in <see cref="StreamEvent.Name"/>.</summary>
    Other = 7,
}

/// <summary>The kinds of <see cref="StreamEvent"/>.</summary>
public enum StreamEventKind
{
    /// <summary>The response started. <see cref="StreamEvent.Id"/> is the response id and <see cref="StreamEvent.Name"/> the model, when known.</summary>
    MessageStart = 0,

    /// <summary>More text for the block <see cref="StreamEvent.Index"/>, in <see cref="StreamEvent.Utf8"/>.</summary>
    TextDelta = 1,

    /// <summary>More reasoning text for the block <see cref="StreamEvent.Index"/>, in <see cref="StreamEvent.Utf8"/>.</summary>
    ReasoningDelta = 2,

    /// <summary>A reasoning block is complete. <see cref="StreamEvent.Name"/> is its provider and <see cref="StreamEvent.Data"/> the replay payload.</summary>
    ReasoningEnd = 3,

    /// <summary>More refusal text for the block <see cref="StreamEvent.Index"/>, in <see cref="StreamEvent.Utf8"/>.</summary>
    RefusalDelta = 4,

    /// <summary>A client-side tool call began: <see cref="StreamEvent.Id"/> is the call id and <see cref="StreamEvent.Name"/> the tool.</summary>
    ToolCallStart = 5,

    /// <summary>More of the tool call's JSON arguments, in <see cref="StreamEvent.Utf8"/>. Fragments need not be valid JSON on their own.</summary>
    ToolCallArgumentsDelta = 6,

    /// <summary>The tool call's arguments are complete.</summary>
    ToolCallEnd = 7,

    /// <summary>A hosted tool call began. <see cref="StreamEvent.Part"/> is a <see cref="HostedToolCallPart"/> holding what is known so far.</summary>
    HostedToolCallStart = 8,

    /// <summary>More of the hosted tool call's JSON inputs, in <see cref="StreamEvent.Utf8"/>.</summary>
    HostedToolCallArgumentsDelta = 9,

    /// <summary>The hosted tool call is complete. <see cref="StreamEvent.Part"/> is the finished <see cref="HostedToolCallPart"/> with result and status.</summary>
    HostedToolCallEnd = 10,

    /// <summary>A citation for the text block <see cref="StreamEvent.Index"/>: <see cref="StreamEvent.Annotation"/>.</summary>
    Annotation = 11,

    /// <summary>A whole part that does not stream (an asset, a file reference, provider content): <see cref="StreamEvent.Part"/>.</summary>
    PartAdded = 12,

    /// <summary>A usage report: <see cref="StreamEvent.Usage"/>. Several may arrive; later non-zero counters win.</summary>
    Usage = 13,

    /// <summary>The response is complete. <see cref="StreamEvent.Finish"/> says why; <see cref="StreamEvent.Name"/> holds the provider's own word.</summary>
    Done = 14,

    /// <summary>
    /// Provider fields that belong to the whole message and have no neutral name (a reusable container, the stop sequence that ended the turn):
    /// <see cref="StreamEvent.Name"/> is the key they are kept under in <see cref="Message.ExtensionData"/> and
    /// <see cref="StreamEvent.Extension"/> the JSON object.
    /// </summary>
    MessageExtension = 15,

    /// <summary>A notice about the request, such as a parameter removed because the model does not take it: <see cref="StreamEvent.Notice"/>. Carries no content; it can arrive at any point, usually first.</summary>
    Warning = 16,
}

/// <summary>
/// One event of a streamed response. Provider codecs emit them as they parse server-sent events; <see cref="MessageAccumulator"/> builds the
/// same <see cref="Message"/> a non-streaming call would.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Utf8"/> points into the codec's read buffer so deltas need no string. It is valid only until the consumer asks for the next event:
/// use it (append it to a buffer, decode it with <see cref="GetText"/>) before then, and copy it to keep it.
/// </para>
/// <para>
/// <see cref="Index"/> identifies a block within one response and is unique across kinds (a text block and a tool call never share an index).
/// Codecs for providers that number blocks (Anthropic content blocks, OpenAI output items) pass their number; others count.
/// </para>
/// </remarks>
public readonly struct StreamEvent
{
    private readonly object? _payload;

    private StreamEvent(StreamEventKind kind, int index, ReadOnlyMemory<byte> utf8, string? id, string? name, object? payload, FinishReason finish)
    {
        Kind = kind;
        Index = index;
        Utf8 = utf8;
        Id = id;
        Name = name;
        _payload = payload;
        Finish = finish;
    }

    /// <summary>What happened.</summary>
    public StreamEventKind Kind { get; }

    /// <summary>The block this event belongs to. See the type remarks.</summary>
    public int Index { get; }

    /// <summary>The delta as UTF-8 text, for the delta kinds. Valid only until the next event is requested.</summary>
    public ReadOnlyMemory<byte> Utf8 { get; }

    /// <summary>The response id (<see cref="StreamEventKind.MessageStart"/>) or the tool call id (<see cref="StreamEventKind.ToolCallStart"/>).</summary>
    public string? Id { get; }

    /// <summary>The model, tool, or provider name, or the provider's finish word; see each kind.</summary>
    public string? Name { get; }

    /// <summary>Why the response stopped, on <see cref="StreamEventKind.Done"/>.</summary>
    public FinishReason Finish { get; }

    /// <summary>The part carried by <see cref="StreamEventKind.PartAdded"/> and the hosted tool call events.</summary>
    public Part? Part => _payload as Part;

    /// <summary>The citation carried by <see cref="StreamEventKind.Annotation"/>.</summary>
    public Annotation? Annotation => _payload as Annotation;

    /// <summary>The usage carried by <see cref="StreamEventKind.Usage"/>.</summary>
    public Usage? Usage => _payload as Usage;

    /// <summary>The notice of a <see cref="StreamEventKind.Warning"/> event.</summary>
    public Providers.ResponseWarning? Notice => _payload as Providers.ResponseWarning;

    /// <summary>The replay payload carried by <see cref="StreamEventKind.ReasoningEnd"/>.</summary>
    public JsonObject? Data => _payload as JsonObject;

    /// <summary>
    /// A JSON object of provider fields with no neutral name, on <see cref="StreamEventKind.ToolCallStart"/> (kept in the call's
    /// <see cref="ToolCall.ExtensionData"/>) and <see cref="StreamEventKind.MessageExtension"/>. Empty on other kinds and when there is none.
    /// Valid only until the next event is requested.
    /// </summary>
    public ReadOnlyMemory<byte> Extension => Kind is StreamEventKind.ToolCallStart or StreamEventKind.MessageExtension ? Utf8 : default;

    /// <summary>Decodes <see cref="Utf8"/> to a string. This allocates; consumers that only append can use <see cref="Utf8"/> directly.</summary>
    /// <returns>The delta text.</returns>
    public string GetText() => Encoding.UTF8.GetString(Utf8.Span);

    /// <summary>Creates a <see cref="StreamEventKind.MessageStart"/> event.</summary>
    /// <param name="responseId">The provider's response id.</param>
    /// <param name="model">The model that is answering.</param>
    /// <returns>The event.</returns>
    public static StreamEvent MessageStart(string? responseId, string? model) => new(StreamEventKind.MessageStart, 0, default, responseId, model, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.TextDelta"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="utf8">The text, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent TextDelta(int index, ReadOnlyMemory<byte> utf8) => new(StreamEventKind.TextDelta, index, utf8, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ReasoningDelta"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="utf8">The reasoning text, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ReasoningDelta(int index, ReadOnlyMemory<byte> utf8) => new(StreamEventKind.ReasoningDelta, index, utf8, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ReasoningEnd"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="provider">The provider that owns the block.</param>
    /// <param name="data">The payload needed to replay the block, or null.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ReasoningEnd(int index, string provider, JsonObject? data) => new(StreamEventKind.ReasoningEnd, index, default, null, provider, data, default);

    /// <summary>Creates a <see cref="StreamEventKind.RefusalDelta"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="utf8">The refusal text, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent RefusalDelta(int index, ReadOnlyMemory<byte> utf8) => new(StreamEventKind.RefusalDelta, index, utf8, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ToolCallStart"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="id">The call id.</param>
    /// <param name="tool">The tool's name.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ToolCallStart(int index, string id, string tool) => new(StreamEventKind.ToolCallStart, index, default, id, tool, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ToolCallStart"/> event that also carries provider fields.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="id">The call id.</param>
    /// <param name="tool">The tool's name.</param>
    /// <param name="extensionJson">A JSON object of the block's other fields, as UTF-8; they end up in <see cref="ToolCall.ExtensionData"/>.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ToolCallStart(int index, string id, string tool, ReadOnlyMemory<byte> extensionJson) => new(StreamEventKind.ToolCallStart, index, extensionJson, id, tool, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.MessageExtension"/> event.</summary>
    /// <param name="key">The key to keep the object under in the message's extension data (the provider's name).</param>
    /// <param name="extensionJson">A JSON object, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent MessageExtension(string key, ReadOnlyMemory<byte> extensionJson) => new(StreamEventKind.MessageExtension, 0, extensionJson, null, key, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ToolCallArgumentsDelta"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="utf8">A fragment of the arguments, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ToolCallArgumentsDelta(int index, ReadOnlyMemory<byte> utf8) => new(StreamEventKind.ToolCallArgumentsDelta, index, utf8, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.ToolCallEnd"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <returns>The event.</returns>
    public static StreamEvent ToolCallEnd(int index) => new(StreamEventKind.ToolCallEnd, index, default, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.HostedToolCallStart"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="started">What is known when the call starts: at least the tool name.</param>
    /// <returns>The event.</returns>
    public static StreamEvent HostedToolCallStart(int index, HostedToolCallPart started) => new(StreamEventKind.HostedToolCallStart, index, default, null, null, started, default);

    /// <summary>Creates a <see cref="StreamEventKind.HostedToolCallArgumentsDelta"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="utf8">A fragment of the inputs, as UTF-8.</param>
    /// <returns>The event.</returns>
    public static StreamEvent HostedToolCallArgumentsDelta(int index, ReadOnlyMemory<byte> utf8) => new(StreamEventKind.HostedToolCallArgumentsDelta, index, utf8, null, null, null, default);

    /// <summary>Creates a <see cref="StreamEventKind.HostedToolCallEnd"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="finished">The finished call.</param>
    /// <returns>The event.</returns>
    public static StreamEvent HostedToolCallEnd(int index, HostedToolCallPart finished) => new(StreamEventKind.HostedToolCallEnd, index, default, null, null, finished, default);

    /// <summary>Creates a <see cref="StreamEventKind.Annotation"/> event.</summary>
    /// <param name="index">The index of the text block the citation belongs to.</param>
    /// <param name="annotation">The citation.</param>
    /// <returns>The event.</returns>
    public static StreamEvent AnnotationAdded(int index, Annotation annotation) => new(StreamEventKind.Annotation, index, default, null, null, annotation, default);

    /// <summary>Creates a <see cref="StreamEventKind.PartAdded"/> event.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="part">The whole part.</param>
    /// <returns>The event.</returns>
    public static StreamEvent PartAdded(int index, Part part) => new(StreamEventKind.PartAdded, index, default, null, null, part, default);

    /// <summary>Creates a <see cref="StreamEventKind.Warning"/> event.</summary>
    /// <param name="warning">The notice.</param>
    /// <returns>The event.</returns>
    public static StreamEvent Warning(Providers.ResponseWarning warning) => new(StreamEventKind.Warning, 0, default, null, null, warning ?? throw new ArgumentNullException(nameof(warning)), default);

    /// <summary>Creates a <see cref="StreamEventKind.Usage"/> event.</summary>
    /// <param name="usage">The usage reported so far.</param>
    /// <returns>The event.</returns>
    public static StreamEvent UsageReported(Usage usage) => new(StreamEventKind.Usage, 0, default, null, null, usage, default);

    /// <summary>Creates a <see cref="StreamEventKind.Done"/> event.</summary>
    /// <param name="finish">Why the response stopped.</param>
    /// <param name="providerWord">The provider's own word for it, for example <c>end_turn</c>.</param>
    /// <returns>The event.</returns>
    public static StreamEvent Done(FinishReason finish, string? providerWord = null) => new(StreamEventKind.Done, 0, default, null, providerWord, null, finish);
}
