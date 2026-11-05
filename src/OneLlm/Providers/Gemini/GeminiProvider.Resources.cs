using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

/// <summary>An explicit context cache held by Gemini (<c>cachedContents/{id}</c>): a prefix of the conversation that later requests can reuse at the cache-read price.</summary>
public sealed record GeminiCachedContent
{
    /// <summary>The cache's resource name, <c>cachedContents/{id}</c>. Pass it as <see cref="CachePolicy.CachedContent"/>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The model the cache was made for. A cache can only be used with that model.</summary>
    public string Model { get; init; } = "";

    /// <summary>The name given at creation, when there was one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>When the cache expires.</summary>
    public DateTimeOffset? ExpireTime { get; init; }

    /// <summary>When the cache was created.</summary>
    public DateTimeOffset? CreateTime { get; init; }

    /// <summary>How many tokens the cache holds.</summary>
    public long? TotalTokens { get; init; }
}

public sealed partial class GeminiProvider
{
    // ---- explicit caching (https://ai.google.dev/api/caching) ----

    /// <summary>
    /// Creates an explicit cache from the system prompt, tools, tool configuration and messages of a request. Later requests name it in
    /// <see cref="CachePolicy.CachedContent"/> and then send only what follows the cached prefix. Implicit caching needs none of this: cache hits on
    /// ordinary requests are reported as cache-read tokens in the usage.
    /// </summary>
    /// <param name="source">The prefix to cache.</param>
    /// <param name="ttl">How long the cache lives from now; at least one second.</param>
    /// <param name="displayName">A name for people, up to 128 characters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The cache.</returns>
    /// <exception cref="ProviderException">The provider refused, for example because the prefix is under the model's minimum size.</exception>
    public async Task<GeminiCachedContent> CreateCachedContentAsync(ChatRequest source, TimeSpan ttl, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ttl < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "A cache must live at least one second.");
        }

        byte[] body = _writer.CachedContent(source, SpecFor(source.Model), _endpoint.ModelResourceName(source.Model), ttl, displayName);
        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Post, _endpoint.Resource("cachedContents"), body, cancellationToken).ConfigureAwait(false);
        return ToCache(doc.RootElement);
    }

    /// <summary>Reads a cache.</summary>
    /// <param name="name">The cache's resource name, <c>cachedContents/{id}</c>, or the id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The cache.</returns>
    public async Task<GeminiCachedContent> GetCachedContentAsync(string name, CancellationToken cancellationToken = default)
    {
        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Get, _endpoint.Resource(CacheName(name)), null, cancellationToken).ConfigureAwait(false);
        return ToCache(doc.RootElement);
    }

    /// <summary>Changes how long a cache lives. The expiration is the only part of a cache that can change.</summary>
    /// <param name="name">The cache's resource name or id.</param>
    /// <param name="ttl">The new lifetime from now.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The cache.</returns>
    public async Task<GeminiCachedContent> UpdateCachedContentTtlAsync(string name, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant($"{{\"ttl\":\"{(long)Math.Ceiling(ttl.TotalSeconds)}s\"}}"));
        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Patch, _endpoint.Resource(CacheName(name) + "?updateMask=ttl"), body, cancellationToken).ConfigureAwait(false);
        return ToCache(doc.RootElement);
    }

    /// <summary>Deletes a cache, which stops its storage charge.</summary>
    /// <param name="name">The cache's resource name or id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the cache is deleted.</returns>
    public async Task DeleteCachedContentAsync(string name, CancellationToken cancellationToken = default) =>
        (await SendForJsonAsync(HttpMethod.Delete, _endpoint.Resource(CacheName(name)), null, cancellationToken).ConfigureAwait(false)).Dispose();

    private static string CacheName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return name.StartsWith("cachedContents/", StringComparison.Ordinal) ? name : "cachedContents/" + name;
    }

    private static GeminiCachedContent ToCache(JsonElement e)
    {
        long? tokens = null;
        if (e.TryGetProperty("usageMetadata"u8, out JsonElement usage) && usage.TryGetProperty("totalTokenCount"u8, out JsonElement t) && t.TryGetInt64(out long n))
        {
            tokens = n;
        }

        return new GeminiCachedContent
        {
            Name = Str(e, "name"u8) ?? "",
            Model = Str(e, "model"u8) ?? "",
            DisplayName = Str(e, "displayName"u8),
            ExpireTime = Time(e, "expireTime"u8),
            CreateTime = Time(e, "createTime"u8),
            TotalTokens = tokens,
        };
    }

    private static DateTimeOffset? Time(JsonElement e, ReadOnlySpan<byte> name) =>
        Str(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t) ? t : null;

    // ---- files (https://ai.google.dev/api/files) ----

    /// <summary>
    /// Uploads a file with the resumable upload protocol and returns a reference to put in a message. Use it for inputs too large to send inline
    /// (the inline limit is 20 MB per request) and for files used by many requests. Uploaded files are deleted by the service after 48 hours.
    /// </summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="contentType">The media type, for example <c>application/pdf</c>.</param>
    /// <param name="displayName">A name for people, up to 512 characters.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>A file reference for this provider. Large videos may need a moment before the service has processed them.</returns>
    /// <exception cref="ProviderException">The upload failed or the service sent an upload address that is not its own.</exception>
    public async Task<FileRefPart> UploadFileAsync(ReadOnlyMemory<byte> data, string contentType, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        var start = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(start))
        {
            w.WriteStartObject();
            w.WriteStartObject("file");
            if (displayName is { Length: > 0 })
            {
                w.WriteString("displayName", displayName);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        Uri uploadUri = _endpoint.Upload();
        using HttpResponseMessage first = await SendHttpAsync(
            HttpMethod.Post,
            uploadUri,
            start.WrittenSpan.ToArray(),
            cancellationToken,
            m =>
            {
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", data.Length.ToString(CultureInfo.InvariantCulture));
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Type", contentType);
            }).ConfigureAwait(false);
        if (!first.Headers.TryGetValues("x-goog-upload-url", out IEnumerable<string>? urls) || !Uri.TryCreate(urls.FirstOrDefault(), UriKind.Absolute, out Uri? target))
        {
            throw new MalformedResponseException(Name, "the upload start answer has no upload address.");
        }

        // The key goes along to the upload address, so it must be the service's own.
        if (!string.Equals(target.Host, uploadUri.Host, StringComparison.OrdinalIgnoreCase) || target.Scheme != uploadUri.Scheme)
        {
            throw new MalformedResponseException(Name, "the upload address is not on the service's host; the upload was not sent.");
        }

        byte[] bytes = data.ToArray();
        using HttpResponseMessage second = await SendHttpAsync(
            HttpMethod.Post,
            target,
            null,
            cancellationToken,
            m =>
            {
                m.Content = new ByteArrayContent(bytes);
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
                m.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
            }).ConfigureAwait(false);
        byte[] answer = await _http.ReadBodyAsync(second, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = ParseDocument(answer);
        if (!doc.RootElement.TryGetProperty("file"u8, out JsonElement file) || Str(file, "name"u8) is not { } name)
        {
            throw new MalformedResponseException(Name, "the upload answer has no file.");
        }

        return new FileRefPart(Name, name) { ContentType = Str(file, "mimeType"u8) ?? contentType, Name = Str(file, "displayName"u8) ?? displayName, Uri = Str(file, "uri"u8) };
    }

    /// <summary>Deletes an uploaded file.</summary>
    /// <param name="fileIdOrName">The file's name, <c>files/{id}</c>, or the id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is deleted.</returns>
    public async Task DeleteFileAsync(string fileIdOrName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileIdOrName);
        string name = fileIdOrName.StartsWith("files/", StringComparison.Ordinal) ? fileIdOrName : "files/" + fileIdOrName;
        (await SendForJsonAsync(HttpMethod.Delete, _endpoint.Resource(name), null, cancellationToken).ConfigureAwait(false)).Dispose();
    }
}
