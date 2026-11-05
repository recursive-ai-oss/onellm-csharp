using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OneLlm.Templating;

/// <summary>
/// Template values: <c>null</c>, <c>string</c>, <c>bool</c>, <c>long</c> (Go's int), <c>double</c> (float64), lists, string-keyed maps,
/// <see cref="DateTimeOffset"/> and <see cref="TemplateFunction"/>. Host data may use other numeric types, <see cref="JsonElement"/>,
/// arrays, <see cref="IList"/>, <see cref="IDictionary"/> and <see cref="IReadOnlyDictionary{TKey, TValue}"/>; they are read
/// through these helpers without reflection.
/// </summary>
internal static class V
{
    /// <summary>A map lookup that found nothing. Prints as <c>&lt;no value&gt;</c>; passing it where a string is needed is an error.</summary>
    public static readonly object Missing = new MissingValue();

    private sealed class MissingValue
    {
        public override string ToString() => "<no value>";
    }

    public static object? Norm(object? v)
    {
        switch (v)
        {
            case null:
            case string:
            case bool:
            case long:
            case double:
                return v;
            case int i:
                return (long)i;
            case short s:
                return (long)s;
            case sbyte sb:
                return (long)sb;
            case byte:
                return v; // Go's uint8: what indexing a string gives. It prints as a number but is not an int argument.
            case ushort us:
                return (long)us;
            case uint ui:
                return (long)ui;
            case ulong ul:
                return ul <= long.MaxValue ? (long)ul : (double)ul;
            case float f:
                return (double)f;
            case decimal m:
                return (double)m;
            case char c:
                return c.ToString();
            case DateTime dt:
                return new DateTimeOffset(dt);
            case JsonElement je:
                switch (je.ValueKind)
                {
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return null;
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.String:
                        return je.GetString();
                    case JsonValueKind.Number:
                        // Integer syntax gives a long, anything with a point or exponent a double; the same rule the
                        // reference harness applies when it decodes JSON.
                        return je.TryGetInt64(out long l) ? (object)l : je.GetDouble();
                    default:
                        return je;
                }

            default:
                return v;
        }
    }

    public static bool IsList(object? v) => v is not string && (v is IList || (v is JsonElement je && je.ValueKind == JsonValueKind.Array));

    public static int ListCount(object v) => v switch
    {
        JsonElement je => je.GetArrayLength(),
        IList l => l.Count,
        _ => 0,
    };

    public static object? ListGet(object v, int i) => v switch
    {
        JsonElement je => Norm(je[i]),
        IList l => Norm(l[i]),
        _ => null,
    };

    public static bool IsMap(object? v) => v is IDictionary<string, object?> or IDictionary or IReadOnlyDictionary<string, object?> || (v is JsonElement je && je.ValueKind == JsonValueKind.Object);

    public static int MapCount(object v)
    {
        switch (v)
        {
            case JsonElement je:
                int n = 0;
                foreach (JsonProperty _ in je.EnumerateObject())
                {
                    n++;
                }

                return n;
            case IDictionary<string, object?> d:
                return d.Count;
            case IReadOnlyDictionary<string, object?> r:
                return r.Count;
            case IDictionary nd:
                return nd.Count;
            default:
                return 0;
        }
    }

