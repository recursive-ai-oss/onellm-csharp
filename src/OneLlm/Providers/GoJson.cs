using System.Buffers;
using System.Globalization;
using System.Text;

namespace OneLlm.Providers;

/// <summary>
/// Writes JSON the way Go's <c>encoding/json</c> does, which is the byte form decider requests are specified in: strings escape
/// <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> as <c>\u003c</c>-style sequences, map keys are sorted, and every number is a
/// double printed in its shortest form. Request bytes are compared as bytes, so these details are part of the contract.
/// </summary>
internal static class GoJson
{
    private const string Hex = "0123456789abcdef";

    /// <summary>Writes a JSON string literal, with the escapes <c>encoding/json</c> uses when HTML escaping is on (its default).</summary>
    public static void WriteString(IBufferWriter<byte> output, string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        AppendString(sb, value);
        WriteAscii(output, sb);
    }

    /// <summary>Appends a JSON string literal as text.</summary>
    public static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u00").Append(Hex[c >> 4]).Append(Hex[c & 15]);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // Invalid UTF-16 becomes U+FFFD, as invalid UTF-8 does in Go.
                        sb.Append("\\ufffd");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>Writes any plain value (null, bool, string, number, list, string-keyed dictionary) as Go would marshal the decoded form of the same JSON.</summary>
    public static void WriteValue(IBufferWriter<byte> output, object? value)
    {
        var sb = new StringBuilder();
        AppendValue(sb, value);
        WriteAscii(output, sb);
    }

    /// <summary>Appends a plain value as JSON text.</summary>
    public static void AppendValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                AppendString(sb, s);
                break;
            case double d:
                AppendDouble(sb, d);
                break;
            case long l:
                // Go decodes every JSON number as a float64, so a long is printed as that float.
                AppendDouble(sb, l);
                break;
            case int i:
                AppendDouble(sb, i);
                break;
            case decimal m:
                AppendDouble(sb, (double)m);
                break;
            case System.Collections.IDictionary dict:
                {
                    // Go marshals a map with its keys in sorted order.
                    var keys = new List<string>(dict.Count);
                    foreach (object key in dict.Keys)
                    {
                        keys.Add((string)key);
                    }

                    keys.Sort(string.CompareOrdinal);
                    sb.Append('{');
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append(',');
                        }

                        AppendString(sb, keys[k]);
                        sb.Append(':');
                        AppendValue(sb, dict[keys[k]]);
                    }

                    sb.Append('}');
                    break;
                }

            case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object? item in list)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        AppendValue(sb, item);
                    }

                    sb.Append(']');
                    break;
                }

            default:
                throw new InvalidOperationException($"A value of type {value.GetType().Name} cannot be written as JSON here.");
        }
    }

    /// <summary>Appends a double as <c>encoding/json</c> prints a float64: plain decimal notation, or exponent form below 1e-6 and from 1e21.</summary>
    public static void AppendDouble(StringBuilder sb, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException("JSON cannot hold a NaN or an infinity.");
        }

        if (value == 0)
        {
            sb.Append(double.IsNegative(value) ? "-0" : "0");
            return;
        }

        (string digits, int exp10) = ShortestDigits(Math.Abs(value));
        if (value < 0)
        {
            sb.Append('-');
        }

        double abs = Math.Abs(value);
        // The value is 0.<digits> * 10^exp10.
        if (abs < 1e-6 || abs >= 1e21)
        {
            int e = exp10 - 1;
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }

            sb.Append('e').Append(e < 0 ? '-' : '+');
            int ae = Math.Abs(e);
            // Go prints at least two exponent digits and the JSON encoder then drops the leading zero of a negative one (e-07 becomes e-7).
            if (ae < 10)
            {
                if (e >= 0)
                {
                    sb.Append('0');
                }

                sb.Append((char)('0' + ae));
            }
            else
            {
                sb.Append(ae.ToString(CultureInfo.InvariantCulture));
            }

            return;
        }

        if (exp10 <= 0)
        {
            sb.Append("0.").Append('0', -exp10).Append(digits);
        }
        else if (digits.Length <= exp10)
        {
            sb.Append(digits).Append('0', exp10 - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, exp10).Append('.').Append(digits, exp10, digits.Length - exp10);
        }
    }

    // The shortest digit string that reads back as the same double, and the decimal exponent such that value = 0.<digits> * 10^exp.
    private static (string Digits, int Exp10) ShortestDigits(double abs)
    {
        // "R" is the shortest round-trip text in .NET; parse its mantissa and exponent.
        string r = abs.ToString("R", CultureInfo.InvariantCulture);
        int e = 0;
        int ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            e = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }

        int dot = r.IndexOf('.');
        string intPart = dot < 0 ? r : r[..dot];
        string frac = dot < 0 ? "" : r[(dot + 1)..];
        string all = intPart + frac;
        int exp10 = intPart.Length + e;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }

        all = all[lead..];
        exp10 -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            all = "0";
        }

        return (all, exp10);
    }

    private static void WriteAscii(IBufferWriter<byte> output, StringBuilder sb)
    {
        int max = Encoding.UTF8.GetMaxByteCount(sb.Length);
        Span<byte> span = output.GetSpan(max);
        int written = 0;
        foreach (ReadOnlyMemory<char> chunk in sb.GetChunks())
        {
            written += Encoding.UTF8.GetBytes(chunk.Span, span[written..]);
        }

        output.Advance(written);
    }

    /// <summary>Writes raw bytes.</summary>
    public static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }

    /// <summary>Writes a string of ASCII text.</summary>
    public static void WriteText(IBufferWriter<byte> output, string text)
    {
        Span<byte> span = output.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        int n = Encoding.UTF8.GetBytes(text, span);
        output.Advance(n);
    }
}
