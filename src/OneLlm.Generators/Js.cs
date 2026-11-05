using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneLlm.Generators;

// One JSON Schema fragment as keyword slots. The reflection path in the core library writes the same slots in the same order
// (docs/tool-schemas.md), and a parity test holds the two together.
internal sealed class Js
{
    internal static readonly string[] KeyOrder =
    [
        "type", "format", "description", "enum", "minimum", "maximum", "minLength", "maxLength", "pattern", "contentEncoding",
        "items", "minItems", "maxItems", "uniqueItems", "properties", "required", "additionalProperties", "default",
    ];

    private readonly Dictionary<string, string> _slots = new Dictionary<string, string>(StringComparer.Ordinal);

    public Js Set(string key, string rawJson)
    {
        _slots[key] = rawJson;
        return this;
    }

    public Js Str(string key, string value) => Set(key, Quote(value));

    public Js Int(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public bool Has(string key) => _slots.ContainsKey(key);

    public string? Get(string key) => _slots.TryGetValue(key, out string? v) ? v : null;

    public Js Clone()
    {
        var copy = new Js();
        foreach (KeyValuePair<string, string> pair in _slots)
        {
            copy._slots[pair.Key] = pair.Value;
        }

        return copy;
    }

    public string Render()
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (string key in KeyOrder)
        {
            if (!_slots.TryGetValue(key, out string? raw))
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(Quote(key)).Append(':').Append(raw);
        }

        return sb.Append('}').ToString();
    }

    // "type": "string" becomes ["string","null"]; an enum gains null, since an enum without it would reject the null the type now allows.
    public void MakeNullable()
    {
        string? type = Get("type");
        if (type is null || !type.StartsWith("\"", StringComparison.Ordinal))
        {
            return;
        }

        Set("type", "[" + type + ",\"null\"]");
        string? e = Get("enum");
        if (e is not null)
        {
            Set("enum", e.Substring(0, e.Length - 1) + ",null]");
        }
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
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

        return sb.Append('"').ToString();
    }
}
