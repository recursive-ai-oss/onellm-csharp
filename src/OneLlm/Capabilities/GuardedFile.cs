using OneLlm.Tools;

namespace OneLlm.Access;

/// <summary>Opens files for the built-in tools, resolving through the grants before and after the open.</summary>
/// <remarks>See <see cref="PathGuard"/> for what the double check guarantees and what it does not.</remarks>
internal static class GuardedFile
{
    /// <summary>Test seam: runs between the first resolution and the open. Null in production.</summary>
    internal static Action<string>? BeforeOpen { get; set; }

    internal static FileStream OpenRead(ICapabilities caps, string path)
    {
        string real = caps.ResolveRead(path);
        BeforeOpen?.Invoke(real);
        var stream = new FileStream(real, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        try
        {
            Recheck(real, caps.ResolveRead(path), path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens for writing without truncating, checks, and only then truncates (unless appending), so a swapped link never costs data.</summary>
    internal static FileStream OpenWrite(ICapabilities caps, string path, bool append)
    {
        string real = caps.ResolveWrite(path);
        string? dir = Path.GetDirectoryName(real);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        BeforeOpen?.Invoke(real);
        var stream = new FileStream(real, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        });
        try
        {
            Recheck(real, caps.ResolveWrite(path), path);
            if (append)
            {
                stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                stream.SetLength(0);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // A link swapped in after the first resolution makes the second one differ, so the file that was opened is refused.
    private static void Recheck(string first, string second, string path)
    {
        if (!string.Equals(first, second, PathGuard.PathComparison))
        {
            throw PathGuard.Denied(path, "the path changed while it was being opened");
        }

    }
}
