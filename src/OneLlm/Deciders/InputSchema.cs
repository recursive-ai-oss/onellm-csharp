using System.Globalization;
using System.Text.Json;

namespace OneLlm.Deciders;

/// <summary>A decider's inputs do not fit the schema that ships beside it.</summary>
public sealed class DeciderInputException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, with the path to the offending value.</param>
    /// <param name="path">The path in the inputs, such as <c>inputs.ticket.status</c>.</param>
    public DeciderInputException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>The path in the inputs.</summary>
    public string Path { get; }
}

/// <summary>
/// A checker for the part of JSON Schema that decider input schemas use: <c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c> (false or a schema), <c>items</c>, <c>enum</c>, <c>const</c>, <c>minimum</c>, <c>maximum</c> and <c>anyOf</c>.
/// Other keywords (descriptions, formats, patterns, references) are ignored, so a schema is never stricter than it reads here.
/// </summary>
internal static class InputSchema
{
    private const int MaxDepth = 64;

    /// <summary>Checks a value; returns null when it fits, or a message naming the first problem.</summary>
    public static string? Check(JsonElement schema, JsonElement value, string path) => Check(schema, value, path, 0);

    private static string? Check(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > MaxDepth)
        {
            return null;
        }

        if (schema.TryGetProperty("anyOf", out JsonElement any) && any.ValueKind == JsonValueKind.Array)
        {
            bool matched = false;
            foreach (JsonElement alternative in any.EnumerateArray())
            {
                if (Check(alternative, value, path, depth + 1) is null)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return $"{path} matches none of the allowed forms.";
            }
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !TypeMatches(type, value))
        {
            return $"{path} should be {Describe(type)} but is {Kind(value)}.";
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !Same(constant, value))
        {
            return $"{path} should be {constant.GetRawText()}.";
        }

        if (schema.TryGetProperty("enum", out JsonElement options) && options.ValueKind == JsonValueKind.Array && !options.EnumerateArray().Any(o => Same(o, value)))
        {
            return $"{path} is not one of {options.GetRawText()}.";
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            double n = value.GetDouble();
            if (schema.TryGetProperty("minimum", out JsonElement min) && min.TryGetDouble(out double lo) && n < lo)
            {
                return $"{path} is below the minimum {min.GetRawText()}.";
            }

            if (schema.TryGetProperty("maximum", out JsonElement max) && max.TryGetDouble(out double hi) && n > hi)
            {
                return $"{path} is above the maximum {max.GetRawText()}.";
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return CheckObject(schema, value, path, depth);
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Object)
        {
            int i = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (Check(items, item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1) is { } problem)
                {
                    return problem;
                }

                i++;
            }
        }

        return null;
    }

    private static string? CheckObject(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (name.GetString() is { } key && !value.TryGetProperty(key, out _))
                {
                    return $"{path} is missing the required field '{key}'.";
                }
            }
        }

        JsonElement properties = default;
        bool hasProperties = schema.TryGetProperty("properties", out properties) && properties.ValueKind == JsonValueKind.Object;
        schema.TryGetProperty("additionalProperties", out JsonElement additional);
        foreach (JsonProperty p in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(p.Name, out JsonElement sub))
            {
                if (Check(sub, p.Value, path + "." + p.Name, depth + 1) is { } problem)
                {
                    return problem;
                }
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                return $"{path} has the field '{p.Name}', which the schema does not allow.";
            }
            else if (additional.ValueKind == JsonValueKind.Object && Check(additional, p.Value, path + "." + p.Name, depth + 1) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static bool TypeMatches(JsonElement type, JsonElement value)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(t => TypeMatches(t, value));
        }

        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && d == Math.Floor(d),
            _ => true,
        };
    }

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind && !(a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => a.ValueKind == b.ValueKind,
            JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.Null => "null",
        _ => "a boolean",
    };

    private static string Describe(JsonElement type) => type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(static t => t.GetString())) : type.GetString() ?? "valid";
}
