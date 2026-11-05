using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OneLlm.Messages;

/// <summary>
/// Reads untyped JSON as the same plain values OneYaml gives for untyped YAML (<c>null</c>, <see cref="bool"/>, <see cref="long"/>,
/// <see cref="BigInteger"/> for integers that do not fit, <see cref="double"/>, <see cref="string"/>, <c>List&lt;object?&gt;</c> and
/// <c>Dictionary&lt;string, object?&gt;</c> in document order), and writes those values back. Extension data therefore has one shape
/// whether it came from a JSON or a YAML file, and moves between the two without conversion.
/// </summary>
/// <remarks>
/// Add it to the <see cref="JsonSerializerOptions.Converters"/> (or <c>JsonSourceGenerationOptions.Converters</c>) of a context that serializes
/// message-model types with extension data directly. The message and part converters already use it.
/// </remarks>
public sealed class NaturalObjectJsonConverter : JsonConverter<object>
{
    private const int MaxWriteDepth = 64;

    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadValue(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) => WriteValue(writer, value, 0);

    /// <summary>Reads one JSON value from UTF-8 text into the plain values extension data holds.</summary>
    /// <param name="utf8">One JSON value.</param>
    /// <returns>The value (null, bool, long, big integer, double, string, list or dictionary).</returns>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    internal static object? Parse(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read())
        {
            throw new JsonException("The extension payload is empty.");
        }

        return ReadValue(ref reader);
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long l))
                {
                    return l;
                }

                // An integer too large for long keeps every digit; anything with a fraction or exponent is a double.
                string digits = reader.HasValueSequence ? System.Text.Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence)) : System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
                if (digits.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger big))
                {
                    return big;
                }

                return reader.GetDouble();
            case JsonTokenType.StartArray:
                var list = new List<object?>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    list.Add(ReadValue(ref reader));
                }

                return list;
            case JsonTokenType.StartObject:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string key = reader.GetString()!;
                    reader.Read();
                    map[key] = ReadValue(ref reader);
                }

                return map;
            default:
                throw new JsonException($"Unexpected token {reader.TokenType}.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > MaxWriteDepth)
        {
            throw new JsonException("Extension data is nested too deeply or contains a cycle.");
        }

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case BigInteger big:
                writer.WriteRawValue(big.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case JsonElement e:
                e.WriteTo(writer);
                break;
            case JsonNode n:
                n.WriteTo(writer);
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable seq:
                writer.WriteStartArray();
                foreach (object? item in seq)
                {
                    WriteValue(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            default:
                // Any other number or enum type goes out through its invariant text, so a value another reader produced is not rejected.
                if (value is IFormattable f && value.GetType().IsPrimitive)
                {
                    writer.WriteRawValue(f.ToString(null, CultureInfo.InvariantCulture));
                    break;
                }

                throw new JsonException($"Extension data holds a {value.GetType().Name}, which has no JSON form.");
        }
    }
}
