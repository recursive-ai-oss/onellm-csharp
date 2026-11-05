using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Turns Responses API output into the shared <see cref="StreamEvent"/>s. A streamed response is fed one server-sent event at a time
/// through <see cref="ProcessEvent"/>; a whole response goes through <see cref="ProcessResponse"/>. Both end in the same events for the
/// same content, so a <see cref="MessageAccumulator"/> builds equal messages from either (the output item shapes and the event list are in the
/// Responses API reference).
/// </summary>
internal sealed class ResponsesDecoder(string provider)
{
    // Each output item owns a block of 64 stream indexes; its content parts or summary parts take the slots inside it.
    private const int SlotsPerItem = 64;

    private static readonly byte[] s_paragraphBreak = "\n\n"u8.ToArray();

    private readonly string _provider = provider;
    private readonly List<StreamEvent> _events = [];
    private readonly HashSet<int> _textSeen = [];
    private readonly HashSet<int> _argsSeen = [];
    private readonly HashSet<int> _started = [];
    private readonly HashSet<int> _summarySeen = [];
    private readonly Dictionary<string, int> _hosted = [];
    private byte[] _arena = new byte[256];
    private int _arenaUsed;
    private bool _sawCall;
    private bool _sawRefusal;
    private bool _sawText;

    /// <summary>The events produced by the last call. Valid until the next call.</summary>
    public List<StreamEvent> Events => _events;

    /// <summary>True once a completed or incomplete response ended the stream.</summary>
    public bool Finished { get; private set; }

    /// <summary>Decodes one server-sent event's data.</summary>
    /// <param name="data">The JSON data of the event.</param>
    /// <exception cref="ProviderException">The event reports a failure.</exception>
    public void ProcessEvent(ReadOnlyMemory<byte> data)
    {
        try
        {
            ProcessEventCore(data);
        }
        catch (InvalidOperationException)
        {
            // A string that is not valid UTF-8 fails when it is first read.
            throw new MalformedResponseException(_provider, "a stream event held text that is not valid UTF-8.");
        }
    }

    private void ProcessEventCore(ReadOnlyMemory<byte> data)
    {
        _events.Clear();
        _arenaUsed = 0;
        if (TryProcessHot(data))
        {
            return;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(data.Span) as JsonObject ?? throw new MalformedResponseException(_provider, "a stream event was not a JSON object.");
        }
        catch (JsonException)
        {
            throw new MalformedResponseException(_provider, "a stream event was not valid JSON.");
        }

        string type = Str(root["type"]) ?? "";
        switch (type)
        {
            case "response.created":
                if (root["response"] is JsonObject created)
                {
                    _events.Add(StreamEvent.MessageStart(Str(created["id"]), Str(created["model"])));
                }

                break;
            case "response.output_item.added":
                OnItemAdded(root);
                break;
            case "response.output_item.done":
                OnItemDone(root);
                break;
            case "response.reasoning_summary_part.added":
                int summaryIndex = Int(root["summary_index"]);
                if (summaryIndex > 0 && _summarySeen.Contains(Int(root["output_index"]) * SlotsPerItem))
                {
                    _events.Add(StreamEvent.ReasoningDelta(Int(root["output_index"]) * SlotsPerItem, s_paragraphBreak));
                }

                break;
            case "response.output_text.annotation.added":
                if (root["annotation"] is JsonObject annotation)
                {
                    int index = (Int(root["output_index"]) * SlotsPerItem) + Slot(root["content_index"]);
                    _events.Add(StreamEvent.AnnotationAdded(index, MapAnnotation(annotation)));
                }

                break;
            case "response.completed":
            case "response.incomplete":
                if (root["response"] is JsonObject done)
                {
                    Finish(done);
                }

                break;
            case "response.output_text.delta":
            case "response.function_call_arguments.delta":
            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
            case "response.refusal.delta":
                // Reached only when the fast path could not read the event (the type came after the delta).
                Emit(ClassifyHot(Encoding.UTF8.GetBytes(type)), Int(root["output_index"]), Slot(root["content_index"] ?? root["summary_index"]), Encoding.UTF8.GetBytes(Str(root["delta"]) ?? ""));
                break;
            case "response.failed":
                JsonObject? failed = (root["response"] as JsonObject)?["error"] as JsonObject;
                throw OpenAIErrors.FromStreamError(_provider, Str(failed?["code"]), Str(failed?["message"]) ?? "the response failed.", null);
            case "error":
                throw OpenAIErrors.FromStreamError(_provider, Str(root["code"]) ?? Str((root["error"] as JsonObject)?["code"]), Str(root["message"]) ?? Str((root["error"] as JsonObject)?["message"]), Str(root["error_type"]));
            default:
                // Progress events for tools (searching, interpreting, ...) and anything newer are not needed to build the message:
                // the finished output item carries the result.
                break;
        }
    }

