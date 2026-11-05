using System.Buffers.Text;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace OneLlm.Tools;

/// <summary>
/// Reads tool arguments out of JSON. The tool generator calls these from the code it writes; they are public only because that code lives in
/// the user's assembly. Every method throws <see cref="ToolInputException"/> naming the argument path when the JSON does not fit, and none uses reflection.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ToolBinder
{
    /// <summary>Reads a required argument.</summary>
    /// <typeparam name="T">The C# type.</typeparam>
    /// <param name="inputs">The arguments.</param>
    /// <param name="name">The argument's JSON name.</param>
    /// <param name="convert">Converts the JSON value. It receives a null node only when the JSON value is <c>null</c> and handles it itself.</param>
    /// <returns>The converted value.</returns>
    /// <exception cref="ToolInputException">The argument is missing or does not convert.</exception>
    public static T Required<T>(JsonObject inputs, string name, Func<JsonNode?, string, T> convert)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(convert);
        if (!inputs.TryGetPropertyValue(name, out JsonNode? node))
        {
            throw new ToolInputException(name, "is required but missing.");
        }

        return convert(node, name);
    }

    /// <summary>Reads an optional argument.</summary>
    /// <typeparam name="T">The C# type.</typeparam>
    /// <param name="inputs">The arguments.</param>
    /// <param name="name">The argument's JSON name.</param>
    /// <param name="convert">Converts the JSON value.</param>
    /// <param name="defaultValue">The value when the argument is absent: the parameter's declared default.</param>
    /// <returns>The converted value, or <paramref name="defaultValue"/> when the argument is absent.</returns>
    /// <exception cref="ToolInputException">The argument is present and does not convert.</exception>
    public static T Optional<T>(JsonObject inputs, string name, Func<JsonNode?, string, T> convert, T defaultValue)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(convert);
        return inputs.TryGetPropertyValue(name, out JsonNode? node) ? convert(node, name) : defaultValue;
    }

    /// <summary>Fails when the model sent an argument the tool does not declare.</summary>
    /// <param name="inputs">The arguments.</param>
    /// <param name="known">The declared argument names.</param>
    /// <exception cref="ToolInputException">An undeclared argument is present.</exception>
    public static void RejectUnknown(JsonObject inputs, params string[] known)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (KeyValuePair<string, JsonNode?> pair in inputs)
        {
            if (System.Array.IndexOf(known, pair.Key) < 0)
            {
                throw new ToolInputException(pair.Key, "is not a parameter of this tool.");
            }
        }
    }

    /// <summary>Wraps a converter so JSON <c>null</c> gives <c>null</c> for a nullable value type.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="convert">The converter for non-null values.</param>
    /// <returns>The value or null.</returns>
    public static T? NullableValue<T>(JsonNode? node, string path, Func<JsonNode?, string, T> convert)
        where T : struct => node is null ? null : convert(node, path);

    /// <summary>Wraps a converter so JSON <c>null</c> gives <c>null</c> for a nullable reference type.</summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="convert">The converter for non-null values.</param>
    /// <returns>The value or null.</returns>
    public static T? NullableRef<T>(JsonNode? node, string path, Func<JsonNode?, string, T> convert)
        where T : class => node is null ? null : convert(node, path);

    private static JsonNode NotNull(JsonNode? node, string path) => node ?? throw new ToolInputException(path, "must not be null.");

    private static ToolInputException Wrong(string path, string expected) => new(path, $"must be {expected}.");

    private static JsonValue ValueOf(JsonNode? node, string path, string expected) =>
        NotNull(node, path) as JsonValue ?? throw Wrong(path, expected);

    // The numeric converters accept exactly the JSON numbers that fit the C# type; a string such as "5" is refused, because the schema says integer.
    private static T Number<T>(JsonNode? node, string path, string expected)
    {
        JsonValue v = ValueOf(node, path, expected);
        return v.TryGetValue(out T? result) && result is not null && IsNumber(v) ? result : throw Wrong(path, expected);
    }

    private static bool IsNumber(JsonValue v) => v.GetValueKind() == JsonValueKind.Number;

    /// <summary>Reads a string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The string.</returns>
    public static string ReadString(JsonNode? node, string path) =>
        ValueOf(node, path, "a string").TryGetValue(out string? s) && s is not null ? s : throw Wrong(path, "a string");

    /// <summary>Reads a boolean.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The boolean.</returns>
    public static bool ReadBoolean(JsonNode? node, string path)
    {
        JsonValue v = ValueOf(node, path, "true or false");
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Wrong(path, "true or false"),
        };
    }

    /// <summary>Reads an <see cref="sbyte"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static sbyte ReadSByte(JsonNode? node, string path) => Number<sbyte>(node, path, "an integer from -128 to 127");

    /// <summary>Reads a <see cref="byte"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static byte ReadByte(JsonNode? node, string path) => Number<byte>(node, path, "an integer from 0 to 255");

    /// <summary>Reads a <see cref="short"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static short ReadInt16(JsonNode? node, string path) => Number<short>(node, path, "an integer from -32768 to 32767");

    /// <summary>Reads a <see cref="ushort"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static ushort ReadUInt16(JsonNode? node, string path) => Number<ushort>(node, path, "an integer from 0 to 65535");

    /// <summary>Reads an <see cref="int"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static int ReadInt32(JsonNode? node, string path) => Number<int>(node, path, "an integer from -2147483648 to 2147483647");

    /// <summary>Reads a <see cref="uint"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static uint ReadUInt32(JsonNode? node, string path) => Number<uint>(node, path, "an integer from 0 to 4294967295");

    /// <summary>Reads a <see cref="long"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static long ReadInt64(JsonNode? node, string path) => Number<long>(node, path, "an integer from -9223372036854775808 to 9223372036854775807");

    /// <summary>Reads a <see cref="ulong"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static ulong ReadUInt64(JsonNode? node, string path) => Number<ulong>(node, path, "an integer from 0 to 18446744073709551615");

    /// <summary>Reads a <see cref="float"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static float ReadSingle(JsonNode? node, string path) => Number<float>(node, path, "a number");

    /// <summary>Reads a <see cref="double"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static double ReadDouble(JsonNode? node, string path) => Number<double>(node, path, "a number");

    /// <summary>Reads a <see cref="decimal"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static decimal ReadDecimal(JsonNode? node, string path) => Number<decimal>(node, path, "a number");

    /// <summary>Reads a <see cref="char"/> from a one-character string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The character.</returns>
    public static char ReadChar(JsonNode? node, string path)
    {
        string s = ReadString(node, path);
        return s.Length == 1 ? s[0] : throw Wrong(path, "a string of exactly one character");
    }

    /// <summary>Reads a <see cref="Guid"/> from its string form.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static Guid ReadGuid(JsonNode? node, string path) =>
        System.Guid.TryParse(ReadString(node, path), out Guid g) ? g : throw Wrong(path, "a UUID string");

    /// <summary>Reads a <see cref="DateTime"/> from an RFC 3339 string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static DateTime ReadDateTime(JsonNode? node, string path) =>
        System.DateTime.TryParse(ReadString(node, path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d)
            ? d
            : throw Wrong(path, "an RFC 3339 date-time string");

    /// <summary>Reads a <see cref="DateTimeOffset"/> from an RFC 3339 string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static DateTimeOffset ReadDateTimeOffset(JsonNode? node, string path) =>
        System.DateTimeOffset.TryParse(ReadString(node, path), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset d)
            ? d
            : throw Wrong(path, "an RFC 3339 date-time string");

    /// <summary>Reads a <see cref="DateOnly"/> from a <c>yyyy-MM-dd</c> string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static DateOnly ReadDateOnly(JsonNode? node, string path) =>
        System.DateOnly.TryParseExact(ReadString(node, path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d
            : throw Wrong(path, "a date string such as 2026-03-14");

    /// <summary>Reads a <see cref="TimeOnly"/> from an <c>HH:mm:ss</c> string with optional fraction.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static TimeOnly ReadTimeOnly(JsonNode? node, string path) =>
        System.TimeOnly.TryParseExact(ReadString(node, path), ["HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t
            : throw Wrong(path, "a time string such as 13:45:00");

    /// <summary>Reads a <see cref="TimeSpan"/> from its constant ("c") string form, <c>[-][d.]hh:mm:ss[.fffffff]</c>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static TimeSpan ReadTimeSpan(JsonNode? node, string path) =>
        System.TimeSpan.TryParseExact(ReadString(node, path), "c", CultureInfo.InvariantCulture, out TimeSpan t)
            ? t
            : throw Wrong(path, "a duration string such as 01:30:00 or 1.12:00:00");

    /// <summary>Reads an absolute <see cref="Uri"/>.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The value.</returns>
    public static Uri ReadUri(JsonNode? node, string path) =>
        System.Uri.TryCreate(ReadString(node, path), UriKind.Absolute, out Uri? u) ? u : throw Wrong(path, "an absolute URI string");

    /// <summary>Reads a byte array from a base64 string.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ReadBytes(JsonNode? node, string path)
    {
        string s = ReadString(node, path);
        byte[] buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(s.Length)];
        return System.Convert.TryFromBase64String(s, buffer, out int written) ? buffer.AsSpan(0, written).ToArray() : throw Wrong(path, "a base64 string");
    }

    /// <summary>Reads an enum from the name of one of its members, case-sensitive.</summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="names">The member names, as the schema lists them.</param>
    /// <param name="values">The member values, parallel to <paramref name="names"/>.</param>
    /// <returns>The member.</returns>
    public static T ReadEnum<T>(JsonNode? node, string path, string[] names, T[] values)
        where T : struct, System.Enum
    {
        string s = ReadString(node, path);
        int i = System.Array.IndexOf(names, s);
        return i >= 0 ? values[i] : throw Wrong(path, "one of: " + string.Join(", ", names));
    }

    /// <summary>Reads any JSON value as a <see cref="JsonElement"/>. JSON <c>null</c> gives an element of kind Null.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>A copy that does not depend on the input.</returns>
    public static JsonElement ReadElement(JsonNode? node, string path)
    {
        _ = path;
        using JsonDocument doc = JsonDocument.Parse(node is null ? "null" : node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Reads any JSON value as a <see cref="JsonNode"/>; JSON <c>null</c> gives null.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>A deep copy.</returns>
    public static JsonNode? ReadNode(JsonNode? node, string path)
    {
        _ = path;
        return node?.DeepClone();
    }

    /// <summary>Reads a JSON object.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>A deep copy.</returns>
    public static JsonObject ReadObject(JsonNode? node, string path) =>
        NotNull(node, path) is JsonObject o ? (JsonObject)o.DeepClone() : throw Wrong(path, "an object");

    /// <summary>Reads a JSON array.</summary>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <returns>A deep copy.</returns>
    public static JsonArray ReadArray(JsonNode? node, string path) =>
        NotNull(node, path) is JsonArray a ? (JsonArray)a.DeepClone() : throw Wrong(path, "an array");

    /// <summary>Reads a JSON array into a <see cref="List{T}"/>; the generator also uses it for arrays, sets and read-only lists.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="element">Converts each element; the path it receives is <c>path[index]</c>.</param>
    /// <returns>The list.</returns>
    public static List<T> List<T>(JsonNode? node, string path, Func<JsonNode?, string, T> element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (NotNull(node, path) is not JsonArray array)
        {
            throw Wrong(path, "an array");
        }

        var list = new List<T>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            list.Add(element(array[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
        }

        return list;
    }

    /// <summary>Reads a JSON array into a <see cref="HashSet{T}"/>, refusing duplicates as the schema's <c>uniqueItems</c> says.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="element">Converts each element.</param>
    /// <returns>The set.</returns>
    public static HashSet<T> Set<T>(JsonNode? node, string path, Func<JsonNode?, string, T> element)
    {
        List<T> items = List(node, path, element);
        var set = new HashSet<T>(items.Count);
        foreach (T item in items)
        {
            if (!set.Add(item))
            {
                throw Wrong(path, "an array of unique items");
            }
        }

        return set;
    }

    /// <summary>Reads a JSON object into a dictionary with string keys, keeping the order the model wrote.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="value">Converts each value; the path it receives is <c>path.key</c>.</param>
    /// <returns>The dictionary.</returns>
    public static Dictionary<string, T> Map<T>(JsonNode? node, string path, Func<JsonNode?, string, T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (NotNull(node, path) is not JsonObject obj)
        {
            throw Wrong(path, "an object");
        }

        var map = new Dictionary<string, T>(obj.Count);
        foreach (KeyValuePair<string, JsonNode?> pair in obj)
        {
            map[pair.Key] = value(pair.Value, path + "." + pair.Key);
        }

        return map;
    }

    /// <summary>Reads a record or class through a source-generated serializer context.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="node">The JSON value.</param>
    /// <param name="path">The argument path.</param>
    /// <param name="typeInfo">The type's metadata from the user's <c>JsonSerializerContext</c>.</param>
    /// <returns>The value.</returns>
    public static T Complex<T>(JsonNode? node, string path, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        JsonNode n = NotNull(node, path);
        try
        {
            return n.Deserialize(typeInfo) ?? throw new ToolInputException(path, "must not be null.");
        }
        catch (JsonException e)
        {
            throw new ToolInputException(path, "does not match its schema: " + e.Message);
        }
        catch (NotSupportedException e)
        {
            throw new ToolInputException(path, "does not match its schema: " + e.Message);
        }
    }
}
