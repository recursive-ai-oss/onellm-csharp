using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OneLlm.Templating;

/// <summary>
/// The standard function set: path, string and logic helpers every prompt can rely on, plus OneLLM's own extensions
/// (<c>toJson toPrettyJson toYaml join split indent nindent truncate now date regexMatch regexReplace dict list</c>).
/// Path functions follow Go's <c>path/filepath</c> on a POSIX system (forward slashes), so rendered text does not depend on the host OS.
/// </summary>
internal static class StandardFunctions
{
    public static readonly IReadOnlyDictionary<string, TemplateFunction> All = Build();

    private const ArgKind S = ArgKind.Text;
    private const ArgKind A = ArgKind.Any;

    private static Dictionary<string, TemplateFunction> Build()
    {
        var d = new Dictionary<string, TemplateFunction>(StringComparer.Ordinal);

        void Add(string name, ArgKind[] ps, Func<object?[], object?> fn, ArgKind? variadic = null)
            => d[name] = new TemplateFunction(name, ps, fn, variadic);

        // Literal-output escape hatch: the argument is returned unchanged.
        Add("raw", new[] { S }, a => a[0]);

        d["embed"] = new TemplateFunction("embed", new[] { S }, _ => null)
        {
            Context = (ctx, a) => ctx.Embed((string)a[0]!),
        };

        // Path functions (POSIX semantics).
        Add("base", new[] { S }, a => Base((string)a[0]!));
        Add("dir", new[] { S }, a => Dir((string)a[0]!));
        Add("ext", new[] { S }, a => Ext((string)a[0]!));
        Add("trimExt", new[] { S }, a =>
        {
            var s = (string)a[0]!;
            string e = Ext(s);
            return e.Length > 0 ? s.Substring(0, s.Length - e.Length) : s;
        });
        Add("pathJoin", Array.Empty<ArgKind>(), a => PathJoin(a), S);
        Add("isAbs", new[] { S }, a => ((string)a[0]!).StartsWith('/'));

        // Strings.
        // Go maps U+0130 to "i" and U+0131 to "I"; the invariant culture keeps both, so they are mapped first.
        Add("lower", new[] { S }, a => ((string)a[0]!).Replace("\u0130", "i", StringComparison.Ordinal).ToLowerInvariant());
        Add("upper", new[] { S }, a => ((string)a[0]!).Replace("\u0131", "I", StringComparison.Ordinal).ToUpperInvariant());
        Add("trim", new[] { S }, a => ((string)a[0]!).Trim());
        Add("trimPrefix", new[] { S, S }, a =>
        {
            var s = (string)a[0]!;
            var p = (string)a[1]!;
            return p.Length > 0 && s.StartsWith(p, StringComparison.Ordinal) ? s.Substring(p.Length) : s;
        });
        Add("trimSuffix", new[] { S, S }, a =>
        {
            var s = (string)a[0]!;
            var p = (string)a[1]!;
            return p.Length > 0 && s.EndsWith(p, StringComparison.Ordinal) ? s.Substring(0, s.Length - p.Length) : s;
        });
        Add("replace", new[] { S, S, S }, a => ReplaceAll((string)a[0]!, (string)a[1]!, (string)a[2]!));
        Add("contains", new[] { S, S }, a => ((string)a[0]!).Contains((string)a[1]!, StringComparison.Ordinal));
        Add("hasPrefix", new[] { S, S }, a => ((string)a[0]!).StartsWith((string)a[1]!, StringComparison.Ordinal));
        Add("hasSuffix", new[] { S, S }, a => ((string)a[0]!).EndsWith((string)a[1]!, StringComparison.Ordinal));

        // default <fallback> <value>: the fallback replaces nil and the empty string only (not 0 or false).
        Add("default", new[] { A, A }, a => a[1] is null || a[1] is string { Length: 0 } ? a[0] : a[1]);

        // ---- OneLLM extensions ----
        Add("toJson", new[] { A }, a => JsonText.Write(a[0], pretty: false));
        Add("toPrettyJson", new[] { A }, a => JsonText.Write(a[0], pretty: true));
        Add("toYaml", new[] { A }, a => ToYaml(a[0]));
        Add("join", new[] { S, A }, a => Join((string)a[0]!, a[1]));
        Add("split", new[] { S, S }, a => Split((string)a[0]!, (string)a[1]!));
        Add("indent", new[] { ArgKind.Whole, S }, a => Indent((long)a[0]!, (string)a[1]!));
        Add("nindent", new[] { ArgKind.Whole, S }, a => "\n" + Indent((long)a[0]!, (string)a[1]!));
        Add("truncate", new[] { ArgKind.Whole, S }, a => Truncate((long)a[0]!, (string)a[1]!));
        d["now"] = new TemplateFunction("now", Array.Empty<ArgKind>(), _ => null)
        {
            Context = (ctx, _) => (ctx.Clock ?? throw new TemplateFunctionException("no TimeProvider was given to the render, so there is no clock")).GetLocalNow(),
        };
        Add("date", new[] { S, A }, a => GoTime.Format(ToTime(a[1]), (string)a[0]!));
        Add("regexMatch", new[] { S, S }, a => GetRegex((string)a[0]!).IsMatch((string)a[1]!));
        Add("regexReplace", new[] { S, S, S }, a => RegexReplace((string)a[0]!, (string)a[1]!, (string)a[2]!));
        Add("dict", Array.Empty<ArgKind>(), a => Dict(a), A);
        Add("list", Array.Empty<ArgKind>(), a => new List<object?>(a), A);
        return d;
    }

