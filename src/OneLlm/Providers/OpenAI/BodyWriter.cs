using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Writes one JSON request body field by field in a fixed order, so recorded request bytes are stable. Fields named in the request's
/// <c>ExtraParameters</c> replace the ones the codec would write and are appended at the end.
/// </summary>
internal sealed class BodyWriter : IDisposable
{
    private static readonly JsonWriterOptions s_options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ArrayBufferWriter<byte> _buffer;
    private readonly JsonObject? _extras;

    /// <param name="extras">Fields that replace the codec's own and are appended at the end.</param>
    /// <param name="capacity">The size the body is expected to reach; a buffer that starts there is filled once instead of doubling its way up.</param>
    public BodyWriter(JsonObject? extras, int capacity = 4096)
    {
        _buffer = new ArrayBufferWriter<byte>(capacity);
        _extras = extras is { Count: > 0 } ? extras : null;
        Writer = new Utf8JsonWriter(_buffer, s_options);
        Writer.WriteStartObject();
    }

    public Utf8JsonWriter Writer { get; }

    /// <summary>
    /// A close guess at the size of the JSON body of a request: the text it carries plus a fixed allowance per message and tool call.
    /// It only sizes the first buffer; a body that turns out larger still grows as usual.
    /// </summary>
    public static int EstimateSize(ChatRequest r)
    {
        long size = 1024 + (r.System?.Length ?? 0);
        foreach (Message m in r.Messages)
        {
            size += 64;
            size += PartsSize(m.User);
            size += PartsSize(m.Assistant);
            foreach (ToolCall call in m.ToolCalls)
            {
                size += 160 + PartsSize(call.Result);
            }
        }

        return (int)Math.Min(size, 64 * 1024 * 1024);
    }

    private static long PartsSize(IReadOnlyList<Part> parts)
    {
        long size = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            size += parts[i] switch
            {
                TextPart t => t.Text.Length + 48,
                AssetPart { Asset.Data: { } data } => data.Length + 128,
                _ => 128,
            };
        }

        return size;
    }

    /// <summary>Starts a field. Returns false when an extra parameter replaces it, in which case the caller writes nothing.</summary>
    public bool Field(string name)
    {
        if (_extras is not null && _extras.ContainsKey(name))
        {
            return false;
        }

        Writer.WritePropertyName(name);
        return true;
    }

    public ReadOnlyMemory<byte> Finish()
    {
        if (_extras is not null)
        {
            foreach ((string key, JsonNode? value) in _extras)
            {
                Writer.WritePropertyName(key);
                if (value is null)
                {
                    Writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(Writer);
                }
            }
        }

        Writer.WriteEndObject();
        Writer.Flush();
        return _buffer.WrittenMemory;
    }

    public void Dispose() => Writer.Dispose();
}
