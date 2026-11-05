using OneLlm.Messages;

namespace OneLlm;

/// <summary>Makes asset parts (images, documents, audio) to send with a prompt.</summary>
/// <remarks>
/// The bytes travel inline, as base64 in the asset's <c>data</c>, which is accepted only on the way in: a saved instance holds an
/// asset reference without the bytes once an asset store has them.
/// </remarks>
public static class Asset
{
    /// <summary>Reads a file into an asset part. The content type comes from the extension.</summary>
    /// <param name="path">The file.</param>
    /// <param name="contentType">The media type; null picks one from the extension (<c>application/octet-stream</c> when unknown).</param>
    /// <returns>The part.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static AssetPart File(string path, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        return FromBytes(bytes, contentType ?? ContentTypeOf(path), Path.GetFileName(path));
    }

    /// <summary>Makes an asset part from bytes.</summary>
    /// <param name="data">The content.</param>
    /// <param name="contentType">The media type, for example <c>image/png</c>.</param>
    /// <param name="name">A file name shown to the model where the provider takes one.</param>
    /// <returns>The part.</returns>
    public static AssetPart FromBytes(ReadOnlySpan<byte> data, string contentType, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        return new AssetPart(new AssetRef
        {
            Storage = "inline",
            Key = name ?? "",
            ContentType = contentType,
            Size = data.Length,
            Name = name,
            Data = Convert.ToBase64String(data),
        });
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
