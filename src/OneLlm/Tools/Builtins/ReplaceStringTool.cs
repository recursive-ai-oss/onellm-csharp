using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Access;

namespace OneLlm.Tools.Builtins;

/// <summary>
/// replace_string: replaces every occurrence of each <c>find</c> text, in order, matching across line-ending styles and keeping the file's own style.
/// When a <c>find</c> has no exact match, a unique match after collapsing long runs of look-alike separator glyphs is accepted and flagged.
/// </summary>
internal sealed class ReplaceStringTool : BuiltinTool
{
    // Characters models mistake for each other in long banner lines, grouped under one canonical character.
    private static readonly Dictionary<char, char> s_glyphs = BuildGlyphs();

    public ReplaceStringTool()
        : base(Spec("replace_string", BuiltinSchemas.ReplaceStringDescription, BuiltinSchemas.ReplaceStringSchema, TimeSpan.FromSeconds(30)), ToolCapability.FileRead | ToolCapability.FileWrite)
    {
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = Args.RequiredString(inputs, "filePath");
        if (!inputs.TryGetPropertyValue("replacements", out JsonNode? node) || node is not JsonArray array || array.Count == 0)
        {
            throw new ToolInputException("replacements", "must be a non-empty array");
        }

        var edits = new List<(string Find, string Replace)>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                throw new ToolInputException($"replacements[{i}]", "must be an object with find and replace");
            }

            string find = Args.OptionalString(item, "find") ?? "";
            string replace = Args.OptionalString(item, "replace") ?? throw new ToolInputException($"replacements[{i}].replace", "is required");
            if (find.Length == 0)
            {
                return Fail($"replacements[{i}].find is required");
            }

            if (find == replace)
            {
                return Fail($"replacements[{i}].find and replace are identical (both {GoQuote(Truncate(find, 40))}) - no operation needed");
            }

