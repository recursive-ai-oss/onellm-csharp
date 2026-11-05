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
                return real;
            }
        }

        throw Denied(path, $"it is outside the directories granted for {what} access");
    }

    /// <summary>Whether <paramref name="path"/> equals <paramref name="root"/> or lies below it. Both must already be real paths.</summary>
    internal static bool IsUnder(string path, string root) => IsUnder(path, root, PathComparison);

    internal static bool IsUnder(string path, string root, StringComparison cmp)
    {
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

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw Denied(path, "device and extended-length paths are not allowed");
            }
        }

        return Path.IsPathFullyQualified(path) ? path : Path.Join(workingDirectory, path);
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