    public static bool MapTryGet(object v, string key, out object? value)
    {
        value = null;
        switch (v)
        {
            case JsonElement je:
                if (je.TryGetProperty(key, out JsonElement p))
                {
                    value = Norm(p);
                    return true;
                }

                return false;
            case IDictionary<string, object?> d:
                if (d.TryGetValue(key, out object? x))
                {
                    value = Norm(x);
                    return true;
                }

                return false;
            case IReadOnlyDictionary<string, object?> r:
                if (r.TryGetValue(key, out object? y))
                {
                    value = Norm(y);
                    return true;
                }

                return false;
            case IDictionary nd:
                if (nd.Contains(key))
                {
                    value = Norm(nd[key]);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    public static string[] MapKeys(object v)
    {
        var keys = new List<string>();
        switch (v)
        {
            case JsonElement je:
                foreach (JsonProperty p in je.EnumerateObject())
                {
                    keys.Add(p.Name);
                }

                break;
            case IDictionary<string, object?> d:
                keys.AddRange(d.Keys);
                break;
            case IReadOnlyDictionary<string, object?> r:
                keys.AddRange(r.Keys);
                break;
            case IDictionary nd:
                foreach (object k in nd.Keys)
                {
                    if (k is string s)
                    {
                        keys.Add(s);
                    }
                }

                break;
        }

        string[] arr = keys.ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }

    /// <summary>Go's truth: false, zero, nil, and empty strings, lists and maps are false.</summary>
    public static bool IsTrue(object? v)
    {
        v = Norm(v);
        return v switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            long l => l != 0,
            byte ub => ub != 0,
            double d => d != 0,
            _ when ReferenceEquals(v, Missing) => false,
            _ when IsList(v) => ListCount(v) > 0,
            _ when IsMap(v) => MapCount(v) > 0,
            _ => true,
        };
    }

    public static string TypeName(object? v)
    {
        v = Norm(v);
        return v switch
        {
            null => "<nil>",
            string => "string",
            bool => "bool",
            long => "int",
            byte => "uint8",
            double => "float64",
            DateTimeOffset => "time.Time",
            TemplateFunction => "func",
            _ when ReferenceEquals(v, Missing) => "<nil>",
            _ when IsList(v) => "[]interface {}",
            _ when IsMap(v) => "map[string]interface {}",
            _ => v.GetType().Name,
        };
    }

    /// <summary>Byte length of a string in UTF-8, which is what Go's <c>len</c> reports.</summary>
    public static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);
}

/// <summary>Go-compatible value formatting for <c>print</c>, <c>printf</c>, <c>println</c> and action output.</summary>
internal static class GoFmt
{
    public static string Sprint(object?[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            // Sprint adds a space between operands when neither is a string.
            if (i > 0 && V.Norm(args[i]) is not string && V.Norm(args[i - 1]) is not string)
            {
                sb.Append(' ');
            }

            AppendV(sb, args[i]);
        }

        return sb.ToString();
    }

