using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OneLlm.Access;

namespace OneLlm.Tools.Builtins;

/// <summary>
/// apply_patch: applies a unified diff (the output of <c>diff -u</c> or <c>git diff</c>) to files under the granted roots. Every hunk of
/// every file is checked against the current contents before anything is written.
/// </summary>
internal sealed partial class ApplyPatchTool : BuiltinTool
{
    internal const string Description = """
        Apply a patch in unified diff format (what `diff -u` and `git diff` print) to one or more files. Paths are relative to the working directory.

        Format:
        - File headers: `--- a/path` then `+++ b/path`. A leading `a/` and `b/` are removed when both are present. Use `/dev/null` as the old path to create a file and as the new path to delete one. Renames are not supported.
        - Hunks: `@@ -oldStart,oldCount +newStart,newCount @@`, then lines starting with a space (unchanged), `-` (removed) or `+` (added). `\ No newline at end of file` is honoured.
        - Line numbers are hints. A hunk applies where its unchanged and removed lines match the file exactly, nearest to the stated line. If they match nowhere, the whole patch is rejected and nothing is written.
        - Line endings of the file (LF or CRLF) are kept.
        """;

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "patch": { "type": "string", "description": "The unified diff to apply." },
            "dry_run": { "type": "boolean", "description": "If true, check that the patch applies and report what it would change, without writing anything. Defaults to false.", "default": false }
          },
          "required": ["patch"]
        }
        """;

    public ApplyPatchTool()
        : base(Spec("apply_patch", Description, Schema, TimeSpan.FromSeconds(30)), ToolCapability.FileRead | ToolCapability.FileWrite)
    {
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    private sealed class Hunk
    {
        public int OldStart { get; init; }

        public int OldCount { get; init; }

        public int NewCount { get; init; }

        public List<(char Kind, string Text)> Lines { get; } = [];

        public bool NewNoEol { get; set; }
    }

    private sealed class FilePatch
    {
        public string? OldPath { get; init; }

        public string? NewPath { get; init; }

        public List<Hunk> Hunks { get; } = [];

        public string Path => NewPath ?? OldPath!;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string patchText = Args.RequiredString(inputs, "patch");
        bool dryRun = Args.Bool(inputs, "dry_run", false);
        List<FilePatch> patches;
        try
        {
            patches = Parse(TextFile.NormalizeNewlines(patchText));
        }
        catch (FormatException e)
        {
            return Fail("Invalid patch: " + e.Message);
        }

        if (patches.Count == 0)
        {
            return Fail("Invalid patch: no file headers (--- / +++) found.");
        }

        var plans = new List<(FilePatch Patch, string? NewText, byte[]? Output, int Added, int Removed, string Verb)>();
        foreach (FilePatch fp in patches)
        {
            ICapabilities caps = context.Capabilities;
            _ = caps.ResolveWrite(fp.Path);
            bool create = fp.OldPath is null;
            bool delete = fp.NewPath is null;
            string? text = null;
            string style = "lf";
            bool bom = false;
            TextFile.Kind kind = TextFile.Kind.Utf8;
            if (!create)
            {
                string real = caps.ResolveRead(fp.OldPath!);
                if (!File.Exists(real))
                {
                    return Fail($"Cannot patch '{fp.Path}': the file does not exist.");
                }

                byte[] raw;
                await using (FileStream s = GuardedFile.OpenRead(caps, fp.OldPath!))
                {
                    raw = new byte[s.Length];
                    await s.ReadExactlyAsync(raw, cancellationToken).ConfigureAwait(false);
                }

                if (!TextFile.TryDecode(raw, out string decoded, out kind, out bom))
                {
                    return Fail($"Cannot patch '{fp.Path}': it is not UTF-8 or UTF-16 text.");
                }

                (style, text) = ReplaceStringTool.NormalizeLineEndings(decoded);
            }
            else if (File.Exists(caps.ResolveWrite(fp.Path)))
            {
                return Fail($"Cannot create '{fp.Path}': it already exists.");
            }

            string? applied;
            int added = 0;
            int removed = 0;
            try
            {
                applied = Apply(fp, text, ref added, ref removed);
            }
            catch (FormatException e)
            {
                return Fail($"Patch does not apply to '{fp.Path}': {e.Message} Nothing was written.");
            }

            if (delete)
            {
                plans.Add((fp, null, null, 0, removed, "deleted"));
                continue;
            }

            string final = ReplaceStringTool.ConvertLineEndings(applied!, style);
            byte[] output = kind == TextFile.Kind.Utf8 ? TextFile.Utf8(final, bom) : Encoding.UTF8.GetBytes(final);
            plans.Add((fp, final, output, added, removed, create ? "created" : "modified"));
        }

        if (!dryRun)
        {
            foreach ((FilePatch fp, string? _, byte[]? output, int _, int _, string _) in plans)
            {
                if (output is null)
                {
                    string real = context.Capabilities.ResolveWrite(fp.OldPath!);
                    File.Delete(real);
                    continue;
                }

                await using FileStream w = GuardedFile.OpenWrite(context.Capabilities, fp.Path, append: false);
                await w.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        var sb = new StringBuilder();
        sb.Append(dryRun ? "Patch applies cleanly to " : "Applied patch to ").Append(plans.Count.ToString(CultureInfo.InvariantCulture)).Append(" file(s)")
            .Append(dryRun ? " (dry run, nothing written):" : ":");
        foreach ((FilePatch fp, string? _, byte[]? _, int added, int removed, string verb) in plans)
        {
            sb.Append("\n  ").Append(verb).Append(' ').Append(fp.Path).Append(" (+").Append(added.ToString(CultureInfo.InvariantCulture))
                .Append(" -").Append(removed.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        return ToolResult.FromText(sb.ToString());
    }

    private static List<FilePatch> Parse(string patch)
    {
        string[] lines = patch.Split('\n');
        var result = new List<FilePatch>();
        int i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("--- ", StringComparison.Ordinal) || i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (lines[i].StartsWith("rename ", StringComparison.Ordinal) || lines[i].StartsWith("copy ", StringComparison.Ordinal))
                {
                    throw new FormatException("renames and copies are not supported.");
                }

                i++;
                continue;
            }

            string oldRaw = HeaderPath(lines[i][4..]);
            string newRaw = HeaderPath(lines[i + 1][4..]);
            i += 2;
            bool strip = (oldRaw == "/dev/null" || oldRaw.StartsWith("a/", StringComparison.Ordinal)) && (newRaw == "/dev/null" || newRaw.StartsWith("b/", StringComparison.Ordinal));
            string? oldPath = oldRaw == "/dev/null" ? null : strip ? oldRaw[2..] : oldRaw;
            string? newPath = newRaw == "/dev/null" ? null : strip ? newRaw[2..] : newRaw;
            if (oldPath is null && newPath is null)
            {
                throw new FormatException("a file cannot be both created and deleted.");
            }

            if (oldPath is not null && newPath is not null && oldPath != newPath)
            {
                throw new FormatException($"renaming '{oldPath}' to '{newPath}' is not supported.");
            }

            var fp = new FilePatch { OldPath = oldPath, NewPath = newPath };
            while (i < lines.Length && !(lines[i].StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal)))
            {
                Match m = HunkHeader().Match(lines[i]);
                if (!m.Success)
                {
                    i++;
                    continue;
                }

                int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                int newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
                var hunk = new Hunk { OldStart = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), OldCount = oldCount, NewCount = newCount };
                i++;
                int oldSeen = 0;
                int newSeen = 0;
                while ((oldSeen < oldCount || newSeen < newCount || (i < lines.Length && lines[i].StartsWith('\\'))) && i < lines.Length)
                {
                    string line = lines[i];
                    char kind = line.Length == 0 ? ' ' : line[0];
                    if (kind == '\\')
                    {
                        // "\ No newline at end of file" refers to the line just before it.
                        if (hunk.Lines.Count > 0 && hunk.Lines[^1].Kind is '+' or ' ')
                        {
                            hunk.NewNoEol = true;
                        }

                        i++;
                        continue;
                    }

                    if (kind is not (' ' or '-' or '+'))
                    {
                        throw new FormatException($"unexpected line in a hunk of '{fp.Path}': '{(line.Length > 40 ? line[..40] : line)}'.");
                    }

                    hunk.Lines.Add((kind, line.Length == 0 ? "" : line[1..]));
                    if (kind != '+')
                    {
                        oldSeen++;
                    }

                    if (kind != '-')
                    {
                        newSeen++;
                    }

                    i++;
                }

                if (oldSeen != oldCount || newSeen != newCount)
                {
                    throw new FormatException($"a hunk of '{fp.Path}' is shorter than its header says.");
                }

                fp.Hunks.Add(hunk);
            }

            if (fp.Hunks.Count == 0)
            {
                throw new FormatException($"'{fp.Path}' has no hunks.");
            }

            result.Add(fp);
        }

        return result;
    }

    private static string HeaderPath(string raw)
    {
        // A timestamp may follow the path after a tab.
        int tab = raw.IndexOf('\t', StringComparison.Ordinal);
        string p = (tab >= 0 ? raw[..tab] : raw).Trim();
        return p.Length >= 2 && p[0] == '"' && p[^1] == '"' ? p[1..^1] : p;
    }

    private static string? Apply(FilePatch fp, string? original, ref int added, ref int removed)
    {
        bool endsWithNewline = original is null || original.EndsWith('\n');
        List<string> lines = original is null ? [] : [.. (endsWithNewline ? original[..^1] : original).Split('\n')];
        if (original is { Length: 0 })
        {
            lines.Clear();
        }

        int delta = 0;
        int floor = 0;
        foreach (Hunk h in fp.Hunks)
        {
            List<string> expected = [.. h.Lines.Where(l => l.Kind != '+').Select(l => l.Text)];
            int want = Math.Max(0, (h.OldCount == 0 ? h.OldStart : h.OldStart - 1) + delta);
            int at = Find(lines, expected, want, floor);
            if (at < 0)
            {
                throw new FormatException($"the context of the hunk at line {h.OldStart} was not found.");
            }

            List<string> replacement = [.. h.Lines.Where(l => l.Kind != '-').Select(l => l.Text)];
            lines.RemoveRange(at, expected.Count);
            lines.InsertRange(at, replacement);
            delta += replacement.Count - expected.Count;
            floor = at + replacement.Count;
            added += h.Lines.Count(l => l.Kind == '+');
            removed += h.Lines.Count(l => l.Kind == '-');
            if (floor >= lines.Count)
            {
                endsWithNewline = !h.NewNoEol;
            }
        }

        if (fp.NewPath is null)
        {
            return lines.Count == 0 ? "" : throw new FormatException("the deletion does not remove every line of the file.");
        }

        return lines.Count == 0 ? "" : string.Join('\n', lines) + (endsWithNewline ? "\n" : "");
    }

    // The nearest position, at or after `floor`, where the expected lines match exactly, searching outward from the hinted line.
    private static int Find(List<string> lines, List<string> expected, int want, int floor)
    {
        int maxStart = lines.Count - expected.Count;
        if (maxStart < floor)
        {
            return -1;
        }

        want = Math.Clamp(want, floor, maxStart);
        for (int d = 0; d <= lines.Count; d++)
        {
            int[] candidates = d == 0 ? [want] : [want - d, want + d];
            foreach (int pos in candidates)
            {
                if (pos >= floor && pos <= maxStart && Matches(lines, expected, pos))
                {
                    return pos;
                }
            }
        }

        return -1;
    }

    private static bool Matches(List<string> lines, List<string> expected, int pos)
    {
        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(lines[pos + i], expected[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