    /// <summary>Decodes a whole response body, as returned by a call that did not stream.</summary>
    /// <param name="body">The response JSON.</param>
    /// <exception cref="ProviderException">The response reports a failure.</exception>
    public void ProcessResponse(ReadOnlyMemory<byte> body)
    {
        _events.Clear();
        _arenaUsed = 0;
        JsonObject root;
        try
        {
            root = JsonNode.Parse(body.Span) as JsonObject ?? throw new MalformedResponseException(_provider, "the response was not a JSON object.");
        }
        catch (JsonException)
        {
            throw new MalformedResponseException(_provider, "the response was not valid JSON.");
        }

        ProcessResponseObject(root);
    }

    /// <summary>Decodes a response object (a whole body, or one line of a batch output file).</summary>
    /// <param name="root">The response object.</param>
    public void ProcessResponseObject(JsonObject root)
    {
        try
        {
            ProcessResponseObjectCore(root);
        }
        catch (InvalidOperationException)
        {
            // A string that is not valid UTF-8 fails when it is first read.
            throw new MalformedResponseException(_provider, "the response held text that is not valid UTF-8.");
        }
    }

    private void ProcessResponseObjectCore(JsonObject root)
    {
        _events.Clear();
        _arenaUsed = 0;
        string status = Str(root["status"]) ?? "completed";
        if (status == "failed")
        {
            JsonObject? error = root["error"] as JsonObject;
            throw OpenAIErrors.FromStreamError(_provider, Str(error?["code"]), Str(error?["message"]) ?? "the response failed.", null);
        }

        _events.Add(StreamEvent.MessageStart(Str(root["id"]), Str(root["model"])));
        if (root["output"] is JsonArray output)
        {
            for (int i = 0; i < output.Count; i++)
            {
                if (output[i] is JsonObject item)
                {
                    EmitWholeItem(item, i * SlotsPerItem);
                }
            }
        }

        Finish(root);
    }

    // ---- the hot path: text, argument and summary deltas, read straight from the bytes ----

    private enum Hot
    {
        None,
        Text,
        Arguments,
        Summary,
        ReasoningText,
        Refusal,
    }

