using System.Text.Json.Serialization;

namespace OneLlm.Messages;

/// <summary>Where a stored file lives and what it is. The bytes are fetched through an asset store, not held here.</summary>
public sealed class AssetRef
{
    /// <summary>Which asset store holds the file, for example <c>local</c> or <c>s3</c>.</summary>
    public string Storage { get; init; } = "";

    /// <summary>The file's key in that store.</summary>
    public string Key { get; init; } = "";

    /// <summary>A content hash, when one was computed.</summary>
    public string? Hash { get; init; }

    /// <summary>The media type, for example <c>image/png</c>.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>The size in bytes, or 0 when unknown (0 is not written).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Size { get; init; }

    /// <summary>The file name, when known.</summary>
    public string? Name { get; init; }

    /// <summary>Base64 bytes accepted when a message comes in. A store moves them out and clears this before the message is saved.</summary>
    public string? Data { get; init; }

    /// <summary>Fields that were present when the reference was read and that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A downscaled copy of an image, made once for an image-size cap and reused on later turns.</summary>
public sealed class ResizedImage
{
    /// <summary>The cap, in pixels on the longer side, the copy was made for. It stays valid while the current cap is at least this large.</summary>
    public int MaxImageSize { get; init; }

    /// <summary>Width of the copy in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the copy in pixels.</summary>
    public int Height { get; init; }

    /// <summary>The stored copy.</summary>
    public AssetRef? Asset { get; init; }

    /// <summary>A URL a provider can fetch the copy from.</summary>
    public string? PublicUrl { get; init; }

    /// <summary>When <see cref="PublicUrl"/> stops being trustworthy.</summary>
    [JsonConverter(typeof(Rfc3339JsonConverter))]
    [OneYaml.YamlConverter(typeof(Rfc3339YamlConverter))]
    public DateTimeOffset? PublicUrlExpires { get; init; }

    /// <summary>Fields that were present when the value was read and that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
