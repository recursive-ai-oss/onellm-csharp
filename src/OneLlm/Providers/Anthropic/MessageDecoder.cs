using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// Turns Anthropic response data into <see cref="StreamEvent"/>s, from server-sent events or from a whole message (API reference: Create a
/// message, Streaming messages). Both inputs go through the same block handlers, so a streamed answer and a whole answer build the same
/// <see cref="Message"/> in a <see cref="MessageAccumulator"/>.
/// </summary>
/// <remarks>
/// Content blocks become parts like this: <c>text</c> to text with its citations as annotations; <c>thinking</c> and
/// <c>redacted_thinking</c> to reasoning parts whose data holds the block's signature or opaque payload for replay; <c>tool_use</c> to a tool
/// call; <c>server_tool_use</c> together with its later <c>*_tool_result</c> block to one hosted tool call; any other block to a provider part
/// holding the block as received. Unknown fields on tool-use blocks are kept in <see cref="ToolCallExtras"/>; unknown fields on text blocks
/// are not kept, because a streamed text block cannot carry them to the message.
/// </remarks>
internal sealed class MessageDecoder
{
    // Index for the refusal part: after every content block the API numbers.
    private const int RefusalIndex = 1_000_000;

    private enum BlockKind
    {
        Text,
        Thinking,
        Redacted,
        ToolUse,
        Hosted,
        ResultConsumed,
        Raw,
    }

    private enum EventType
    {
        Unknown,
        MessageStart,
        ContentBlockStart,
        ContentBlockDelta,
        ContentBlockStop,
        MessageDelta,
        MessageStop,
        Ping,
        Error,
    }

    private enum DeltaType
    {
        Unknown,
        Text,
        InputJson,
        Thinking,
        Signature,
        Citations,
    }

    private sealed class Block(BlockKind kind, int index)
    {
        public BlockKind Kind { get; } = kind;

        public int Index { get; } = index;

        public string? Id { get; set; }

        public JsonObject? Start { get; set; }

        public string? Signature { get; set; }

        public JsonObject? ThinkingExtra { get; set; }

        public ArrayBufferWriter<byte>? Args { get; set; }

        public HostedToolCallPart? Skeleton { get; set; }

        public string CallType { get; set; } = "server_tool_use";

        public JsonObject? CallExtra { get; set; }
    }

    private readonly Dictionary<int, Block> _blocks = [];
    private readonly Dictionary<string, Block> _hostedById = new(StringComparer.Ordinal);
    private readonly Func<string, string> _scrub;
    private byte[] _scratch = new byte[4096];
    private FinishReason _finish;
    private string? _finishRaw;
    private bool _refusalAdded;

    public MessageDecoder(Func<string, string> scrub) => _scrub = scrub;

    /// <summary>The events produced since the caller last cleared the list.</summary>
    public List<StreamEvent> Events { get; } = [];

    /// <summary>Fields of tool-use blocks beyond the modelled ones, by tool call id.</summary>
    public Dictionary<string, Dictionary<string, object?>> ToolCallExtras { get; } = new(StringComparer.Ordinal);

    /// <summary>Message-level fields worth keeping (the container for reuse, the stop sequence that ended the turn), or null.</summary>
    public Dictionary<string, object?>? MessageExtras { get; private set; }

    /// <summary>True once <c>message_stop</c> was seen, or a whole message was decoded.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The request id found in an error event, for the error that is thrown.</summary>
    public string? RequestId { get; set; }

    // ---- a whole message ----

    /// <summary>Decodes a complete message object, as returned by a non-streaming call or inside a batch result.</summary>
    public void DecodeMessage(JsonObject message)
    {
        string? type = AnthropicJson.GetString(message, "type");
        if (type == "error")
        {
            throw ErrorFrom(message);
        }

        OnMessageStart(message);
        if (message["content"] is JsonArray content)
        {
            for (int i = 0; i < content.Count; i++)
            {
                if (content[i] is JsonObject block)
                {
                    OnBlockStart(i, block, complete: true);
                    OnBlockStop(i);
                }
            }
        }

        ApplyStop(message);
        Stop();
    }

    // ---- server-sent events ----