    private bool TryProcessHot(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        var reader = new Utf8JsonReader(span, isFinalBlock: true, default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            Hot hot = Hot.None;
            int outputIndex = 0;
            int slot = 0;
            int deltaStart = -1;
            int deltaLength = 0;
            bool deltaEscaped = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    hot = ClassifyHot(reader.ValueSpan);
                    if (hot == Hot.None)
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("output_index"u8))
                {
                    reader.Read();
                    outputIndex = reader.GetInt32();
                }
                else if (reader.ValueTextEquals("content_index"u8) || reader.ValueTextEquals("summary_index"u8))
                {
                    reader.Read();
                    slot = Math.Clamp(reader.GetInt32(), 0, SlotsPerItem - 1);
                }
                else if (reader.ValueTextEquals("delta"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    deltaStart = (int)reader.TokenStartIndex + 1;
                    deltaLength = reader.HasValueSequence ? -1 : reader.ValueSpan.Length;
                    deltaEscaped = reader.ValueIsEscaped;
                    if (deltaEscaped)
                    {
                        if (hot == Hot.None)
                        {
                            // The type had not been seen yet; the general path reads the event whole.
                            return false;
                        }

                        ReadOnlyMemory<byte> unescaped = Unescape(ref reader);
                        Emit(hot, outputIndex, slot, unescaped);
                        return true;
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (hot == Hot.None || deltaStart < 0 || deltaLength < 0)
            {
                return false;
            }

            Emit(hot, outputIndex, slot, data.Slice(deltaStart, deltaLength));
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            // Let the general path report the malformed event.
            return false;
        }
    }

    private static Hot ClassifyHot(ReadOnlySpan<byte> type)
    {
        if (type.SequenceEqual("response.output_text.delta"u8))
        {
            return Hot.Text;
        }

        if (type.SequenceEqual("response.function_call_arguments.delta"u8))
        {
            return Hot.Arguments;
        }

        if (type.SequenceEqual("response.reasoning_summary_text.delta"u8))
        {
            return Hot.Summary;
        }

        if (type.SequenceEqual("response.reasoning_text.delta"u8))
        {
            return Hot.ReasoningText;
        }

        return type.SequenceEqual("response.refusal.delta"u8) ? Hot.Refusal : Hot.None;
    }

    private void Emit(Hot hot, int outputIndex, int slot, ReadOnlyMemory<byte> delta)
    {
        int block = outputIndex * SlotsPerItem;
        switch (hot)
        {
            case Hot.Text:
                _textSeen.Add(block + slot);
                _sawText = true;
                _events.Add(StreamEvent.TextDelta(block + slot, delta));
                break;
            case Hot.Arguments:
                _argsSeen.Add(block);
                _events.Add(StreamEvent.ToolCallArgumentsDelta(block, delta));
                break;
            case Hot.Summary:
            case Hot.ReasoningText:
                _summarySeen.Add(block);
                _events.Add(StreamEvent.ReasoningDelta(block, delta));
                break;
            case Hot.Refusal:
                _sawRefusal = true;
                _events.Add(StreamEvent.RefusalDelta(block + slot, delta));
                break;
        }
    }

    // The reader sits on a string token that contains escapes; the decoded UTF-8 goes into a per-event arena so the slice stays valid
    // until the next event is read.
    private ReadOnlyMemory<byte> Unescape(ref Utf8JsonReader reader)
    {
        int max = reader.ValueSpan.Length;
        if (_arenaUsed + max > _arena.Length)
        {
            _arena = new byte[Math.Max(_arena.Length * 2, _arenaUsed + max)];
            _arenaUsed = 0;
        }

        int written = reader.CopyString(_arena.AsSpan(_arenaUsed, max));
        var slice = new ReadOnlyMemory<byte>(_arena, _arenaUsed, written);
        _arenaUsed += written;
        return slice;
    }

    // ---- output items ----

    private void OnItemAdded(JsonObject root)
    {
        if (root["item"] is not JsonObject item)
        {
            return;
        }

        int block = Int(root["output_index"]) * SlotsPerItem;
        string type = Str(item["type"]) ?? "";
        if (type == "function_call")
        {
            _started.Add(block);
            _sawCall = true;
            _events.Add(StreamEvent.ToolCallStart(block, Str(item["call_id"]) ?? Str(item["id"]) ?? "", Str(item["name"]) ?? ""));
        }
        else if (IsHosted(type))
        {
            _events.Add(StreamEvent.HostedToolCallStart(block, BuildHosted(item, finished: false)));
        }
    }

    private void OnItemDone(JsonObject root)
    {
        if (root["item"] is not JsonObject item)
        {
            return;
        }

        int block = Int(root["output_index"]) * SlotsPerItem;
        string type = Str(item["type"]) ?? "";
        switch (type)
        {
            case "message":
                // Text normally arrived as deltas. Content parts that did not (a server that sends only the finished item) are added now.
                if (item["content"] is JsonArray content)
                {
                    for (int j = 0; j < content.Count; j++)
                    {
                        if (content[j] is JsonObject c && !_textSeen.Contains(block + Math.Min(j, SlotsPerItem - 1)))
                        {
                            EmitContent(c, block + Math.Min(j, SlotsPerItem - 1));
                        }
                    }
                }

                break;
            case "reasoning":
                if (!_summarySeen.Contains(block))
                {
                    EmitReasoningText(item, block);
                }

                _events.Add(StreamEvent.ReasoningEnd(block, _provider, (JsonObject)item.DeepClone()));
                break;
            case "function_call":
                if (!_started.Contains(block))
                {
                    _sawCall = true;
                    _events.Add(StreamEvent.ToolCallStart(block, Str(item["call_id"]) ?? Str(item["id"]) ?? "", Str(item["name"]) ?? ""));
                }

                if (!_argsSeen.Contains(block) && Str(item["arguments"]) is { Length: > 0 } args)
                {
                    _events.Add(StreamEvent.ToolCallArgumentsDelta(block, Encoding.UTF8.GetBytes(args)));
                }

                _events.Add(StreamEvent.ToolCallEnd(block));
                break;
            default:
                if (IsHosted(type))
                {
                    HostedToolCallPart part = BuildHosted(item, finished: true);
                    Count(part.Tool);
                    _events.Add(StreamEvent.HostedToolCallEnd(block, part));
                }
                else if (type is not "")
                {
                    EmitOtherItem(item, block);
                }

                break;
        }
    }

    private void EmitWholeItem(JsonObject item, int block)
    {
        string type = Str(item["type"]) ?? "";
        switch (type)
        {
            case "message":
                if (item["content"] is JsonArray content)
                {
                    for (int j = 0; j < content.Count; j++)
                    {
                        if (content[j] is JsonObject c)
                        {
                            EmitContent(c, block + Math.Min(j, SlotsPerItem - 1));
                        }
                    }
                }

                break;
            case "reasoning":
                EmitReasoningText(item, block);
                _events.Add(StreamEvent.ReasoningEnd(block, _provider, (JsonObject)item.DeepClone()));
                break;
            case "function_call":
                _sawCall = true;
                _events.Add(StreamEvent.ToolCallStart(block, Str(item["call_id"]) ?? Str(item["id"]) ?? "", Str(item["name"]) ?? ""));
                if (Str(item["arguments"]) is { Length: > 0 } args)
                {
                    _events.Add(StreamEvent.ToolCallArgumentsDelta(block, Encoding.UTF8.GetBytes(args)));
                }

                _events.Add(StreamEvent.ToolCallEnd(block));
                break;
            default:
                if (IsHosted(type))
                {
                    HostedToolCallPart part = BuildHosted(item, finished: true);
                    Count(part.Tool);
                    _events.Add(StreamEvent.HostedToolCallStart(block, BuildHosted(item, finished: false)));
                    _events.Add(StreamEvent.HostedToolCallEnd(block, part));
                }
                else if (type is not "")
                {
                    EmitOtherItem(item, block);
                }

                break;
        }
    }

    private void EmitContent(JsonObject content, int index)
    {
        switch (Str(content["type"]))
        {
            case "output_text":
                if (Str(content["text"]) is { Length: > 0 } text)
                {
                    _sawText = true;
                    _events.Add(StreamEvent.TextDelta(index, Encoding.UTF8.GetBytes(text)));
                }

                if (content["annotations"] is JsonArray annotations)
                {
                    foreach (JsonNode? a in annotations)
                    {
                        if (a is JsonObject annotation)
                        {
                            _events.Add(StreamEvent.AnnotationAdded(index, MapAnnotation(annotation)));
                        }
                    }
                }

                break;
            case "refusal":
                _sawRefusal = true;
                _events.Add(StreamEvent.RefusalDelta(index, Encoding.UTF8.GetBytes(Str(content["refusal"]) ?? "")));
                break;
            default:
                // Unknown content kinds keep their JSON.
                _events.Add(StreamEvent.PartAdded(index, new ProviderPart(_provider, content.DeepClone()) { Type = Str(content["type"]) }));
                break;
        }
    }

    // The summary parts, joined by blank lines, are the reasoning's readable form. Open-weight servers put raw reasoning under "content".
    private void EmitReasoningText(JsonObject item, int block)
    {
        var text = new StringBuilder();
        foreach (JsonNode? s in item["summary"] as JsonArray ?? [])
        {
            if (Str((s as JsonObject)?["text"]) is { Length: > 0 } t)
            {
                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }

                text.Append(t);
            }
        }

        if (text.Length == 0)
        {
            foreach (JsonNode? c in item["content"] as JsonArray ?? [])
            {
                if (Str((c as JsonObject)?["text"]) is { Length: > 0 } t)
                {
                    text.Append(t);
                }
            }
        }

        if (text.Length > 0)
        {
            _events.Add(StreamEvent.ReasoningDelta(block, Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    private void EmitOtherItem(JsonObject item, int block)
    {
        string type = Str(item["type"]) ?? "";
        if (type == "custom_tool_call")
        {
            // A custom tool takes free text, not JSON. It is carried as {"input": "..."} so the message model keeps one shape for client tool calls.
            _sawCall = true;
            var inputs = new JsonObject { ["input"] = Str(item["input"]) ?? "" };
            _events.Add(StreamEvent.ToolCallStart(block, Str(item["call_id"]) ?? Str(item["id"]) ?? "", Str(item["name"]) ?? ""));
            _events.Add(StreamEvent.ToolCallArgumentsDelta(block, Encoding.UTF8.GetBytes(inputs.ToJsonString())));
            _events.Add(StreamEvent.ToolCallEnd(block));
            return;
        }

        // Anything else (compaction items, client-side tool calls, newer kinds) is kept whole so it can be sent back unchanged.
        _events.Add(StreamEvent.PartAdded(block, new ProviderPart(_provider, item.DeepClone()) { Type = type }));
    }

    // ---- hosted tools ----

    private static bool IsHosted(string type) =>
        type is "web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call" or "mcp_call" or "mcp_list_tools"
            or "tool_search_call" or "tool_search_output";

    private HostedToolCallPart BuildHosted(JsonObject item, bool finished)
    {
        string type = Str(item["type"]) ?? "";
        string tool = type.EndsWith("_call", StringComparison.Ordinal) ? type[..^5] : type;
        if (!finished)
        {
            return new HostedToolCallPart(tool) { Provider = _provider, Id = Str(item["id"]), Status = Str(item["status"]) };
        }

        JsonObject inputs = [];
        JsonNode? result = null;
        foreach ((string key, JsonNode? value) in item)
        {
            switch (key)
            {
                case "type" or "id" or "status":
                    break;
                case "result" or "results" or "outputs" or "output" or "tools":
                    result ??= value?.DeepClone();
                    break;
                default:
                    inputs[key] = value?.DeepClone();
                    break;
            }
        }

        return new HostedToolCallPart(tool)
        {
            Provider = _provider,
            Id = Str(item["id"]),
            Status = Str(item["status"]),
            Inputs = inputs.Count > 0 ? inputs : null,
            Result = result,
            Data = (JsonObject)item.DeepClone(),
        };
    }

    private void Count(string tool) => _hosted[tool] = _hosted.GetValueOrDefault(tool) + 1;

    // ---- the end of a response ----

    private void Finish(JsonObject response)
    {
        Usage? usage = BuildUsage(response);
        if (usage is not null)
        {
            _events.Add(StreamEvent.UsageReported(usage));
        }

        string status = Str(response["status"]) ?? "completed";
        string? reason = Str((response["incomplete_details"] as JsonObject)?["reason"]);
        FinishReason finish;
        if (status == "incomplete")
        {
            finish = reason switch
            {
                "max_output_tokens" => FinishReason.Length,
                "content_filter" => FinishReason.ContentFilter,
                _ => FinishReason.Other,
            };
        }
        else if (_sawCall)
        {
            finish = FinishReason.ToolCalls;
        }
        else
        {
            finish = _sawRefusal && !_sawText ? FinishReason.Refusal : FinishReason.Stop;
        }

        _events.Add(StreamEvent.Done(finish, reason ?? status));
        Finished = true;
    }

    // Responses usage: input_tokens already counts cached and cache-write tokens, and output_tokens counts reasoning tokens. OneLLM's
    // Usage.InputTokens holds only the uncached part, so the cached and written tokens are taken out of it.
    private Usage? BuildUsage(JsonObject response)
    {
        string model = Str(response["model"]) ?? "";
        string? tier = Str(response["service_tier"]);
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string tool, int count) in _hosted)
        {
            counters[CounterFor(tool)] = count;
        }

        if (response["tool_usage"]?["image_gen"] is JsonObject imageGen)
        {
            Set(counters, "imageGenInputTokens", Long(imageGen["input_tokens"]));
            Set(counters, "imageGenOutputTokens", Long(imageGen["output_tokens"]));
        }

        if (response["usage"] is not JsonObject u)
        {
            return counters.Count == 0 && tier is null ? null : new Usage { Model = model, ServiceTier = tier, Counters = counters.Count > 0 ? counters : null };
        }

        JsonObject? inDetails = u["input_tokens_details"] as JsonObject;
        long input = Long(u["input_tokens"]);
        long cached = Long(inDetails?["cached_tokens"]);
        long written = Long(inDetails?["cache_write_tokens"]);
        if (inDetails?["cache_write_tokens_details"] is JsonObject ttl)
        {
            Set(counters, "cacheCreation30mInputTokens", Long(ttl["ttl_30m_tokens"]));
            Set(counters, "cacheCreation12hInputTokens", Long(ttl["ttl_12h_tokens"]));
        }

        Set(counters, "reasoningTokens", Long((u["output_tokens_details"] as JsonObject)?["reasoning_tokens"]));
        return new Usage
        {
            Model = model,
            ServiceTier = tier,
            InputTokens = Math.Max(0, input - cached - written),
            OutputTokens = Long(u["output_tokens"]),
            CacheCreationInputTokens = written,
            CacheReadInputTokens = cached,
            Counters = counters.Count > 0 ? counters : null,
        };
    }

    // Hosted tool calls are counted per tool under the counter names the catalog's price items use (toolCalls.web_search, ...).
    private static string CounterFor(string tool) => "toolCalls." + tool;

    private static void Set(Dictionary<string, long> counters, string name, long value)
    {
        if (value != 0)
        {
            counters[name] = value;
        }
    }

    // ---- helpers ----

    internal static Annotation MapAnnotation(JsonObject a)
    {
        Dictionary<string, object?>? extra = null;
        foreach ((string key, JsonNode? value) in a)
        {
            if (key is "type" or "url" or "title" or "filename" or "file_id" or "start_index" or "end_index" or "index")
            {
                continue;
            }

            extra ??= [];
            extra[key] = value is JsonValue v ? ScalarOf(v) : value?.ToJsonString();
        }

        return new Annotation
        {
            Type = Str(a["type"]) ?? "",
            Url = Str(a["url"]),
            Title = Str(a["title"]) ?? Str(a["filename"]),
            SourceId = Str(a["file_id"]),
            Start = a["start_index"] is null ? (a["index"] is null ? null : Int(a["index"])) : Int(a["start_index"]),
            End = a["end_index"] is null ? null : Int(a["end_index"]),
            ExtensionData = extra,
        };
    }

    private static object? ScalarOf(JsonValue v)
    {
        if (v.TryGetValue(out string? s))
        {
            return s;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        if (v.TryGetValue(out bool b))
        {
            return b;
        }

        return v.ToJsonString();
    }

    private static int Slot(JsonNode? node) => Math.Clamp(Int(node), 0, SlotsPerItem - 1);

    internal static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    internal static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long l) ? l : 0;
}
