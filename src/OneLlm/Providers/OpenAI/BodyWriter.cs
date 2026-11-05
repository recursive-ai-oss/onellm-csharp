using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Writes one JSON request body field by field in a fixed order, so recorded request bytes are stable. Fields named in the request's
/// <c>ExtraParameters</c> replace the ones the codec would write and are appended at the end.
/// </summary>
internal sealed class BodyWriter : IDisposable
{
    private static readonly JsonWriterOptions s_options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly JsonObject? _extras;

    public BodyWriter(JsonObject? extras)
    {
        _extras = extras is { Count: > 0 } ? extras : null;
        Writer = new Utf8JsonWriter(_buffer, s_options);
        Writer.WriteStartObject();
    }

    public Utf8JsonWriter Writer { get; }

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