    // ---- path ----

    public static string Base(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        path = path.TrimEnd('/');
        int i = path.LastIndexOf('/');
        if (i >= 0)
        {
            path = path.Substring(i + 1);
        }

        return path.Length == 0 ? "/" : path;
    }

    public static string Dir(string path)
    {
        int i = path.LastIndexOf('/');
        return Clean(path.Substring(0, i + 1));
    }

    public static string Ext(string path)
    {
        for (int i = path.Length - 1; i >= 0 && path[i] != '/'; i--)
        {
            if (path[i] == '.')
            {
                return path.Substring(i);
            }
        }

        return "";
    }

    public static string PathJoin(object?[] elems)
    {
        var parts = new List<string>();
        foreach (object? e in elems)
        {
            var s = (string)e!;
            if (s.Length > 0)
            {
                parts.Add(s);
            }
        }

        return parts.Count == 0 ? "" : Clean(string.Join('/', parts));
    }

    /// <summary>Lexical path cleanup (Go's <c>filepath.Clean</c> on POSIX).</summary>
    public static string Clean(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        bool rooted = path[0] == '/';
        var outParts = new List<string>();
        foreach (string seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (outParts.Count > 0 && outParts[^1] != "..")
                {
                    outParts.RemoveAt(outParts.Count - 1);
                }
                else if (!rooted)
                {
                    outParts.Add("..");
                }

                continue;
            }

            outParts.Add(seg);
        }

        string joined = string.Join('/', outParts);
        if (rooted)
        {
            return "/" + joined;
        }

