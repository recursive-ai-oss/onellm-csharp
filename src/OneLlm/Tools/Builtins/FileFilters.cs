using System.Globalization;

namespace OneLlm.Tools.Builtins;

/// <summary>Shell-style name matching: <c>*</c> and <c>?</c> never match a <c>/</c>, <c>[a-z]</c> and <c>[^a-z]</c> are classes, and a backslash escapes the next character.</summary>
internal static class Glob
{
    // Iterative matching with one remembered star: when the rest of the pattern fails, the most recent star takes one more character
    // and matching resumes after it. That is O(pattern x name) however many stars there are; a recursive matcher is exponential on
    // patterns such as "*a*a*a*b", and a model chooses these patterns. Only the latest star needs remembering because a star never
    // crosses '/', so an earlier star could not reach any text the latest one cannot.
    internal static bool Match(string p, string s)
    {
        int pi = 0;
        int si = 0;
        int starPi = -1;
        int starSi = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && p[pi] == '*')
            {
                while (pi < p.Length && p[pi] == '*')
                {
                    pi++;
                }

                if (pi == p.Length)
                {
                    return s.IndexOf('/', si) < 0;
                }

                starPi = pi;
                starSi = si;
                continue;
            }

            if (pi < p.Length && StepMatches(p, ref pi, s[si]))
            {
                si++;
                continue;
            }

            if (starPi < 0 || starSi >= s.Length || s[starSi] == '/')
            {
                return false;
            }