    public static string Sprintln(object?[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            AppendV(sb, args[i]);
        }

        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Appends the <c>%v</c> form of a value.</summary>
    public static void AppendV(StringBuilder sb, object? v)
    {
        v = V.Norm(v);
        switch (v)
        {
            case null:
                sb.Append("<nil>");
                return;
            case string s:
                sb.Append(s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            case byte ub:
                sb.Append(ub.ToString(CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(FormatFloat(d, 'g', -1));
                return;
            case DateTimeOffset t:
                sb.Append(GoTime.ToGoString(t));
                return;
        }

        if (ReferenceEquals(v, V.Missing))
        {
            sb.Append("<nil>");
        }
        else if (V.IsList(v))
        {
            sb.Append('[');
            int n = V.ListCount(v);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                AppendV(sb, V.ListGet(v, i));
            }

            sb.Append(']');
        }
        else if (V.IsMap(v))
        {
            sb.Append("map[");
            bool first = true;
            foreach (string k in V.MapKeys(v))
            {
                if (!first)
                {
                    sb.Append(' ');
                }

                first = false;
                sb.Append(k).Append(':');
                V.MapTryGet(v, k, out object? val);
                AppendV(sb, val);
            }

            sb.Append(']');
        }
        else
        {
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
        }
    }

    // ---- printf ----

    private struct Spec
    {
        public bool Plus, Minus, Sharp, Space, Zero;
        public int Width, Prec;
        public bool HasWidth, HasPrec;
    }

    public static string Sprintf(string format, object?[] args)
    {
        var sb = new StringBuilder();
        int argNum = 0;
        int i = 0;
        int n = format.Length;
        while (i < n)
        {
            char c = format[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            i++;
            var spec = default(Spec);
            for (; i < n; i++)
            {
                char f = format[i];
                if (f == '+')
                {
                    spec.Plus = true;
                }
                else if (f == '-')
                {
                    spec.Minus = true;
                    spec.Zero = false;
                }
                else if (f == '#')
                {
                    spec.Sharp = true;
                }
                else if (f == ' ')
                {
                    spec.Space = true;
                }
                else if (f == '0')
                {
                    spec.Zero = !spec.Minus;
                }
                else
                {
                    break;
                }
            }

            // width
            if (i < n && format[i] == '*')
            {
                i++;
                if (argNum < args.Length && V.Norm(args[argNum]) is long w)
                {
                    argNum++;
                    spec.HasWidth = true;
                    spec.Width = (int)Math.Clamp(w, -1_000_000, 1_000_000);
                    if (spec.Width < 0)
                    {
                        spec.Minus = true;
                        spec.Width = -spec.Width;
                        spec.Zero = false;
                    }
                }
                else
                {
                    argNum++;
                    sb.Append("%!(BADWIDTH)");
                }
            }
            else
            {
                int w = 0;
                bool any = false;
                while (i < n && format[i] >= '0' && format[i] <= '9')
                {
                    w = Math.Min((w * 10) + (format[i] - '0'), 1_000_000);
                    any = true;
                    i++;
                }

                if (any)
                {
                    spec.HasWidth = true;
                    spec.Width = w;
                }
            }

            // precision
            if (i + 1 < n && format[i] == '.')
            {
                i++;
                if (i < n && format[i] == '*')
                {
                    i++;
                    if (argNum < args.Length && V.Norm(args[argNum]) is long p)
                    {
                        argNum++;
                        if (p >= 0)
                        {
                            spec.HasPrec = true;
                            spec.Prec = (int)Math.Min(p, 1_000_000);
                        }
                    }
                    else
                    {
                        argNum++;
                        sb.Append("%!(BADPREC)");
                    }
                }
                else
                {
                    int p = 0;
                    while (i < n && format[i] >= '0' && format[i] <= '9')
                    {
                        p = Math.Min((p * 10) + (format[i] - '0'), 1_000_000);
                        i++;
                    }

                    spec.HasPrec = true;
                    spec.Prec = p;
                }
            }

            if (i >= n)
            {
                sb.Append("%!(NOVERB)");
                break;
            }

            int verbLen = char.IsHighSurrogate(format[i]) && i + 1 < n ? 2 : 1;
            string verbStr = format.Substring(i, verbLen);
            char verb = format[i];
            i += verbLen;

            if (verb == '%')
            {
                sb.Append('%');
                continue;
            }

            if (argNum >= args.Length)
            {
                sb.Append("%!").Append(verbStr).Append("(MISSING)");
                continue;
            }

            object? arg = V.Norm(args[argNum++]);
            if (verb == 'T')
            {
                Pad(sb, V.TypeName(arg), spec);
                continue;
            }

            FormatArg(sb, verb, verbStr, spec, arg, top: true);
        }

        if (argNum < args.Length)
        {
            sb.Append("%!(EXTRA ");
            for (int k = argNum; k < args.Length; k++)
            {
                if (k > argNum)
                {
                    sb.Append(", ");
                }

                object? a = V.Norm(args[k]);
                if (a is null)
                {
                    sb.Append("<nil>");
                }
                else
                {
                    sb.Append(V.TypeName(a)).Append('=');
                    AppendV(sb, a);
                }
            }

            sb.Append(')');
        }

        return sb.ToString();
    }

    // Go prints the offending value with %v but keeps the width and precision of the failed verb.
    private static void BadVerb(StringBuilder sb, string verb, object? arg, Spec spec = default)
    {
        sb.Append("%!").Append(verb).Append('(');
        if (arg is null)
        {
            sb.Append("<nil>");
        }
        else
        {
            sb.Append(V.TypeName(arg)).Append('=');
            FormatArg(sb, 'v', "v", spec, arg, top: false);
        }

        sb.Append(')');
    }

    private static void FormatArg(StringBuilder sb, char verb, string verbStr, Spec spec, object? arg, bool top)
    {
        arg = V.Norm(arg);
        if (ReferenceEquals(arg, V.Missing))
        {
            arg = null;
        }

        switch (arg)
        {
            case null:
                if (verb == 'v' || !top)
                {
                    Pad(sb, "<nil>", spec);
                }
                else
                {
                    BadVerb(sb, verbStr, null);
                }

                return;
            case string s:
                FormatString(sb, verb, verbStr, spec, s);
                return;
            case bool b:
                if (verb is 'v' or 't')
                {
                    Pad(sb, b ? "true" : "false", spec);
                }
                else
                {
                    BadVerb(sb, verbStr, arg, spec);
                }

                return;
            case long l:
                FormatInt(sb, verb, verbStr, spec, l);
                return;
            case byte ub:
                FormatInt(sb, verb, verbStr, spec, ub, "uint8");
                return;
            case double d:
                FormatFloatVerb(sb, verb, verbStr, spec, d);
                return;
            case DateTimeOffset t:
                if (verb is 'v' or 's')
                {
                    Pad(sb, GoTime.ToGoString(t), spec);
                }
                else
                {
                    BadVerb(sb, verbStr, arg, spec);
                }

                return;
        }

        if (V.IsList(arg))
        {
            sb.Append('[');
            int n = V.ListCount(arg);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                FormatArg(sb, verb, verbStr, spec, V.ListGet(arg, i), top: false);
            }

            sb.Append(']');
            return;
        }

        if (V.IsMap(arg))
        {
            sb.Append("map[");
            bool first = true;
            foreach (string k in V.MapKeys(arg))
            {
                if (!first)
                {
                    sb.Append(' ');
                }

                first = false;
                FormatArg(sb, verb, verbStr, spec, k, top: false);
                sb.Append(':');
                V.MapTryGet(arg, k, out object? val);
                FormatArg(sb, verb, verbStr, spec, val, top: false);
            }

            sb.Append(']');
            return;
        }

        _ = top;
        if (verb is 'v' or 's')
        {
            Pad(sb, Convert.ToString(arg, CultureInfo.InvariantCulture) ?? "", spec);
        }
        else
        {
            BadVerb(sb, verbStr, arg, spec);
        }
    }

    private static void FormatString(StringBuilder sb, char verb, string verbStr, Spec spec, string s)
    {
        switch (verb)
        {
            case 'v':
            case 's':
                Pad(sb, spec.HasPrec ? TruncateRunes(s, spec.Prec) : s, spec);
                return;
            case 'q':
                Pad(sb, GoQuoteWriter.Quote(spec.HasPrec ? TruncateRunes(s, spec.Prec) : s), spec);
                return;
            case 'x':
            case 'X':
            {
                var hex = new StringBuilder();
                foreach (byte b in Encoding.UTF8.GetBytes(s))
                {
                    hex.Append(b.ToString(verb == 'x' ? "x2" : "X2", CultureInfo.InvariantCulture));
                }

                Pad(sb, hex.ToString(), spec);
                return;
            }

            default:
                BadVerb(sb, verbStr, s, spec);
                return;
        }
    }

    private static string TruncateRunes(string s, int max)
    {
        int count = 0;
        int i = 0;
        while (i < s.Length && count < max)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            count++;
        }

        return s.Substring(0, i);
    }

    private static void FormatInt(StringBuilder sb, char verb, string verbStr, Spec spec, long v, string? typeName = null)
    {
        string digits;
        bool neg = v < 0;
        ulong mag = neg ? (ulong)(-(v + 1)) + 1UL : (ulong)v;
        switch (verb)
        {
            case 'v':
            case 'd':
                digits = mag.ToString(CultureInfo.InvariantCulture);
                break;
            case 'b':
                digits = Convert.ToString((long)mag, 2);
                break;
            case 'o':
            case 'O':
                digits = Convert.ToString((long)mag, 8);
                if (verb == 'O')
                {
                    digits = "0o" + digits;
                }
                else if (spec.Sharp)
                {
                    digits = "0" + digits;
                }

                break;
            case 'x':
                digits = mag.ToString("x", CultureInfo.InvariantCulture);
                if (spec.Sharp)
                {
                    digits = "0x" + digits;
                }

                break;
            case 'X':
                digits = mag.ToString("X", CultureInfo.InvariantCulture);
                if (spec.Sharp)
                {
                    digits = "0X" + digits;
                }

                break;
            case 'c':
                Pad(sb, v >= 0 && v <= 0x10FFFF && !(v >= 0xD800 && v <= 0xDFFF) ? char.ConvertFromUtf32((int)v) : "\uFFFD", spec);
                return;
            case 'q':
                Pad(sb, GoQuoteWriter.QuoteRune(v), spec);
                return;
            case 'U':
                Pad(sb, "U+" + mag.ToString("X4", CultureInfo.InvariantCulture), spec);
                return;
            default:
                BadVerb(sb, verbStr, typeName is null ? v : (byte)v, spec);
                return;
        }

        if (spec.HasPrec)
        {
            if (digits.Length < spec.Prec)
            {
                digits = new string('0', spec.Prec - digits.Length) + digits;
            }

            if (spec.Prec == 0 && v == 0)
            {
                digits = "";
            }
        }

        string sign = neg ? "-" : spec.Plus ? "+" : spec.Space ? " " : "";
        if (spec.Zero && spec.HasWidth && !spec.Minus && !spec.HasPrec)
        {
            int padLen = spec.Width - sign.Length - digits.Length;
            if (padLen > 0)
            {
                digits = new string('0', padLen) + digits;
            }

            sb.Append(sign).Append(digits);
            return;
        }

        Pad(sb, sign + digits, spec with { Zero = false });
    }

    private static void FormatFloatVerb(StringBuilder sb, char verb, string verbStr, Spec spec, double d)
    {
        char fmt;
        int prec = spec.HasPrec ? spec.Prec : -1;
        switch (verb)
        {
            case 'v':
                fmt = 'g';
                break;
            case 'x':
            case 'X':
            {
                string hex = HexFloat(Math.Abs(d), verb == 'X');
                bool negative = d < 0 || (d == 0 && double.IsNegative(d));
                Pad(sb, (negative ? "-" : spec.Plus ? "+" : "") + hex, spec with { Zero = false });
                return;
            }

            case 'e':
            case 'E':
            case 'f':
            case 'F':
            case 'g':
            case 'G':
                fmt = char.ToLowerInvariant(verb);
                if (prec < 0 && fmt != 'g')
                {
                    prec = 6;
                }

                break;
            default:
                BadVerb(sb, verbStr, d, spec);
                return;
        }

        string body = FormatFloat(Math.Abs(d), fmt, prec);
        if (double.IsNaN(d))
        {
            body = "NaN";
        }

        if (verb is 'E' or 'G')
        {
            body = body.ToUpperInvariant();
        }

        bool neg = d < 0 || (d == 0 && double.IsNegative(d));
        if (double.IsPositiveInfinity(d))
        {
            body = "Inf";
            neg = false;
        }

        string sign = neg ? "-" : spec.Plus || double.IsPositiveInfinity(d) && spec.Plus ? "+" : spec.Space ? " " : "";
        if (double.IsPositiveInfinity(d) && verb == 'v')
        {
            sign = "+";
        }

        if (double.IsNaN(d))
        {
            sign = spec.Plus ? "+" : spec.Space ? " " : "";
        }

        if (spec.Zero && spec.HasWidth && !spec.Minus && !double.IsInfinity(d) && !double.IsNaN(d))
        {
            int padLen = spec.Width - sign.Length - body.Length;
            if (padLen > 0)
            {
                body = new string('0', padLen) + body;
            }

            sb.Append(sign).Append(body);
            return;
        }

        Pad(sb, sign + body, spec with { Zero = false });
    }

    // %x on a float: 0x1.8p+00, the shortest mantissa that is exact.
    private static string HexFloat(double d, bool upper)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return double.IsNaN(d) ? "NaN" : "+Inf";
        }

        string text;
        if (d == 0)
        {
            text = "0x0p+00";
        }
        else
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            int exp = (int)((bits >> 52) & 0x7FF);
            long mant = bits & 0xFFFFFFFFFFFFFL;
            if (exp == 0)
            {
                // subnormal: normalise so the leading digit is 1
                exp = -1022;
                while ((mant & (1L << 52)) == 0)
                {
                    mant <<= 1;
                    exp--;
                }

                mant &= 0xFFFFFFFFFFFFFL;
            }
            else
            {
                exp -= 1023;
            }

            string frac = mant.ToString("x13", CultureInfo.InvariantCulture).TrimEnd('0');
            text = "0x1" + (frac.Length > 0 ? "." + frac : "") + "p" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("D2", CultureInfo.InvariantCulture);
        }

