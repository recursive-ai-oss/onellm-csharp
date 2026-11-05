using System.Text.Json;

namespace OneLlm.TemplateDiff;

/// <summary>
/// Generates templates and data for the differential run. Most expressions are well-typed against a fixed data shape so that
/// many cases render; a share is deliberately wrong (unknown fields, wrong types, undefined variables, broken syntax) so
/// error behaviour is compared too.
/// </summary>
internal sealed class Gen
{
    private enum T { S, I, F, B, L, M, A }

    private readonly Random _r;
    private readonly List<string> _vars = new();
    private int _depth;
    private int _rangeDepth;
    private int _defCount;
    private int _stmtDepth;
    private readonly List<string> _defs = new();
    private bool _topLevel = true;

    public Gen(int seed)
    {
        _r = new Random(seed);
    }

    private bool P(double p) => _r.NextDouble() < p;

    private T1 Pick<T1>(params T1[] xs) => xs[_r.Next(xs.Length)];

    // ---- data ----

    private static readonly string[] Strs = { "", " ", "hello", "Hello World", "a/b/c.txt", "/abs/path.tar.gz", "x,y,,z", "é☃ü", "  padded  ", "line1\nline2", "<b>&\"'</b>", "a.b", "..", "42", "tab\there" };

    public Case NextCase()
    {
        _vars.Clear();
        _defs.Clear();
        _depth = 0;
        _rangeDepth = 0;
        _defCount = 0;
        _stmtDepth = 0;
        _topLevel = true;
        bool light;
        var c = new Case { DataJson = Data() };
        c.MissingError = P(0.12);
        light = P(0.5);
        if (P(0.3))
        {
            c.Embeds["e1"] = P(0.7) ? Text(2) : "{{ .name }} and {{ .n }}";
            c.Embeds["e2"] = P(0.5) ? "{{ embed \"e1\" }}!" : "plain";
            c.Embeds["cyc"] = "{{ embed \"cyc\" }}";
            c.Embeds["bad"] = "{{ .name | nosuchfunc }}";
        }

        c.Text = light ? Text(1) : Text(P(0.3) ? 4 : 2);
        if (P(0.08))
        {
            c.Text += Pick(TailPool);
        }

        if (P(0.04))
        {
            c.Text = Mangle(c.Text);
        }

        return c;
    }

    private string Mangle(string s)
    {
        if (s.Length == 0)
        {
            return "{{";
        }

        int i = _r.Next(s.Length);
        return P(0.5) ? s.Remove(i, Math.Min(_r.Next(1, 4), s.Length - i)) : s.Insert(i, Pick("{{", "}}", "(", ")", "\"", "|", "{{end}}", "{{else}}"));
    }

    private string Data()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            void Key(string k, Action write)
            {
                double roll = _r.NextDouble();
                if (roll < 0.06)
                {
                    return; // missing
                }

                w.WritePropertyName(k);
                if (roll < 0.1)
                {
                    w.WriteNullValue();
                    return;
                }

                write();
            }

