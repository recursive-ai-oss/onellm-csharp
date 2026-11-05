using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;

namespace OneLlm.Extensions.AI;

/// <summary>
/// Content that has no Microsoft.Extensions.AI counterpart (a hosted tool call and its result, a provider-specific part), carried as the
/// OneLLM <see cref="Part"/> itself. It survives a trip through any pipeline that passes unknown content along, and returns to OneLLM unchanged.
/// </summary>
public sealed class OneLlmPartContent : AIContent
{
    /// <summary>Wraps <paramref name="part"/>.</summary>
    /// <param name="part">The OneLLM part to carry. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is <see langword="null"/>.</exception>
    public OneLlmPartContent(Part part) => Part = part ?? throw new ArgumentNullException(nameof(part));

    /// <summary>The carried part.</summary>
    public Part Part { get; }
}

/// <summary>JSON value conversions between System.Text.Json's node and element types and the loosely typed values Microsoft.Extensions.AI uses.</summary>
internal static class JsonBridge
{
    public static JsonElement ToElement(JsonNode node)
    {
        using JsonDocument doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    public static JsonNode? ToNode(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    /// <summary>An object value of any plain shape as a JSON node. Types with no JSON form fall back to their invariant text.</summary>
    public static JsonNode? ObjectToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode n:
                return n.DeepClone();
            case JsonElement e:
                return ToNode(e);
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case short sh:
                return JsonValue.Create(sh);
            case byte by:
                return JsonValue.Create(by);
            case uint ui:
                return JsonValue.Create(ui);
            case ulong ul:
                return JsonValue.Create(ul);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case decimal m:
                return JsonValue.Create(m);
            case IEnumerable<KeyValuePair<string, object?>> map:
                var obj = new JsonObject();
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    obj[pair.Key] = ObjectToNode(pair.Value);
                }

                return obj;
            case System.Collections.IEnumerable list:
                var array = new JsonArray();
                foreach (object? item in list)
                {
                    array.Add(ObjectToNode(item));
                }

                return array;
            case IFormattable formattable:
                return JsonValue.Create(formattable.ToString(null, CultureInfo.InvariantCulture));
            default:
                return JsonValue.Create(value.ToString() ?? "");
        }
    }

    /// <summary>The arguments of a function call as the dictionary Microsoft.Extensions.AI expects: each value is a <see cref="JsonElement"/>.</summary>
    public static Dictionary<string, object?>? ToArguments(JsonObject? inputs)
    {
        if (inputs is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(inputs.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> pair in inputs)
        {
            result[pair.Key] = pair.Value is null ? null : ToElement(pair.Value);
        }

        return result;
    }

    public static JsonObject FromArguments(IDictionary<string, object?>? arguments)
    {
        var obj = new JsonObject();
        if (arguments is null)
        {
            return obj;
        }

        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            obj[pair.Key] = ObjectToNode(pair.Value);
        }

        return obj;
    }

    public static JsonObject? AsObject(object? value) => value switch
    {
        JsonObject o => (JsonObject)o.DeepClone(),
        JsonElement { ValueKind: JsonValueKind.Object } e => ToNode(e) as JsonObject,
        _ => null,
    };
}
