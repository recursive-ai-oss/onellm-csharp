using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Turns Chat Completions output, a whole response or a stream of <c>chat.completion.chunk</c> objects, into shared <see cref="StreamEvent"/>s.
/// Servers differ in small ways the decoder absorbs: reasoning text arrives as <c>reasoning_content</c> or <c>reasoning</c>, a call may arrive in
/// one chunk, and cache hits are reported as <c>cached_tokens</c> or as <c>prompt_cache_hit_tokens</c>.
/// </summary>
internal sealed class ChatCompletionsDecoder(string provider)
{
    private const int ReasoningBlock = 0;
    private const int TextBlock = 1;
    private const int RefusalBlock = 2;
    private const int CallBlocks = 16;

    private readonly string _provider = provider;
    private readonly List<StreamEvent> _events = [];
    private readonly SortedDictionary<int, bool> _calls = [];
    private byte[] _arena = new byte[256];
    private int _arenaUsed;
    private bool _started;
    private bool _sawReasoning;
    private bool _sawText;
    private bool _sawRefusal;
    private bool _usageSent;
    private string? _finish;
    private string? _responseId;
    private string? _model;
    private string? _serviceTier;
    private JsonObject? _usageNode;

    /// <summary>The events produced by the last call. Valid until the next call.</summary>
    public List<StreamEvent> Events => _events;

