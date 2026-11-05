using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Runners;

/// <summary>
/// Conversions between the plain values instances and templates hold (strings, booleans, <c>long</c>, <c>double</c>, lists and
/// string-keyed dictionaries) and JSON. Nothing here uses reflection.
/// </summary>
internal static class PlainValues
{
    /// <summary>Converts a value a caller supplied into plain values, so it can be saved in an instance and read by a template.</summary>
    public static object? Normalize(object? value, string path)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case long:
            case double:
                return value;
            case int or short or byte or sbyte or ushort:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case uint u:
                return (long)u;
            case ulong ul when ul <= long.MaxValue:
                return (long)ul;
            case float f:
                return (double)f;
            case decimal d:
                return d;
            case BigInteger bi when bi >= long.MinValue && bi <= long.MaxValue:
                return (long)bi;
            case Guid g:
                return g.ToString("D");
            case DateTimeOffset dto:
                return Messages.Rfc3339.Format(dto);
            case Enum e:
                return e.ToString();
            case JsonElement element:
                return FromElement(element);
            case JsonNode node:
                using (JsonDocument doc = JsonDocument.Parse(node.ToJsonString()))
                {
                    return FromElement(doc.RootElement);
                }
            case System.Collections.IDictionary dict:
                {
                    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "";
                        result[key] = Normalize(entry.Value, path + "." + key);
                    }

                    return result;
                }

            case System.Collections.IEnumerable list:
                {
                    var result = new List<object?>();
                    int i = 0;
                    foreach (object? item in list)
                    {
                        result.Add(Normalize(item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
                        i++;
                    }

                    return result;
                }

            default:
                throw new ArgumentException(
                    $"The input '{path}' is a {value.GetType().Name}, which OneLLM cannot read without reflection. Pass a dictionary, a JsonElement, or use the overload that takes a JsonTypeInfo.",
                    nameof(value));
        }
    }

    public static Dictionary<string, object?> NormalizeInputs(IReadOnlyDictionary<string, object?>? inputs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (inputs is not null)
        {
            foreach ((string key, object? value) in inputs)
            {
                result[key] = Normalize(value, key);
            }
        }

        return result;
    }

    public static object? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    map[p.Name] = FromElement(p.Value);
                }

                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }

                return list;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                return e.TryGetInt64(out long l) ? l : e.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>Reads JSON text into plain values; null when the text is not JSON.</summary>
    public static object? TryParseJson(string text, out bool ok)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            ok = true;
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            ok = false;
            return null;
        }
    }

    public static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case decimal m:
                return JsonValue.Create(m);
            case JsonNode n:
                return n.DeepClone();
            case System.Collections.IDictionary dict:
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    obj[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = ToNode(entry.Value);
                }

                return obj;
            case System.Collections.IEnumerable list:
                var arr = new JsonArray();
                foreach (object? item in list)
                {
                    arr.Add(ToNode(item));
                }

                return arr;
            default:
                return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }
}