        return upper ? text.ToUpperInvariant() : text;
    }

    private static void Pad(StringBuilder sb, string s, Spec spec)
    {
        if (!spec.HasWidth)
        {
            sb.Append(s);
            return;
        }

        int runes = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsLowSurrogate(s[i]))
            {
                runes++;
            }
        }

        int pad = spec.Width - runes;
        if (pad <= 0)
        {
            sb.Append(s);
        }
        else if (spec.Minus)
        {
            sb.Append(s).Append(' ', pad);
        }
        else
        {
            sb.Append(spec.Zero ? '0' : ' ', pad).Append(s);
        }
    }

    // ---- float text ----

    /// <summary>
    /// Formats a non-negative or negative double like Go's strconv.FormatFloat: <paramref name="fmt"/> is 'e', 'f' or 'g',
    /// <paramref name="prec"/> is -1 for the shortest text that round-trips.
    /// </summary>
    public static string FormatFloat(double d, char fmt, int prec)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "+Inf" : "-Inf";
        }

        string sign = d < 0 || (d == 0 && double.IsNegative(d)) ? "-" : "";
        d = Math.Abs(d);
        string digits;
        int dp; // value = 0.digits * 10^dp
        if (d == 0)
        {
            digits = "0";
            dp = 1;
            if (prec >= 0 && fmt != 'g')
            {
                digits = "0";
            }
        }
        else if (prec < 0)
        {
            ShortestDigits(d, out digits, out dp);
        }
        else
        {
            int sig = fmt switch
            {
                'e' => prec + 1,
                'g' => prec == 0 ? 1 : prec,
                _ => 0,
            };
            if (fmt == 'f')
            {
                string f = d.ToString("F" + prec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return sign + f;
            }

            string e = d.ToString("E" + (sig - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            int ePos = e.IndexOf('E');
            digits = e.Substring(0, ePos).Replace(".", "", StringComparison.Ordinal);
            dp = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) + 1;
        }

        switch (fmt)
        {
            case 'e':
                return sign + FmtE(digits, dp, prec < 0 ? digits.Length - 1 : prec);
            case 'f':
                return sign + FmtF(digits, dp, prec < 0 ? Math.Max(digits.Length - dp, 0) : prec);
            default:
            {
                int eprec = prec;
                if (eprec > digits.Length && digits.Length >= dp)
                {
                    eprec = digits.Length;
                }

                if (prec < 0)
                {
                    eprec = 6;
                }

                if (d == 0)
                {
                    return sign + "0";
                }

                // %e is used if the exponent is below -4 or at least the precision.
                int exp = dp - 1;
                string trimmed = digits.TrimEnd('0');
                if (trimmed.Length == 0)
                {
                    trimmed = "0";
                }

                if (exp < -4 || exp >= eprec)
                {
                    return sign + FmtE(trimmed, dp, trimmed.Length - 1);
                }

                return sign + FmtF(trimmed, dp, Math.Max(trimmed.Length - dp, 0));
            }
        }
    }

    private static string FmtE(string digits, int dp, int prec)
    {
        var sb = new StringBuilder();
        sb.Append(digits[0]);
        if (prec > 0)
        {
            sb.Append('.');
            for (int i = 1; i <= prec; i++)
            {
                sb.Append(i < digits.Length ? digits[i] : '0');
            }
        }

        int exp = digits == "0" ? 0 : dp - 1;
        sb.Append('e').Append(exp < 0 ? '-' : '+');
        int ae = Math.Abs(exp);
        if (ae < 10)
        {
            sb.Append('0');
        }

        sb.Append(ae.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string FmtF(string digits, int dp, int prec)
    {
        var sb = new StringBuilder();
        if (dp > 0)
        {
            for (int i = 0; i < dp; i++)
            {
                sb.Append(i < digits.Length ? digits[i] : '0');
            }
        }
        else
        {
            sb.Append('0');
        }

        if (prec > 0)
        {
            sb.Append('.');
            for (int i = 0; i < prec; i++)
            {
                int j = dp + i;
                sb.Append(j < 0 || j >= digits.Length ? '0' : digits[j]);
            }
        }

        return sb.ToString();
    }

    private static void ShortestDigits(double d, out string digits, out int dp)
    {
        // "R" gives the shortest text that round-trips; split it into digits and a decimal exponent.
        string r = d.ToString("R", CultureInfo.InvariantCulture);
        int exp10 = 0;
        int ePos = r.AsSpan().IndexOfAny('E', 'e');
        string mant = r;
        if (ePos >= 0)
        {
            exp10 = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mant = r.Substring(0, ePos);
        }

        int dot = mant.IndexOf('.');
        string intPart = dot < 0 ? mant : mant.Substring(0, dot);
        string frac = dot < 0 ? "" : mant.Substring(dot + 1);
        string all = intPart + frac;
        int point = intPart.Length + exp10;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }

        all = all.Substring(lead);
        point -= lead;
        digits = all.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        dp = point;
    }
}

/// <summary>Go's strconv.Quote and QuoteRune for <c>%q</c>.</summary>
internal static class GoQuoteWriter
{
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (Rune r in s.EnumerateRunes())
        {
            AppendRune(sb, r.Value, '"');
        }

        sb.Append('"');
        return sb.ToString();
    }

    public static string QuoteRune(long v)
    {
        var sb = new StringBuilder();
        sb.Append('\'');
        AppendRune(sb, v is >= 0 and <= 0x10FFFF && !(v is >= 0xD800 and <= 0xDFFF) ? (int)v : 0xFFFD, '\'');
        sb.Append('\'');
        return sb.ToString();
    }

    private static void AppendRune(StringBuilder sb, int r, char quote)
    {
        if (r == quote || r == '\\')
        {
            sb.Append('\\').Append((char)r);
            return;
        }

        switch (r)
        {
            case 7: sb.Append("\\a"); return;
            case 8: sb.Append("\\b"); return;
            case 12: sb.Append("\\f"); return;
            case 10: sb.Append("\\n"); return;
            case 13: sb.Append("\\r"); return;
            case 9: sb.Append("\\t"); return;
            case 11: sb.Append("\\v"); return;
        }

        if (r < 0x20 || r == 0x7f)
        {
            sb.Append("\\x").Append(r.ToString("x2", CultureInfo.InvariantCulture));
        }
        else if (IsPrint(r))
        {
            sb.Append(char.ConvertFromUtf32(r));
        }
        else if (r < 0x10000)
        {
            sb.Append("\\u").Append(r.ToString("x4", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("\\U").Append(r.ToString("x8", CultureInfo.InvariantCulture));
        }
    }

    internal static bool IsPrint(int r)
    {
        if (r == ' ')
        {
            return true;
        }

        switch (CharUnicodeInfo.GetUnicodeCategory(r))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
            case UnicodeCategory.MathSymbol:
            case UnicodeCategory.CurrencySymbol:
            case UnicodeCategory.ModifierSymbol:
            case UnicodeCategory.OtherSymbol:
                return true;
            default:
                return false;
        }
    }
}
