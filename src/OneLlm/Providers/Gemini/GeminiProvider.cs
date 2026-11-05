using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// The Gemini API (https://ai.google.dev/api): chat with streaming, function calling, hosted tools, structured output, thinking with signature replay,
/// context caching, token counting, embeddings and batch mode, with file upload for inputs that are too large to send inline.
/// </summary>
/// <remarks>
/// The authentication and addresses come from <see cref="GeminiOptions"/>, so a cloud deployment of Gemini reuses every request and response shape.
/// An instance is thread-safe and meant to be shared.
/// </remarks>
public sealed partial class GeminiProvider : IChatProvider, IEmbeddingProvider, ITokenCountingProvider, IBatchProvider
{
    private const int MaxResponseBytes = 256 * 1024 * 1024;
    private const string ResourceName = "OneLlm.Providers.Gemini.llm-provider.yaml";

    private readonly GeminiAuth _auth;
    private readonly GeminiEndpoint _endpoint;
    private readonly ProviderHttpClient _http;
    private readonly ModelCatalog? _catalog;
    private readonly Action<string>? _dropped;
    private readonly GeminiRequestWriter _writer;

    // The shipped catalog entries join the registry the default catalog loads, once, the first time the provider is used.
    static GeminiProvider() => CatalogLoader.RegisterShipped(typeof(GeminiProvider).Assembly, ResourceName);

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The settings.</param>
    public GeminiProvider(GeminiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _auth = options.Auth;
        _endpoint = options.Endpoint ?? new GeminiDirectEndpoint();
        _catalog = options.Catalog;
        _dropped = options.OnParameterDropped;
        Name = options.Name;
        Deployment = options.Deployment;
        _http = new ProviderHttpClient(Name, options.Time, options.Http, options.Retry, _auth.GetSecrets(), options.Timeout, options.Jitter, GeminiErrors.FromContext);
        _writer = new GeminiRequestWriter(Name, _dropped, FileUri);
    }

    // Reconciles the request with the catalog entry once (stripping what the model ignores, refusing what it cannot do), then builds a writer that
    // reports the Gemini-specific changes into the same warning list.
    private (ChatRequest Request, GeminiRequestWriter Writer) Prepare(ChatRequest request, List<ResponseWarning> warnings)
    {
        ChatRequest adjusted = request;
        string id = request.Model.StartsWith("models/", StringComparison.Ordinal) ? request.Model["models/".Length..] : request.Model;
        if (_catalog is not null && _catalog.TryResolve(Name + ":" + id, out ResolvedModel? resolved) && resolved is not null)
        {
            AdjustedRequest result = RequestAdjuster.Adjust(resolved, request);
            adjusted = result.Request;
            warnings.AddRange(result.Warnings);
            foreach (ResponseWarning w in result.Warnings)
            {
                _dropped?.Invoke(w.Message);
            }
        }

        var writer = new GeminiRequestWriter(Name, w => { warnings.Add(new ResponseWarning("parameter_stripped", w)); _dropped?.Invoke(w); }, FileUri);
        return (adjusted, writer);
    }

    private string FileUri(string id) => _endpoint.Resource(id.StartsWith("files/", StringComparison.Ordinal) ? id : "files/" + id).AbsoluteUri;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Deployment { get; }

    /// <summary>The catalog entries that ship with the provider: every Gemini model in scope, with limits, capabilities, thinking settings and dated prices.</summary>
    /// <returns>The shipped layer.</returns>
    public static CatalogLayer GetCatalogLayer()
    {
        using Stream stream = typeof(GeminiProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Gemini catalog is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return CatalogLayer.FromYaml(CatalogLayer.Shipped, reader.ReadToEnd(), "gemini/llm-provider.yaml");
    }

    // ---- chat ----

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Each call collects its own notices, so concurrent calls on one provider do not mix them.
        var warnings = new List<ResponseWarning>();
        (ChatRequest adjusted, GeminiRequestWriter writer) = Prepare(request, warnings);
        byte[] body = writer.GenerateContent(adjusted, SpecFor(request.Model));
        using HttpResponseMessage response = await PostAsync(_endpoint.Model(request.Model, "generateContent"), body, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _http.ReadBodyAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        ChatResponse result = ToResponse(bytes, request.Model);
        return warnings.Count == 0 ? result : result with { Warnings = warnings };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (ChatRequest adjusted, GeminiRequestWriter writer) = Prepare(request, new List<ResponseWarning>());
        byte[] body = writer.GenerateContent(adjusted, SpecFor(request.Model));
        using HttpResponseMessage response = await PostAsync(_endpoint.Model(request.Model, "streamGenerateContent", sse: true), body, cancellationToken).ConfigureAwait(false);
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new GeminiSseReader(stream);
        var parser = new GeminiResponseParser(Name, request.Model);
        var events = new List<StreamEvent>();
        while (await NextEventAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            parser.Parse(reader.Data, events);
            foreach (StreamEvent e in events)
            {
                yield return e;
            }

            events.Clear();
            parser.ReleaseBuffers();
        }

        parser.Complete(events);
        foreach (StreamEvent e in events)
        {
            yield return e;
        }
    }

    private async ValueTask<bool> NextEventAsync(GeminiSseReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new TransportException(Name, _http.Scrub("the stream broke: " + e.Message));
        }
        catch (InvalidDataException e)
        {
            throw new MalformedResponseException(Name, e.Message);
        }
    }