            Key("name", () => w.WriteStringValue(Pick(Strs)));
            Key("s", () => w.WriteStringValue(Pick(Strs)));
            Key("n", () => w.WriteNumberValue(Pick(0, 1, 2, 3, 1, 2, -1, 7)));
            Key("f", () => w.WriteRawValue(Pick("0.5", "1.5", "2.0", "1e6", "123456789.5", "0.000001", "1e21", "100000.0", "1.0e-7", "0.0", "-3.25", "0.1", "1e100")));
            Key("ok", () => w.WriteBooleanValue(P(0.5)));
            Key("items", () =>
            {
                w.WriteStartArray();
                bool ints = P(0.5);
                int n = _r.Next(0, 5);
                for (int i = 0; i < n; i++)
                {
                    if (ints)
                    {
                        w.WriteNumberValue(_r.Next(-3, 20));
                    }
                    else
                    {
                        w.WriteStringValue(Pick(Strs));
                    }
                }

                w.WriteEndArray();
            });
            Key("rows", () =>
            {
                w.WriteStartArray();
                int n = _r.Next(0, 4);
                for (int i = 0; i < n; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", i);
                    w.WriteString("name", Pick(Strs));
                    w.WriteStartArray("tags");
                    for (int j = 0; j < _r.Next(0, 3); j++)
                    {
                        w.WriteStringValue(Pick("a", "b", "c"));
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            });
            Key("m", () =>
            {
                w.WriteStartObject();
                foreach (string k in new[] { "k1", "k2", "z", "a" })
                {
                    if (P(0.6))
                    {
                        if (P(0.5))
                        {
                            w.WriteNumber(k, _r.Next(0, 9));
                        }
                        else
                        {
                            w.WriteString(k, Pick(Strs));
                        }
                    }
                }

                w.WriteEndObject();
            });
            Key("nested", () =>
            {
                w.WriteStartObject();
                w.WriteStartObject("x");
                w.WriteString("y", Pick(Strs));
                w.WriteEndObject();
                w.WriteEndObject();
            });
            if (P(0.3))
            {
                w.WriteNull("nul");
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---- statements ----

    private string Text(int depth)
    {
        var sb = new StringBuilder();
        int n = _r.Next(1, 6);
        for (int i = 0; i < n; i++)
        {
            sb.Append(Stmt(depth));
        }

        return sb.ToString();
    }

    private static readonly string[] TextPool =
    {
        "", " ", "\n", "x", "  a  ", "é☃", "}} ", "{ ", "{ { ", "hello ", "\n\n", "\t", "a\r\nb",
        "\n```\n{{ .name }} {{\n```\n", "`{{.x}}` ", "\n~~~\n}}\n~~~\n", "``a`{{b}}`` ", "    ```\n{{x}}\n    ```\n", "\n```go\nvar x = {{ \"{{\" }}\n```\n", "\n```\n{{ .n }}\n```\n",
    };

    // Fences that never close swallow the rest of the text, so they only appear at the very end.
    private static readonly string[] TailPool = { "```go\nvar x = {{ \"{{\" }}\n", "\n```\n{{ .n }}\n```\n```\n{{", "~~~ \n}}", "`unclosed {{ .name }}" };

    private string Stmt(int depth)
    {
        double roll = _r.NextDouble();
        if (roll < 0.22 || depth <= 0)
        {
            return roll < 0.12 ? Pick(TextPool) : Pick(TextPool) + (P(0.5) ? Act() : "");
        }

        if (roll < 0.55)
        {
            return Act();
        }

        if (roll < 0.62)
        {
            return Trim("{{/* " + Pick("a comment", "", "x }} y") + " */}}");
        }

        if (roll < 0.70)
        {
            return DeclAct();
        }

        if (roll < 0.78)
        {
            return IfBlock(depth);
        }

        if (roll < 0.88)
        {
            return RangeBlock(depth);
        }

        if (roll < 0.94)
        {
            return WithBlock(depth);
        }

        return DefineAndCall(depth);
    }

    private string Trim(string action)
    {
        if (P(0.25) && action.StartsWith("{{", StringComparison.Ordinal) && action.Length > 4)
        {
            action = "{{- " + action.Substring(2).TrimStart(' ');
        }

        if (P(0.25) && action.EndsWith("}}", StringComparison.Ordinal) && action.Length > 4)
        {
            action = action.Substring(0, action.Length - 2).TrimEnd(' ') + " -}}";
        }

        return action;
    }

    private string Act() => Trim("{{ " + Pipeline(Pick(T.S, T.S, T.I, T.A, T.B, T.L, T.M, T.F)) + " }}");

    private string DeclAct()
    {
        string name = "$" + Pick("v", "w", "x", "i", "e");
        T t = Pick(T.S, T.I, T.A, T.L, T.B);
        if (_vars.Contains(name) && P(0.5))
        {
            return Trim("{{ " + name + " = " + Expr(t, 2) + " }}");
        }

        string a = Trim("{{ " + name + " := " + Expr(t, 2) + " }}");
        if (!_vars.Contains(name))
        {
            _vars.Add(name);
        }

        return a;
    }

    private string Body(int depth)
    {
        int mark = _vars.Count;
        _stmtDepth++;
        string s = Text(depth - 1);
        _stmtDepth--;
        _vars.RemoveRange(mark, _vars.Count - mark);
        return s;
    }

    private string IfBlock(int depth)
    {
        int mark = _vars.Count;
        var sb = new StringBuilder();
        sb.Append(Trim("{{ if " + Expr(Pick(T.B, T.A, T.S, T.I), 2) + " }}"));
        sb.Append(Body(depth));
        int elifs = P(0.3) ? _r.Next(1, 3) : 0;
        for (int i = 0; i < elifs; i++)
        {
            sb.Append(Trim("{{ else if " + Expr(T.B, 2) + " }}"));
            sb.Append(Body(depth));
        }

        if (P(0.5))
        {
            sb.Append(Trim("{{ else }}"));
            sb.Append(Body(depth));
        }

        sb.Append(Trim("{{ end }}"));
        _vars.RemoveRange(Math.Min(mark, _vars.Count), _vars.Count - Math.Min(mark, _vars.Count));
        return sb.ToString();
    }

    private string RangeBlock(int depth)
    {
        int mark = _vars.Count;
        var sb = new StringBuilder();
        string head;
        double roll = _r.NextDouble();
        string src = P(0.15) ? Expr(T.M, 2) : P(0.2) ? Expr(T.I, 1) : Expr(T.L, 2);
        if (roll < 0.35)
        {
            head = "range " + src;
        }
        else if (roll < 0.65)
        {
            head = "range $e := " + src;
        }
        else
        {
            head = "range $i, $e := " + src;
        }

        sb.Append(Trim("{{ " + head + " }}"));
        if (head.Contains("$e", StringComparison.Ordinal))
        {
            _vars.Add("$e");
        }

        if (head.Contains("$i", StringComparison.Ordinal))
        {
            _vars.Add("$i");
        }

        _rangeDepth++;
        sb.Append(Body(depth));
        if (P(0.3))
        {
            sb.Append(Trim("{{ if " + Expr(T.B, 1) + " }}{{ " + Pick("break", "continue") + " }}{{ end }}"));
        }

        _rangeDepth--;
        if (P(0.3))
        {
            sb.Append(Trim("{{ else }}"));
            sb.Append(Body(depth));
        }

        sb.Append(Trim("{{ end }}"));
        _vars.RemoveRange(mark, _vars.Count - mark);
        return sb.ToString();
    }

    private string WithBlock(int depth)
    {
        int mark = _vars.Count;
        var sb = new StringBuilder();
        bool decl = P(0.3);
        sb.Append(Trim("{{ with " + (decl ? "$w := " : "") + Expr(Pick(T.M, T.A, T.S, T.L), 2) + " }}"));
        if (decl)
        {
            _vars.Add("$w");
        }

        sb.Append(Body(depth));
        if (P(0.3))
        {
            sb.Append(Trim("{{ else with " + Expr(T.A, 1) + " }}"));
            sb.Append(Body(depth));
        }

        if (P(0.3))
        {
            sb.Append(Trim("{{ else }}"));
            sb.Append(Body(depth));
        }

        sb.Append(Trim("{{ end }}"));
        _vars.RemoveRange(mark, _vars.Count - mark);
        return sb.ToString();
    }

    private string DefineAndCall(int depth)
    {
        if (!_topLevel || (_stmtDepth > 0 && !P(0.03)))
        {
            return Act();
        }

        string name = "t" + _defCount++;
        _defs.Add(name);
        var saved = new List<string>(_vars);
        _vars.Clear();
        bool top = _topLevel;
        _topLevel = false;
        string body = Text(Math.Max(depth - 1, 0));
        _topLevel = top;
        _vars.AddRange(saved);
        string def = Trim("{{ define \"" + name + "\" }}") + body + Trim("{{ end }}");
        string call = Trim("{{ template \"" + name + "\" " + (P(0.7) ? Expr(Pick(T.A, T.M, T.S), 2) : "") + " }}");
        return P(0.5) ? def + call : call + def;
    }

    // ---- expressions ----

    private string Pipeline(T t)
    {
        string e = Expr(t, 3);
        if (P(0.25))
        {
            // pipe into a one-argument-last function
            return e + " | " + Pick("lower", "upper", "trim", "printf \"[%v]\"", "printf \"%q\"", "printf \"%5.2f\"", "html", "js", "urlquery", "len", "toJson", "print", "println", "base", "default \"dflt\"", "truncate 3", "indent 2", "join \",\"", "not", "raw", "trimPrefix \"h\"", "replace \"l\" \"L\"", "split \",\"", "regexMatch \"^h\"", "contains \"o\"");
        }

        return e;
    }

    private string Expr(T t, int d)
    {
        _depth++;
        try
        {
            if (P(0.004))
            {
                return Wrong();
            }

            return d <= 0 || P(0.35) ? Atom(t) : Call(t, d);
        }
        finally
        {
            _depth--;
        }
    }

    private string Wrong()
    {
        return Pick(".missing", ".missing.deeper", ".nul.x", "$undefined", "nosuch 1", "(", ")", "1 2", "\"unterminated", ".name.x", ".n.x", ". .", "len", "index .items", "nil", "and", "5 | 6", "{{", "printf", "lower 5", "len 5", "eq 1 \"a\"", "lt nil 1", "slice .name 5", "index .m 1", "call .name", "break", "template", "else", "end", "-}}");
    }

    private string Atom(T t)
    {
        double roll = _r.NextDouble();
        switch (t)
        {
            case T.S:
                if (roll < 0.35)
                {
                    return Pick(".name", ".s", ".nested.x.y", ".m.k1", ".m.z", ".nul", ".missing", ".name", ".s");
                }

                if (roll < 0.45 && _vars.Count > 0)
                {
                    return Pick(_vars.ToArray());
                }

                if (roll < 0.5 && _rangeDepth > 0)
                {
                    return ".";
                }

                return Lit(Pick(Strs));
            case T.I:
                return roll < 0.25 ? Pick(".n", ".n", ".m.k1") : roll < 0.3 ? ".missing" : roll < 0.4 && _vars.Count > 0 ? Pick(_vars.ToArray()) : roll < 0.8 ? Pick("0", "1", "2", "1", "0") : Pick("3", "-1", "5", "10", "0x10", "1_000", "'a'", "007");
            case T.F:
                return roll < 0.5 ? ".f" : Pick("1.5", "0.5", "2.0", "1e3", "1e6", ".5", "-0.25", "3.0", "1e21", "100000.0");
            case T.B:
                return roll < 0.4 ? Pick(".ok", ".missing", ".nul") : Pick("true", "false", ".ok", "nil");
            case T.L:
                return roll < 0.6 ? Pick(".items", ".rows", ".items", ".items", ".rows", ".nul", ".m") : roll < 0.6 && _vars.Count > 0 ? Pick(_vars.ToArray()) : "(list 1 2 3)";
            case T.M:
                return roll < 0.6 ? Pick(".m", ".nested", ".nested.x", ".rows", ".", "$") : "(dict \"a\" 1 \"b\" \"two\")";
            default:
                T u = Pick(T.S, T.I, T.F, T.B, T.L, T.M);
                return Atom(u);
        }
    }

    private static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", _ => c.ToString() });
        }

        return sb.Append('"').ToString();
    }

    private string Call(T t, int d)
    {
        string S(int dd = 0) => Expr(T.S, d - 1);
        string I() => Expr(T.I, d - 1);
        string B() => Expr(T.B, d - 1);
        string L() => Expr(T.L, d - 1);
        string A() => Expr(T.A, d - 1);
        string Wrap(string s) => "(" + s + ")";

        switch (t)
        {
            case T.S:
                switch (_r.Next(30))
                {
                    case 0: return Wrap("lower " + S());
                    case 1: return Wrap("upper " + S());
                    case 2: return Wrap("trim " + S());
                    case 3: return Wrap("base " + S());
                    case 4: return Wrap("dir " + S());
                    case 5: return Wrap("ext " + S());
                    case 6: return Wrap("trimExt " + S());
                    case 7: return Wrap("trimPrefix " + S() + " " + S());
                    case 8: return Wrap("trimSuffix " + S() + " " + S());
                    case 9: return Wrap("replace " + S() + " " + S() + " " + S());
                    case 10: return Wrap("printf " + Lit(Pick("%v", "%s", "%d", "%q", "%5d|%-5d|", "%x", "%t", "%08.3f", "%e", "%g", "%v %v", "%%", "%c", "%U", "%5s|", "%.2s", "%+d", "%T", "%v-%v-%v", "%z", "%")) + " " + A() + (P(0.3) ? " " + A() : ""));
                    case 11: return Wrap("print " + A() + " " + A());
                    case 12: return Wrap("println " + A());
                    case 13: return Wrap("html " + A());
                    case 14: return Wrap("js " + A());
                    case 15: return Wrap("urlquery " + A());
                    case 16: return Wrap("toJson " + A());
                    case 17: return Wrap("toPrettyJson " + A());
                    case 18: return Wrap("join " + S() + " " + L());
                    case 19: return Wrap("indent " + I() + " " + S());
                    case 20: return Wrap("nindent " + I() + " " + S());
                    case 21: return Wrap("truncate " + I() + " " + S());
                    case 22: return Wrap("regexReplace " + Lit(Pick("l+", "^h", "[aeiou]", "(\\w)o", "x*", "\\s+", "(?i)H", "(?P<w>l)")) + " " + Lit(Pick("-", "$1!", "${1}x", "$0", "<$w>", "$$", "")) + " " + S());
                    case 23: return Wrap("pathJoin " + S() + " " + S() + (P(0.3) ? " " + S() : ""));
                    case 24: return Wrap("default " + S() + " " + S());
                    case 25: return Wrap("index " + L() + " " + I());
                    case 26: return Wrap("raw " + S());
                    case 27: return Wrap("slice " + S() + " " + I() + (P(0.5) ? " " + I() : ""));
                    case 28: return Wrap("date " + Lit(Pick("2006-01-02", "Jan _2 15:04:05", "Monday, 02-Jan-06 3:04PM", "2006-01-02T15:04:05.000Z07:00", "MST -0700", "002 .999", "January 2, 2006")) + " " + Lit(Pick("2024-03-05T14:07:09Z", "2020-12-31T23:59:59.123456+05:30", "1999-01-01T00:00:00-08:00")));
                    default:
                        if (P(0.5) && _defs.Count == 0)
                        {
                            return Wrap("embed " + Lit(Pick("e1", "e2", "cyc", "bad", "nope")));
                        }

                        return Wrap("toJson " + A());
                }

            case T.I:
                switch (_r.Next(8))
                {
                    case 0: return Wrap("len " + S());
                    case 1: return Wrap("len " + L());
                    case 2: return Wrap("len " + Expr(T.M, d - 1));
                    case 3: return Wrap("index " + L() + " " + I());
                    case 4: return Wrap("default " + I() + " " + I());
                    case 5: return Wrap("index " + Expr(T.S, d - 1) + " " + I());
                    case 6: return Wrap("or " + I() + " " + I());
                    default: return Wrap("len " + A());
                }

            case T.B:
                switch (_r.Next(14))
                {
                    case 0: return Wrap("eq " + A() + " " + A());
                    case 1: return Wrap("eq " + S() + " " + S() + " " + S());
                    case 2: return Wrap("ne " + I() + " " + I());
                    case 3: return Wrap("lt " + I() + " " + I());
                    case 4: return Wrap("le " + S() + " " + S());
                    case 5: return Wrap("gt " + Expr(T.F, d - 1) + " " + Expr(T.F, d - 1));
                    case 6: return Wrap("ge " + I() + " " + I());
                    case 7: return Wrap("and " + B() + " " + B());
                    case 8: return Wrap("or " + B() + " " + B() + " " + B());
                    case 9: return Wrap("not " + A());
                    case 10: return Wrap("contains " + S() + " " + S());
                    case 11: return Wrap("hasPrefix " + S() + " " + S());
                    case 12: return Wrap("regexMatch " + Lit(Pick("^h", "o$", "[0-9]+", "(?i)hello", "a|b")) + " " + S());
                    default: return Wrap("isAbs " + S());
                }

            case T.L:
                switch (_r.Next(5))
                {
                    case 0: return Wrap("list " + A() + " " + A());
                    case 1: return Wrap("split " + Lit(Pick(",", "", "/", " ")) + " " + S());
                    case 2: return Wrap("slice " + L() + " " + I() + (P(0.6) ? " " + I() : ""));
                    case 3: return Wrap("index " + Expr(T.M, d - 1) + " " + Lit(Pick("tags", "k1", "z")));
                    default: return Wrap("or " + L() + " " + L());
                }

            case T.M:
                switch (_r.Next(3))
                {
                    case 0: return Wrap("dict " + Lit(Pick("a", "b", "k")) + " " + A() + (P(0.5) ? " " + Lit("z") + " " + A() : ""));
                    case 1: return Wrap("index " + L() + " " + I());
                    default: return Wrap("or " + Expr(T.M, d - 1) + " " + Expr(T.M, d - 1));
                }

            case T.F:
                return Wrap("default " + Expr(T.F, d - 1) + " " + Expr(T.F, d - 1));
            default:
                switch (_r.Next(6))
                {
                    case 0: return Wrap("and " + A() + " " + A());
                    case 1: return Wrap("or " + A() + " " + A());
                    case 2: return Wrap("default " + A() + " " + A());
                    case 3: return Wrap("index " + Expr(T.M, d - 1) + " " + Lit(Pick("k1", "z", "a", "id")));
                    case 4: return Wrap("print " + A());
                    default: return Wrap("slice " + L() + " " + I());
                }
        }
    }
}
