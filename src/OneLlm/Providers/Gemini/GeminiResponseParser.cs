using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// Reads Gemini responses into <see cref="StreamEvent"/>s. A streamed answer is a series of whole <c>GenerateContentResponse</c> objects, each carrying
/// the next parts, and a non-streamed answer is one such object, so one parser serves both and the two paths build the same message by construction.
/// </summary>
/// <remarks>
/// <para>How Gemini's parts become blocks (https://ai.google.dev/api/generate-content, "Content" and "Candidate"):</para>
/// <list type="bullet">
/// <item>Consecutive answer text parts are one text block; parts flagged <c>thought</c> are one reasoning block (the thought summary).</item>
/// <item>
/// A <c>thoughtSignature</c> can sit on any part. It is kept as its own reasoning block placed just before the part it belongs to, with data
/// <c>{"signature", "on"}</c> naming the kind of part (<c>text</c>, <c>functionCall</c>, <c>inlineData</c>); the writer puts it back on that part. On a thought
/// summary the signature is stored in that reasoning block's data with <c>on</c> = <c>thought</c>. On a hosted-tool part it stays inside the raw part kept for replay.
/// </item>
/// <item><c>functionCall</c> is a client tool call; the id is the API's when it gave one, otherwise one is made (and never sent back).</item>
/// <item><c>executableCode</c> with its <c>codeExecutionResult</c>, and <c>toolCall</c> with its <c>toolResponse</c>, are one hosted tool call each; the raw parts are kept in the call's data for replay.</item>
/// <item><c>inlineData</c> the model produced is an asset with base64 bytes; any other part is kept whole as a provider part.</item>
/// <item>Grounding metadata becomes citations on the text it supports plus a hosted tool call per tool that produced it (appended after the answer).</item>
/// </list>
/// Only the first candidate is read. Not thread-safe; one instance per response.
/// </remarks>
internal sealed class GeminiResponseParser
{
    private readonly string _provider;
    private readonly string _requestModel;
    private readonly List<byte[]> _rented = [];

    private int _next;
    private int _text = -1;
    private int _reasoning = -1;
    private string? _reasoningSignature;
    private bool _started;
    private string? _responseId;
    private string? _modelVersion;
    private int _callCounter;
    private bool _sawFunctionCall;
    private string? _finishWord;
    private FinishReason _finish;
    private bool _done;

    // The answer text so far as UTF-8, and where each text block starts in it, to turn grounding byte offsets into character positions inside a block.
    private readonly ArrayBufferWriter<byte> _answer = new(256);
    private readonly List<(int Index, int Start)> _textBlocks = [];

    private readonly List<PendingHosted> _pending = [];
    private JsonObject? _grounding;
    private JsonObject? _urlContext;
    private readonly Dictionary<string, object?> _extension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _unknown = new(StringComparer.Ordinal);
    private string? _blockReason;
    private bool _sawCandidate;

    private sealed class PendingHosted(int index, HostedToolCallPart part, JsonArray replay, string kind)
    {
        public int Index { get; } = index;

        public HostedToolCallPart Part { get; } = part;

        public JsonArray Replay { get; } = replay;

        public string Kind { get; } = kind;

        public string? Id => Part.Id;
    }

    public GeminiResponseParser(string provider, string requestModel)
    {
        _provider = provider;
        _requestModel = requestModel;
    }

    /// <summary>Returns the buffers lent to events of the chunks parsed so far. Call after those events were consumed.</summary>
    public void ReleaseBuffers()
    {
        foreach (byte[] b in _rented)
        {
            ArrayPool<byte>.Shared.Return(b);
        }

        _rented.Clear();
    }

