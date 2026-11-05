using System.Text.Json.Nodes;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// Converts a JSON Schema to the OpenAPI-subset <c>Schema</c> object that Gemini's <c>responseSchema</c> field takes
/// (https://ai.google.dev/api/generate-content, "Schema"). Models that take <c>responseJsonSchema</c> get the JSON Schema as written instead.
/// </summary>
/// <remarks>
/// <c>$ref</c> into <c>$defs</c> or <c>definitions</c> is expanded in place (a cycle is cut at depth 12 by leaving the object open); <c>type</c> lists
/// with <c>"null"</c> become <c>nullable</c>; <c>oneOf</c> becomes <c>anyOf</c>; <c>const</c> becomes a one-value <c>enum</c>; <c>additionalProperties</c>,
/// <c>$schema</c>, <c>$id</c> and other keywords the subset lacks are dropped. Property order is kept in <c>propertyOrdering</c>, because the model
/// writes properties in that order.
/// </remarks>
internal static class GeminiSchema
{
    private const int MaxDepth = 12;

    private static readonly HashSet<string> Copied = new(StringComparer.Ordinal)
    {
        "format", "title", "description", "enum", "required", "minItems", "maxItems", "minProperties", "maxProperties",
        "minimum", "maximum", "minLength", "maxLength", "pattern", "example", "default",
    };

    public static JsonNode ToOpenApiSubset(JsonObject schema)
    {
        JsonObject? defs = schema["$defs"] as JsonObject ?? schema["definitions"] as JsonObject;
        return Convert(schema, defs, 0);
    }

    private static JsonNode Convert(JsonObject s, JsonObject? defs, int depth)
    {
        if (s["$ref"] is JsonValue refValue && refValue.TryGetValue(out string? target) && target is not null)
        {
            string name = target[(target.LastIndexOf('/') + 1)..];
            if (depth < MaxDepth && defs?[name] is JsonObject resolved)
            {
                return Convert(resolved, defs, depth + 1);
            }

            return new JsonObject { ["type"] = "OBJECT" };
        }

        var o = new JsonObject();
        bool nullable = false;
        string? type = null;
        switch (s["type"])
        {
            case JsonValue single when single.TryGetValue(out string? t):
                type = t;
                break;
            case JsonArray list:
                foreach (JsonNode? item in list)
                {
                    string? name = item?.GetValue<string>();
                    if (name == "null")
                    {
                        nullable = true;
                    }
                    else
                    {
                        type ??= name;
                    }
                }

                break;
        }

        if (type is not null)
        {
            o["type"] = type.ToUpperInvariant();
        }

        if (nullable || s["nullable"]?.GetValue<bool>() == true)
        {
            o["nullable"] = true;
        }

        foreach (string key in Copied)
        {
            if (s[key] is { } v)
            {
                o[key] = v.DeepClone();
            }
        }

        if (s["const"] is { } constant)
        {
            o["enum"] = new JsonArray(constant.DeepClone());
        }

        if (s["items"] is JsonObject items)
        {
            o["items"] = Convert(items, defs, depth + 1);
        }

        if (s["properties"] is JsonObject props)
        {
            var converted = new JsonObject();
            var order = new JsonArray();
            foreach ((string name, JsonNode? child) in props)
            {
                if (child is JsonObject c)
                {
                    converted[name] = Convert(c, defs, depth + 1);
                    JsonNode orderName = JsonValue.Create(name)!;
                    order.Add(orderName);
                }
            }

            o["properties"] = converted;
            o["propertyOrdering"] = order;
        }

        JsonArray? any = s["anyOf"] as JsonArray ?? s["oneOf"] as JsonArray;
        if (any is not null)
        {
            var list = new JsonArray();
            foreach (JsonNode? alt in any)
            {
                if (alt is JsonObject a)
                {
                    list.Add(Convert(a, defs, depth + 1));
                }
            }

            o["anyOf"] = list;
        }

        return o;
    }
}
