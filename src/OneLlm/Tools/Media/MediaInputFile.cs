using System.Globalization;
using OneLlm.Access;

namespace OneLlm.Tools.Media;

/// <summary>
/// Reads an input file for a media tool through the host's file grant: the path is resolved with symbolic links followed and must lie under a
/// read root, and the file is opened with the same double check the built-in file tools use.
/// </summary>
internal static class MediaInputFile
{
    public static async Task<(byte[] Data, string ContentType, string Name)> ReadAsync(ICapabilities capabilities, string parameter, string path, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = GuardedFile.OpenRead(capabilities, path);
            if (stream.Length > maxBytes)
            {
                throw new ToolInputException(parameter, string.Create(CultureInfo.InvariantCulture, $"is {stream.Length} bytes; the most this tool accepts is {maxBytes}"));
            }

            byte[] data = new byte[stream.Length];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return (data, ContentTypeOf(path), Path.GetFileName(path));
        }
        catch (CapabilityMissingException e)
        {
            throw new ToolInputException(parameter, "can't be read: " + e.Message);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ToolInputException(parameter, "does not name an existing file");
        }
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
