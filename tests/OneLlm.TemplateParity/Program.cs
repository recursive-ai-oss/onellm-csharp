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
//   TemplateParity <instances-dir> <results-file> [sample-size] [candidates] [seed] [reference-driver|-] [swarm-root]
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
        string? InvocationMode, string? CallerInstanceId, string Features, DateTimeOffset? StartedAt, string? AgentDefinition);

    private static GoRef? s_go;
    private static string? s_swarmRoot;
    private static int s_clockMatches;
    private static readonly SortedDictionary<int, int> s_clockLag = new();

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: TemplateParity <instances-dir> <results-file> [sample-size] [candidates] [seed] [reference-driver|-] [swarm-root]");
            return 2;
        }

        string dir = args[0];
        string resultsPath = args[1];
        int sampleSize = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300;
        int candidates = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 12000;
        int seed = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 20261105;
        s_go = args.Length > 5 && args[5] != "-" ? new GoRef(args[5]) : null;
        s_swarmRoot = args.Length > 6 ? args[6] : null;

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
                    doc.GetValueOrDefault("callerInstanceId") as string, Features(tmpl), FirstMessageTime(doc), doc.GetValueOrDefault("agentDefinition") as string));
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
                string? g = s_go.Render(c.Template, BuildScope(c, c.StartedAt));
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
        Line($"clock derived from the first message time (render happened this many seconds later) for {s_clockMatches} matches: " + string.Join(", ", s_clockLag.Select(kv => $"{kv.Key}s x{kv.Value}")));
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

    // Scope the saved file does not record: git state, process environment, host details and the host's own configuration
    // namespace (its template key comes from ONELLM_PARITY_CONFIG_KEY, so no deployment name is built into this tool).
    private static readonly Regex s_unrecorded = new(
        @"\.(git|env|agentDefinition|swarm" + (Environment.GetEnvironmentVariable("ONELLM_PARITY_CONFIG_KEY") is { Length: > 0 } k ? "|" + Regex.Escape(k) : "")
        + @")\b|\.system\.(os|arch|hostname|username|homeDir|tempDir)\b",
        RegexOptions.CultureInvariant);

    private static Regex UnrecordedScope() => s_unrecorded;

    // A reference to the clock; the part of it that needs the second (the others only need the date and zone).
    [GeneratedRegex(@"\.time\b")]
    private static partial Regex TimeRef();

    [GeneratedRegex(@"\.time(\.now\b|\s*(\||\}\}|\)))")]
    private static partial Regex TimeNowRef();

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
    private static Dictionary<string, object?> BuildScope(Candidate c, DateTimeOffset? at)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UnixEpoch;
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
                ["timezone"] = ZoneName(now) ?? "UTC",
            },
            ["git"] = new Dictionary<string, object?> { ["isRepo"] = false, ["branch"] = "", ["root"] = "", ["remote"] = "" },
            ["env"] = env,
        };
    }

    // The zone abbreviation the runtime would have printed, from the offset the first message recorded: the host's own zone
    // (named as the platform's database names it) or UTC. Anything else cannot be derived.
    private static string? ZoneName(DateTimeOffset t)
    {
        if (t.Offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        TimeZoneInfo local = TimeZoneInfo.Local;
        if (local.GetUtcOffset(t) != t.Offset)
        {
            return null;
        }

        string name = local.IsDaylightSavingTime(t) ? local.DaylightName : local.StandardName;
        return name switch
        {
            "Central Standard Time" or "Central Daylight Time" or "Central Time" => local.IsDaylightSavingTime(t) ? "CDT" : "CST",
            _ => null,
        };
    }

    // Finds the markdown body stored for a swarm URI: <root>/<uri>/prompt.md without its front matter. Relative references
    // are tried against the directory of the instance's agent definition URI.
    private sealed class SwarmPromptSource(string root, string? agentDefinition) : IEmbedSource
    {
        public int Unresolved;
        public int Resolved;
        public DateTime NewestFile;
        public bool UsesUnrecorded;

        public EmbedContent? Resolve(string reference, string? referrerKey)
        {
            string uri = reference;
            if (!uri.StartsWith('/'))
            {
                string? baseUri = agentDefinition is null ? null : agentDefinition[..Math.Max(0, agentDefinition.LastIndexOf('/'))];
                if (string.IsNullOrEmpty(baseUri))
                {
                    Unresolved++;
                    return null;
                }

                uri = baseUri + "/" + uri;
            }

            string full = Path.GetFullPath(Path.Combine(root, uri.TrimStart('/'), "prompt.md"));
            string rootFull = Path.GetFullPath(root).TrimEnd('/') + "/";
            if (!full.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(full))
            {
                Unresolved++;
                return null;
            }

            Resolved++;
            DateTime mtime = File.GetLastWriteTimeUtc(full);
            NewestFile = mtime > NewestFile ? mtime : NewestFile;
            string body = StripFrontMatter(File.ReadAllText(full));
            UsesUnrecorded |= UnrecordedScope().IsMatch(body);
            return new EmbedContent("swarm:" + uri, body);
        }

        // A leading "---" line opens YAML front matter that ends at the next "---" line; the body is what follows.
        private static string StripFrontMatter(string text)
        {
            if (!text.StartsWith("---", StringComparison.Ordinal))
            {
                return text;
            }

            int nl = text.IndexOf('\n', StringComparison.Ordinal);
            if (nl < 0)
            {
                return text;
            }

            int pos = nl + 1;
            while (pos < text.Length)
            {
                int end = text.IndexOf('\n', pos);
                string line = (end < 0 ? text[pos..] : text[pos..end]).TrimEnd('\r');
                if (line == "---")
                {
                    return end < 0 ? "" : text[(end + 1)..];
                }

                if (end < 0)
                {
                    break;
                }

                pos = end + 1;
            }

            return text;
        }
    }

    private static string Check(Candidate c, out long diffAt, out string? ours)
    {
        string cause = CheckCore(c, out diffAt, out ours);
        if (cause != "match" && !cause.StartsWith("unrecoverable:", StringComparison.Ordinal) && c.Inputs is Dictionary<string, object?> inputs)
        {
            // A saved multi-line string input comes back with a trailing newline the caller may not have sent. Greedily drop
            // that newline from the inputs whose removal moves the first difference later; if the render then matches, the
            // stored input (not the engine) is what differs.
            var trial = new Dictionary<string, object?>(inputs);
            long at = diffAt;
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (string key in trial.Keys.ToArray())
                {
                    if (trial[key] is not string text || !text.EndsWith('\n'))
                    {
                        continue;
                    }

                    var next = new Dictionary<string, object?>(trial) { [key] = text[..^1] };
                    string again = CheckCore(c with { Inputs = next }, out long nextAt, out _);
                    if (again == "match")
                    {
                        return "unrecoverable: a stored string input has a trailing newline the original lacked";
                    }

                    if (nextAt > at)
                    {
                        trial = next;
                        at = nextAt;
                        progress = true;
                    }
                }
            }
        }

        return cause;
    }

    private static string CheckCore(Candidate c, out long diffAt, out string? ours)
    {
        diffAt = -1;
        ours = null;
        bool unrecorded = UnrecordedScope().IsMatch(c.Template);
        bool embeds = EmbedRef().IsMatch(c.Template);
        bool usesTime = TimeRef().IsMatch(c.Template);
        if (usesTime && (c.StartedAt is null || ZoneName(c.StartedAt.Value) is null))
        {
            unrecorded = true;
        }

        // The file records no render time, but the first message is stamped just before the prompt is rendered (observed: seconds
        // to minutes); search a window of seconds after it and count which offset matched.
        int[] lags = usesTime && TimeNowRef().IsMatch(c.Template) ? Enumerable.Range(-10, 911).ToArray() : new[] { 0 };
        DateTimeOffset? at0 = c.StartedAt is null ? null : new DateTimeOffset(c.StartedAt.Value.Ticks - c.StartedAt.Value.Ticks % TimeSpan.TicksPerSecond, c.StartedAt.Value.Offset);
        SwarmPromptSource? src = embeds && s_swarmRoot is not null ? new SwarmPromptSource(s_swarmRoot, c.AgentDefinition) : null;
        var render = new TemplateRenderOptions { EmbedSources = src is null ? null : new IEmbedSource[] { src } };

        string got = "";
        string best = "";
        int bestPrefix = -1;
        TemplateException? failure = null;
        foreach (int lag in lags)
        {
            DateTimeOffset? now = at0?.AddSeconds(lag);
            try
            {
                got = Template.Render(c.Template, BuildScope(c, now), null, render);
                failure = null;
            }
            catch (TemplateException ex)
            {
                failure = ex;
                got = "";
                break;
            }

            if (lag == 0)
            {
                ours = got;
            }

            // With a clock to search, the closest candidate (longest shared prefix) is the one a mismatch is reported against,
            // so the first differing offset points past the timestamp at the real difference.
            int common = 0;
            int limit = Math.Min(got.Length, c.Rendered.Length);
            while (common < limit && got[common] == c.Rendered[common])
            {
                common++;
            }

            if (common > bestPrefix)
            {
                bestPrefix = common;
                best = got;
            }

            if (string.Equals(got, c.Rendered, StringComparison.Ordinal))
            {
                if (usesTime && lags.Length > 1)
                {
                    s_clockMatches++;
                    s_clockLag[lag] = s_clockLag.GetValueOrDefault(lag) + 1;
                }

                return "match";
            }

            if (lags.Length > 1 && string.Equals(got.TrimEnd('\n'), c.Rendered.TrimEnd('\n'), StringComparison.Ordinal))
            {
                return "unrecoverable: stored text lost trailing newlines";
            }
        }

        got = lags.Length > 1 && failure is null ? best : ours ?? got;
        if (usesTime && c.StartedAt is not null && Environment.GetEnvironmentVariable("ONELLM_PARITY_SHAPE") == "1")
        {
            Match tm = Regex.Match(c.Rendered, @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(Z|[-+]\d\d:\d\d)");
            if (tm.Success && DateTimeOffset.TryParse(tm.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset st))
            {
                Console.WriteLine($"[clock] stored minus first message: {(long)(st - c.StartedAt.Value).TotalSeconds}s");
            }
            else
            {
                Console.WriteLine("[clock] no timestamp in stored text");
            }
        }

        if (failure is not null)
        {
            if (embeds && (src is null || src.Unresolved > 0))
            {
                return "unrecoverable: embed target is not a stored prompt file on disk";
            }

            if (unrecorded)
            {
                return "unrecoverable: needs scope the file does not record";
            }

            string kind = Regex.Replace(failure.Detail, "\"[^\"]*\"", "\"..\"");
            kind = Regex.Replace(kind, @"\d+", "N");
            return $"{failure.Kind} error: {kind}";
        }

        if (unrecorded)
        {
            return "unrecoverable: needs scope the file does not record";
        }

        if (src is { UsesUnrecorded: true })
        {
            return "unrecoverable: needs scope the file does not record";
        }

        // The embedded files are read as they are now; one edited after the instance rendered cannot reproduce its prompt.
        if (src is { Resolved: > 0 } && c.StartedAt is not null && src.NewestFile > c.StartedAt.Value.UtcDateTime)
        {
            return "unrecoverable: an embedded file was modified after the instance rendered";
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
            if (Environment.GetEnvironmentVariable("ONELLM_PARITY_ACTIONS") == "1")
            {
                Console.WriteLine("[actions] " + string.Join(" ", Regex.Matches(c.Template, @"\{\{.*?\}\}", RegexOptions.Singleline).Select(m => Regex.Replace(m.Value, @"(?<![\w.])(\w{5,})", "w").Replace("\n", "↵")).Take(25)));
            }
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
        // ONELLM_PARITY_DIGITS=1 keeps digits, to compare timestamps and numbers; letters stay masked.
        bool keepDigits = Environment.GetEnvironmentVariable("ONELLM_PARITY_DIGITS") == "1";
        foreach (char x in w)
        {
            sb.Append(char.IsAsciiDigit(x) && keepDigits ? x : char.IsLetterOrDigit(x) ? 'x' : x == '\n' ? '↵' : x);
        }

        return sb.ToString();
    }
}