        return joined.Length == 0 ? "." : joined;
    }

    // ---- strings ----

    private static string ReplaceAll(string s, string old, string neu)
    {
        if (old.Length > 0)
        {
            return s.Replace(old, neu, StringComparison.Ordinal);
        }

        // Go inserts the replacement before every character and at the end.
        var sb = new StringBuilder();
        foreach (Rune r in s.EnumerateRunes())
        {
            sb.Append(neu).Append(r.ToString());
        }

        sb.Append(neu);
        return sb.ToString();
    }

    private static string Join(string sep, object? list)
    {
        list = V.Norm(list);
        if (list is null || ReferenceEquals(list, V.Missing))
        {
            return "";
        }

        if (!V.IsList(list))
        {
            throw new TemplateFunctionException($"join needs a list, got {V.TypeName(list)}");
        }

        var sb = new StringBuilder();
        int n = V.ListCount(list);
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(sep);
            }

            GoFmt.AppendV(sb, V.ListGet(list, i));
        }

        return sb.ToString();
    }

    private static List<object?> Split(string sep, string s)
    {
        var result = new List<object?>();
        if (sep.Length == 0)
        {
            foreach (Rune r in s.EnumerateRunes())
            {
                result.Add(r.ToString());
            }

            return result;
        }

        foreach (string part in s.Split(sep, StringSplitOptions.None))
        {
            result.Add(part);
        }

        return result;
    }

    private static string Indent(long n, string s)
    {
        if (n < 0)
        {
            throw new TemplateFunctionException("indent: negative width");
        }

        if (n > 10_000)
        {
            throw new TemplateFunctionException("indent: width is too large");
        }

        string pad = new(' ', (int)n);
        return pad + s.Replace("\n", "\n" + pad, StringComparison.Ordinal);
    }

    private static string Truncate(long n, string s)
    {
        if (n <= 0)
        {
            return "";
        }

        int count = 0;
        int i = 0;
        while (i < s.Length && count < n)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            count++;
        }

        return s.Substring(0, i);
    }

    private static Dictionary<string, object?> Dict(object?[] a)
    {
        if (a.Length % 2 != 0)
        {
            throw new TemplateFunctionException("dict needs an even number of arguments (key, value, ...)");
        }

        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < a.Length; i += 2)
        {
            if (V.Norm(a[i]) is not string key)
            {
                throw new TemplateFunctionException("dict keys must be strings");
            }

            m[key] = a[i + 1];
        }

        return m;
    }

    private static string ToYaml(object? v)
    {
        // OneYaml writes the plain value model (dictionaries, lists, scalars) without reflection.
        object? plain = ToPlain(v, 0);
        return OneYaml.YamlNode.Create(plain).ToString();
    }

    private static object? ToPlain(object? v, int depth)
    {
        if (depth > 200)
        {
            throw new TemplateFunctionException("toYaml: value is nested too deeply");
        }

        v = V.Norm(v);
        if (v is null || ReferenceEquals(v, V.Missing))
        {
            return null;
        }

        if (V.IsList(v))
        {
            var l = new List<object?>();
            int n = V.ListCount(v);
            for (int i = 0; i < n; i++)
            {
                l.Add(ToPlain(V.ListGet(v, i), depth + 1));
            }

            return l;
        }

        if (V.IsMap(v))
        {
            var m = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string k in V.MapKeys(v))
            {
                V.MapTryGet(v, k, out object? val);
                m[k] = ToPlain(val, depth + 1);
            }

            return m;
        }

        if (v is DateTimeOffset t)
        {
            return t.ToString("O", CultureInfo.InvariantCulture);
        }

        return v is string or bool or long or double or byte ? v : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // RFC 3339: "Z" or a numeric offset with a colon, and an optional fraction (up to 7 digits). Like the reference parser, a
    // one-digit hour is accepted.
    private static readonly string[] Rfc3339Formats = { "yyyy-MM-dd'T'H:mm:ssK", "yyyy-MM-dd'T'H:mm:ss.FFFFFFFK" };

    private static DateTimeOffset ToTime(object? v)
    {
        v = V.Norm(v);
        switch (v)
        {
            case DateTimeOffset t:
                return t;
            case long unix:
                return DateTimeOffset.FromUnixTimeSeconds(unix);
            // A fraction needs at least one digit after the point (Go rejects "23:59:59.Z").
            case string s when s.Length > 6 && (s[^1] == 'Z' || (s[^6] is '+' or '-' && char.IsAsciiDigit(s[^5]) && char.IsAsciiDigit(s[^4]) && s[^3] == ':' && char.IsAsciiDigit(s[^2]) && char.IsAsciiDigit(s[^1]))) && !(s.Length > 20 && s[19] == '.' && !char.IsAsciiDigit(s[20])) && DateTimeOffset.TryParseExact(s, Rfc3339Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed):
                return parsed;
            default:
                throw new TemplateFunctionException("date needs a time from now, an RFC 3339 string or Unix seconds");
        }
    }

    // ---- regular expressions ----

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    private static Regex GetRegex(string pattern)
    {
        if (RegexCache.TryGetValue(pattern, out Regex? cached))
        {
            return cached;
        }

        Regex re;
        try
        {
            // Linear-time matching, so a hostile pattern or input cannot stall a render. It supports the same pattern
            // language family as Go's regexp (no backreferences or lookaround); (?P<name>) is the Go spelling of a named group.
            re = new Regex(pattern.Replace("(?P<", "(?<", StringComparison.Ordinal), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new TemplateFunctionException("invalid regular expression: " + ex.Message);
        }

        if (RegexCache.Count > 256)
        {
            RegexCache.Clear();
        }

        RegexCache[pattern] = re;
        return re;
    }

    private static string RegexReplace(string pattern, string repl, string input)
    {
        Regex re = GetRegex(pattern);
        var sb = new StringBuilder();
        int last = 0;
        int prevEnd = -1;
        foreach (Match m in re.Matches(input))
        {
            // An empty match right after the previous match is ignored, as in Go's regexp.
            if (m.Length == 0 && m.Index == prevEnd)
            {
                continue;
            }

            prevEnd = m.Index + m.Length;
            sb.Append(input, last, m.Index - last);
            Expand(sb, repl, m, re);
            last = m.Index + m.Length;
        }

        sb.Append(input, last, input.Length - last);
        return sb.ToString();
    }

    // Replacement syntax as in Go's regexp: $1, ${1}, $name, ${name}, $$. A name takes the longest run of letters, digits and underscores.
    private static void Expand(StringBuilder sb, string template, Match m, Regex re)
    {
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$' || i + 1 >= template.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (template[i + 1] == '$')
            {
                sb.Append('$');
                i += 2;
                continue;
            }

            string? name;
            int next;
            if (template[i + 1] == '{')
            {
                int close = template.IndexOf('}', i + 2);
                if (close < 0)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                name = template.Substring(i + 2, close - i - 2);
                next = close + 1;
                if (name.Length == 0 || !IsNameChars(name))
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
            }
            else
            {
                int j = i + 1;
                while (j < template.Length && (template[j] == '_' || char.IsAsciiLetterOrDigit(template[j])))
                {
                    j++;
                }

                if (j == i + 1)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                name = template.Substring(i + 1, j - i - 1);
                next = j;
            }

            Group? g = null;
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int num))
            {
                if (num < m.Groups.Count)
                {
                    g = m.Groups[num];
                }
            }
            else if (Array.IndexOf(re.GetGroupNames(), name) >= 0)
            {
                g = m.Groups[name];
            }

            if (g is { Success: true })
            {
                sb.Append(g.Value);
            }

            i = next;
        }
    }

    private static bool IsNameChars(string s)
    {
        foreach (char c in s)
        {
            if (c != '_' && !char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Compact and indented JSON for <c>toJson</c> / <c>toPrettyJson</c>. Keys are sorted; text is not HTML-escaped.</summary>
internal static class JsonText
{
    public static string Write(object? v, bool pretty)
    {
        var sb = new StringBuilder();
        WriteValue(sb, v, pretty, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? v, bool pretty, int depth)
    {
        if (depth > 200)
        {
            throw new TemplateFunctionException("toJson: value is nested too deeply");
        }

        v = V.Norm(v);
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
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
                WriteDouble(sb, d);
                return;
            case DateTimeOffset t:
                WriteString(sb, t.ToString("O", CultureInfo.InvariantCulture));
                return;
        }

        if (ReferenceEquals(v, V.Missing))
        {
            sb.Append("null");
        }
        else if (V.IsList(v))
        {
            int n = V.ListCount(v);
            if (n == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                NewLine(sb, pretty, depth + 1);
                WriteValue(sb, V.ListGet(v, i), pretty, depth + 1);
            }

            NewLine(sb, pretty, depth);
            sb.Append(']');
        }
        else if (V.IsMap(v))
        {
            string[] keys = V.MapKeys(v);
            if (keys.Length == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                NewLine(sb, pretty, depth + 1);
                WriteString(sb, keys[i]);
                sb.Append(pretty ? ": " : ":");
                V.MapTryGet(v, keys[i], out object? val);
                WriteValue(sb, val, pretty, depth + 1);
            }

            NewLine(sb, pretty, depth);
            sb.Append('}');
        }
        else
        {
            throw new TemplateFunctionException($"toJson: unsupported value of type {V.TypeName(v)}");
        }
    }

    private static void NewLine(StringBuilder sb, bool pretty, int depth)
    {
        if (pretty)
        {
            sb.Append('\n').Append(' ', depth * 2);
        }
    }

    // Number text as Go's encoding/json writes it: plain decimal unless the magnitude is below 1e-6 or at least 1e21.
    private static void WriteDouble(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new TemplateFunctionException("toJson: NaN and infinity have no JSON form");
        }

        double abs = Math.Abs(d);
        if (abs != 0 && (abs < 1e-6 || abs >= 1e21))
        {
            string e = GoFmt.FormatFloat(d, 'e', -1);
            // e-09 becomes e-9
            int ePos = e.IndexOf('e');
            if (ePos >= 0 && e.Length > ePos + 3 && e[ePos + 2] == '0')
            {
                e = e.Remove(ePos + 2, 1);
            }

            sb.Append(e);
            return;
        }

        sb.Append(GoFmt.FormatFloat(d, 'f', -1));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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
}
