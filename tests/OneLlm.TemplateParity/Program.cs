using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OneLlm.Templating;
using OneYaml;

// Renders the system prompt template stored in saved agent instances with the inputs recorded beside it and compares the
// result with the rendered prompt stored in the same file, byte for byte.
//
// This reads files in place and prints counts only: no prompt text, input value or file name is written anywhere.
//
//   TemplateParity <instances-dir> <results-file> [sample-size] [candidates] [seed] [reference-driver]
//
// With a reference driver (the differential tool's Go program) every sampled template is also rendered by it with the same
// scope, which checks the engine on real templates even where the stored text cannot be reproduced.
//
// Strata: file age (terciles of modification time) and template features (plain text, field substitutions only, control
// flow, embed calls, code regions holding braces). Misses are classified by cause; causes that come from scope the file
// does not record (clock, git state, environment, host details) are counted as unrecoverable, not as failures.

internal static partial class Program
{
    private sealed record Candidate(
        string Path, DateTime Modified, string Template, string Rendered, object? Inputs, string? WorkingDirectory,
        string? InvocationMode, string? CallerInstanceId, string Features, DateTimeOffset? StartedAt);

    private static GoRef? s_go;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: TemplateParity <instances-dir> <results-file> [sample-size] [candidates] [seed] [reference-driver]");
            return 2;
        }

        string dir = args[0];
        string resultsPath = args[1];
        int sampleSize = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300;
        int candidates = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 12000;
        int seed = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 20261105;
        s_go = args.Length > 5 ? new GoRef(args[5]) : null;

        var report = new StringBuilder();
        void Line(string s)
        {
            report.AppendLine(s);
            Console.WriteLine(s);
        }

        var all = new List<string>(Directory.EnumerateFiles(dir, "*.yaml"));
        Line($"files in corpus: {all.Count}");
        var rng = new Random(seed);
        var picked = new List<string>();
        var chosen = new HashSet<int>();
        while (picked.Count < Math.Min(candidates, all.Count))
        {
            int i = rng.Next(all.Count);
            if (chosen.Add(i))
            {
                picked.Add(all[i]);
            }
        }

        var cands = new List<Candidate>();
        int unreadable = 0;
        int noTemplate = 0;
        int tooBig = 0;
        foreach (string path in picked)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 3_000_000)
                {
                    tooBig++;
                    continue;
                }

                string text = File.ReadAllText(path);
                if (!text.Contains("renderedSystemPrompt", StringComparison.Ordinal))
                {
                    noTemplate++;
                    continue;
                }

                var doc = YamlSerializer.Deserialize<object>(text) as Dictionary<string, object?>;
                if (doc is null || doc.GetValueOrDefault("systemPrompt") is not string tmpl
                    || doc.GetValueOrDefault("metadata") is not Dictionary<string, object?> meta
                    || meta.GetValueOrDefault("renderedSystemPrompt") is not string rendered)
                {
                    noTemplate++;
                    continue;
                }

                cands.Add(new Candidate(path, fi.LastWriteTimeUtc, tmpl, rendered, doc.GetValueOrDefault("inputs"),
                    doc.GetValueOrDefault("workingDirectory") as string, doc.GetValueOrDefault("invocationMode") as string,
                    doc.GetValueOrDefault("callerInstanceId") as string, Features(tmpl), FirstMessageTime(doc)));
            }
            catch (Exception)
            {
                unreadable++;
            }
        }

        Line($"candidates read: {picked.Count}; usable: {cands.Count}; no template or rendered prompt: {noTemplate}; unreadable: {unreadable}; over size cap: {tooBig}");

        // Age terciles over the usable candidates.
        DateTime[] times = cands.Select(c => c.Modified).OrderBy(t => t).ToArray();
        DateTime t1 = times[times.Length / 3];
        DateTime t2 = times[2 * times.Length / 3];
        string Age(DateTime d) => d < t1 ? "age:oldest-third" : d < t2 ? "age:middle-third" : "age:newest-third";

        // Pick per feature class, spread over the age terciles, rarest classes first so they are not crowded out.
        string[] classes = { "embed", "code-region", "control", "fields", "plain" };
        int perClass = sampleSize / classes.Length;
        var sample = new List<Candidate>();
        var used = new HashSet<string>();
        foreach (string cls in classes.OrderBy(c => cands.Count(x => x.Features.Split(',').Contains(c))))
        {
            var pool = cands.Where(c => !used.Contains(c.Path) && c.Features.Split(',').Contains(cls)).ToList();
            var byAge = pool.GroupBy(c => Age(c.Modified)).OrderBy(g => g.Key).Select(g => g.ToArray()).ToList();
            int taken = 0;
            for (int round = 0; taken < perClass && byAge.Any(g => g.Length > round); round++)
            {
                foreach (var g in byAge)
                {
                    if (round < g.Length && taken < perClass)
                    {
                        sample.Add(g[round]);
                        used.Add(g[round].Path);
                        taken++;
                    }
                }
            }
        }

        Line($"sampled: {sample.Count}");
        var strata = new SortedDictionary<string, int[]>(); // stratum -> [sampled, matched, unrecoverable, missed]
        var causes = new SortedDictionary<string, int>();
        var offsets = new List<long>();
        var refs = new SortedDictionary<string, int>();
        int goEqual = 0;
        int goDiffer = 0;
        var goCauses = new SortedDictionary<string, int>();
        int matched = 0;
        int unrecoverable = 0;

        foreach (Candidate c in sample)
        {
            string cause = Check(c, out long diffAt, out string? ours);
            bool match = cause == "match";
            bool unrec = cause.StartsWith("unrecoverable:", StringComparison.Ordinal);
            matched += match ? 1 : 0;
            unrecoverable += unrec ? 1 : 0;
            foreach (string stratum in new[] { Age(c.Modified) }.Concat(c.Features.Split(',').Select(f => "feature:" + f)))
            {
                if (!strata.TryGetValue(stratum, out int[]? row))
                {
                    strata[stratum] = row = new int[4];
                }

                row[0]++;
                row[match ? 1 : unrec ? 2 : 3]++;
            }

            causes[cause] = causes.GetValueOrDefault(cause) + 1;
            if (cause == "unrecoverable: needs scope the file does not record")
            {
                foreach (string r in UnrecordedScope().Matches(c.Template).Select(m => m.Value.TrimStart('.')).Distinct())
                {
                    refs[r] = refs.GetValueOrDefault(r) + 1;
                }
            }
            if (!match && !unrec && diffAt >= 0)
            {
                offsets.Add(diffAt);
            }

            if (s_go is not null)
            {
                string? g = s_go.Render(c.Template, BuildScope(c));
                if (string.Equals(ours, g, StringComparison.Ordinal))
                {
                    goEqual++;
                }
                else
                {
                    goDiffer++;
                    string k = ours is null ? "ours failed, reference rendered" : g is null ? "reference failed, ours rendered" : "outputs differ";
                    goCauses[k] = goCauses.GetValueOrDefault(k) + 1;
                }
            }
        }

        Line("");
        Line("stratum (a file counts once per stratum it belongs to): sampled / matched / unrecoverable scope / missed");
        foreach (var kv in strata)
        {
            Line($"  {kv.Key,-28} {kv.Value[0],4} / {kv.Value[1],4} / {kv.Value[2],4} / {kv.Value[3],4}");
        }

        Line("");
        Line("causes (field path: systemPrompt vs metadata.renderedSystemPrompt):");
        foreach (var kv in causes.OrderByDescending(k => k.Value))
        {
            Line($"  {kv.Value,4}  {kv.Key}");
        }

        if (refs.Count > 0)
        {
            Line("");
            Line("unrecorded scope referenced by those files (a file may use several): " + string.Join(", ", refs.Select(kv => $"{kv.Key} {kv.Value}")));
        }

        if (offsets.Count > 0)
        {
            Line("");
            Line("first differing byte offset of output mismatches: " + string.Join(", ", offsets.Take(40)));
        }

        if (s_go is not null)
        {
            Line("");
            Line($"engine against the reference implementation on the same scope: {goEqual} equal, {goDiffer} differ");
            foreach (var kv in goCauses)
            {
                Line($"  {kv.Value,4}  {kv.Key}");
            }
        }

        Line("");
        Line($"total: sampled {sample.Count}, matched {matched}, unrecoverable scope {unrecoverable}, missed {sample.Count - matched - unrecoverable}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultsPath))!);
        File.WriteAllText(resultsPath, report.ToString());
        return 0;
    }

    private static string Features(string tmpl)
    {
        if (!tmpl.Contains("{{", StringComparison.Ordinal))
        {
            return "plain";
        }

        var f = new List<string>();
        if (EmbedRef().IsMatch(tmpl))
        {
            f.Add("embed");
        }

        if (CodeBraces().IsMatch(tmpl))
        {
            f.Add("code-region");
        }

        if (ControlRef().IsMatch(tmpl))
        {
            f.Add("control");
        }

        if (f.Count == 0)
        {
            f.Add("fields");
        }

        return string.Join(',', f);
    }

    [GeneratedRegex(@"\{\{-?\s*embed\b")]
    private static partial Regex EmbedRef();

    // Braces inside a fenced block or inline span (an approximation used only to choose strata).
    [GeneratedRegex(@"(`[^`\n]*\{\{[^`\n]*`)|(```[\s\S]*?\{\{[\s\S]*?```)")]
    private static partial Regex CodeBraces();

    [GeneratedRegex(@"\{\{-?\s*(if|range|with|define|template|block)\b")]
    private static partial Regex ControlRef();

    // Scope the saved file does not record: clock, git state, process environment, host details, platform config.
    [GeneratedRegex(@"\.(time|git|env|agentDefinition|swarm)\b|\.system\.(os|arch|hostname|username|homeDir|tempDir)\b")]
    private static partial Regex UnrecordedScope();

    private static DateTimeOffset? FirstMessageTime(Dictionary<string, object?> doc)
    {
        if (doc.GetValueOrDefault("messages") is List<object?> { Count: > 0 } msgs && msgs[0] is Dictionary<string, object?> m
            && m.GetValueOrDefault("dateTime") is string s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t))
        {
            return t;
        }

        return null;
    }

    // The scope as the runtime built it, as far as the file and this host allow: inputs, mode, working directory and caller
    // come from the file; the clock comes from the first message; host details and environment come from this machine.
    private static Dictionary<string, object?> BuildScope(Candidate c)
    {
        DateTimeOffset now = c.StartedAt ?? DateTimeOffset.UnixEpoch;
        // Only a few harmless names: the process environment can hold credentials, and templates must not see those.
        var env = new Dictionary<string, object?>();
        foreach (string name in new[] { "HOME", "USER", "LANG", "SHELL", "PWD" })
        {
            env[name] = Environment.GetEnvironmentVariable(name) ?? "";
        }

        return new Dictionary<string, object?>
        {
            ["inputs"] = c.Inputs ?? new Dictionary<string, object?>(),
            ["system"] = new Dictionary<string, object?>
            {
                ["os"] = "linux",
                ["arch"] = "amd64",
                ["hostname"] = Environment.MachineName,
                ["username"] = Environment.UserName,
                ["homeDir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["tempDir"] = "/tmp",
                ["workingDir"] = c.WorkingDirectory ?? "",
            },
            ["invocationMode"] = c.InvocationMode ?? "",
            ["caller"] = new Dictionary<string, object?> { ["instanceId"] = c.CallerInstanceId ?? "" },
            ["time"] = new Dictionary<string, object?>
            {
                ["now"] = now.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
                ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["timezone"] = "UTC",
            },
            ["git"] = new Dictionary<string, object?> { ["isRepo"] = false, ["branch"] = "", ["root"] = "", ["remote"] = "" },
            ["env"] = env,
        };
    }

    private static string Check(Candidate c, out long diffAt, out string? ours)
    {
        diffAt = -1;
        ours = null;
        bool unrecorded = UnrecordedScope().IsMatch(c.Template);
        bool embeds = EmbedRef().IsMatch(c.Template);

        string got;
        try
        {
            got = Template.Render(c.Template, BuildScope(c));
            ours = got;
        }
        catch (TemplateException ex)
        {
            if (embeds)
            {
                return "unrecoverable: embed content is not available";
            }

            if (unrecorded)
            {
                return "unrecoverable: needs scope the file does not record";
            }

            string kind = Regex.Replace(ex.Detail, "\"[^\"]*\"", "\"..\"");
            kind = Regex.Replace(kind, @"\d+", "N");
            return $"{ex.Kind} error: {kind}";
        }

        if (string.Equals(got, c.Rendered, StringComparison.Ordinal))
        {
            return "match";
        }

        if (embeds)
        {
            return "unrecoverable: embed content is not available";
        }

        if (unrecorded)
        {
            return "unrecoverable: needs scope the file does not record";
        }

        // Text with no actions renders as itself, so a stored prompt that differs was not produced by rendering this template.
        if (!c.Template.Contains("{{", StringComparison.Ordinal))
        {
            return "unrecoverable: stored prompt differs from an action-free template (edited after rendering)";
        }

        // A stored block scalar keeps at most one trailing newline, so extra trailing newlines in the output cannot be compared.
        if (string.Equals(got.TrimEnd('\n'), c.Rendered.TrimEnd('\n'), StringComparison.Ordinal))
        {
            return "unrecoverable: stored text lost trailing newlines";
        }

        byte[] a = Encoding.UTF8.GetBytes(got);
        byte[] b = Encoding.UTF8.GetBytes(c.Rendered);
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        diffAt = i;
        if (Environment.GetEnvironmentVariable("ONELLM_PARITY_SHAPE") == "1")
        {
            Console.WriteLine($"[shape] offset {i}: ours …{Shape(got, i)}… stored …{Shape(c.Rendered, i)}…");
        }

        if (a.Length == b.Length)
        {
            return "output differs (same length)";
        }

        return i == n ? (a.Length < b.Length ? "output is a prefix of the stored text" : "stored text is a prefix of the output") : "output differs (different length)";
    }

    // Letters and digits masked: shows punctuation and whitespace layout around a difference without the text.
    private static string Shape(string s, int byteOffset)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        int ch = Encoding.UTF8.GetString(bytes, 0, Math.Min(byteOffset, bytes.Length)).Length;
        int start = Math.Max(0, ch - 15);
        string w = s.Substring(start, Math.Min(40, s.Length - start));
        var sb = new StringBuilder();
        foreach (char x in w)
        {
            sb.Append(char.IsLetterOrDigit(x) ? 'x' : x == '\n' ? '↵' : x);
        }

        return sb.ToString();
    }
}