    private ChatResponse ToResponse(byte[] bytes, string requestModel)
    {
        var parser = new GeminiResponseParser(Name, requestModel);
        var events = new List<StreamEvent>();
        parser.Parse(bytes, events);
        parser.Complete(events);
        var accumulator = new MessageAccumulator();
        foreach (StreamEvent e in events)
        {
            accumulator.Add(e);
        }

        parser.ReleaseBuffers();
        return Build(accumulator, parser);
    }

    /// <summary>Builds the response the streaming and the non-streaming paths share, so callers (and tests) can compare them.</summary>
    internal static ChatResponse Build(MessageAccumulator accumulator, GeminiResponseParser parser)
    {
        Message m = accumulator.ToMessage();
        if (parser.GetMessageExtension() is { } extension)
        {
            m = new Message
            {
                Id = m.Id,
                User = m.User,
                Assistant = m.Assistant,
                ToolCalls = m.ToolCalls,
                DateTime = m.DateTime,
                Source = m.Source,
                Cache = m.Cache,
                Usage = m.Usage,
                When = m.When,
                ExtensionData = extension,
            };
        }

        return new ChatResponse
        {
            Message = m,
            Id = accumulator.ResponseId,
            Model = accumulator.Model,
            Finish = accumulator.Finish,
            FinishReasonRaw = accumulator.FinishReasonRaw,
        };
    }

    // ---- token counting ----

    /// <inheritdoc />
    public async Task<TokenCount> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] body = _writer.CountTokens(request, SpecFor(request.Model), _endpoint.ModelResourceName(request.Model));
        using HttpResponseMessage response = await PostAsync(_endpoint.Model(request.Model, "countTokens"), body, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _http.ReadBodyAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = ParseDocument(bytes);
        if (!doc.RootElement.TryGetProperty("totalTokens"u8, out JsonElement total) || !total.TryGetInt64(out long n))
        {
            throw new MalformedResponseException(Name, "the countTokens answer has no totalTokens.");
        }

        return new TokenCount(n, TokenCountTier.Provider);
    }

    // ---- plumbing ----

    internal ModelSpec? SpecFor(string model)
    {
        if (_catalog is null)
        {
            return null;
        }

        string id = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
        return _catalog.TryResolve(Name + ":" + id, out ResolvedModel? resolved) ? resolved?.Model : null;
    }

    private Task<HttpResponseMessage> PostAsync(Uri uri, byte[] body, CancellationToken cancellationToken) =>
        SendHttpAsync(HttpMethod.Post, uri, body, cancellationToken);

    private async Task<HttpResponseMessage> SendHttpAsync(HttpMethod method, Uri uri, byte[]? body, CancellationToken cancellationToken, Action<HttpRequestMessage>? extra = null)
    {
        IReadOnlyList<KeyValuePair<string, string>> headers = await _auth.GetHeadersAsync(cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(method, uri);
                foreach ((string name, string value) in headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }

                if (body is not null)
                {
                    message.Content = new ByteArrayContent(body);
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                extra?.Invoke(message);
                return message;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // Parses a small JSON answer, turning a syntax error into a typed one that does not quote the input.
    private JsonDocument ParseDocument(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new MalformedResponseException(Name, $"the response is not valid JSON (at byte {e.BytePositionInLine}).");
        }
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpMethod method, Uri uri, byte[]? body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendHttpAsync(method, uri, body, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _http.ReadBodyAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? JsonDocument.Parse("{}"u8.ToArray()) : ParseDocument(bytes);
    }

    private static string? Str(JsonElement e, ReadOnlySpan<byte> name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Counts that the API writes as numbers or as strings (int64 in protobuf JSON is a string).
    private static int? Int(JsonElement e, ReadOnlySpan<byte> name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s) ? s : null;
    }
}
