using System.Globalization;
using System.Text;

namespace OneLlm.Templating;

/// <summary>The language builtins: <c>and or not len index slice print printf println eq ne lt le gt ge call html js urlquery</c>.</summary>
internal static class Builtins
{
    public static readonly IReadOnlyDictionary<string, TemplateFunction> Table = Build();

    public static TemplateFunction SliceFunction => Table["slice"];

    private static Dictionary<string, TemplateFunction> Build()
    {
        var any = Array.Empty<ArgKind>();
        var d = new Dictionary<string, TemplateFunction>(StringComparer.Ordinal);

        void Add(string name, ArgKind[] fixedArgs, ArgKind? variadic, Func<object?[], object?> fn, FunctionRole role = FunctionRole.Normal)
            => d[name] = new TemplateFunction(name, fixedArgs, fn, variadic, role);

        Add("and", any, ArgKind.Any, _ => null, FunctionRole.And);
        Add("or", any, ArgKind.Any, _ => null, FunctionRole.Or);
        Add("not", new[] { ArgKind.Any }, null, a => !V.IsTrue(a[0]));
        Add("len", new[] { ArgKind.Any }, null, a => Len(a[0]));
        Add("index", new[] { ArgKind.Any }, ArgKind.Any, a => Index(a[0], a.AsSpan(1)));
        Add("slice", new[] { ArgKind.Any }, ArgKind.Any, a => Slice(a[0], a.AsSpan(1)));
        Add("print", any, ArgKind.Any, a => GoFmt.Sprint(a));
        Add("println", any, ArgKind.Any, a => GoFmt.Sprintln(a));
        Add("printf", new[] { ArgKind.Text }, ArgKind.Any, a => GoFmt.Sprintf((string)a[0]!, a.AsSpan(1).ToArray()));
        Add("eq", new[] { ArgKind.Any }, ArgKind.Any, a => Eq(a[0], a.AsSpan(1)));
        Add("ne", new[] { ArgKind.Any, ArgKind.Any }, null, a => !Eq(a[0], a.AsSpan(1)));
        Add("lt", new[] { ArgKind.Any, ArgKind.Any }, null, a => Less(a[0], a[1]));
        Add("le", new[] { ArgKind.Any, ArgKind.Any }, null, a => Less(a[0], a[1]) || Eq(a[0], a.AsSpan(1)));
        Add("gt", new[] { ArgKind.Any, ArgKind.Any }, null, a => !(Less(a[0], a[1]) || Eq(a[0], a.AsSpan(1))));
        Add("ge", new[] { ArgKind.Any, ArgKind.Any }, null, a => !Less(a[0], a[1]));
        Add("html", any, ArgKind.Any, a => HtmlEscape(EvalArgsString(a)));
        Add("js", any, ArgKind.Any, a => JsEscape(EvalArgsString(a)));
        Add("urlquery", any, ArgKind.Any, a => QueryEscape(EvalArgsString(a)));
        d["call"] = new TemplateFunction("call", new[] { ArgKind.Any }, _ => null, ArgKind.Any)
        {
            Context = (ctx, a) =>
            {
                if (V.Norm(a[0]) is not TemplateFunction fn)
                {
                    throw new TemplateFunctionException($"non-function of type {V.TypeName(a[0])}");
                }

                return ctx.CallValue(fn, a.AsSpan(1).ToArray());
            },
        };
        return d;
    }

    private static long Len(object? v)
    {
        v = V.Norm(v);
        return v switch
        {
            null => throw new TemplateFunctionException("len of untyped nil"),
            string s => V.Utf8Length(s),
            _ when ReferenceEquals(v, V.Missing) => throw new TemplateFunctionException("len of untyped nil"),
            _ when V.IsList(v) => V.ListCount(v),
            _ when V.IsMap(v) => V.MapCount(v),
            _ => throw new TemplateFunctionException($"len of type {V.TypeName(v)}"),
        };
    }

    private static int IndexArg(object? idx, int cap)
    {
        object? n = V.Norm(idx);
        long l;
        if (n is long ln)
        {
            l = ln;
        }
        else if (n is byte bn)
        {
            l = bn;
        }
        else
        {
            throw new TemplateFunctionException($"cannot index slice/array with type {V.TypeName(idx)}");
        }

        if (l < 0 || l >= cap)
        {
            throw new TemplateFunctionException($"index out of range: {l.ToString(CultureInfo.InvariantCulture)}");
        }

        return (int)l;
    }

    private static object? Index(object? item, ReadOnlySpan<object?> indexes)
    {
        item = V.Norm(item);
        if (item is null || ReferenceEquals(item, V.Missing))
        {
            throw new TemplateFunctionException("index of untyped nil");
        }

