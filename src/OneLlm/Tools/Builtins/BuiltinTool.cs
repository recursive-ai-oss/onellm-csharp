using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Messages;

namespace OneLlm.Tools.Builtins;

/// <summary>What a tool needs the host to have granted before it can work at all.</summary>
[Flags]
public enum ToolCapability
{
    /// <summary>Nothing: the tool works on its own.</summary>
    None = 0,

    /// <summary>Reading files under a granted root.</summary>
    FileRead = 1,

    /// <summary>Writing files under a granted root.</summary>
    FileWrite = 2,

    /// <summary>Running shell commands.</summary>
    Shell = 4,

    /// <summary>Network egress to at least one host.</summary>
    Network = 8,
}

/// <summary>Implemented by tools that need a grant, so the runtime can tell an ungranted reference before the model sees the tool.</summary>
public interface ICapabilityTool : ITool
{
    /// <summary>The grants the tool needs.</summary>
    ToolCapability Requires { get; }
}

/// <summary>Shared behaviour of the built-in tools: refusals and file errors come back as error results the model can read.</summary>
internal abstract class BuiltinTool : ICapabilityTool
{
    protected BuiltinTool(ToolSpec definition, ToolCapability requires)
    {
        Definition = definition;
        Requires = requires;
    }

    public ToolSpec Definition { get; }

    public ToolCapability Requires { get; }

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(inputs, context, cancellationToken).ConfigureAwait(false);
        }
        catch (CapabilityMissingException e)
        {
            return Fail(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return Fail(e.Message);
        }
    }

    protected abstract ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken);

    /// <summary>An error result whose text starts with <c>Error: </c>, the form existing definitions expect.</summary>
    protected static ToolResult Fail(string message) => ToolResult.Error("Error: " + message);

    protected static ToolSpec Spec(string name, string description, string schema, TimeSpan timeout)
    {
        JsonObject parsed = JsonNode.Parse(schema)!.AsObject();
        return new ToolSpec(name, description, parsed) { Timeout = timeout };
    }
}

/// <summary>Reads typed arguments from the JSON the model sent.</summary>
internal static class Args
{
    internal static string RequiredString(JsonObject o, string name)
    {
        string? v = OptionalString(o, name);
        return string.IsNullOrEmpty(v) ? throw new ToolInputException(name, "is required") : v;
    }

    internal static string? OptionalString(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return null;
        }

        return n is JsonValue v && v.TryGetValue(out string? s) ? s : throw new ToolInputException(name, "must be a string");
    }

    internal static bool Bool(JsonObject o, string name, bool fallback)
    {
        if (!o.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return fallback;
        }

        return n is JsonValue v && v.TryGetValue(out bool b) ? b : throw new ToolInputException(name, "must be true or false");
    }

    internal static int? OptionalInt(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return null;
        }

        if (n is JsonValue v)
        {
            if (v.TryGetValue(out int i))
            {
                return i;
            }

            if (v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue)
            {
                return (int)d;
            }
        }

        throw new ToolInputException(name, "must be an integer");
    }

    internal static List<string>? OptionalStringList(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return null;
        }

        if (n is not JsonArray a)
        {
            throw new ToolInputException(name, "must be an array of strings");
        }

        var list = new List<string>(a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            list.Add(a[i] is JsonValue v && v.TryGetValue(out string? s) ? s : throw new ToolInputException($"{name}[{i}]", "must be a string"));
        }

        return list;
    }
}

/// <summary>Detects and converts the text encodings the file tools handle.</summary>
internal static class TextFile
{
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    internal enum Kind
    {
        Utf8,
        Utf16Le,
        Utf16Be,
    }

    /// <summary>Decodes UTF-8 (with or without a BOM) and UTF-16 with a BOM. Returns false for anything else, which is treated as binary.</summary>
    internal static bool TryDecode(byte[] data, out string text, out Kind kind, out bool bom)
    {
        text = "";
        bom = false;
        kind = Kind.Utf8;
        try
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                bom = true;
                text = s_strictUtf8.GetString(data, 3, data.Length - 3);
                return true;
            }

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                bom = true;
                kind = Kind.Utf16Le;
                text = new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2);
                return true;
            }

            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                bom = true;
                kind = Kind.Utf16Be;
                text = new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2);
                return true;
            }

            if (Array.IndexOf(data, (byte)0) >= 0)
            {
                return false;
            }

            text = s_strictUtf8.GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static byte[] Utf8(string s, bool bom)
    {
        byte[] body = Encoding.UTF8.GetBytes(s);
        if (!bom)
        {
            return body;
        }

        byte[] all = new byte[body.Length + 3];
        all[0] = 0xEF;
        all[1] = 0xBB;
        all[2] = 0xBF;
        body.CopyTo(all, 3);
        return all;
    }

    /// <summary>A byte count as the file tools print it: <c>512 B</c>, <c>1.5 KB</c>, <c>2 MB</c>.</summary>
    internal static string HumanSize(long size)
    {
        if (size < 1024)
        {
            return size.ToString(CultureInfo.InvariantCulture) + " B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        decimal value = size;
        string unit = "B";
        foreach (string u in units)
        {
            value /= 1024;
            unit = u;
            if (value < 1024)
            {
                break;
            }
        }

        string formatted = value.ToString("0.0", CultureInfo.InvariantCulture);
        if (formatted.EndsWith(".0", StringComparison.Ordinal))
        {
            formatted = formatted[..^2];
        }

        return formatted + " " + unit;
    }

    /// <summary>A byte count as the directory tree prints it: <c>512B</c>, <c>1.5KB</c>.</summary>
    internal static string TreeSize(long size)
    {
        if (size < 1024)
        {
            return size.ToString(CultureInfo.InvariantCulture) + "B";
        }

        long div = 1024;
        int exp = 0;
        for (long n = size / 1024; n >= 1024; n /= 1024)
        {
            div *= 1024;
            exp++;
        }

        return ((decimal)size / div).ToString("0.0", CultureInfo.InvariantCulture) + "KMGTPE"[exp] + "B";
    }

    /// <summary>Makes a text part into the asset part a media file becomes.</summary>
    internal static ToolResult Asset(byte[] data, string contentType, string? name = null) =>
        new([new AssetPart(new AssetRef { ContentType = contentType, Size = data.Length, Data = Convert.ToBase64String(data), Name = name })]);

    internal static JsonElement ParseJson(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
