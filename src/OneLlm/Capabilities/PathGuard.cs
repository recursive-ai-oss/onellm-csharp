namespace OneLlm.Access;

/// <summary>
/// Turns a path a tool was given into the real path to open, and refuses paths that are not under a granted root.
/// </summary>
/// <remarks>
/// <para>The path is resolved one component at a time: every symbolic link (and, on Windows, junction) is followed and every
/// <c>..</c> is applied to the real directory reached so far, as the operating system would. The last component may not exist
/// yet, which is what a write target looks like; its parent chain is resolved the same way. The result is compared with the
/// roots, which are resolved the same way, so a link inside a root that points outside it is refused whether it is a file, a
/// directory or dangling.</para>
/// <para>What is guaranteed: at the moment of the check, the real path lies under a root. <see cref="GuardedFile"/> checks again
/// after it opens the file and refuses when the path no longer resolves to the same place, which catches a link swapped in
/// between the two steps and left there. A concurrent writer that swaps a link in and out again within that window, or one that
/// puts a hard link to an outside file inside a root, is not stopped; use an operating-system sandbox when the tree is
/// writable by someone hostile.</para>
/// </remarks>
internal static class PathGuard
{
    private const int MaxLinkDepth = 40;

    // Names Windows treats as devices in any directory, with or without an extension.
    private static readonly HashSet<string> s_reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³", "COM0", "LPT0",
    };

    /// <summary>Resolves <paramref name="path"/> and checks it lies under one of <paramref name="roots"/>.</summary>
    internal static string Resolve(IReadOnlyList<string> roots, string? workingDirectory, string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(path);
        string what = write ? "write" : "read";
        if (roots.Count == 0)
        {
            throw Denied(path, $"no directory is granted for {what} access");
        }

        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw Denied(path.Replace("\0", "\\0", StringComparison.Ordinal), "the path is empty or contains a NUL character");
        }

        string absolute = MakeAbsolute(path, workingDirectory ?? roots[0]);
        if (OperatingSystem.IsWindows() && !roots.Any(r => string.Equals(Path.GetPathRoot(MakeAbsolute(r, r)), Path.GetPathRoot(absolute), StringComparison.OrdinalIgnoreCase)))
        {
            // A different drive or a network share: refused before any component is probed, so no connection is attempted.
            throw Denied(path, "it is on a drive or share no directory is granted on");
        }

        string real = RealPath(absolute, path);
        foreach (string root in roots)
        {
            string realRoot;
            try
            {
                realRoot = RealPath(MakeAbsolute(root, root), root);
            }
            catch (CapabilityMissingException)
            {
                continue;
            }

            if (IsUnder(real, realRoot))
            {
                if (OperatingSystem.IsWindows())
                {
                    RefuseShortNames(realRoot, real, path);
                }

                return real;
            }
        }

        throw Denied(path, $"it is outside the directories granted for {what} access");
    }

    // Names that make a tool read its configuration or run hooks from the tree: a write there turns an allow-listed program into
    // arbitrary command execution.
    private static readonly string[] s_repoControlNames = [".git", ".hg", ".svn", ".gitmodules", ".bzr", ".jj", "_darcs", ".fslckout", ".sl", "CVS"];

    /// <summary>
    /// After a write path resolved under a root: refuses repository control paths unless allowed, and anything under a protected directory.
    /// </summary>
    internal static void CheckWriteProtection(IReadOnlyList<string> roots, string real, IReadOnlyList<string> protectedPaths, bool allowRepoControl, string original, IReadOnlySet<string>? pending = null, bool gitListed = false)
    {
        if (IsInProtected(real, protectedPaths))
        {
            throw Refused(original, "it is inside the runtime's own state, which tools may not read or change");
        }

        if (allowRepoControl)
        {
            return;
        }

        // Every component of the path counts, the root's own and the ones above it included: a root that is a control directory, or
        // lies below one (.git/hooks, a bare repository's hooks or refs/heads), is not exempt. A host that wants one passes
        // WithRepoControlWrites().
        string top = Path.GetPathRoot(real) ?? "";
        string[] parts = real[top.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (s_repoControlNames.Any(n => NameEquals(part.TrimEnd(' ', '.'), n)))
            {
                throw Refused(original, "repository control files can change what a program on the shell allow-list runs");
            }
        }

        // By role: with git on the shell allow-list, the files git reads its configuration from and the directories it runs hooks from
        // are refused wherever they are. The content rule below cannot see a directory that is still empty, and a listed program
        // (git init --separate-git-dir, mkdir) can complete it around a planted config. Files of these names stay writable when
        // git is not listed, because nothing then executes them.
        if (gitListed)
        {
            string last = parts.Length > 0 ? parts[^1] : "";
            if (s_gitRoleFiles.Any(n => NameEquals(last, n)) || parts.Take(parts.Length - 1).Any(p => NameEquals(p, "hooks") || NameEquals(p, "info")))
            {
                throw Refused(original, "git is on the shell allow-list, and git runs the commands a file of this kind names (config, hooks)");
            }
        }

        // A git directory is recognised by what it holds, not by its name: a repository made with a separate git dir (the working tree's
        // .git is a file pointing at it) or a bare repository has no ".git" in its path, yet a planted config or hook in it runs
        // commands for an allow-listed git. A directory is one when it has HEAD, objects and refs. The check counts the file being
        // written, so the write that would complete the set is refused too, whatever order the pieces arrive in.
        string dir = top;
        for (int i = -1; i < parts.Length - 1; i++)
        {
            if (i >= 0)
            {
                dir = Path.Join(dir, parts[i]);
            }

            string first = parts[i + 1];
            // Files being created by the same operation count as present, so a batch cannot complete a git directory piece by piece.
            bool Pending(string name, bool tree) => pending is not null && pending.Any(p => tree
                ? IsUnder(p, Path.Join(dir, name)) && !string.Equals(p, Path.Join(dir, name), PathComparison)
                : string.Equals(p, Path.Join(dir, name), PathComparison));
            bool head = NameEquals(first, "HEAD") || File.Exists(Path.Join(dir, "HEAD")) || Pending("HEAD", false);
            bool objects = NameEquals(first, "objects") || Directory.Exists(Path.Join(dir, "objects")) || Pending("objects", true);
            bool refs = NameEquals(first, "refs") || Directory.Exists(Path.Join(dir, "refs")) || Pending("refs", true);
            if (head && objects && refs)
            {
                throw Refused(original, "it is inside a git directory (HEAD, objects and refs), whose config and hooks can make an allow-listed git run commands");
            }
        }
    }

    private static readonly string[] s_gitRoleFiles = ["config", "config.worktree", "commondir", "gitdir", ".gitconfig"];

    /// <summary>
    /// Whether the shell policy lists git, so that its configuration and hook files are executable content. An entry counts when its program
    /// is git or a git-* helper by name, or when the program it resolves to is: the entry is looked up on the host's PATH and its links are
    /// followed to the final target, so a symlink called mygit that points at git is git.
    /// </summary>
    internal static bool GitListed(ShellPolicy policy) =>
        policy.Enabled && policy.Commands is { } cmds && cmds.Any(static c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } tokens
            && (IsGitDriver(tokens[0]) || tokens.Any(IsGitProgram)));

    // Programs that start other programs, or drive git themselves. An entry naming one of them is treated as listing git: refusing more is
    // the safe reading, because a bare launcher entry (env, nice, timeout) runs anything, git included, and a git front end runs git.
    private static readonly string[] s_gitDrivers =
    [
        "env", "nice", "ionice", "timeout", "nohup", "xargs", "stdbuf", "time", "command", "exec", "setsid", "chroot", "sudo", "doas", "watch", "parallel",
        "gh", "hub", "gitk", "tig", "lazygit", "make", "gmake", "npm", "npx", "yarn", "pnpm", "cargo", "pip", "go", "dotnet", "find",
    ];

    private static bool IsGitDriver(string program) =>
        s_gitDrivers.Contains(Path.GetFileNameWithoutExtension(program.Replace('\\', '/').Split('/')[^1]), StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_gitPrograms = new(StringComparer.Ordinal);

    private static bool IsGitProgram(string program) => program.Length > 0 && s_gitPrograms.GetOrAdd(program, static p =>
    {
        if (GitName(p))
        {
            return true;
        }

        try
        {
            string? file = null;
            if (p.AsSpan().IndexOfAny('/', '\\') >= 0)
            {
                file = p;
            }
            else
            {
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Join(dir, p);
                    if (Path.IsPathFullyQualified(dir) && File.Exists(candidate))
                    {
                        file = candidate;
                        break;
                    }
                }
            }

            if (file is null)
            {
                return false;
            }

            FileSystemInfo? target = new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && GitName(target.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An entry that cannot be looked up is judged by its name alone.
            return false;
        }
    });

    // git, git.exe and the git-* programs (git-upload-pack, git-receive-pack, ...), by file name.
    private static bool GitName(string program)
    {
        string stem = Path.GetFileNameWithoutExtension(program.Replace('\\', '/').Split('/')[^1]);
        return stem.Equals("git", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("git-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two names are the same file name for protection purposes: always case-insensitive (a false match only over-refuses). On Windows and macOS, where U+017F (long s) and U+212A (Kelvin sign) are
    /// folded by the file system's own tables or may be, and U+0131 (dotless i) might be; folding all three over-refuses at worst.
    /// </summary>
    internal static bool NameEquals(string a, string b) =>
        string.Equals(OperatingSystem.IsLinux() ? a : Fold(a), OperatingSystem.IsLinux() ? b : Fold(b), StringComparison.OrdinalIgnoreCase);

    internal static string Fold(string s) =>
        s.AsSpan().IndexOfAny('ı', 'ſ', 'K') < 0 ? s : s.Replace('ı', 'i').Replace('ſ', 's').Replace('K', 'k');

    /// <summary>Whether a real path lies under any protected directory.</summary>
    internal static bool IsInProtected(string real, IReadOnlyList<string> protectedPaths)
    {
        foreach (string realProtected in RealProtected(protectedPaths))
        {
            if (IsUnder(real, realProtected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The protected directories as real paths (links resolved), so a link to the state directory is caught too.</summary>
    internal static string[] RealProtected(IReadOnlyList<string> protectedPaths)
    {
        var result = new string[protectedPaths.Count];
        for (int i = 0; i < result.Length; i++)
        {
            string p = protectedPaths[i];
            try
            {
                result[i] = RealPath(MakeAbsolute(p, p), p);
            }
            catch (CapabilityMissingException)
            {
                result[i] = p;
            }
        }

        return result;
    }

    /// <summary>Refuses a read inside a protected directory: the runtime's saved instances and records hold other agents' transcripts.</summary>
    internal static void CheckReadProtection(string real, IReadOnlyList<string> protectedPaths, string original)
    {
        if (protectedPaths.Count > 0 && IsInProtected(real, protectedPaths))
        {
            throw Denied(original, "it is inside the runtime's own state, which tools may not read or change");
        }
    }

    private static CapabilityMissingException Refused(string path, string why) =>
        new($"Writing '{path}' ({why})", "write access to it from the host, with Capabilities.WithRepoControlWrites() or by not protecting the directory");

    /// <summary>Whether <paramref name="path"/> equals <paramref name="root"/> or lies below it. Both must already be real paths.</summary>
    internal static bool IsUnder(string path, string root) => IsUnder(path, root, PathComparison);

    internal static bool IsUnder(string path, string root, StringComparison cmp)
    {
        if (cmp != StringComparison.Ordinal)
        {
            path = Fold(path);
            root = Fold(root);
        }

        string r = root.Length > 1 ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : root;
        if (string.Equals(path, r, cmp))
        {
            return true;
        }

        string prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, cmp);
    }

    /// <summary>How paths compare: ordinal on Linux, ignoring case on Windows and macOS, whose usual file systems do.</summary>
    internal static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string MakeAbsolute(string path, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            // "\dir" and "C:dir" depend on process state (the current drive and its current directory); refuse them rather than guess.
            if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            {
                throw Denied(path, "the path names a drive or root without being fully qualified");
            }

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                throw Denied(path, "device and extended-length paths are not allowed");
            }
        }

        return Path.IsPathFullyQualified(path) ? path : Path.Join(workingDirectory, path);
    }

    // The part of a path below its root, cut by length so no short name is expanded on the way (see RefuseShortNames); "." for the root.
    private static string RelativeBelow(string root, string real) =>
        real.Length > root.Length ? real[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : ".";

    // Windows also answers to a generated 8.3 name for a long name (GIT~1 for .git, ONELLM~1 for .onellm). The protection checks compare
    // names, so a short alias would walk around them. A component below the root that contains '~' must therefore be the exact name of an
    // entry in its directory; one that exists only through an alias is refused. (The root is the host's own spelling and is not checked.)
    private static void RefuseShortNames(string realRoot, string real, string original)
    {
        // Cut the root off by length. Path.GetRelativePath (like Path.GetFullPath) asks Windows to expand short names, which would hide
        // exactly the aliases this looks for.
        string relative = RelativeBelow(realRoot, real);
        if (relative == "." || !relative.Contains('~', StringComparison.Ordinal))
        {
            return;
        }

        string dir = realRoot;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            string next = Path.Join(dir, part);
            if (part.Contains('~', StringComparison.Ordinal) && (File.Exists(next) || Directory.Exists(next)))
            {
                bool exact;
                try
                {
                    exact = Directory.EnumerateFileSystemEntries(dir).Any(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    exact = false;
                }

                if (!exact)
                {
                    throw Denied(original, "it uses a short (8.3) alias of a name; use the full name");
                }
            }

            dir = next;
        }
    }

    private static string RealPath(string absolute, string original)
    {
        string root = Path.GetPathRoot(absolute) ?? throw Denied(original, "the path has no root");
        char[] seps = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        var pending = new Stack<string>();
        PushComponents(pending, absolute[root.Length..], seps);
        string current = root;
        int links = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                string? parent = Path.GetDirectoryName(current.TrimEnd(seps));
                current = string.IsNullOrEmpty(parent) ? root : parent;
                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                CheckWindowsName(part, original);

                // Win32 drops trailing dots and spaces from a name, so ".onellm." is ".onellm"; compare the name the file system will use.
                string trimmed = part.TrimEnd(' ', '.');
                if (trimmed.Length == 0)
                {
                    throw Denied(original, "a name made only of dots and spaces");
                }

                part = trimmed;
            }

            string next = Path.Join(current, part);
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw Denied(original, "a path component cannot be inspected");
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinkDepth)
            {
                throw Denied(original, "too many levels of symbolic links");
            }

            if (OperatingSystem.IsWindows() && target.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                target = target[4..];
            }

            if (Path.IsPathRooted(target))
            {
                string targetRoot = Path.GetPathRoot(target) ?? root;
                current = targetRoot;
                PushComponents(pending, target[targetRoot.Length..], seps);
            }
            else
            {
                PushComponents(pending, target, seps);
            }
        }

        return current;
    }

    private static void PushComponents(Stack<string> pending, string relative, char[] seps)
    {
        string[] parts = relative.Split(seps);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    internal static void CheckWindowsName(string part, string original)
    {
        // A colon names an alternate data stream; reserved names open devices. Neither is a plain file.
        if (part.Contains(':', StringComparison.Ordinal))
        {
            throw Denied(original, "alternate data streams are not allowed");
        }

        string trimmed = part.TrimEnd(' ', '.');
        int dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        string stem = dot >= 0 ? trimmed[..dot] : trimmed;
        if (s_reservedNames.Contains(stem.TrimEnd(' ')))
        {
            throw Denied(original, "reserved device names are not allowed");
        }
    }

    internal static CapabilityMissingException Denied(string path, string why) =>
        new($"Access to '{path}' ({why})", "a file root that contains it to the runtime, with Capabilities.Files(...)");
}
