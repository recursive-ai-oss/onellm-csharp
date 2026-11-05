using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

/// <summary>A long-lived reference process (the differential tool's Go program) that renders one template per line.</summary>
internal sealed class GoRef
{
    private readonly Process _p;

    public GoRef(string path)
    {
        _p = Process.Start(new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        })!;
    }

    /// <summary>Returns the rendered text, or null when the reference failed.</summary>
    public string? Render(string template, object scope)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", template);
            w.WritePropertyName("data");
            WriteValue(w, scope, 0);
            w.WriteString("missingKey", "default");
            w.WriteStartObject("embeds");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        _p.StandardInput.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
        _p.StandardInput.Flush();
        string line = _p.StandardOutput.ReadLine() ?? throw new InvalidOperationException("reference exited");
        using var d = JsonDocument.Parse(line);
        if (!d.RootElement.GetProperty("ok").GetBoolean())
        {
            return null;
        }

        return d.RootElement.TryGetProperty("out", out JsonElement o) ? o.GetString() : "";
    }

    // JSON with the number rule both engines share: integer syntax is an int, a point or exponent makes a float.
    private static string FloatText(double d)
    {
        string t = d.ToString("R", CultureInfo.InvariantCulture);
        return t.Contains('.', StringComparison.Ordinal) || t.Contains('E', StringComparison.Ordinal) ? t : t + ".0";
    }

    private static void WriteValue(Utf8JsonWriter w, object? v, int depth)
    {
        if (depth > 100)
        {
            w.WriteNullValue();
            return;
        }

        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case long l: w.WriteNumberValue(l); break;
            case int i: w.WriteNumberValue(i); break;
            case double d: w.WriteRawValue(FloatText(d)); break;
            case BigInteger bi: w.WriteRawValue(FloatText((double)bi)); break;
            case Dictionary<string, object?> m:
                w.WriteStartObject();
                foreach (var kv in m)
                {
                    w.WritePropertyName(kv.Key);
                    WriteValue(w, kv.Value, depth + 1);
                }

                w.WriteEndObject();
                break;
            case List<object?> list:
                w.WriteStartArray();
                foreach (object? e in list)
                {
                    WriteValue(w, e, depth + 1);
                }

                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }
}
