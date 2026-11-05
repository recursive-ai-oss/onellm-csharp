using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// The Anthropic Messages API: chat (whole or streamed), client and hosted tools, structured output, thinking with exact replay, prompt caching,
/// token counting and message batches. Where it runs (Anthropic's own API, or a cloud) is the <see cref="IAnthropicDeployment"/>; the
/// request and response shapes are the same everywhere. Thread-safe.
/// </summary>
public sealed class AnthropicProvider : IChatProvider, ITokenCountingProvider, IBatchProvider
{
    /// <summary>The provider's name in the catalog.</summary>
    public const string ProviderName = AnthropicErrors.Provider;

    private const int MaxResponseBytes = 256 * 1024 * 1024;

    private readonly IAnthropicDeployment _deployment;
    private readonly TimeProvider _time;
    private readonly AnthropicOptions _options;
    private readonly ProviderHttpClient _http;

    /// <summary>Creates a provider over a deployment.</summary>
    /// <param name="deployment">Where requests go and how they are authenticated.</param>
    /// <param name="time">The clock for time limits, retry waits and the lifetime of public URLs.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    public AnthropicProvider(IAnthropicDeployment deployment, TimeProvider time, AnthropicOptions? options = null)
    {
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new AnthropicOptions();
        AnthropicCatalog.Register();
        _http = new ProviderHttpClient(ProviderName, time, _options.Http, _options.Retry, deployment.Secrets, _options.Timeout, _options.Jitter, deployment.ErrorMapper ?? AnthropicErrors.FromResponse);
    }

    /// <summary>Creates a provider for Anthropic's own API.</summary>
    /// <param name="apiKey">The API key. It is sent as <c>x-api-key</c> and is removed from any error text.</param>
    /// <param name="time">The clock; the default runtime passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    /// <returns>The provider.</returns>
    public static AnthropicProvider Create(Secret apiKey, TimeProvider time, AnthropicOptions? options = null) =>
        new(new AnthropicDirectDeployment(apiKey, options), time, options);

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public string Deployment => _deployment.Name;

    // ---- chat ----

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<ResponseWarning>();
        (ReadOnlyMemory<byte> body, string model) = await BuildChatBodyAsync(request, BodyMode.Message, cancellationToken, warnings).ConfigureAwait(false);
        var info = new AnthropicRequestInfo(AnthropicOperation.Messages, model, false, null, request.Model);
        using HttpResponseMessage response = await SendRequestAsync(info, body, cancellationToken).ConfigureAwait(false);
        JsonObject root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
        return ToChatResponse(root, RequestIdOf(response), warnings);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        StreamCoreAsync(request, new MessageDecoder(_http.Scrub), cancellationToken);

    private async IAsyncEnumerable<StreamEvent> StreamCoreAsync(ChatRequest request, MessageDecoder decoder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<ResponseWarning>();
        (ReadOnlyMemory<byte> body, string model) = await BuildChatBodyAsync(request, BodyMode.Stream, cancellationToken, warnings).ConfigureAwait(false);
        // A stream has no response object to carry notices about the request, so they go out first, as events.
        foreach (ResponseWarning w in warnings)
        {
            yield return StreamEvent.Warning(w);
        }

        var info = new AnthropicRequestInfo(AnthropicOperation.Messages, model, true, null, request.Model);
        using HttpResponseMessage response = await SendRequestAsync(info, body, cancellationToken).ConfigureAwait(false);
        // A cloud deployment may wrap the body (Bedrock frames events in its binary event stream); the codec still sees server-sent events.
        Stream stream = _deployment.AdaptStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        await using (stream.ConfigureAwait(false))
        {
            using var sse = new SseReader(stream);
            decoder.RequestId = RequestIdOf(response);
            while (true)
            {
                bool more;
                try
                {
                    more = await sse.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    // The inner exception is not kept: its text can carry what a server or proxy sent back.
                    throw new TransportException(ProviderName, _http.Scrub("the stream broke: " + e.Message));
                }

                if (!more)
                {
                    break;
                }

                if (sse.Data.IsEmpty)
                {
                    continue;
                }

                if (sse.Data.Span.SequenceEqual("[DONE]"u8))
                {
                    break;
                }

                decoder.DecodeEvent(sse.Data.Span);
                foreach (StreamEvent e in decoder.Events)
                {
                    yield return e;
                }

                decoder.Events.Clear();
                if (decoder.Stopped)
                {
                    break;
                }
            }

            if (!decoder.Stopped)
            {
                throw new MalformedResponseException(ProviderName, "the stream ended before message_stop.", (int)response.StatusCode);
            }
        }
    }

