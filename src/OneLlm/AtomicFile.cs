using System.Text;

namespace OneLlm;

/// <summary>Writes files so that a reader, or a crash, never sees a half-written one.</summary>
internal static class AtomicFile
{
    // Tests set this to stop a write halfway and check the old file survives. Null in production.
    internal static Action<FileStream>? FaultAfterPartialWrite;

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temporary file in the same directory, flushes it to the disk, then renames it over
    /// <paramref name="path"/>. The temporary file's name starts with a dot so listings skip it, and it is removed on failure.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int half = bytes.Length / 2;
                fs.Write(bytes, 0, half);
                FaultAfterPartialWrite?.Invoke(fs);
                fs.Write(bytes, half, bytes.Length - half);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // The leftover is a dot-file nothing reads; the original error matters more.
            }

            throw;
        }
    }
}