    /// <summary>Fields of the response that have no neutral place, to keep on the message under the provider's name. Null when there are none.</summary>
    public Dictionary<string, object?>? GetMessageExtension()
    {
        var gemini = new Dictionary<string, object?>(_extension, StringComparer.Ordinal);
        if (_unknown.Count > 0)
        {
            gemini["unknown"] = new Dictionary<string, object?>(_unknown, StringComparer.Ordinal);
        }

        return gemini.Count == 0 ? null : new Dictionary<string, object?> { [_provider] = gemini };
    }

    /// <summary>Reads one response object.</summary>
    /// <param name="data">The JSON. It must stay valid until the events were consumed.</param>
    /// <param name="events">Receives the events, in order.</param>
    /// <exception cref="ContentFilterException">The prompt was blocked.</exception>
    /// <exception cref="ProviderException">The object is an error.</exception>
    /// <exception cref="MalformedResponseException">The object is not a Gemini response.</exception>
    public void Parse(ReadOnlyMemory<byte> data, List<StreamEvent> events)
    {
        int first = events.Count;
        try
        {
            ParseObject(data, events);
        }
        catch (JsonException e)
        {
            // The text of a JSON error can quote the input, so only its position is kept.
            throw new MalformedResponseException(_provider, $"the response is not valid JSON (at byte {e.BytePositionInLine}).");
        }

        if (!_started && (_responseId is not null || _modelVersion is not null || events.Count > first || _sawCandidate))
        {
            _started = true;
            events.Insert(first, StreamEvent.MessageStart(_responseId, _modelVersion ?? _requestModel));
        }
    }

    /// <summary>Ends the response: closes open blocks, adds the hosted tool calls grounding metadata describes, and reports why it stopped.</summary>
    /// <param name="events">Receives the final events.</param>
    public void Complete(List<StreamEvent> events)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        if (!_started)
        {
            _started = true;
            events.Insert(0, StreamEvent.MessageStart(_responseId, _modelVersion ?? _requestModel));
        }

        CloseReasoning(events);
        foreach (PendingHosted p in _pending)
        {
            events.Add(StreamEvent.HostedToolCallEnd(p.Index, WithReplay(p.Part, p.Replay)));
        }

        _pending.Clear();
        GroundingTools(events);
        // Search grounding is billed per query (newer models) or per grounded prompt (2.5 models), so both counts are reported for the catalog to price.
        if (_grounding?["webSearchQueries"] is JsonArray { Count: > 0 } queries)
        {
            events.Add(StreamEvent.UsageReported(new Usage
            {
                Model = _modelVersion ?? _requestModel,
                Counters = new Dictionary<string, long> { ["toolCalls.google_search"] = queries.Count, ["groundedPrompts"] = 1 },
            }));
        }

        FinishReason finish = _finish;
        if (_sawFunctionCall && finish is FinishReason.Stop or FinishReason.Unknown)
        {
            finish = FinishReason.ToolCalls;
        }

