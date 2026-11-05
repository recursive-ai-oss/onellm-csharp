using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Providers.Anthropic;

/// <summary>Small JSON helpers shared by the Anthropic request writer and response reader.</summary>
internal static class AnthropicJson
{
    /// <summary>Writer settings for request bodies: compact, and readable non-ASCII text instead of \u escapes.</summary>
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Converts a JSON value to the plain values extension data holds (null, bool, long, BigInteger, double, string, list, dictionary).</summary>
    public static object? ToNatural(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                var dict = new Dictionary<string, object?>(o.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonNode?> p in o)
                {
                    dict[p.Key] = ToNatural(p.Value);
                }

                return dict;
            case JsonArray a:
                var list = new List<object?>(a.Count);
                foreach (JsonNode? item in a)
                {
                    list.Add(ToNatural(item));
                }

                return list;
            case JsonValue v:
                JsonElement e = v.GetValue<JsonElement>();
                switch (e.ValueKind)
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.String:
                        return e.GetString();
                    case JsonValueKind.Number:
                        if (e.TryGetInt64(out long l))
                        {
                            return l;
                        }

                        string raw = e.GetRawText();
                        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger big))
                        {
                            return big;
                        }

                        return e.GetDouble();
                    default:
                        return null;
                }

            default:
                return null;
        }
    }

    /// <summary>Writes a plain value (as produced by <see cref="ToNatural"/> or read into extension data) as JSON.</summary>
    public static void WriteNatural(Utf8JsonWriter w, object? value, int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("Extension data is nested too deeply to write.");
        }

        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case BigInteger big:
                w.WriteRawValue(big.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case JsonNode n:
                n.WriteTo(w);
                break;
            case IDictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (KeyValuePair<string, object?> p in dict)
                {
                    w.WritePropertyName(p.Key);
                    WriteNatural(w, p.Value, depth + 1);
                }

                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartArray();
                foreach (object? item in list)
                {
                    WriteNatural(w, item, depth + 1);
                }

                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>Writes a JSON value as compact UTF-8.</summary>
    public static byte[] ToUtf8(JsonNode node)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            node.WriteTo(w);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a JSON value as compact text.</summary>
    public static string ToJson(JsonNode node) => System.Text.Encoding.UTF8.GetString(ToUtf8(node));

    public static string? GetString(JsonObject o, string key) => o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static long GetLong(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue(out long l) ? l : 0;
}