        foreach (object? raw in indexes)
        {
            object? idx = V.Norm(raw);
            if (item is null || ReferenceEquals(item, V.Missing))
            {
                throw new TemplateFunctionException("index of untyped nil");
            }

            if (item is string s)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                item = bytes[IndexArg(idx, bytes.Length)];
            }
            else if (V.IsList(item))
            {
                item = V.ListGet(item, IndexArg(idx, V.ListCount(item)));
            }
            else if (V.IsMap(item))
            {
                if (idx is not string key)
                {
                    throw new TemplateFunctionException($"value has type {V.TypeName(idx)}; should be string");
                }

                item = V.MapTryGet(item, key, out object? val) ? val : null;
            }
            else
            {
                throw new TemplateFunctionException($"can't index item of type {V.TypeName(item)}");
            }
        }

        return item;
    }

    private static object? Slice(object? item, ReadOnlySpan<object?> indexes)
    {
        item = V.Norm(item);
        if (item is null || ReferenceEquals(item, V.Missing))
        {
            throw new TemplateFunctionException("slice of untyped nil");
        }

        if (indexes.Length > 3)
        {
            throw new TemplateFunctionException($"too many slice indexes: {indexes.Length.ToString(CultureInfo.InvariantCulture)}");
        }

        byte[]? bytes = null;
        int cap;
        if (item is string s)
        {
            if (indexes.Length == 3)
            {
                throw new TemplateFunctionException("cannot 3-index slice a string");
            }

            bytes = Encoding.UTF8.GetBytes(s);
            cap = bytes.Length;
        }
        else if (V.IsList(item))
        {
            cap = V.ListCount(item);
        }
        else
        {
            throw new TemplateFunctionException($"can't slice item of type {V.TypeName(item)}");
        }

        var idx = new long[3];
        int given = indexes.Length;
        for (int i = 0; i < given; i++)
        {
            object? sv = V.Norm(indexes[i]);
            long l;
            if (sv is long sl)
            {
                l = sl;
            }
            else if (sv is byte sbv)
            {
                l = sbv;
            }
            else
            {
                throw new TemplateFunctionException($"cannot index slice/array with type {V.TypeName(indexes[i])}");
            }

            if (l < 0 || l > cap)
            {
                throw new TemplateFunctionException($"index out of range: {l.ToString(CultureInfo.InvariantCulture)}");
            }

            idx[i] = l;
        }

        long lo = given > 0 ? idx[0] : 0;
        long hi = given > 1 ? idx[1] : cap;
        if (lo > hi)
        {
            throw new TemplateFunctionException($"invalid slice index: {lo.ToString(CultureInfo.InvariantCulture)} > {hi.ToString(CultureInfo.InvariantCulture)}");
        }

        if (given == 3 && idx[1] > idx[2])
        {
            throw new TemplateFunctionException($"invalid slice index: {idx[1].ToString(CultureInfo.InvariantCulture)} > {idx[2].ToString(CultureInfo.InvariantCulture)}");
        }

        if (bytes is not null)
        {
            return Encoding.UTF8.GetString(bytes, (int)lo, (int)(hi - lo));
        }

        var result = new List<object?>((int)(hi - lo));
        for (long i = lo; i < hi; i++)
        {
            result.Add(V.ListGet(item, (int)i));
        }

        return result;
    }

    // ---- comparison ----

    private enum Kind { Invalid, Bool, Int, Float, String, Uint }

    private static Kind BasicKind(object? v) => v switch
    {
        bool => Kind.Bool,
        long => Kind.Int,
        byte => Kind.Uint,
        double => Kind.Float,
        string => Kind.String,
        _ => Kind.Invalid,
    };

    private static object? Clean(object? v)
    {
        v = V.Norm(v);
        return ReferenceEquals(v, V.Missing) ? null : v;
    }

    private static bool Eq(object? a1, ReadOnlySpan<object?> rest)
    {
        object? arg1 = Clean(a1);
        if (rest.Length == 0)
        {
            throw new TemplateFunctionException("missing argument for comparison");
        }

        Kind k1 = BasicKind(arg1);
        foreach (object? raw in rest)
        {
            object? arg = Clean(raw);
            Kind k2 = BasicKind(arg);
            if (k1 != k2)
            {
                if ((k1 == Kind.Int && k2 == Kind.Uint) || (k1 == Kind.Uint && k2 == Kind.Int))
                {
                    long a = k1 == Kind.Int ? (long)arg1! : (byte)arg1!;
                    long c = k2 == Kind.Int ? (long)arg! : (byte)arg!;
                    if (a == c)
                    {
                        return true;
                    }

                    continue;
                }

                if (arg1 is not null && arg is not null)
                {
                    throw new TemplateFunctionException($"incompatible types for comparison: {V.TypeName(arg1)} and {V.TypeName(arg)}");
                }

                continue; // nil against anything is simply not equal
            }

            bool truth;
            switch (k1)
            {
                case Kind.Bool:
                    truth = (bool)arg1! == (bool)arg!;
                    break;
                case Kind.Int:
                    truth = (long)arg1! == (long)arg!;
                    break;
                case Kind.Uint:
                    truth = (byte)arg1! == (byte)arg!;
                    break;
                case Kind.Float:
                    truth = (double)arg1! == (double)arg!;
                    break;
                case Kind.String:
                    truth = string.Equals((string)arg1!, (string)arg!, StringComparison.Ordinal);
                    break;
                default:
                    if (arg1 is null || arg is null)
                    {
                        truth = arg1 is null && arg is null;
                    }
                    else if (arg1 is DateTimeOffset t1 && arg is DateTimeOffset t2)
                    {
                        truth = t1 == t2;
                    }
                    else if (arg1 is TemplateFunction && arg is TemplateFunction)
                    {
                        throw new TemplateFunctionException("non-comparable type func");
                    }
                    else
                    {
                        throw new TemplateFunctionException($"non-comparable types {V.TypeName(arg1)} and {V.TypeName(arg)}");
                    }

                    break;
            }

            if (truth)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Less(object? a1, object? a2)
    {
        object? arg1 = Clean(a1);
        object? arg2 = Clean(a2);
        Kind k1 = BasicKind(arg1);
        Kind k2 = BasicKind(arg2);
        if (k1 == Kind.Invalid)
        {
            throw new TemplateFunctionException("invalid type for comparison");
        }

        if (k2 == Kind.Invalid)
        {
            throw new TemplateFunctionException("invalid type for comparison");
        }

        if ((k1 == Kind.Int && k2 == Kind.Uint) || (k1 == Kind.Uint && k2 == Kind.Int))
        {
            long a = k1 == Kind.Int ? (long)arg1! : (byte)arg1!;
            long c = k2 == Kind.Int ? (long)arg2! : (byte)arg2!;
            return a < c;
        }

        if (k1 != k2)
        {
            throw new TemplateFunctionException($"incompatible types for comparison: {V.TypeName(arg1)} and {V.TypeName(arg2)}");
        }

        return k1 switch
        {
            Kind.Uint => (byte)arg1! < (byte)arg2!,
            Kind.Int => (long)arg1! < (long)arg2!,
            Kind.Float => (double)arg1! < (double)arg2!,
            Kind.String => string.CompareOrdinal((string)arg1!, (string)arg2!) < 0,
            _ => throw new TemplateFunctionException("invalid type for comparison"),
        };
    }

    // ---- escapers ----

    private static string EvalArgsString(object?[] args)
    {
        if (args.Length == 1 && V.Norm(args[0]) is string s)
        {
            return s;
        }

        // An absent value prints as <no value> here, as it does in actions.
        var shown = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            object? a = V.Norm(args[i]);
            shown[i] = a is null || ReferenceEquals(a, V.Missing) ? "<no value>" : a;
        }

        return GoFmt.Sprint(shown);
    }

    public static string HtmlEscape(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            string? rep = s[i] switch
            {
                '"' => "&#34;",
                '\'' => "&#39;",
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\0' => "\uFFFD",
                _ => null,
            };
            if (rep is not null)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 16);
                sb.Append(rep);
            }
            else
            {
                sb?.Append(s[i]);
            }
        }

        return sb?.ToString() ?? s;
    }

    public static string JsEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (Rune r in s.EnumerateRunes())
        {
            int c = r.Value;
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '"': sb.Append("\\\""); break;
                case '<': sb.Append("\\u003C"); break;
                case '>': sb.Append("\\u003E"); break;
                case '&': sb.Append("\\u0026"); break;
                case '=': sb.Append("\\u003D"); break;
                default:
                    if (GoQuoteWriter.IsPrint(c))
                    {
                        sb.Append(r.ToString());
                    }
                    else if (c < 0x10000)
                    {
                        sb.Append("\\u").Append(c.ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        // Go writes a non-BMP non-printable rune as two UTF-16 escapes.
                        string pair = char.ConvertFromUtf32(c);
                        sb.Append("\\u").Append(((int)pair[0]).ToString("X4", CultureInfo.InvariantCulture));
                        sb.Append("\\u").Append(((int)pair[1]).ToString("X4", CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    public static string QueryEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else if (b == ' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}