            edits.Add((find, replace));
        }

        string shown = Path.GetFullPath(path, context.Capabilities.WorkingDirectory ?? Directory.GetCurrentDirectory());
        _ = context.Capabilities.ResolveWrite(path);
        byte[] raw;
        await using (FileStream read = GuardedFile.OpenRead(context.Capabilities, path))
        {
            raw = new byte[read.Length];
            await read.ReadExactlyAsync(raw, cancellationToken).ConfigureAwait(false);
        }

        if (!TextFile.TryDecode(raw, out string text, out TextFile.Kind kind, out bool bom))
        {
            return Fail("Failed to process file encoding: the file is not UTF-8 or UTF-16 text, so it was left unchanged.");
        }

        (string style, string content) = NormalizeLineEndings(text);
        int total = 0;
        var details = new List<(string Find, int Count, bool Fuzzy)>(edits.Count);
        foreach ((string findRaw, string replaceRaw) in edits)
        {
            string find = TextFile.NormalizeNewlines(findRaw);
            string replace = TextFile.NormalizeNewlines(replaceRaw);
            int count = CountOccurrences(content, find);
            bool fuzzy = false;
            if (count > 0)
            {
                content = content.Replace(find, replace, StringComparison.Ordinal);
                total += count;
            }
            else
            {
                (string collapsedContent, int[] map) = CollapseRuns(content);
                (string collapsedFind, _) = CollapseRuns(find);
                if (collapsedFind.Length > 0 && CountOccurrences(collapsedContent, collapsedFind) == 1)
                {
                    int start = collapsedContent.IndexOf(collapsedFind, StringComparison.Ordinal);
                    int end = start + collapsedFind.Length;
                    content = string.Concat(content.AsSpan(0, map[start]), replace, content.AsSpan(map[end]));
                    count = 1;
                    fuzzy = true;
                    total++;
                }
            }

            details.Add((findRaw, count, fuzzy));
        }

        if (total == 0)
        {
            return ToolResult.FromText($"No matches found for any find strings in {shown}");
        }

        string final = ConvertLineEndings(content, style);
        byte[] output = kind == TextFile.Kind.Utf8 ? TextFile.Utf8(final, bom) : Encoding.UTF8.GetBytes(final);
        await using (FileStream write = GuardedFile.OpenWrite(context.Capabilities, path, append: false))
        {
            await write.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Successfully made {total} total replacement(s) in {shown}:");
        foreach ((string find, int count, bool fuzzy) in details)
        {
            if (count > 0)
            {
                string preview = find.Length > 40 ? find[..40] + "..." : find;
                string suffix = fuzzy ? " (fuzzy match — long runs of separator glyphs collapsed; verify the replacement landed where you expected)" : "";
                sb.Append(CultureInfo.InvariantCulture, $"\n  - {count} occurrence(s) of {GoQuote(preview)}{suffix}");
            }
        }

        // The hash is SHA-256 (the file tool this follows used MD5); the labels and layout are unchanged.
        sb.Append("\n\nFile updated: ").Append(context.TimeProvider.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("\nPrevious file hash: ").Append(Convert.ToHexStringLower(SHA256.HashData(raw)));
        sb.Append(CultureInfo.InvariantCulture, $"\nPrevious file size: {raw.Length} bytes");
        sb.Append("\nNew file hash: ").Append(Convert.ToHexStringLower(SHA256.HashData(output)));
        sb.Append(CultureInfo.InvariantCulture, $"\nNew file size: {output.Length} bytes");
        return ToolResult.FromText(sb.ToString());
    }

    private static int CountOccurrences(string text, string find)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += find.Length;
        }

        return count;
    }

    // The file's dominant line ending is restored on write: CRLF wins any mix that has CRLF in it, then LF, then lone CR.
    internal static (string Style, string Content) NormalizeLineEndings(string s)
    {
        int crlf = 0;
        int lf = 0;
        int cr = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r')
            {
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (s[i] == '\n')
            {
                lf++;
            }
        }

        string style = crlf > 0 && lf == 0 && cr == 0 ? "crlf"
            : lf > 0 && crlf == 0 && cr == 0 ? "lf"
            : cr > 0 && crlf == 0 && lf == 0 ? "cr"
            : crlf > 0 || lf > 0 || cr > 0 ? (crlf >= lf && crlf >= cr ? "crlf" : "lf")
            : "lf";
        return (style, TextFile.NormalizeNewlines(s));
    }

    internal static string ConvertLineEndings(string s, string style) => style switch
    {
        "crlf" => s.Replace("\n", "\r\n", StringComparison.Ordinal),
        "cr" => s.Replace('\n', '\r'),
        _ => s,
    };

    // Runs of four or more characters from one glyph class become exactly four canonical characters; the map sends each
    // collapsed position back to the original text so a match found in the collapsed view can be spliced into the original.
    private static (string Text, int[] Map) CollapseRuns(string s)
    {
        var sb = new StringBuilder(s.Length);
        var map = new List<int>(s.Length + 1);
        int i = 0;
        while (i < s.Length)
        {
            if (!s_glyphs.TryGetValue(s[i], out char canon))
            {
                map.Add(i);
                sb.Append(s[i]);
                i++;
                continue;
            }

            int j = i;
            while (j < s.Length && s_glyphs.TryGetValue(s[j], out char c2) && c2 == canon)
            {
                j++;
            }

            if (j - i >= 4)
            {
                for (int k = 0; k < 4; k++)
                {
                    map.Add(i);
                    sb.Append(canon);
                }
            }
            else
            {
                for (int k = i; k < j; k++)
                {
                    map.Add(k);
                    sb.Append(s[k]);
                }
            }

            i = j;
        }

        map.Add(s.Length);
        return (sb.ToString(), [.. map]);
    }

    private static Dictionary<char, char> BuildGlyphs()
    {
        var d = new Dictionary<char, char>();
        void Add(char canon, string chars)
        {
            foreach (char c in chars)
            {
                d[c] = canon;
            }
        }

        Add('-', "-–—―‐‑‒⁃−\u00AD─━═╌╍╴╶╸╺▬▭=≡");
        Add('_', "_‗");
        Add('*', "*∗⁎✱");
        Add('~', "~∼〜～");
        Add('.', ".·•‧⋅");
        return d;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // Go's %q for the strings we print: double quotes, backslash escapes for the usual controls, printable text kept as is.
    internal static string GoQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
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