    /// <summary>Decodes the JSON data of one server-sent event.</summary>
    /// <param name="json">The event data. For delta events the produced <see cref="StreamEvent.Utf8"/> points into a buffer of this decoder, valid until the next call.</param>
    public void DecodeEvent(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new MalformedResponseException(AnthropicErrors.Provider, "a stream event is not a JSON object.");
        }

        EventType type = EventType.Unknown;
        int index = -1;
        Utf8JsonReader delta = default;
        bool hasDelta = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                type = ParseEventType(ref reader);
            }
            else if (reader.ValueTextEquals("index"u8))
            {
                reader.Read();
                reader.TryGetInt32(out index);
            }
            else if (reader.ValueTextEquals("delta"u8))
            {
                reader.Read();
                delta = reader;
                hasDelta = true;
                reader.Skip();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        switch (type)
        {
            case EventType.ContentBlockDelta:
                if (hasDelta && delta.TokenType == JsonTokenType.StartObject)
                {
                    OnDelta(index, delta);
                }

                break;
            case EventType.ContentBlockStart:
            case EventType.ContentBlockStop:
            case EventType.MessageStart:
            case EventType.MessageDelta:
            case EventType.Error:
                OnTreeEvent(type, index, json);
                break;
            case EventType.MessageStop:
                Stop();
                break;
            default:
                // ping, and event types added later: the versioning policy says to ignore what is not known.
                break;
        }
    }

    private static EventType ParseEventType(ref Utf8JsonReader r)
    {
        if (r.ValueTextEquals("content_block_delta"u8))
        {
            return EventType.ContentBlockDelta;
        }

        if (r.ValueTextEquals("content_block_start"u8))
        {
            return EventType.ContentBlockStart;
        }

        if (r.ValueTextEquals("content_block_stop"u8))
        {
            return EventType.ContentBlockStop;
        }

        if (r.ValueTextEquals("message_delta"u8))
        {
            return EventType.MessageDelta;
        }

        if (r.ValueTextEquals("message_start"u8))
        {
            return EventType.MessageStart;
        }

        if (r.ValueTextEquals("message_stop"u8))
        {
            return EventType.MessageStop;
        }

        if (r.ValueTextEquals("ping"u8))
        {
            return EventType.Ping;
        }

        return r.ValueTextEquals("error"u8) ? EventType.Error : EventType.Unknown;
    }

    private void OnTreeEvent(EventType type, int index, ReadOnlySpan<byte> json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new MalformedResponseException(AnthropicErrors.Provider, "a stream event is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new MalformedResponseException(AnthropicErrors.Provider, "a stream event is not valid JSON: " + _scrub(e.Message));
        }

        switch (type)
        {
            case EventType.MessageStart:
                if (root["message"] is JsonObject message)
                {
                    OnMessageStart(message);
                }

                break;
            case EventType.ContentBlockStart:
                if (root["content_block"] is JsonObject block)
                {
                    OnBlockStart(index, block, complete: false);
                }

                break;
            case EventType.ContentBlockStop:
                OnBlockStop(index);
                break;
            case EventType.MessageDelta:
                if (root["delta"] is JsonObject delta)
                {
                    ApplyStop(delta);
                }

                if (root["usage"] is JsonObject usage)
                {
                    Events.Add(StreamEvent.UsageReported(ParseUsage(usage)));
                }

                break;
            case EventType.Error:
                throw ErrorFrom(root);
        }
    }

    private ProviderException ErrorFrom(JsonObject root)
    {
        string? requestId = AnthropicJson.GetString(root, "request_id") ?? RequestId;
        return root["error"] is JsonObject error
            ? AnthropicErrors.FromErrorObject(error, requestId, _scrub)
            : new MalformedResponseException(AnthropicErrors.Provider, "the stream reported an error without details.");
    }

    // ---- message level ----

    private void OnMessageStart(JsonObject message)
    {
        Events.Add(StreamEvent.MessageStart(AnthropicJson.GetString(message, "id"), AnthropicJson.GetString(message, "model")));
        if (message["usage"] is JsonObject usage)
        {
            Events.Add(StreamEvent.UsageReported(ParseUsage(usage)));
        }

        Extra("container", message["container"]);
    }

    // stop_reason, stop_sequence, stop_details and container arrive in message_delta when streaming and on the message itself otherwise.
    private void ApplyStop(JsonObject o)
    {
        if (AnthropicJson.GetString(o, "stop_reason") is { } reason)
        {
            _finishRaw = reason;
            _finish = reason switch
            {
                "end_turn" or "stop_sequence" => FinishReason.Stop,
                "max_tokens" or "model_context_window_exceeded" => FinishReason.Length,
                "tool_use" => FinishReason.ToolCalls,
                "pause_turn" => FinishReason.PauseTurn,
                "refusal" => FinishReason.Refusal,
                _ => FinishReason.Other,
            };
        }

        Extra("stop_sequence", o["stop_sequence"]);
        Extra("container", o["container"]);
        if (!_refusalAdded && o["stop_details"] is JsonObject details)
        {
            _refusalAdded = true;
            string? category = AnthropicJson.GetString(details, "category");
            string explanation = AnthropicJson.GetString(details, "explanation") ?? (category is null ? "The model declined to answer." : $"The model declined to answer (category: {category}).");
            var part = new RefusalPart(explanation)
            {
                ExtensionData = category is null ? null : new Dictionary<string, object?> { ["category"] = category },
            };
            Events.Add(StreamEvent.PartAdded(RefusalIndex, part));
        }
    }

    private void Extra(string key, JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        (MessageExtras ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = AnthropicJson.ToNatural(value);
    }

    private void Stop()
    {
        Stopped = true;
        Events.Add(StreamEvent.Done(_finish, _finishRaw));
    }

    // ---- usage ----

    /// <summary>Reads a usage object (API reference: Usage). Counters beyond the four token counts are named as the catalog's price items expect.</summary>
    public static Usage ParseUsage(JsonObject u)
    {
        // Anthropic's input_tokens already excludes cache reads and writes, which matches Usage.InputTokens.
        long cacheCreation = AnthropicJson.GetLong(u, "cache_creation_input_tokens");
        Dictionary<string, long>? counters = null;

        // The part of the cache writes that used the 1-hour lifetime is priced differently; cache_creation_input_tokens is the total.
        if (u["cache_creation"] is JsonObject breakdown && AnthropicJson.GetLong(breakdown, "ephemeral_1h_input_tokens") is > 0 and long oneHour)
        {
            (counters ??= new Dictionary<string, long>())["cacheCreation1hInputTokens"] = oneHour;
        }

        if (u["output_tokens_details"] is JsonObject details && AnthropicJson.GetLong(details, "thinking_tokens") is > 0 and long thinking)
        {
            (counters ??= new Dictionary<string, long>())["reasoningTokens"] = thinking;
        }

        if (u["server_tool_use"] is JsonObject tools)
        {
            foreach (KeyValuePair<string, JsonNode?> p in tools)
            {
                const string suffix = "_requests";
                if (p.Key.EndsWith(suffix, StringComparison.Ordinal) && AnthropicJson.GetLong(tools, p.Key) is > 0 and long count)
                {
                    (counters ??= new Dictionary<string, long>())["toolCalls." + p.Key[..^suffix.Length]] = count;
                }
            }
        }

        string? tier = AnthropicJson.GetString(u, "service_tier");
        string? speed = AnthropicJson.GetString(u, "speed");
        string? serviceTier = speed == "fast" ? "fast" : tier is null or "standard" ? null : tier;

        Dictionary<string, object?>? rest = null;
        foreach (KeyValuePair<string, JsonNode?> p in u)
        {
            if (p.Key is "input_tokens" or "output_tokens" or "cache_creation_input_tokens" or "cache_read_input_tokens" or "cache_creation" or "output_tokens_details" or "server_tool_use" or "service_tier")
            {
                continue;
            }

            (rest ??= new Dictionary<string, object?>(StringComparer.Ordinal))[p.Key] = AnthropicJson.ToNatural(p.Value);
        }

        return new Usage
        {
            InputTokens = AnthropicJson.GetLong(u, "input_tokens"),
            OutputTokens = AnthropicJson.GetLong(u, "output_tokens"),
            CacheCreationInputTokens = cacheCreation,
            CacheReadInputTokens = AnthropicJson.GetLong(u, "cache_read_input_tokens"),
            ServiceTier = serviceTier,
            Counters = counters,
            ExtensionData = rest,
        };
    }

    // ---- blocks ----

    private void OnBlockStart(int index, JsonObject block, bool complete)
    {
        string type = AnthropicJson.GetString(block, "type") ?? "";
        switch (type)
        {
            case "text":
                _blocks[index] = new Block(BlockKind.Text, index);
                if (AnthropicJson.GetString(block, "text") is { Length: > 0 } text)
                {
                    Events.Add(StreamEvent.TextDelta(index, Encoding.UTF8.GetBytes(text)));
                }

                if (block["citations"] is JsonArray citations)
                {
                    foreach (JsonNode? c in citations)
                    {
                        if (c is JsonObject co)
                        {
                            Events.Add(StreamEvent.AnnotationAdded(index, MapCitation(co)));
                        }
                    }
                }

                break;
            case "thinking":
                var thinking = new Block(BlockKind.Thinking, index) { Signature = AnthropicJson.GetString(block, "signature") is { Length: > 0 } sig ? sig : null };
                foreach (KeyValuePair<string, JsonNode?> p in block)
                {
                    if (p.Key is not ("type" or "thinking" or "signature"))
                    {
                        (thinking.ThinkingExtra ??= new JsonObject())[p.Key] = p.Value?.DeepClone();
                    }
                }

                _blocks[index] = thinking;
                if (AnthropicJson.GetString(block, "thinking") is { Length: > 0 } thought)
                {
                    Events.Add(StreamEvent.ReasoningDelta(index, Encoding.UTF8.GetBytes(thought)));
                }

                break;
            case "redacted_thinking":
                _blocks[index] = new Block(BlockKind.Redacted, index) { Start = (JsonObject)block.DeepClone() };
                break;
            case "tool_use":
                StartToolUse(index, block, complete);
                break;
            case "server_tool_use":
            case "mcp_tool_use":
                StartHosted(index, block, type, complete);
                break;
            default:
                if (type.EndsWith("_tool_result", StringComparison.Ordinal) && AnthropicJson.GetString(block, "tool_use_id") is { } callId && _hostedById.TryGetValue(callId, out Block? call))
                {
                    FinishHosted(call, block, type);
                    _blocks[index] = new Block(BlockKind.ResultConsumed, index);
                }
                else
                {
                    // A block this version of OneLLM has no part for is kept whole and replayed as it came.
                    _blocks[index] = new Block(BlockKind.Raw, index) { Start = (JsonObject)block.DeepClone() };
                }

                break;
        }
    }

    private void StartToolUse(int index, JsonObject block, bool complete)
    {
        string id = AnthropicJson.GetString(block, "id") ?? "";
        string name = AnthropicJson.GetString(block, "name") ?? "";
        _blocks[index] = new Block(BlockKind.ToolUse, index) { Id = id };
        Events.Add(StreamEvent.ToolCallStart(index, id, name));

        Dictionary<string, object?>? extras = null;
        foreach (KeyValuePair<string, JsonNode?> p in block)
        {
            if (p.Key is not ("type" or "id" or "name" or "input"))
            {
                (extras ??= new Dictionary<string, object?>(StringComparer.Ordinal))[p.Key] = AnthropicJson.ToNatural(p.Value);
            }
        }

        if (extras is not null)
        {
            ToolCallExtras[id] = extras;
        }

        // A streamed block starts with an empty input and fills it by deltas; a whole block carries it.
        if (complete && block["input"] is JsonObject { Count: > 0 } input)
        {
            Events.Add(StreamEvent.ToolCallArgumentsDelta(index, AnthropicJson.ToUtf8(input)));
        }
    }

    private void StartHosted(int index, JsonObject block, string type, bool complete)
    {
        string id = AnthropicJson.GetString(block, "id") ?? "";
        string name = AnthropicJson.GetString(block, "name") ?? "";
        JsonObject? callExtra = null;
        foreach (KeyValuePair<string, JsonNode?> p in block)
        {
            if (p.Key is not ("type" or "id" or "name" or "input"))
            {
                (callExtra ??= new JsonObject())[p.Key] = p.Value?.DeepClone();
            }
        }

        var state = new Block(BlockKind.Hosted, index) { Id = id, CallType = type, CallExtra = callExtra };
        state.Skeleton = new HostedToolCallPart(name) { Provider = AnthropicErrors.Provider, Id = id, Data = ReplayData(state, null, null) };
        _blocks[index] = state;
        if (id.Length > 0)
        {
            _hostedById[id] = state;
        }

        Events.Add(StreamEvent.HostedToolCallStart(index, state.Skeleton));
        if (complete && block["input"] is JsonObject { Count: > 0 } input)
        {
            byte[] bytes = AnthropicJson.ToUtf8(input);
            (state.Args ??= new ArrayBufferWriter<byte>(bytes.Length)).Write(bytes);
            Events.Add(StreamEvent.HostedToolCallArgumentsDelta(index, bytes));
        }
    }

    // What replay needs to rebuild the two API blocks of a hosted call: their types and the fields beyond the modelled ones.
    private static JsonObject? ReplayData(Block call, string? resultType, JsonObject? resultExtra)
    {
        var data = new JsonObject();
        if (call.CallType != "server_tool_use")
        {
            data["callType"] = call.CallType;
        }

        if (call.CallExtra is not null)
        {
            data["callExtra"] = call.CallExtra.DeepClone();
        }

        if (resultType is not null)
        {
            data["resultType"] = resultType;
        }

        if (resultExtra is { Count: > 0 })
        {
            data["resultExtra"] = resultExtra;
        }

        return data.Count == 0 ? null : data;
    }

    private void FinishHosted(Block call, JsonObject resultBlock, string resultType)
    {
        JsonObject? inputs = null;
        if (call.Args is { WrittenCount: > 0 })
        {
            try
            {
                inputs = JsonNode.Parse(call.Args.WrittenSpan) as JsonObject;
            }
            catch (JsonException)
            {
                inputs = null;
            }
        }

        JsonObject? resultExtra = null;
        foreach (KeyValuePair<string, JsonNode?> p in resultBlock)
        {
            if (p.Key is not ("type" or "tool_use_id" or "content"))
            {
                (resultExtra ??= new JsonObject())[p.Key] = p.Value?.DeepClone();
            }
        }

        JsonNode? content = resultBlock["content"]?.DeepClone();
        // A result whose content is an error object (for example web_search_tool_result_error) is a failed call.
        bool failed = content is JsonObject co && AnthropicJson.GetString(co, "type") is { } ct && ct.EndsWith("_error", StringComparison.Ordinal);
        var finished = new HostedToolCallPart(call.Skeleton!.Tool)
        {
            Provider = AnthropicErrors.Provider,
            Id = call.Id,
            Status = failed ? "error" : "completed",
            Inputs = inputs,
            Result = content,
            Data = ReplayData(call, resultType, resultExtra),
        };
        Events.Add(StreamEvent.HostedToolCallEnd(call.Index, finished));
    }

    private void OnBlockStop(int index)
    {
        if (!_blocks.TryGetValue(index, out Block? b))
        {
            return;
        }

        switch (b.Kind)
        {
            case BlockKind.ToolUse:
                Events.Add(StreamEvent.ToolCallEnd(index));
                break;
            case BlockKind.Thinking:
                var data = new JsonObject { ["type"] = "thinking" };
                if (b.Signature is not null)
                {
                    data["signature"] = b.Signature;
                }

                if (b.ThinkingExtra is not null)
                {
                    foreach (KeyValuePair<string, JsonNode?> p in b.ThinkingExtra)
                    {
                        data[p.Key] = p.Value?.DeepClone();
                    }
                }

                Events.Add(StreamEvent.ReasoningEnd(index, AnthropicErrors.Provider, data));
                break;
            case BlockKind.Redacted:
                Events.Add(StreamEvent.ReasoningEnd(index, AnthropicErrors.Provider, b.Start));
                break;
            case BlockKind.Raw:
                Events.Add(StreamEvent.PartAdded(index, new ProviderPart(AnthropicErrors.Provider, b.Start!) { Type = AnthropicJson.GetString(b.Start!, "type") }));
                break;
        }
    }

    // ---- deltas (the hot path: no strings for text) ----

    private void OnDelta(int index, Utf8JsonReader r)
    {
        DeltaType kind = DeltaType.Unknown;
        Utf8JsonReader payload = default;
        bool hasPayload = false;
        Utf8JsonReader citation = default;
        bool hasCitation = false;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("type"u8))
            {
                r.Read();
                kind = ParseDeltaType(ref r);
            }
            else if (r.ValueTextEquals("text"u8) || r.ValueTextEquals("partial_json"u8) || r.ValueTextEquals("thinking"u8) || r.ValueTextEquals("signature"u8))
            {
                r.Read();
                payload = r;
                hasPayload = true;
            }
            else if (r.ValueTextEquals("citation"u8))
            {
                r.Read();
                citation = r;
                hasCitation = true;
                r.Skip();
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        switch (kind)
        {
            case DeltaType.Text:
                if (hasPayload && CopyString(ref payload) is { Length: > 0 } text)
                {
                    Events.Add(StreamEvent.TextDelta(index, text));
                }

                break;
            case DeltaType.Thinking:
                if (hasPayload && CopyString(ref payload) is { Length: > 0 } thought)
                {
                    Events.Add(StreamEvent.ReasoningDelta(index, thought));
                }

                break;
            case DeltaType.InputJson:
                if (hasPayload && _blocks.TryGetValue(index, out Block? target) && CopyString(ref payload) is { Length: > 0 } json)
                {
                    if (target.Kind == BlockKind.ToolUse)
                    {
                        Events.Add(StreamEvent.ToolCallArgumentsDelta(index, json));
                    }
                    else if (target.Kind == BlockKind.Hosted)
                    {
                        (target.Args ??= new ArrayBufferWriter<byte>(256)).Write(json.Span);
                        Events.Add(StreamEvent.HostedToolCallArgumentsDelta(index, json));
                    }
                }

                break;
            case DeltaType.Signature:
                if (hasPayload && payload.TokenType == JsonTokenType.String && _blocks.TryGetValue(index, out Block? thinking) && thinking.Kind == BlockKind.Thinking)
                {
                    thinking.Signature = payload.GetString();
                }

                break;
            case DeltaType.Citations:
                if (hasCitation && JsonNode.Parse(ref citation) is JsonObject c)
                {
                    Events.Add(StreamEvent.AnnotationAdded(index, MapCitation(c)));
                }

                break;
        }
    }

    private static DeltaType ParseDeltaType(ref Utf8JsonReader r)
    {
        if (r.ValueTextEquals("text_delta"u8))
        {
            return DeltaType.Text;
        }

        if (r.ValueTextEquals("input_json_delta"u8))
        {
            return DeltaType.InputJson;
        }

        if (r.ValueTextEquals("thinking_delta"u8))
        {
            return DeltaType.Thinking;
        }

        if (r.ValueTextEquals("signature_delta"u8))
        {
            return DeltaType.Signature;
        }

        return r.ValueTextEquals("citations_delta"u8) ? DeltaType.Citations : DeltaType.Unknown;
    }

    // Unescapes a JSON string into the decoder's scratch buffer. The result is valid until the next call.
    private ReadOnlyMemory<byte> CopyString(ref Utf8JsonReader payload)
    {
        if (payload.TokenType != JsonTokenType.String)
        {
            return default;
        }

        // The unescaped text is never longer than the escaped one, so the raw length is a safe size.
        int raw = payload.HasValueSequence ? checked((int)payload.ValueSequence.Length) : payload.ValueSpan.Length;
        if (_scratch.Length < raw)
        {
            _scratch = new byte[Math.Max(raw, _scratch.Length * 2)];
        }

        int n = payload.CopyString(_scratch);
        return _scratch.AsMemory(0, n);
    }

    /// <summary>Maps an API citation to an annotation. Fields without a neutral name stay in extension data under their own names, so the citation can be sent back unchanged.</summary>
    public static Annotation MapCitation(JsonObject c)
    {
        Dictionary<string, object?>? rest = null;
        foreach (KeyValuePair<string, JsonNode?> p in c)
        {
            if (p.Key is not ("type" or "cited_text"))
            {
                (rest ??= new Dictionary<string, object?>(StringComparer.Ordinal))[p.Key] = AnthropicJson.ToNatural(p.Value);
            }
        }

        string? source = AnthropicJson.GetString(c, "file_id");
        if (source is null && c["document_index"] is JsonValue di && di.TryGetValue(out long docIndex))
        {
            source = docIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Annotation
        {
            Type = AnthropicJson.GetString(c, "type") ?? "",
            CitedText = AnthropicJson.GetString(c, "cited_text"),
            Title = AnthropicJson.GetString(c, "document_title") ?? AnthropicJson.GetString(c, "title"),
            Url = AnthropicJson.GetString(c, "url"),
            SourceId = source,
            ExtensionData = rest,
        };
    }
}