    /// <summary>Decodes one chunk (a server-sent event's data) or one whole response body.</summary>
    /// <param name="data">The JSON object.</param>
    /// <exception cref="ProviderException">The object reports an error.</exception>
    public void Process(ReadOnlyMemory<byte> data)
    {
        _events.Clear();
        _arenaUsed = 0;
        var reader = new Utf8JsonReader(data.Span, isFinalBlock: true, default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new MalformedResponseException(_provider, "a chunk was not a JSON object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("choices"u8))
                {
                    reader.Read();
                    ReadChoices(ref reader, data);
                }
                else if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    _responseId = reader.TokenType == JsonTokenType.String ? reader.GetString() : _responseId;
                }
                else if (reader.ValueTextEquals("model"u8))
                {
                    reader.Read();
                    _model = reader.TokenType == JsonTokenType.String ? reader.GetString() : _model;
                }
                else if (reader.ValueTextEquals("service_tier"u8))
                {
                    reader.Read();
                    _serviceTier = reader.TokenType == JsonTokenType.String ? reader.GetString() : _serviceTier;
                }
                else if (reader.ValueTextEquals("usage"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        _usageNode = JsonNode.Parse(ref reader) as JsonObject;
                    }
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    JsonNode? error = JsonNode.Parse(ref reader);
                    throw OpenAIErrors.FromStreamError(_provider, ResponsesDecoder.Str(error?["code"]), ResponsesDecoder.Str(error?["message"]) ?? error?.ToJsonString(), ResponsesDecoder.Str(error?["type"]));
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string that is not valid UTF-8 fails when it is first read.
            throw new MalformedResponseException(_provider, "a chunk was not valid JSON.");
        }

        // The start event waits until the id and model are known, which is the end of the first chunk; usage is sent as soon as it is seen.
        if (!_started && (_responseId is not null || _model is not null || _events.Count > 0))
        {
            _started = true;
            _events.Insert(0, StreamEvent.MessageStart(_responseId, _model));
        }

        // Built after the whole object is read: the service tier can come after the usage.
        if (BuildUsage(_usageNode) is { } usage)
        {
            _events.Add(StreamEvent.UsageReported(usage));
            _usageNode = null;
            _usageSent = true;
        }
    }

    /// <summary>Ends the message after the last chunk (<c>[DONE]</c> or the end of the stream, or after a whole response).</summary>
    public void Complete()
    {
        _events.Clear();
        _arenaUsed = 0;
        if (!_started)
        {
            _started = true;
            _events.Add(StreamEvent.MessageStart(_responseId, _model));
        }

        if (_sawReasoning)
        {
            _events.Add(StreamEvent.ReasoningEnd(ReasoningBlock, _provider, null));
        }

        foreach (int index in _calls.Keys)
        {
            _events.Add(StreamEvent.ToolCallEnd(CallBlocks + index));
        }

        if (!_usageSent && _serviceTier is not null)
        {
            _events.Add(StreamEvent.UsageReported(new Usage { Model = _model ?? "", ServiceTier = _serviceTier }));
        }

        FinishReason finish = _finish switch
        {
            "stop" => _sawRefusal && !_sawText ? FinishReason.Refusal : FinishReason.Stop,
            "length" => FinishReason.Length,
            "tool_calls" or "function_call" => FinishReason.ToolCalls,
            "content_filter" => FinishReason.ContentFilter,
            null => _calls.Count > 0 ? FinishReason.ToolCalls : FinishReason.Unknown,
            _ => FinishReason.Other,
        };
        _events.Add(StreamEvent.Done(finish, _finish));
    }

    private void ReadChoices(ref Utf8JsonReader reader, ReadOnlyMemory<byte> data)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        int choice = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            // Only the first choice is read: n > 1 is a low-level feature outside the message model.
            bool first = choice++ == 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (first && (reader.ValueTextEquals("delta"u8) || reader.ValueTextEquals("message"u8)))
                {
                    reader.Read();
                    ReadDelta(ref reader, data);
                }
                else if (first && reader.ValueTextEquals("finish_reason"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        _finish = reader.GetString();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
    }

    private void ReadDelta(ref Utf8JsonReader reader, ReadOnlyMemory<byte> data)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        // Whatever order a server writes the fields in, a message's blocks are reasoning, refusal, text, then tool calls: a whole message
        // and a stream then build the same message.
        int mark = _events.Count;
        ReadOnlyMemory<byte> text = default;
        ReadOnlyMemory<byte> reasoning = default;
        ReadOnlyMemory<byte> refusal = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("content"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0)
                {
                    text = Slice(ref reader, data);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("reasoning_content"u8) || reader.ValueTextEquals("reasoning"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0)
                {
                    reasoning = Slice(ref reader, data);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("refusal"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0)
                {
                    refusal = Slice(ref reader, data);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("tool_calls"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    int position = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (JsonNode.Parse(ref reader) is JsonObject call)
                        {
                            OnToolCall(call, position++);
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        var ordered = new List<StreamEvent>(3);
        if (!reasoning.IsEmpty)
        {
            _sawReasoning = true;
            ordered.Add(StreamEvent.ReasoningDelta(ReasoningBlock, reasoning));
        }

        if (!refusal.IsEmpty)
        {
            _sawRefusal = true;
            ordered.Add(StreamEvent.RefusalDelta(RefusalBlock, refusal));
        }

        if (!text.IsEmpty)
        {
            _sawText = true;
            ordered.Add(StreamEvent.TextDelta(TextBlock, text));
        }

        _events.InsertRange(mark, ordered);
    }

    private void OnToolCall(JsonObject call, int position)
    {
        // Streams number calls with "index"; a whole message or a lax server may leave it out, in which case the position stands in.
        int index = call["index"] is JsonValue iv && iv.TryGetValue(out int i) ? i : position;
        JsonObject? function = call["function"] as JsonObject;
        if (!_calls.ContainsKey(index))
        {
            _calls[index] = true;
            string id = ResponsesDecoder.Str(call["id"]) is { Length: > 0 } given ? given : $"call_{index}";
            _events.Add(StreamEvent.ToolCallStart(CallBlocks + index, id, ResponsesDecoder.Str(function?["name"]) ?? ""));
        }

        if (ResponsesDecoder.Str(function?["arguments"]) is { Length: > 0 } args)
        {
            _events.Add(StreamEvent.ToolCallArgumentsDelta(CallBlocks + index, Encoding.UTF8.GetBytes(args)));
        }
    }

    private ReadOnlyMemory<byte> Slice(ref Utf8JsonReader reader, ReadOnlyMemory<byte> data)
    {
        if (!reader.ValueIsEscaped)
        {
            return data.Slice((int)reader.TokenStartIndex + 1, reader.ValueSpan.Length);
        }

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

    // Chat Completions usage: prompt_tokens includes cached tokens and completion_tokens includes reasoning tokens (usage object in the
    // Chat Completions reference). DeepSeek reports cache hits as prompt_cache_hit_tokens; OpenRouter adds the credits it charged as cost.
    private Usage? BuildUsage(JsonObject? u)
    {
        if (u is null)
        {
            return null;
        }

        long prompt = ResponsesDecoder.Long(u["prompt_tokens"]);
        long cached = ResponsesDecoder.Long((u["prompt_tokens_details"] as JsonObject)?["cached_tokens"]);
        if (cached == 0)
        {
            cached = ResponsesDecoder.Long(u["prompt_cache_hit_tokens"]);
        }

        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        long reasoning = ResponsesDecoder.Long((u["completion_tokens_details"] as JsonObject)?["reasoning_tokens"]);
        if (reasoning != 0)
        {
            counters["reasoningTokens"] = reasoning;
        }

        long audioIn = ResponsesDecoder.Long((u["prompt_tokens_details"] as JsonObject)?["audio_tokens"]);
        if (audioIn != 0)
        {
            counters["audioInputTokens"] = audioIn;
        }

        long audioOut = ResponsesDecoder.Long((u["completion_tokens_details"] as JsonObject)?["audio_tokens"]);
        if (audioOut != 0)
        {
            counters["audioOutputTokens"] = audioOut;
        }

        decimal cost = 0m;
        if (u["cost"] is JsonValue cv && cv.TryGetValue(out decimal reported))
        {
            cost = reported;
        }

        return new Usage
        {
            Model = _model ?? "",
            ServiceTier = _serviceTier,
            InputTokens = Math.Max(0, prompt - cached),
            OutputTokens = ResponsesDecoder.Long(u["completion_tokens"]),
            CacheReadInputTokens = cached,
            Cost = cost,
            Counters = counters.Count > 0 ? counters : null,
        };
    }
}
