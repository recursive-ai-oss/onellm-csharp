using System.Globalization;

namespace OneLlm.Tools.Media;

/// <summary>
/// Reads files for the media tools under the host's file grant. A path is accepted only when, with every symbolic link in it followed, it lies
/// inside one of <see cref="ICapabilities.FileRoots"/> (also with links followed).
/// </summary>
/// <remarks>
/// The check and the open are two steps: a link swapped in between them could point the open elsewhere. The tools read only files a person or
/// the host put in a granted directory, and the window is the time between two calls; a host that runs tools against directories an attacker can
/// write to concurrently should not grant them.
/// </remarks>
internal static class GrantedFiles
{
    private const int MaxLinkDepth = 40;

    public static async Task<(byte[] Data, string ContentType, string Name)> ReadAsync(ICapabilities capabilities, string parameter, string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ToolInputException(parameter, "must be a file path");
        }

        IReadOnlyList<string> roots = capabilities.FileRoots;
        if (roots.Count == 0)
        {
            throw new ToolInputException(parameter, "can't be read: this run has no file access");
        }

        // A relative path is taken from the first granted root, which is the working directory by default.
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(roots[0], path));
        string real = RealPath(full, 0);
        if (!roots.Any(root => IsInside(real, RealPath(Path.GetFullPath(root), 0))))
        {
            throw new ToolInputException(parameter, "is outside the directories this run may read");
        }

        var info = new FileInfo(real);
        if (!info.Exists)
        {
            throw new ToolInputException(parameter, "does not name an existing file");
        }

        if (info.Length > maxBytes)
        {
            throw new ToolInputException(parameter, string.Create(CultureInfo.InvariantCulture, $"is {info.Length} bytes; the most this tool accepts is {maxBytes}"));
        }

        byte[] data = await File.ReadAllBytesAsync(real, cancellationToken).ConfigureAwait(false);
        return (data, ContentTypeOf(real), Path.GetFileName(real));
    }

    private static bool IsInside(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }

    // Follows links segment by segment. A link's target is itself resolved again, because it may pass through more links.
    private static string RealPath(string full, int depth)
    {
        if (depth > MaxLinkDepth)
        {
            throw new ToolInputException("path", "passes through too many symbolic links");
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: false);
                if (target is not null)
                {
                    string resolved = Path.GetFullPath(target.FullName, current);
                    // Resolve what is left of the path first, then the remaining segments below the link.
                    string rest = full[(next.Length)..].TrimStart(Path.DirectorySeparatorChar);
                    return RealPath(rest.Length == 0 ? resolved : Path.Combine(resolved, rest), depth + 1);
                }
            }

            current = next;
        }

        return current;
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp3" or ".mpga" or ".mpeg" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" => "audio/ogg",
        ".m4a" => "audio/mp4",
        ".mp4" => "video/mp4",
        ".webm" => "audio/webm",
        _ => "application/octet-stream",
    };
}
