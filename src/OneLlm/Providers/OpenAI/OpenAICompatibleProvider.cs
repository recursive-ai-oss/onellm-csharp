using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// A server that speaks the Chat Completions API at a base URL: Ollama, vLLM, LM Studio, OpenRouter, Groq, DeepSeek, xAI, Mistral, Together
/// and the like. It offers chat and streaming (function tools, JSON schema output, streamed usage where the server supports it) and
/// embeddings. What a given server supports is declared by its preset in the catalog (<c>capabilities</c>), not discovered at run time.
/// </summary>
/// <remarks>
/// <para>
/// Reasoning text is read from <c>reasoning_content</c> or <c>reasoning</c>, whichever the server sends, into a reasoning part that is not sent back
/// (these APIs have no replay format). A base URL on this machine (<see cref="IsLoopback"/>) is a local server: the catalog prices it at zero unless
/// a user catalog says otherwise. A server that reports the cost it charged (OpenRouter) has it in <see cref="Usage.Cost"/>.
/// </para>
/// <para>Clients are thread-safe.</para>
/// </remarks>
public sealed class OpenAICompatibleProvider : IChatProvider, IEmbeddingProvider
{
    /// <summary>The catalog provider that holds the presets.</summary>
    public const string CatalogProvider = "openai-compatible";

    private readonly OpenAIWire _wire;

    /// <summary>Creates a provider for a server at a base URL.</summary>
    /// <param name="name">The provider name results and errors carry, for example <c>ollama</c>. It is also the preset consulted for per-model rules, when the catalog has one by that name.</param>
    /// <param name="options">The base URL (required), key, HTTP client and the other settings. A missing key sends no authorization header.</param>
    /// <param name="time">The clock used for retry delays and timeouts.</param>
    /// <exception cref="ArgumentException">There is no base URL and no endpoint.</exception>
    public OpenAICompatibleProvider(string name, OpenAIOptions options, TimeProvider time)
        : this(name, options, time, ChatDialect.OpenAI with { MaxTokensField = "max_tokens" })
    {
    }

    internal OpenAICompatibleProvider(string name, OpenAIOptions options, TimeProvider time, ChatDialect dialect)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        if (options.Endpoint is null && options.BaseUrl is null)
        {
            throw new ArgumentException("An OpenAI-compatible provider needs a base URL.", nameof(options));
        }

        OpenAIEndpoint endpoint = options.Endpoint ?? OpenAIEndpoint.Direct(options.ApiKey, options.BaseUrl);
        Name = name;
        _wire = new OpenAIWire(name, name, OpenAIProtocol.ChatCompletions, dialect, endpoint, options, time);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Deployment => _wire.Endpoint.Deployment;

    /// <summary>True when the server is on this machine (a loopback address or <c>localhost</c>).</summary>
    public bool IsLoopback => _wire.Endpoint.IsLoopback;

    /// <summary>
    /// Creates the provider for a preset of the catalog's <c>openai-compatible</c> provider: its base URL and its quirks (which token-limit field it reads,
    /// whether it takes streamed usage, how it spells a required tool choice).
    /// </summary>
    /// <param name="catalog">The catalog holding the preset.</param>
    /// <param name="preset">The preset's name, for example <c>groq</c>.</param>
    /// <param name="time">The clock used for retry delays and timeouts.</param>
    /// <param name="apiKey">The key for servers that need one. The preset's <c>apiKeyEnvVar</c> names the variable a host usually reads it from; this class does not read the environment.</param>
    /// <param name="httpClient">The HTTP client; defaults to the shared one.</param>
    /// <param name="baseUrl">A base URL replacing the preset's, for a server on another port or host.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="ModelNotFoundException">The catalog has no such preset.</exception>
    public static OpenAICompatibleProvider FromPreset(ModelCatalog catalog, string preset, TimeProvider time, Secret? apiKey = null, HttpClient? httpClient = null, Uri? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(preset);
        PresetSpec? spec = null;
        if (catalog.FindProvider(CatalogProvider)?.Presets is { } presets)
        {
            foreach ((string name, PresetSpec candidate) in presets)
            {
                if (string.Equals(name, preset, StringComparison.OrdinalIgnoreCase))
                {
                    preset = name;
                    spec = candidate;
                    break;
                }
            }
        }

        if (spec is null)
        {
            throw new ModelNotFoundException(preset, $"the catalog has no OpenAI-compatible preset '{preset}'.");
        }

        Uri url = baseUrl ?? (spec.BaseUrl is { Length: > 0 } text ? new Uri(text) : throw new BadDefinitionException($"the preset '{preset}' has no baseUrl."));
        var options = new OpenAIOptions { ApiKey = apiKey, BaseUrl = url, HttpClient = httpClient, Catalog = catalog };
        return new OpenAICompatibleProvider(preset, options, time, DialectOf(spec));
    }

    // The preset's properties are the quirks: maxTokensField, streamUsage, reasoningEffort, requiredToolChoice, jsonSchema.
    private static ChatDialect DialectOf(PresetSpec spec)
    {
        ChatDialect dialect = ChatDialect.OpenAI with { MaxTokensField = "max_tokens" };
        if (spec.Properties is null)
        {
            return dialect;
        }

        if (spec.Properties.TryGetValue("maxTokensField", out object? field) && field is string f)
        {
            dialect = dialect with { MaxTokensField = f };
        }

        if (spec.Properties.TryGetValue("streamUsage", out object? usage) && usage is bool u)
        {
            dialect = dialect with { StreamUsage = u };
        }

        if (spec.Properties.TryGetValue("reasoningEffort", out object? effort) && effort is bool e)
        {
            dialect = dialect with { ReasoningEffort = e };
        }

        if (spec.Properties.TryGetValue("requiredToolChoice", out object? required) && required is string r)
        {
            dialect = dialect with { RequiredWord = r };
        }

        if (spec.Properties.TryGetValue("jsonSchema", out object? schema) && schema is bool s)
        {
            dialect = dialect with { JsonSchema = s };
        }

        return dialect;
    }

    /// <inheritdoc />
    public Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _wire.SendAsync(request, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _wire.StreamAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
        _wire.EmbedAsync(request, cancellationToken);
}