            starSi++;
            si = starSi;
            pi = starPi;
        }

        while (pi < p.Length && p[pi] == '*')
        {
            pi++;
        }

        return pi == p.Length;
    }

    // Matches one character of the name against the next pattern element and advances past that element.
    private static bool StepMatches(string p, ref int pi, char ch)
    {
        char c = p[pi];
        switch (c)
        {
            case '?':
                pi++;
                return ch != '/';
            case '[':
                if (ch == '/')
                {
                    return false;
                }

                int end = ClassEnd(p, pi);
                if (end < 0 || !InClass(p, pi + 1, end, ch))
                {
                    return false;
                }

                pi = end + 1;
                return true;
            default:
                if (c == '\\' && pi + 1 < p.Length)
                {
                    pi++;
                    c = p[pi];
                }

                if (ch != c)
                {
                    return false;
                }

                pi++;
                return true;
        }
    }

    private static int ClassEnd(string p, int open)
    {
        int i = open + 1;
        if (i < p.Length && p[i] == '^')
        {
            i++;
        }

        bool first = true;
        for (; i < p.Length; i++)
        {
            if (p[i] == '\\')
            {
                i++;
            }
            else if (p[i] == ']' && !first)
            {
                return i;
            }

            first = false;
        }

        return -1;
    }

    private static bool InClass(string p, int start, int end, char ch)
    {
        bool negate = p[start] == '^';
        int i = negate ? start + 1 : start;
        bool matched = false;
        while (i < end)
        {
            char lo = p[i];
            if (lo == '\\' && i + 1 < end)
            {
                lo = p[++i];
            }

            char hi = lo;
            if (i + 2 < end && p[i + 1] == '-')
            {
                hi = p[i + 2];
                if (hi == '\\' && i + 3 < end)
                {
                    hi = p[i + 3];
                    i++;
                }

                i += 2;
            }

            i++;
            if (lo <= ch && ch <= hi)
            {
                matched = true;
            }
        }

        return matched != negate;
    }

    /// <summary>The include/exclude pattern forms: <c>*.ext</c>, <c>name</c>, <c>**/name</c>, <c>dir/</c> (directories only) and <c>/path</c> (anchored to the search root).</summary>
    internal static bool MatchesPattern(string pattern, string relPath, string name, bool isDir)
    {
        if (pattern.EndsWith('/'))
        {
            if (!isDir)
            {
                return false;
            }

            pattern = pattern[..^1];
        }

        if (pattern.StartsWith('/'))
        {
            return Match(pattern[1..], relPath);
        }

        if (Match(pattern, relPath) || Match(pattern, name))
        {
            return true;
        }

        if (pattern.Contains("**", StringComparison.Ordinal))
        {
            string[] parts = pattern.Split("**");
            if (parts.Length == 2 && parts[0].Length == 0 && parts[1].StartsWith('/'))
            {
                string suffix = parts[1][1..];
                if (Match(suffix, name))
                {
                    return true;
                }

                string[] segs = relPath.Split('/');
                for (int i = 0; i < segs.Length; i++)
                {
                    if (Match(suffix, string.Join('/', segs[i..])))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

/// <summary>The patterns of one <c>.gitignore</c> file.</summary>
internal sealed class GitIgnore
{
    private readonly List<(string Pattern, bool Negation, bool DirOnly, bool Anchored)> _patterns = [];

    internal GitIgnore(string basePath, IEnumerable<string> lines)
    {
        BasePath = basePath;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            bool negation = line[0] == '!';
            if (negation)
            {
                line = line[1..];
            }

            bool dirOnly = line.EndsWith('/');
            if (dirOnly)
            {
                line = line[..^1];
            }

            bool anchored = false;
            if (line.StartsWith('/'))
            {
                anchored = true;
                line = line[1..];
            }
            else if (line.Contains('/', StringComparison.Ordinal) && !line.StartsWith("**/", StringComparison.Ordinal))
            {
                anchored = true;
            }

            _patterns.Add((line, negation, dirOnly, anchored));
        }
    }

    internal string BasePath { get; }

    internal static GitIgnore? TryLoad(string directory)
    {
        string file = Path.Join(directory, ".gitignore");
        try
        {
            return File.Exists(file) ? new GitIgnore(directory, File.ReadLines(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal bool Matches(string relPath, bool isDir)
    {
        bool matched = false;
        string name = relPath[(relPath.LastIndexOf('/') + 1)..];
        foreach ((string pattern, bool negation, bool dirOnly, bool anchored) in _patterns)
        {
            if (dirOnly && !isDir)
            {
                continue;
            }

            bool hit = anchored ? MatchOne(pattern, relPath) : MatchOne(pattern, name) || MatchOne(pattern, relPath);
            if (hit)
            {
                matched = !negation;
            }
        }

        return matched;
    }

    private static bool MatchOne(string pattern, string path)
    {
        if (pattern.Contains("**", StringComparison.Ordinal))
        {
            if (pattern == "**")
            {
                return true;
            }

            if (pattern.StartsWith("**/", StringComparison.Ordinal))
            {
                string suffix = pattern[3..];
                if (Glob.Match(suffix, path) || Glob.Match(suffix, path[(path.LastIndexOf('/') + 1)..]) || path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }

                string[] parts = path.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (Glob.Match(suffix, string.Join('/', parts[i..])))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pattern.EndsWith("/**", StringComparison.Ordinal))
            {
                string prefix = pattern[..^3];
                return path.StartsWith(prefix + "/", StringComparison.Ordinal) || path == prefix;
            }

            string[] halves = pattern.Split("/**/");
            if (halves.Length == 2)
            {
                return path.StartsWith(halves[0] + "/", StringComparison.Ordinal) && path.EndsWith(halves[1], StringComparison.Ordinal);
            }
        }

        return Glob.Match(pattern, path);
    }
}

/// <summary>One entry found by <see cref="FileWalker"/>.</summary>
internal sealed record WalkEntry(string Path, string FullPath, bool IsDir, long Size, DateTime ModifiedUtc, bool IsSymlink, string? LinkTarget);

/// <summary>Walks a directory the way list_files and search_files_regex do, without ever descending into a link.</summary>
internal sealed class FileWalker
{
    private readonly string _baseDir;
    private readonly int _maxDepth;
    private readonly IReadOnlyList<string> _exclude;
    private readonly IReadOnlyList<string> _include;
    private readonly bool _useGitIgnore;
    private readonly Dictionary<string, GitIgnore?> _cache = new(StringComparer.Ordinal);
    private readonly List<GitIgnore> _ancestors = [];
    private readonly string _baseReal;

    // baseDir is the real directory to walk. Ancestor .gitignore files are read only where the grants allow reading.
    internal FileWalker(string baseDir, ICapabilities caps, int maxDepth, IReadOnlyList<string> exclude, IReadOnlyList<string> include, bool useGitIgnore)
    {
        _baseDir = baseDir;
        _baseReal = baseDir;
        _maxDepth = maxDepth;
        _exclude = exclude;
        _include = include;
        _useGitIgnore = useGitIgnore;
        if (useGitIgnore)
        {
            LoadAncestors(caps);
        }
    }

    internal List<WalkEntry> Walk(CancellationToken cancellationToken)
    {
        var result = new List<WalkEntry>();
        WalkDir(_baseDir, "", result, cancellationToken);
        return result;
    }

    internal bool IsExcludedFile(string rel, string name) => !ShouldInclude(rel, name, false);

    private void WalkDir(string dir, string relDir, List<WalkEntry> result, CancellationToken ct)
    {
        FileSystemInfo[] infos;
        try
        {
            infos = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (FileSystemInfo info in infos)
        {
            ct.ThrowIfCancellationRequested();
            string rel = relDir.Length == 0 ? info.Name : relDir + "/" + info.Name;
            string? link = null;
            try
            {
                link = info.LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Treated as a plain entry.
            }

            bool isLink = link is not null;
            bool isRealDir = info is DirectoryInfo && !isLink;
            bool isDir = isRealDir || (isLink && Directory.Exists(info.FullName));
            int depth = rel.Count(c => c == '/');
            if (isDir && _maxDepth >= 0 && depth >= _maxDepth)
            {
                if (!ShouldSkipDir(rel, info.Name))
                {
                    result.Add(Entry(rel, info, true, isLink, link));
                }

                continue;
            }

            if (isDir)
            {
                if (ShouldSkipDir(rel, info.Name))
                {
                    continue;
                }

                if (ShouldInclude(rel, info.Name, true))
                {
                    result.Add(Entry(rel, info, true, isLink, link));
                }

                if (isRealDir)
                {
                    WalkDir(info.FullName, rel, result, ct);
                }

                continue;
            }

            if (ShouldInclude(rel, info.Name, false))
            {
                result.Add(Entry(rel, info, false, isLink, link));
            }
        }
    }

    private static WalkEntry Entry(string rel, FileSystemInfo info, bool isDir, bool isLink, string? link)
    {
        long size = 0;
        DateTime modified = default;
        try
        {
            modified = info.LastWriteTimeUtc;
            if (!isDir)
            {
                // A link to a file reports the target's size; a broken link reports 0.
                size = isLink ? (File.Exists(info.FullName) ? new FileInfo(info.FullName).Length : 0) : ((FileInfo)info).Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the entry with what is known.
        }

        return new WalkEntry(rel, info.FullName, isDir, size, modified, isLink, link);
    }

    private bool ShouldSkipDir(string rel, string name)
    {
        foreach (string p in _exclude)
        {
            if (Glob.MatchesPattern(p, rel, name, true))
            {
                return true;
            }
        }

        return _useGitIgnore && IsGitIgnored(rel, true);
    }

    private bool ShouldInclude(string rel, string name, bool isDir)
    {
        foreach (string p in _exclude)
        {
            if (Glob.MatchesPattern(p, rel, name, isDir))
            {
                return false;
            }
        }

        if (_useGitIgnore && IsGitIgnored(rel, isDir))
        {
            return false;
        }

        if (_include.Count > 0 && !isDir)
        {
            return _include.Any(p => Glob.MatchesPattern(p, rel, name, false));
        }

        return true;
    }

    private bool IsGitIgnored(string rel, bool isDir)
    {
        foreach (GitIgnore gi in _ancestors)
        {
            string entry = Path.Join(_baseReal, rel);
            string relToAncestor = Path.GetRelativePath(gi.BasePath, entry).Replace('\\', '/');
            if (!relToAncestor.StartsWith("../", StringComparison.Ordinal) && relToAncestor != ".." && gi.Matches(relToAncestor, isDir))
            {
                return true;
            }
        }

        string[] parts = rel.Split('/');
        string current = _baseDir;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!_cache.TryGetValue(current, out GitIgnore? gi))
            {
                gi = GitIgnore.TryLoad(current);
                _cache[current] = gi;
            }

            if (gi is not null && gi.Matches(i == 0 ? rel : string.Join('/', parts[i..]), isDir))
            {
                return true;
            }

            if (i < parts.Length - 1)
            {
                current = Path.Join(current, parts[i]);
            }
        }

        return false;
    }

    // Ancestor .gitignore files apply to the walked tree, as in git. Only directories inside a read root are looked at, so the walk
    // never reads outside what the host granted; the climb also stops at a repository boundary.
    private void LoadAncestors(ICapabilities caps)
    {
        var collected = new List<GitIgnore>();
        string? dir = Path.GetDirectoryName(_baseDir);
        while (!string.IsNullOrEmpty(dir) && InsideReadRoot(caps, dir))
        {
            GitIgnore? gi = GitIgnore.TryLoad(dir);
            if (gi is not null)
            {
                collected.Add(gi);
            }

            if (Directory.Exists(Path.Join(dir, ".git")))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        collected.Reverse();
        _ancestors.AddRange(collected);
    }

    private static bool InsideReadRoot(ICapabilities caps, string dir)
    {
        try
        {
            _ = caps.ResolveRead(dir);
            return true;
        }
        catch (CapabilityMissingException)
        {
            return false;
        }
    }

    internal static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