        events.Add(StreamEvent.Done(finish, _finishWord));
    }

    // ---- one response object ----

    private void ParseObject(ReadOnlyMemory<byte> data, List<StreamEvent> events)
    {
        var r = new Utf8JsonReader(data.Span, new JsonReaderOptions { MaxDepth = 64 });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
        {
            throw new MalformedResponseException(_provider, "the response is not a JSON object.");
        }

        JsonObject? groundingThisChunk = null;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("candidates"u8))
            {
                r.Read();
                Candidates(ref r, data, events, out groundingThisChunk);
            }
            else if (r.ValueTextEquals("usageMetadata"u8))
            {
                r.Read();
                events.Add(StreamEvent.UsageReported(ReadUsage(JsonNode.Parse(ref r) as JsonObject)));
            }
            else if (r.ValueTextEquals("promptFeedback"u8))
            {
                r.Read();
                JsonNode? feedback = JsonNode.Parse(ref r);
                if (feedback?["blockReason"]?.GetValue<string>() is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED")
                {
                    _blockReason = reason;
                }

                _extension["promptFeedback"] = PlainJson.From(feedback);
            }
            else if (r.ValueTextEquals("modelVersion"u8))
            {
                r.Read();
                _modelVersion = r.GetString();
            }
            else if (r.ValueTextEquals("responseId"u8))
            {
                r.Read();
                _responseId = r.GetString();
            }
            else if (r.ValueTextEquals("error"u8))
            {
                r.Read();
                using JsonDocument doc = JsonDocument.ParseValue(ref r);
                throw GeminiErrors.FromErrorElement(_provider, doc.RootElement);
            }
            else
            {
                string name = r.GetString()!;
                r.Read();
                _unknown[name] = PlainJson.From(JsonNode.Parse(ref r));
            }
        }

        if (_blockReason is not null && !_sawCandidate)
        {
            throw new ContentFilterException(_provider, $"the prompt was blocked ({_blockReason}). Rephrase it.") { Code = _blockReason };
        }

        if (groundingThisChunk is not null)
        {
            Grounding(groundingThisChunk, events);
        }
    }

    private void Candidates(ref Utf8JsonReader r, ReadOnlyMemory<byte> data, List<StreamEvent> events, out JsonObject? grounding)
    {
        grounding = null;
        if (r.TokenType != JsonTokenType.StartArray)
        {
            r.Skip();
            return;
        }

        bool firstCandidate = true;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (!firstCandidate || r.TokenType != JsonTokenType.StartObject)
            {
                r.Skip();
                continue;
            }

            firstCandidate = false;
            _sawCandidate = true;
            Candidate(ref r, data, events, out grounding);
        }
    }

    private void Candidate(ref Utf8JsonReader r, ReadOnlyMemory<byte> data, List<StreamEvent> events, out JsonObject? grounding)
    {
        grounding = null;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("content"u8))
            {
                r.Read();
                Content(ref r, data, events);
            }
            else if (r.ValueTextEquals("finishReason"u8))
            {
                r.Read();
                _finishWord = r.GetString();
                _finish = GeminiFinish.Map(_finishWord);
            }
            else if (r.ValueTextEquals("groundingMetadata"u8))
            {
                r.Read();
                grounding = JsonNode.Parse(ref r) as JsonObject;
            }
            else if (r.ValueTextEquals("urlContextMetadata"u8))
            {
                r.Read();
                _urlContext = JsonNode.Parse(ref r) as JsonObject;
            }
            else if (r.ValueTextEquals("index"u8))
            {
                r.Read();
                r.Skip();
            }
            else
            {
                // safetyRatings, citationMetadata, finishMessage, avgLogprobs, logprobsResult, continuationToken and fields not known yet.
                string name = r.GetString()!;
                r.Read();
                _extension[name] = PlainJson.From(JsonNode.Parse(ref r));
            }
        }
    }

    private void Content(ref Utf8JsonReader r, ReadOnlyMemory<byte> data, List<StreamEvent> events)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("parts"u8))
            {
                r.Read();
                if (r.TokenType != JsonTokenType.StartArray)
                {
                    r.Skip();
                    continue;
                }

                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        PartObject(ref r, data, events);
                    }
                    else
                    {
                        r.Skip();
                    }
                }
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    // ---- one part ----

    private void PartObject(ref Utf8JsonReader r, ReadOnlyMemory<byte> data, List<StreamEvent> events)
    {
        ReadOnlyMemory<byte> text = default;
        bool hasText = false;
        bool thought = false;
        string? signature = null;
        string? fcId = null;
        string? fcName = null;
        ReadOnlyMemory<byte> fcArgs = default;
        bool hasFunctionCall = false;
        string? inlineMime = null;
        string? inlineBase64 = null;
        bool hasInline = false;
        List<(string Key, JsonNode? Value)>? other = null;

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("text"u8))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.String)
                {
                    text = StringBytes(ref r, data);
                    hasText = true;
                }
                else
                {
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("thought"u8))
            {
                r.Read();
                thought = r.TokenType == JsonTokenType.True;
            }
            else if (r.ValueTextEquals("thoughtSignature"u8))
            {
                r.Read();
                signature = r.TokenType == JsonTokenType.String ? r.GetString() : null;
            }
            else if (r.ValueTextEquals("functionCall"u8))
            {
                r.Read();
                hasFunctionCall = true;
                FunctionCallObject(ref r, data, out fcId, out fcName, out fcArgs);
            }
            else if (r.ValueTextEquals("inlineData"u8))
            {
                r.Read();
                hasInline = true;
                JsonNode? blob = JsonNode.Parse(ref r);
                inlineMime = blob?["mimeType"]?.GetValue<string>();
                inlineBase64 = blob?["data"]?.GetValue<string>();
            }
            else
            {
                string key = r.GetString()!;
                r.Read();
                (other ??= []).Add((key, JsonNode.Parse(ref r)));
            }
        }

        if (thought)
        {
            CloseText();
            if (_reasoning < 0)
            {
                _reasoning = _next++;
            }

            if (hasText && !text.IsEmpty)
            {
                events.Add(StreamEvent.ReasoningDelta(_reasoning, text));
            }

            _reasoningSignature = signature ?? _reasoningSignature;
            return;
        }

        CloseReasoning(events);

        if (hasFunctionCall)
        {
            CloseText();
            Signature(signature, "functionCall", events);
            int index = _next++;
            _sawFunctionCall = true;
            string id = fcId is { Length: > 0 } ? fcId : SyntheticId();
            events.Add(StreamEvent.ToolCallStart(index, id, fcName ?? ""));
            if (!fcArgs.IsEmpty)
            {
                events.Add(StreamEvent.ToolCallArgumentsDelta(index, fcArgs));
            }

            events.Add(StreamEvent.ToolCallEnd(index));
            return;
        }

        if (hasInline)
        {
            CloseText();
            Signature(signature, "inlineData", events);
            var asset = new AssetRef { Storage = "inline", Key = "", ContentType = inlineMime ?? "application/octet-stream", Data = inlineBase64 };
            events.Add(StreamEvent.PartAdded(_next++, new AssetPart(asset)));
            return;
        }

        JsonNode? hostedNode = null;
        string? hostedKey = null;
        if (other is not null)
        {
            foreach ((string key, JsonNode? value) in other)
            {
                if (key is "executableCode" or "codeExecutionResult" or "toolCall" or "toolResponse")
                {
                    hostedKey = key;
                    hostedNode = value;
                    break;
                }
            }
        }

        if (hostedKey is not null)
        {
            CloseText();
            Hosted(hostedKey, hostedNode, signature, events);
            return;
        }

        if (other is { Count: > 0 })
        {
            // fileData and parts that this version does not know: kept whole so they can be sent back unchanged.
            CloseText();
            var raw = new JsonObject();
            foreach ((string key, JsonNode? value) in other)
            {
                raw[key] = value;
            }

            if (signature is not null)
            {
                raw["thoughtSignature"] = signature;
            }

            events.Add(StreamEvent.PartAdded(_next++, new ProviderPart(_provider, raw) { Type = other[0].Key }));
            return;
        }

        // A text part, possibly empty (the API ends some answers with an empty part that carries only the signature).
        if (signature is not null)
        {
            CloseText();
            Signature(signature, "text", events);
        }

        if (hasText && !text.IsEmpty)
        {
            if (_text < 0)
            {
                _text = _next++;
                _textBlocks.Add((_text, _answer.WrittenCount));
            }

            events.Add(StreamEvent.TextDelta(_text, text));
            _answer.Write(text.Span);
        }
    }

    private static void FunctionCallObject(ref Utf8JsonReader r, ReadOnlyMemory<byte> data, out string? id, out string? name, out ReadOnlyMemory<byte> args)
    {
        id = null;
        name = null;
        args = default;
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("id"u8))
            {
                r.Read();
                id = r.GetString();
            }
            else if (r.ValueTextEquals("name"u8))
            {
                r.Read();
                name = r.GetString();
            }
            else if (r.ValueTextEquals("args"u8))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    int start = (int)r.TokenStartIndex;
                    r.Skip();
                    int end = (int)r.TokenStartIndex + 1;
                    args = data.Slice(start, end - start);
                }
                else
                {
                    r.Skip();
                }
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    // The bytes of a string value: a slice of the input when it has no escapes, otherwise an unescaped copy lent until ReleaseBuffers.
    private ReadOnlyMemory<byte> StringBytes(ref Utf8JsonReader r, ReadOnlyMemory<byte> data)
    {
        int length = r.ValueSpan.Length;
        if (!r.ValueIsEscaped)
        {
            return data.Slice((int)r.TokenStartIndex + 1, length);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        _rented.Add(buffer);
        int n = r.CopyString(buffer);
        return new ReadOnlyMemory<byte>(buffer, 0, n);
    }

    private string SyntheticId() =>
        string.Create(CultureInfo.InvariantCulture, $"{GeminiRequestWriter.SyntheticCallIdPrefix}{(_responseId is null ? "" : _responseId + "-")}{_callCounter++}");

    private void CloseText() => _text = -1;

    private void CloseReasoning(List<StreamEvent> events)
    {
        if (_reasoning < 0)
        {
            return;
        }

        var data = new JsonObject { ["on"] = "thought" };
        if (_reasoningSignature is not null)
        {
            data["signature"] = _reasoningSignature;
        }

        events.Add(StreamEvent.ReasoningEnd(_reasoning, _provider, data));
        _reasoning = -1;
        _reasoningSignature = null;
    }

    // A signature of a text, call or media part is its own reasoning block just before that part.
    private void Signature(string? signature, string on, List<StreamEvent> events)
    {
        if (signature is null)
        {
            return;
        }

        var data = new JsonObject { ["signature"] = signature, ["on"] = on };
        events.Add(StreamEvent.ReasoningEnd(_next++, _provider, data));
    }

    // ---- hosted tool calls made of parts ----

    private void Hosted(string key, JsonNode? node, string? signature, List<StreamEvent> events)
    {
        var rawPart = new JsonObject { [key] = node?.DeepClone() };
        if (signature is not null)
        {
            rawPart["thoughtSignature"] = signature;
        }

        JsonObject body = node as JsonObject ?? new JsonObject();
        string? id = body["id"]?.GetValue<string>();
        switch (key)
        {
            case "executableCode":
            {
                var part = new HostedToolCallPart("code_execution")
                {
                    Provider = _provider,
                    Id = id,
                    Inputs = new JsonObject { ["language"] = body["language"]?.DeepClone(), ["code"] = body["code"]?.DeepClone() },
                };
                int index = _next++;
                _pending.Add(new PendingHosted(index, part, [(JsonNode)rawPart], "code"));
                events.Add(StreamEvent.HostedToolCallStart(index, WithReplay(part, [rawPart.DeepClone()])));
                break;
            }

            case "toolCall":
            {
                string tool = ToolName(body["toolType"]?.GetValue<string>(), body["toolName"]?.GetValue<string>());
                var part = new HostedToolCallPart(tool) { Provider = _provider, Id = id, Inputs = body["args"] as JsonObject ?? new JsonObject() };
                int index = _next++;
                _pending.Add(new PendingHosted(index, part, [(JsonNode)rawPart], "tool"));
                events.Add(StreamEvent.HostedToolCallStart(index, WithReplay(part, [rawPart.DeepClone()])));
                break;
            }

            case "codeExecutionResult":
            case "toolResponse":
            {
                string kind = key == "codeExecutionResult" ? "code" : "tool";
                PendingHosted? match = _pending.FirstOrDefault(p => p.Kind == kind && (id is null || p.Id is null || p.Id == id));
                JsonNode? result;
                string? status;
                if (kind == "code")
                {
                    result = new JsonObject { ["outcome"] = body["outcome"]?.DeepClone(), ["output"] = body["output"]?.DeepClone() };
                    status = body["outcome"]?.GetValue<string>() switch
                    {
                        "OUTCOME_OK" => "completed",
                        "OUTCOME_FAILED" => "failed",
                        "OUTCOME_DEADLINE_EXCEEDED" => "timed_out",
                        _ => null,
                    };
                }
                else
                {
                    result = body["response"]?.DeepClone();
                    status = "completed";
                }

                HostedToolCallPart basePart = match?.Part ?? new HostedToolCallPart(kind == "code" ? "code_execution" : ToolName(body["toolType"]?.GetValue<string>(), null)) { Provider = _provider, Id = id };
                JsonArray replay = match is null ? [] : [.. match.Replay.Select(n => n?.DeepClone())];
                JsonNode rawNode = rawPart;
                replay.Add(rawNode);
                var finished = new HostedToolCallPart(basePart.Tool)
                {
                    Provider = _provider,
                    Id = basePart.Id ?? id,
                    Inputs = basePart.Inputs,
                    Result = result,
                    Status = status,
                    Data = new JsonObject { ["replay"] = replay },
                };
                int index = match?.Index ?? _next++;
                if (match is not null)
                {
                    _pending.Remove(match);
                }

                events.Add(StreamEvent.HostedToolCallEnd(index, finished));
                break;
            }
        }
    }

    private static HostedToolCallPart WithReplay(HostedToolCallPart p, JsonArray replay) => new(p.Tool)
    {
        Provider = p.Provider,
        Id = p.Id,
        Status = p.Status,
        Inputs = p.Inputs,
        Result = p.Result,
        Cost = p.Cost,
        Data = new JsonObject { ["replay"] = replay.DeepClone() },
    };

    private static string ToolName(string? toolType, string? toolName) => toolType switch
    {
        "GOOGLE_SEARCH_WEB" => "google_search",
        "GOOGLE_SEARCH_IMAGE" => "google_image_search",
        "URL_CONTEXT" => "url_context",
        "GOOGLE_MAPS" => "google_maps",
        "FILE_SEARCH" => "file_search",
        "MEDIA_PROCESSING" => "media_processing",
        { Length: > 0 } other => other.ToLowerInvariant(),
        _ => toolName ?? "tool",
    };

    // ---- grounding ----

    private void Grounding(JsonObject metadata, List<StreamEvent> events)
    {
        // Chunks and supports arrive in pieces when streaming; supports index into all chunks seen so far.
        _grounding ??= [];
        foreach ((string key, JsonNode? value) in metadata)
        {
            if (value is JsonArray add && key is "groundingChunks" or "groundingSupports" or "webSearchQueries" or "imageSearchQueries")
            {
                JsonArray target = _grounding[key] as JsonArray ?? [];
                _grounding[key] = target;
                foreach (JsonNode? item in add)
                {
                    target.Add(item?.DeepClone());
                }
            }
            else
            {
                _grounding[key] = value?.DeepClone();
            }
        }

        JsonArray chunks = _grounding["groundingChunks"] as JsonArray ?? [];
        if (metadata["groundingSupports"] is not JsonArray supports)
        {
            return;
        }

        foreach (JsonNode? support in supports)
        {
            if (support is not JsonObject s || s["groundingChunkIndices"] is not JsonArray indices)
            {
                continue;
            }

            JsonObject? segment = s["segment"] as JsonObject;
            int startByte = segment?["startIndex"]?.GetValue<int>() ?? 0;
            int endByte = segment?["endIndex"]?.GetValue<int>() ?? startByte;
            string? cited = segment?["text"]?.GetValue<string>();
            (int Index, int Start, int Chars)? block = FindBlock(startByte);
            for (int i = 0; i < indices.Count; i++)
            {
                int ci = indices[i]!.GetValue<int>();
                if (block is null || ci < 0 || ci >= chunks.Count || chunks[ci] is not JsonObject chunk)
                {
                    continue;
                }

                events.Add(StreamEvent.AnnotationAdded(block.Value.Index, MakeAnnotation(chunk, s, i, cited, CharOffset(startByte) - block.Value.Chars, CharOffset(endByte) - block.Value.Chars)));
            }
        }
    }

    private static Annotation MakeAnnotation(JsonObject chunk, JsonObject support, int position, string? cited, int start, int end)
    {
        string type = "url_citation";
        string? title = null;
        string? url = null;
        string? source = null;
        if (chunk["web"] is JsonObject web)
        {
            title = web["title"]?.GetValue<string>();
            url = web["uri"]?.GetValue<string>();
        }
        else if (chunk["retrievedContext"] is JsonObject ctx)
        {
            type = "file_citation";
            title = ctx["title"]?.GetValue<string>();
            url = ctx["uri"]?.GetValue<string>();
            source = ctx["fileSearchStore"]?.GetValue<string>();
        }
        else if (chunk["maps"] is JsonObject maps)
        {
            title = maps["title"]?.GetValue<string>();
            url = maps["uri"]?.GetValue<string>();
            source = maps["placeId"]?.GetValue<string>();
        }
        else if (chunk["image"] is JsonObject image)
        {
            title = image["title"]?.GetValue<string>();
            url = image["sourceUri"]?.GetValue<string>();
        }

        Dictionary<string, object?>? extension = null;
        if (support["confidenceScores"] is JsonArray scores && position < scores.Count && scores[position] is JsonValue score && score.TryGetValue(out double confidence))
        {
            extension = new Dictionary<string, object?> { ["confidenceScore"] = confidence };
        }

        return new Annotation { Type = type, Title = title, Url = url, SourceId = source, CitedText = cited, Start = Math.Max(0, start), End = Math.Max(0, end), ExtensionData = extension };
    }

    // The text block holding the byte offset, with the number of characters before the block.
    private (int Index, int Start, int Chars)? FindBlock(int byteOffset)
    {
        if (_textBlocks.Count == 0)
        {
            return null;
        }

        var found = _textBlocks[0];
        foreach (var b in _textBlocks)
        {
            if (b.Start <= byteOffset)
            {
                found = b;
            }
        }

        return (found.Index, found.Start, CharOffset(found.Start));
    }

    // Offsets in the response are UTF-8 byte offsets; positions in the message are UTF-16 indices.
    private int CharOffset(int byteOffset)
    {
        int clamped = Math.Clamp(byteOffset, 0, _answer.WrittenCount);
        return Encoding.UTF8.GetCharCount(_answer.WrittenSpan[..clamped]);
    }

    private void GroundingTools(List<StreamEvent> events)
    {
        if (_grounding is { } g)
        {
            JsonArray chunks = g["groundingChunks"] as JsonArray ?? [];
            bool maps = chunks.Any(c => c?["maps"] is not null);
            bool files = chunks.Any(c => c?["retrievedContext"] is not null);
            bool search = g["webSearchQueries"] is JsonArray { Count: > 0 } || g["imageSearchQueries"] is JsonArray { Count: > 0 } || chunks.Any(c => c?["web"] is not null);
            if (search)
            {
                events.Add(StreamEvent.HostedToolCallEnd(_next++, GroundedCall("google_search", new JsonObject { ["queries"] = g["webSearchQueries"]?.DeepClone() ?? new JsonArray() }, g, c => c?["web"] is not null || c?["image"] is not null)));
            }

            if (maps)
            {
                events.Add(StreamEvent.HostedToolCallEnd(_next++, GroundedCall("google_maps", new JsonObject(), g, c => c?["maps"] is not null)));
            }

            if (files)
            {
                events.Add(StreamEvent.HostedToolCallEnd(_next++, GroundedCall("file_search", new JsonObject(), g, c => c?["retrievedContext"] is not null)));
            }
        }

        if (_urlContext?["urlMetadata"] is JsonArray urls && urls.Count > 0)
        {
            bool allOk = urls.All(u => u?["urlRetrievalStatus"]?.GetValue<string>() == "URL_RETRIEVAL_STATUS_SUCCESS");
            var inputs = new JsonObject { ["urls"] = new JsonArray([.. urls.Select(u => (JsonNode?)JsonValue.Create(u?["retrievedUrl"]?.GetValue<string>()))]) };
            events.Add(StreamEvent.HostedToolCallEnd(_next++, new HostedToolCallPart("url_context")
            {
                Provider = _provider,
                Inputs = inputs,
                Result = _urlContext.DeepClone(),
                Status = allOk ? "completed" : "failed",
            }));
        }
    }

    // Built from metadata the API attaches to the answer, not from a part the API sent, so it carries no replay data.
    private HostedToolCallPart GroundedCall(string tool, JsonObject inputs, JsonObject grounding, Func<JsonNode?, bool> belongs)
    {
        var result = new JsonObject();
        JsonArray chunks = grounding["groundingChunks"] as JsonArray ?? [];
        result["groundingChunks"] = new JsonArray([.. chunks.Where(belongs).Select(c => c?.DeepClone())]);
        if (grounding["searchEntryPoint"] is { } entry && tool == "google_search")
        {
            result["searchEntryPoint"] = entry.DeepClone();
        }

        return new HostedToolCallPart(tool) { Provider = _provider, Inputs = inputs, Result = result, Status = "completed" };
    }

    // ---- usage ----

    private Usage ReadUsage(JsonObject? u)
    {
        if (u is null)
        {
            return new Usage { Model = _modelVersion ?? _requestModel };
        }

        long prompt = Count(u, "promptTokenCount");
        long cached = Count(u, "cachedContentTokenCount");
        long candidates = Count(u, "candidatesTokenCount");
        long thoughts = Count(u, "thoughtsTokenCount");
        long toolUse = Count(u, "toolUsePromptTokenCount");
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string name, long value)
        {
            if (value != 0)
            {
                counters[name] = value;
            }
        }

        Add("reasoningTokens", thoughts);
        Add("toolUsePromptTokens", toolUse);
        foreach ((string detail, string prefix) in new[] { ("promptTokensDetails", "input"), ("candidatesTokensDetails", "output"), ("toolUsePromptTokensDetails", "toolUseInput") })
        {
            if (u[detail] is JsonArray list)
            {
                foreach (JsonNode? item in list)
                {
                    string modality = item?["modality"]?.GetValue<string>() ?? "";
                    long tokens = item?["tokenCount"] is JsonValue v && v.TryGetValue(out long t) ? t : 0;
                    if (modality is "AUDIO" or "IMAGE" or "VIDEO" or "DOCUMENT")
                    {
                        Add(prefix + char.ToUpperInvariant(modality[0]) + modality[1..].ToLowerInvariant() + "Tokens", tokens);
                    }
                }
            }
        }

        string? tier = u["serviceTier"]?.GetValue<string>();
        return new Usage
        {
            Model = _modelVersion ?? _requestModel,
            ServiceTier = tier is "flex" or "priority" ? tier : null,
            // promptTokenCount already includes the cached part; the library keeps cached tokens in their own counter.
            InputTokens = Math.Max(0, prompt - cached) + toolUse,
            CacheReadInputTokens = cached,
            // Output includes thinking tokens, which the API reports separately and bills as output.
            OutputTokens = candidates + thoughts,
            Counters = counters.Count == 0 ? null : counters,
        };
    }

    private static long Count(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out long n) ? n : 0;
}