    // ---- token counting ----

    /// <inheritdoc />
    public async Task<TokenCount> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (ReadOnlyMemory<byte> body, string model) = await BuildChatBodyAsync(request, BodyMode.Count, cancellationToken).ConfigureAwait(false);
        var info = new AnthropicRequestInfo(AnthropicOperation.CountTokens, model, false, null, request.Model);
        using HttpResponseMessage response = await SendRequestAsync(info, body, cancellationToken).ConfigureAwait(false);
        JsonObject root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetPropertyValue("input_tokens", out JsonNode? n) || n is not JsonValue v || !v.TryGetValue(out long tokens))
        {
            throw new MalformedResponseException(ProviderName, "the token count response has no input_tokens.", (int)response.StatusCode);
        }

        return new TokenCount(tokens, TokenCountTier.Provider);
    }

    // ---- batches ----

    /// <inheritdoc />
    public async Task<BatchJob> SubmitBatchAsync(BatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("A batch needs at least one request.", nameof(request));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<Message>();
        foreach (BatchItem item in request.Items)
        {
            if (!IsValidCustomId(item.CustomId))
            {
                throw new ArgumentException($"The batch id '{item.CustomId}' must be 1 to 64 letters, digits, hyphens or underscores.", nameof(request));
            }

            if (!seen.Add(item.CustomId))
            {
                throw new ArgumentException($"The batch id '{item.CustomId}' is used twice.", nameof(request));
            }

            if (item.Chat is null)
            {
                throw new BadRequestException(ProviderName, "Anthropic batches carry chat requests only (the batch was not sent).");
            }

            all.AddRange(item.Chat.Messages);
        }

        IReadOnlyDictionary<AssetRef, ReadOnlyMemory<byte>> assets = await ResolveAssetsAsync(all, cancellationToken).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, AnthropicJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartArray("requests");
            foreach (BatchItem item in request.Items)
            {
                (ChatRequest chat, string model, ModelSpec? spec) = Adjust(item.Chat!, null);
                w.WriteStartObject();
                w.WriteString("custom_id", item.CustomId);
                w.WritePropertyName("params");
                new MessagesBodyWriter(chat, model, spec, BodyMode.BatchItem, _options, _time, assets).Write(w);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (request.ExtraParameters is not null)
            {
                foreach (KeyValuePair<string, JsonNode?> p in request.ExtraParameters)
                {
                    w.WritePropertyName(p.Key);
                    AnthropicJson.WriteNatural(w, p.Value);
                }
            }

            w.WriteEndObject();
        }

        ReadOnlyMemory<byte> body = buffer.WrittenMemory;
        var info = new AnthropicRequestInfo(AnthropicOperation.CreateBatch, null, false, null);
        using HttpResponseMessage response = await SendRequestAsync(info, body, cancellationToken).ConfigureAwait(false);
        return ParseBatch(await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<BatchJob> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var info = new AnthropicRequestInfo(AnthropicOperation.GetBatch, null, false, batchId);
        using HttpResponseMessage response = await SendRequestAsync(info, default, cancellationToken).ConfigureAwait(false);
        return ParseBatch(await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<BatchJob> CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var info = new AnthropicRequestInfo(AnthropicOperation.CancelBatch, null, false, batchId);
        using HttpResponseMessage response = await SendRequestAsync(info, default, cancellationToken).ConfigureAwait(false);
        return ParseBatch(await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BatchResultItem> GetBatchResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var info = new AnthropicRequestInfo(AnthropicOperation.BatchResults, null, false, batchId);
        using HttpResponseMessage response = await SendRequestAsync(info, default, cancellationToken).ConfigureAwait(false);
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var lines = new StreamReader(stream, System.Text.Encoding.UTF8);
            while (true)
            {
                string? line;
                try
                {
                    line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    throw new TransportException(ProviderName, _http.Scrub("the results stream broke: " + e.Message));
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                yield return ParseBatchResult(line, (int)response.StatusCode);
            }
        }
    }

    private BatchResultItem ParseBatchResult(string line, int status)
    {
        JsonObject o;
        try
        {
            o = JsonNode.Parse(line) as JsonObject ?? throw new MalformedResponseException(ProviderName, "a batch result line is not a JSON object.", status);
        }
        catch (JsonException)
        {
            throw new MalformedResponseException(ProviderName, "a batch result line is not valid JSON.", status);
        }

        string customId = AnthropicJson.GetString(o, "custom_id") ?? "";
        if (o["result"] is not JsonObject result)
        {
            throw new MalformedResponseException(ProviderName, "a batch result has no result member.", status);
        }

        switch (AnthropicJson.GetString(result, "type"))
        {
            case "succeeded":
                if (result["message"] is not JsonObject message)
                {
                    throw new MalformedResponseException(ProviderName, "a succeeded batch result has no message.", status);
                }

                return new BatchResultItem { CustomId = customId, Chat = ToChatResponse(message, null) };
            case "errored":
                // The error is an error response: {"type":"error","error":{...},"request_id":"..."}.
                JsonObject envelope = result["error"] as JsonObject ?? new JsonObject();
                JsonObject inner = envelope["error"] as JsonObject ?? envelope;
                return new BatchResultItem
                {
                    CustomId = customId,
                    Error = AnthropicErrors.FromErrorObject(inner, AnthropicJson.GetString(envelope, "request_id"), _http.Scrub),
                };
            case "canceled":
                return new BatchResultItem { CustomId = customId, Error = new ProviderException(ProviderName, "the request was canceled before it ran.") { Code = "canceled" } };
            case "expired":
                return new BatchResultItem { CustomId = customId, Error = new ProviderException(ProviderName, "the batch expired before the request ran.") { Code = "expired" } };
            default:
                throw new MalformedResponseException(ProviderName, "a batch result has an unknown type.", status);
        }
    }

    private static bool IsValidCustomId(string id)
    {
        if (id.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    // processing_status in_progress | canceling | ended; request_counts processing, succeeded, errored, canceled, expired (Message Batches reference).
    private static BatchJob ParseBatch(JsonObject o)
    {
        JsonObject? counts = o["request_counts"] as JsonObject;
        long processing = AnthropicJson.GetLong(counts, "processing");
        long succeeded = AnthropicJson.GetLong(counts, "succeeded");
        long errored = AnthropicJson.GetLong(counts, "errored");
        long canceled = AnthropicJson.GetLong(counts, "canceled");
        long expired = AnthropicJson.GetLong(counts, "expired");
        bool cancelRequested = AnthropicJson.GetString(o, "cancel_initiated_at") is not null;
        BatchStatus status = AnthropicJson.GetString(o, "processing_status") switch
        {
            "canceling" => BatchStatus.Cancelling,
            "ended" when cancelRequested => BatchStatus.Cancelled,
            "ended" when succeeded == 0 && errored == 0 && expired > 0 => BatchStatus.Expired,
            "ended" => BatchStatus.Completed,
            _ => BatchStatus.InProgress,
        };
        return new BatchJob
        {
            Id = AnthropicJson.GetString(o, "id") ?? "",
            Status = status,
            Total = (int)(processing + succeeded + errored + canceled + expired),
            Succeeded = (int)succeeded,
            Failed = (int)errored,
            CreatedAt = ParseTime(AnthropicJson.GetString(o, "created_at")),
            ExpiresAt = ParseTime(AnthropicJson.GetString(o, "expires_at")),
        };
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t) ? t : null;

    // ---- shared steps ----

    // The deployment may need an asynchronous step before the first attempt (fetching an OAuth token); attempts then build requests synchronously.
    private async Task<HttpResponseMessage> SendRequestAsync(AnthropicRequestInfo info, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        await _deployment.PrepareAsync(info, cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(() => _deployment.CreateRequest(info, body), cancellationToken).ConfigureAwait(false);
    }

    // The shared adjuster strips or refuses what the catalog says the model cannot take (once per request); the body writer then applies the
    // Anthropic-specific rules (API version, thinking shapes, cache limits) and adds its own notices.
    private (ChatRequest Request, string Model, ModelSpec? Spec) Adjust(ChatRequest request, List<ResponseWarning>? warnings)
    {
        ModelCatalog catalog = _options.Catalog ?? AnthropicCatalog.Default;
        if (catalog.TryResolve(request.Model, out ResolvedModel? resolved) && resolved is not null && resolved.Provider.Name == ProviderName)
        {
            AdjustedRequest adjusted = RequestAdjuster.Adjust(resolved, request);
            warnings?.AddRange(adjusted.Warnings);
            return (adjusted.Request, resolved.Name, resolved.Model);
        }

        return (request, request.Model, null);
    }

    private async Task<(ReadOnlyMemory<byte> Body, string Model)> BuildChatBodyAsync(ChatRequest request, BodyMode mode, CancellationToken cancellationToken, List<ResponseWarning>? warnings = null)
    {
        (ChatRequest adjusted, string model, ModelSpec? spec) = Adjust(request, warnings);
        request = adjusted;
        IReadOnlyDictionary<AssetRef, ReadOnlyMemory<byte>> assets = await ResolveAssetsAsync(request.Messages, cancellationToken).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, AnthropicJson.WriterOptions))
        {
            new MessagesBodyWriter(request, model, spec, mode, _options, _time, assets, warnings).Write(w);
        }

        return (buffer.WrittenMemory, model);
    }

    // Fetches the bytes of assets that live in a store. Assets with inline data or a public URL need nothing.
    private async Task<IReadOnlyDictionary<AssetRef, ReadOnlyMemory<byte>>> ResolveAssetsAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        Dictionary<AssetRef, ReadOnlyMemory<byte>>? resolved = null;
        foreach (Message m in messages)
        {
            foreach (Part part in m.User)
            {
                await ResolveAsync(part).ConfigureAwait(false);
            }

            foreach (ToolCall call in m.ToolCalls)
            {
                foreach (Part part in call.Result)
                {
                    await ResolveAsync(part).ConfigureAwait(false);
                }
            }
        }

        return resolved ?? [];

        async Task ResolveAsync(Part part)
        {
            if (part is not AssetPart asset)
            {
                return;
            }

            (AssetRoute route, AssetRef target, _) = MessagesBodyWriter.Route(asset, _time);
            if (route != AssetRoute.Resolve)
            {
                return;
            }

            resolved ??= new Dictionary<AssetRef, ReadOnlyMemory<byte>>(ReferenceEqualityComparer.Instance);
            if (resolved.ContainsKey(target))
            {
                return;
            }

            if (_options.AssetResolver is null)
            {
                throw new BadRequestException(ProviderName, $"the asset '{target.Key}' has no inline data or public URL and no asset resolver is configured (the request was not sent).");
            }

            resolved[target] = await _options.AssetResolver(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[] bytes = await _http.ReadBodyAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(bytes) as JsonObject ?? throw new MalformedResponseException(ProviderName, "the response is not a JSON object.", (int)response.StatusCode);
        }
        catch (JsonException)
        {
            // The body is not echoed: it is not known to be free of secrets.
            throw new MalformedResponseException(ProviderName, "the response is not valid JSON.", (int)response.StatusCode);
        }
    }

    private static string? RequestIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null;

    private ChatResponse ToChatResponse(JsonObject message, string? requestId, IReadOnlyList<ResponseWarning>? warnings = null)
    {
        var decoder = new MessageDecoder(_http.Scrub) { RequestId = requestId };
        decoder.DecodeMessage(message);
        var accumulator = new MessageAccumulator();
        foreach (StreamEvent e in decoder.Events)
        {
            accumulator.Add(e);
        }

        return new ChatResponse
        {
            Message = accumulator.ToMessage(),
            Warnings = warnings ?? [],
            Id = accumulator.ResponseId,
            Model = accumulator.Model,
            Finish = accumulator.Finish,
            FinishReasonRaw = accumulator.FinishReasonRaw,
        };
    }
}
