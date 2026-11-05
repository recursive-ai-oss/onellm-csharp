using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Messages;

namespace OneLlm.Tools;

/// <summary>Helpers the tool generator calls to build results and schemas. Public only because generated code lives in the user's assembly.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ToolReturn
{
    /// <summary>Parses a schema literal written by the generator.</summary>
    /// <param name="json">The schema as JSON text.</param>
    /// <returns>The schema, with key order kept.</returns>
    public static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>A successful result with no content.</summary>
    /// <param name="endsLoop">Whether the call ends the loop.</param>
    /// <returns>The result.</returns>
    public static ToolResult Void(bool endsLoop) => new([]) { EndsLoop = endsLoop };

    /// <summary>A text result. A null string gives no content.</summary>
    /// <param name="text">The text.</param>
    /// <param name="endsLoop">Whether the call ends the loop; the typed result is the string.</param>
    /// <returns>The result.</returns>
    public static ToolResult Text(string? text, bool endsLoop) => text is null
        ? Void(endsLoop)
        : new ToolResult([new TextPart(text)]) { EndsLoop = endsLoop, TypedResult = endsLoop ? JsonValue.Create(text) : null };

    /// <summary>A result of one part.</summary>
    /// <param name="part">The part. Null gives no content.</param>
    /// <param name="endsLoop">Whether the call ends the loop.</param>
    /// <returns>The result.</returns>
    public static ToolResult OnePart(Part? part, bool endsLoop) => part is null ? Void(endsLoop) : new ToolResult([part]) { EndsLoop = endsLoop };

    /// <summary>A result of several parts.</summary>
    /// <param name="parts">The parts. Null gives no content.</param>
    /// <param name="endsLoop">Whether the call ends the loop.</param>
    /// <returns>The result.</returns>
    public static ToolResult Parts(IEnumerable<Part>? parts, bool endsLoop) => new(parts is null ? [] : parts.ToArray()) { EndsLoop = endsLoop };

    /// <summary>A JSON result; the value is also the typed result when the call ends the loop.</summary>
    /// <param name="value">The value. Null becomes the text <c>null</c>.</param>
    /// <param name="endsLoop">Whether the call ends the loop.</param>
    /// <returns>The result.</returns>
    public static ToolResult Json(JsonNode? value, bool endsLoop)
    {
        ToolResult r = ToolResult.FromJson(value);
        return endsLoop ? new ToolResult(r.Parts) { EndsLoop = true, TypedResult = value } : r;
    }

    /// <summary>Makes a JSON value from a boolean.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(bool v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a sbyte.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(sbyte v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a byte.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(byte v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a short.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(short v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a ushort.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(ushort v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from an int.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(int v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a uint.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(uint v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a long.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(long v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a ulong.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(ulong v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a float.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(float v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a double.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(double v) => JsonValue.Create(v);

    /// <summary>Makes a JSON value from a decimal.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(decimal v) => JsonValue.Create(v);

    /// <summary>Makes a JSON string from a char.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(char v) => JsonValue.Create(v.ToString());

    /// <summary>Makes a JSON string from a Guid.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(Guid v) => JsonValue.Create(v);

    /// <summary>Makes a JSON string from a DateTime in round-trip form.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(DateTime v) => JsonValue.Create(v);

    /// <summary>Makes a JSON string from a DateTimeOffset in round-trip form.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(DateTimeOffset v) => JsonValue.Create(v);

    /// <summary>Makes a JSON string <c>yyyy-MM-dd</c> from a DateOnly.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(DateOnly v) => JsonValue.Create(v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>Makes a JSON string <c>HH:mm:ss[.fffffff]</c> from a TimeOnly.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(TimeOnly v) => JsonValue.Create(v.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Makes a JSON string in constant ("c") form from a TimeSpan.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(TimeSpan v) => JsonValue.Create(v.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>Makes a JSON string from a Uri.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(Uri v) => JsonValue.Create(v.OriginalString);

    /// <summary>Makes a base64 JSON string from bytes.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(byte[] v) => JsonValue.Create(Convert.ToBase64String(v));

    /// <summary>Makes a JSON string from an enum member's name.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The node.</returns>
    public static JsonNode Value(Enum v) => JsonValue.Create(v.ToString());

    /// <summary>Gets a type's metadata from a source-generated serializer context, without reflection.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="context">The user's context, listing <typeparamref name="T"/> in a <c>JsonSerializable</c> attribute.</param>
    /// <returns>The type's metadata.</returns>
    /// <exception cref="InvalidOperationException">The context does not list the type.</exception>
    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException($"{context.GetType().Name} does not list {typeof(T)}; add a JsonSerializable attribute for it.");
    }

    /// <summary>Writes a value as JSON through source-generated metadata.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata from <see cref="TypeInfo{T}"/>.</param>
    /// <returns>The JSON, or null for a null value.</returns>
    public static JsonNode? Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToNode(value, typeInfo);
}
