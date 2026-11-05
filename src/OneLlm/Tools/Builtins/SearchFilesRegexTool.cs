using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OneLlm.Tools.Builtins;

/// <summary>
/// search_files_regex: finds files by name and by content with a regular expression, case-insensitively, and prints the matches
/// with line numbers within a fixed token budget. The expression runs in linear time, so a hostile pattern cannot stall the host.
/// </summary>
internal sealed class SearchFilesRegexTool : BuiltinTool
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const int TextSampleSize = 1024;
    private const int MaxTokens = 2000;
    private const int MinTokensPerFile = 50;
    private const int MaxLineLength = 120;

    private static readonly string[] s_defaultExclusions = [".git/", "node_modules/", "__pycache__/", "venv/", ".venv/"];

    private static readonly HashSet<string> s_binaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".mp3", ".mp4", ".avi", ".mov", ".wmv",
    };

    public SearchFilesRegexTool()
        : base(Spec("search_files_regex", BuiltinSchemas.SearchFilesRegexDescription, BuiltinSchemas.SearchFilesRegexSchema, TimeSpan.FromSeconds(60)), ToolCapability.FileRead)
    {
    }

    private sealed class FileHit(string path)
    {
        public string Path { get; } = path;

        public List<(int Line, string Content)> Matches { get; } = [];

        public int NameTokens => Tokens("**" + Path + "**");

        public int TotalTokens => NameTokens + Matches.Sum(m => Tokens(m.Content));

        // Prints the file's block, stopping when the token budget is spent; a gap of three or more lines gets a "..." separator.
        public string Render(int maxTokens)
        {
            var sb = new StringBuilder();
            sb.Append("**").Append(Path).Append("**\n");
            int used = NameTokens + 1;
            int shown = 0;
            int? previousEnd = null;
            foreach ((int _, string content) in Matches)
            {
                used += Tokens(content) + 1;
                if (used > maxTokens)
                {
                    break;
                }

                if (previousEnd is int end && EdgeLine(content, first: true) - end - 1 >= 3)
                {
                    sb.Append("...\n");
                }

                shown++;
                sb.Append(content).Append('\n');
                previousEnd = EdgeLine(content, first: false);
            }

            if (shown < Matches.Count)
            {
                sb.Append("    ... and ").Append((Matches.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more matches\n");
            }

            return sb.ToString();
        }
    }

    protected override ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = Args.OptionalString(inputs, "path") ?? "";
        string pattern = Args.OptionalString(inputs, "pattern") ?? "";
        if (pattern.Length == 0)
        {
            return ValueTask.FromResult(ToolResult.Error("ERROR: DirectoryOrFilePath and Pattern must be provided."));
        }

        bool namesOnly = Args.Bool(inputs, "search_file_names_only", false);
        int? before = Args.OptionalInt(inputs, "context_lines_before");
        int? after = Args.OptionalInt(inputs, "context_lines_after");
        List<string> exclude = Args.OptionalStringList(inputs, "exclusion_patterns") ?? [.. s_defaultExclusions];
        List<string> include = Args.OptionalStringList(inputs, "inclusion_patterns") ?? [];
        bool gitIgnore = Args.Bool(inputs, "exclude_gitignore_matches", true);

        Regex re;
        try
        {
            // RE2 spells named groups (?P<name>...); .NET spells them (?<name>...). Matching is case-insensitive, as the tool always was.
            re = new Regex(pattern.Replace("(?P<", "(?<", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (ArgumentException e)
        {
            return ValueTask.FromResult(ToolResult.Error("ERROR: Invalid regex pattern: " + e.Message));
        }

        string real = context.Capabilities.ResolveRead(path.Length == 0 ? "." : path);
        if (File.Exists(real))
        {
            return ValueTask.FromResult(ToolResult.FromText(SearchSingleFile(real, re, before ?? 3, after ?? 3, cancellationToken)));
        }

        if (!Directory.Exists(real))
        {
            return ValueTask.FromResult(ToolResult.Error("ERROR: The specified path does not exist or is neither a file nor a directory."));
        }

        int ctxBefore = before ?? 0;
        int ctxAfter = after ?? 0;
        var walker = new FileWalker(real, context.Capabilities, -1, exclude, include, gitIgnore);
        var hits = new Dictionary<string, FileHit>(StringComparer.Ordinal);
        foreach (WalkEntry entry in walker.Walk(cancellationToken))
        {
            if (entry.IsDir)
            {
                continue;
            }

            string file = entry.FullPath;
            if (entry.IsSymlink)
            {
                // A link is read only when its target is inside a granted root.
                try
                {
                    file = context.Capabilities.ResolveRead(entry.FullPath);
                }
                catch (CapabilityMissingException)
                {
                    continue;
                }
            }

            if (re.IsMatch(entry.Path))
            {
                hits[entry.Path] = new FileHit(entry.Path);
            }

            if (namesOnly || !ShouldSearchContents(file))
            {
                continue;
            }

            List<(int Line, string Content)> found = SearchContents(file, re, ctxBefore, ctxAfter);
            if (found.Count > 0)
            {
                if (!hits.TryGetValue(entry.Path, out FileHit? hit))
                {
                    hit = new FileHit(entry.Path);
                    hits[entry.Path] = hit;
                }

                hit.Matches.AddRange(found);
            }
        }

        return ValueTask.FromResult(ToolResult.FromText(Summarize(hits.Values)));
    }

    private static string Summarize(IEnumerable<FileHit> all)
    {
        List<FileHit> list = [.. all.OrderBy(h => h.TotalTokens).ThenBy(h => h.Path, StringComparer.Ordinal)];
        if (list.Count == 0)
        {
            return "No matches found.";
        }

        var sb = new StringBuilder();
        sb.Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(" matches found:\n");
        if (list.Sum(h => h.TotalTokens) <= MaxTokens)
        {
            foreach (FileHit h in list)
            {
                sb.Append(h.Render(MaxTokens));
            }

            return sb.ToString();
        }

        // Over budget: each file gets an equal share of what is left, smallest files first, and the rest are counted.
        int remaining = MaxTokens;
        int filesLeft = list.Count;
        int processed = 0;
        foreach (FileHit h in list)
        {
            int perFile = Math.Max(remaining / filesLeft, MinTokensPerFile);
            if (remaining < MinTokensPerFile)
            {
                break;
            }

            string entry = h.Render(Math.Min(perFile, remaining));
            sb.Append(entry);
            remaining -= Tokens(entry + "\n");
            filesLeft--;
            processed++;
        }

        int notShown = list.Count - processed;
        if (notShown > 0)
        {
            int matchesNotShown = list.Skip(processed).Sum(h => h.Matches.Count);
            sb.Append("\n**Output Truncated:** ").Append(matchesNotShown.ToString(CultureInfo.InvariantCulture)).Append(" matches across ")
                .Append(notShown.ToString(CultureInfo.InvariantCulture)).Append(" files not shown.");
        }

        return sb.ToString();
    }

    private static string SearchSingleFile(string file, Regex re, int before, int after, CancellationToken ct)
    {
        if (!ShouldSearchContents(file))
        {
            return "File type is not supported for content searching.";
        }

        string content = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = content.Split('\n');
        int[] starts = LineStarts(content);
        var matching = new HashSet<int>();
        var chunks = new List<(int Start, int End)>();
        foreach (Match m in re.Matches(content))
        {
            ct.ThrowIfCancellationRequested();
            int line = LineOf(starts, m.Index);
            matching.Add(line);
            chunks.Add((Math.Max(1, line - before), Math.Min(lines.Length, line + after)));
        }

        if (chunks.Count == 0)
        {
            return "No matches found in the specified file.";
        }

        chunks.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        (int Start, int End) cur = chunks[0];
        foreach ((int Start, int End) next in chunks.Skip(1))
        {
            if (next.Start - cur.End - 1 <= 2)
            {
                cur = (cur.Start, Math.Max(cur.End, next.End));
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }

        merged.Add(cur);
        var sb = new StringBuilder();
        for (int i = 0; i < merged.Count; i++)
        {
            for (int n = merged[i].Start; n <= merged[i].End; n++)
            {
                string text = n <= lines.Length ? lines[n - 1] : "";
                sb.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(matching.Contains(n) ? '>' : ':').Append(' ').Append(text).Append('\n');
            }

            if (i < merged.Count - 1 && merged[i + 1].Start - merged[i].End - 1 >= 3)
            {
                sb.Append("...\n");
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static List<(int Line, string Content)> SearchContents(string file, Regex re, int before, int after)
    {
        var results = new List<(int, string)>();
        string content;
        try
        {
            content = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        string[] lines = content.Split('\n');
        int[] starts = LineStarts(content);
        var seen = new HashSet<int>();
        foreach (Match m in re.Matches(content))
        {
            int line = LineOf(starts, m.Index);
            if (!seen.Add(line))
            {
                continue;
            }

            if (before > 0 || after > 0)
            {
                var block = new List<string>();
                for (int i = Math.Max(0, line - 1 - before); i <= Math.Min(lines.Length - 1, line - 1 + after); i++)
                {
                    int number = i + 1;
                    string text = TruncateLine(lines[i], re, number == line);
                    block.Add(number.ToString(CultureInfo.InvariantCulture).PadLeft(5) + (number == line ? '>' : ':') + " " + text);
                }

                results.Add((line, string.Join('\n', block)));
            }
            else
            {
                results.Add((line, line.ToString(CultureInfo.InvariantCulture).PadLeft(5) + "> " + TruncateLine(lines[line - 1], re, true)));
            }
        }

        return results;
    }

    // A long matching line is cut to a window around the match; other long lines are cut at the limit.
    private static string TruncateLine(string line, Regex re, bool isMatchLine)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        if (isMatchLine)
        {
            Match m = re.Match(line);
            if (m.Success)
            {
                int matchLen = Math.Min(m.Length, 100);
                int start = Math.Max(0, m.Index - 50);
                int end = Math.Min(line.Length, m.Index + matchLen + 50);
                return (start > 0 ? "..." : "") + line[start..end] + (end < line.Length ? "..." : "");
            }
        }

        return line[..MaxLineLength] + "...";
    }

    private static bool ShouldSearchContents(string file)
    {
        if (s_binaryExtensions.Contains(Path.GetExtension(file)))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFileSizeBytes)
            {
                return false;
            }

            byte[] buffer = new byte[TextSampleSize];
            int n = fs.Read(buffer, 0, buffer.Length);
            string sample = Encoding.UTF8.GetString(buffer, 0, n);
            foreach (char c in sample)
            {
                if (c != '\uFFFD' && char.IsControl(c) && !char.IsWhiteSpace(c))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Offsets where each line starts, so a match offset maps to its line by binary search.
    private static int[] LineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static int LineOf(int[] starts, int index)
    {
        int i = Array.BinarySearch(starts, index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    private static int Tokens(string text) => (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    // The line number at the start (or end) of a printed match block, read back from its "  12>" or "  12:" prefix.
    private static int EdgeLine(string block, bool first)
    {
        string[] lines = block.Split('\n');
        IEnumerable<string> ordered = first ? lines : lines.Reverse();
        foreach (string raw in ordered)
        {
            string line = raw.Trim();
            int i = 0;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                i++;
            }

            if (i > 0 && i < line.Length && (line[i] is '>' or ':') && int.TryParse(line.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }

            if (first)
            {
                return 0;
            }
        }

        return 0;
    }
}
