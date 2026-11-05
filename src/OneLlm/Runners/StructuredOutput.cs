using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Providers;

namespace OneLlm.Runners;

/// <summary>Structured output: the schema sent to the provider's native structured-output feature, and reading the answer back.</summary>
internal static class StructuredOutput
{
    /// <summary>
    /// The response format for a type. The schema comes from the BCL exporter; every object gets <c>additionalProperties: false</c>
    /// and all its properties listed as required, which is the shape strict structured-output modes accept (OpenAI structured
    /// outputs guide; Gemini and Anthropic accept it as well).
    /// </summary>
    public static ResponseFormat FormatFor(JsonTypeInfo typeInfo)
    {
        var options = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = static (_, node) =>
            {
                if (node is JsonObject obj && obj["properties"] is JsonObject props)
                {
                    obj["additionalProperties"] = false;
                    var required = new JsonArray();
                    foreach (KeyValuePair<string, JsonNode?> p in props)
                    {
                        required.Add((JsonNode?)JsonValue.Create(p.Key));
                    }

                    obj["required"] = required;
                }

                return node;
            },
        };

        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(typeInfo, options);
        if (schema is not JsonObject root || root["type"]?.ToString() != "object")
        {
            throw new ArgumentException($"Structured output needs a JSON object type; '{typeInfo.Type.Name}' is not one. Wrap it in a record.", nameof(typeInfo));
        }

        return ResponseFormat.FromSchema(SchemaName(typeInfo.Type), root, strict: true);
    }

    public static ResponseFormat FormatFor(IDictionary<string, object?> schema, string name)
    {
        if (PlainValues.ToNode(schema) is not JsonObject obj)
        {
            throw new ArgumentException("The output schema must be a JSON object.", nameof(schema));
        }

        return ResponseFormat.FromSchema(name, obj, strict: false);
    }

    /// <summary>Reads the model's text as a value of the type. A refusal, a cut-off answer or JSON that does not fit is a typed error.</summary>
    public static T Parse<T>(string text, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            T? value = JsonSerializer.Deserialize(text, typeInfo);
            if (value is null)
            {
                throw new StructuredOutputException($"The model answered JSON null where a {typeInfo.Type.Name} was required.", text);
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new StructuredOutputException($"The model's answer is not a valid {typeInfo.Type.Name}: {ex.Message}", text, ex);
        }
    }

    private static string SchemaName(Type type)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in type.Name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return sb.Length == 0 ? "output" : sb.ToString(0, Math.Min(sb.Length, 64));
    }
}
