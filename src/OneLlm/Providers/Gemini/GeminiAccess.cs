using OneLlm.Catalog;
using OneLlm.Http;

namespace OneLlm.Providers.Gemini;

/// <summary>How requests to Gemini prove who sends them. The Gemini API takes a key; a cloud deployment takes a bearer token.</summary>
public abstract class GeminiAuth
{
    /// <summary>Gets the headers that carry the credentials. Called once per call, before the first attempt, so a token can be fetched asynchronously.</summary>
    /// <param name="cancellationToken">Cancels a token fetch.</param>
    /// <returns>The header names and values to add to every attempt.</returns>
    public abstract ValueTask<IReadOnlyList<KeyValuePair<string, string>>> GetHeadersAsync(CancellationToken cancellationToken);

    /// <summary>The secrets this authentication holds, so they can be removed from error text.</summary>
    /// <returns>The secrets.</returns>
    public abstract IEnumerable<Secret> GetSecrets();
}

/// <summary>Authenticates with a Gemini API key, sent in the <c>x-goog-api-key</c> header (never in the URL, so it cannot reach a log).</summary>
public sealed class GeminiApiKeyAuth : GeminiAuth
{
    private readonly Secret _key;

    /// <summary>Creates the authentication.</summary>
    /// <param name="key">The API key.</param>
    /// <exception cref="ArgumentException">The key is empty.</exception>
    public GeminiApiKeyAuth(Secret key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.IsEmpty)
        {
            throw new ArgumentException("The Gemini API key is empty.", nameof(key));
        }

        _key = key;
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<KeyValuePair<string, string>>> GetHeadersAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([new KeyValuePair<string, string>("x-goog-api-key", _key.Value)]);

    /// <inheritdoc />
    public override IEnumerable<Secret> GetSecrets() => [_key];
}

/// <summary>Where Gemini's methods live. A deployment (the Gemini API, a cloud project) supplies its own addresses over the same request and response shapes.</summary>
public abstract class GeminiEndpoint
{
    /// <summary>The address of a model method, for example <c>generateContent</c>.</summary>
    /// <param name="model">The model's name, with or without the <c>models/</c> prefix.</param>
    /// <param name="method">The method: <c>generateContent</c>, <c>streamGenerateContent</c>, <c>countTokens</c>, <c>embedContent</c>, <c>batchEmbedContents</c>, <c>batchGenerateContent</c>.</param>
    /// <param name="sse">True to ask for server-sent events (<c>alt=sse</c>).</param>
    /// <returns>The address.</returns>
    public abstract Uri Model(string model, string method, bool sse = false);

    /// <summary>The address of a resource such as <c>cachedContents/abc</c> or <c>batches/abc</c>, with an optional method suffix such as <c>:cancel</c>.</summary>
    /// <param name="path">The resource path, relative to the API version root.</param>
    /// <returns>The address.</returns>
    public abstract Uri Resource(string path);

    /// <summary>The address that starts a resumable file upload.</summary>
    /// <returns>The address.</returns>
    public abstract Uri Upload();

    /// <summary>The address from which an uploaded file's content or a batch result file is downloaded.</summary>
    /// <param name="name">The resource name, for example <c>files/abc</c>.</param>
    /// <returns>The address.</returns>
    public abstract Uri Download(string name);

    /// <summary>True when the deployment's <c>countTokens</c> and <c>embedContent</c> take the Vertex AI request bodies: the generation members at the top level (no <c>generateContentRequest</c> wrapper), one embedding per call, and settings under <c>embedContentConfig</c>.</summary>
    public virtual bool UsesVertexBodies => false;

    /// <summary>The model resource name, <c>models/{model}</c>, as request bodies write it.</summary>
    /// <param name="model">The model name, with or without the prefix.</param>
    /// <returns>The resource name.</returns>
    public virtual string ModelResourceName(string model) => model.StartsWith("models/", StringComparison.Ordinal) ? model : "models/" + model;
}

/// <summary>The Gemini API at <c>generativelanguage.googleapis.com</c>, version <c>v1beta</c>.</summary>
public sealed class GeminiDirectEndpoint : GeminiEndpoint
{
    /// <summary>The Gemini API's address.</summary>
    public static Uri DefaultBaseAddress { get; } = new("https://generativelanguage.googleapis.com/");

    private readonly string _root;

    /// <summary>Creates the endpoint.</summary>
    /// <param name="baseAddress">The API root, <see cref="DefaultBaseAddress"/> when null. It must be https, except for a loopback address (tests, a local mock).</param>
    /// <exception cref="ArgumentException">The address is not https and not loopback.</exception>
    public GeminiDirectEndpoint(Uri? baseAddress = null)
    {
        Uri b = baseAddress ?? DefaultBaseAddress;
        if (b.Scheme != Uri.UriSchemeHttps && !(b.Scheme == Uri.UriSchemeHttp && b.IsLoopback))
        {
            throw new ArgumentException("The Gemini address must be https (plain http only for localhost).", nameof(baseAddress));
        }

        _root = b.GetLeftPart(UriPartial.Authority) + "/";
    }

    /// <inheritdoc />
    public override Uri Model(string model, string method, bool sse = false)
    {
        string id = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
        return new Uri($"{_root}v1beta/models/{Uri.EscapeDataString(id)}:{method}{(sse ? "?alt=sse" : "")}");
    }

    /// <inheritdoc />
    public override Uri Resource(string path) => new($"{_root}v1beta/{path}");

    /// <inheritdoc />
    public override Uri Upload() => new($"{_root}upload/v1beta/files");

    /// <inheritdoc />
    public override Uri Download(string name) => new($"{_root}download/v1beta/{name}:download?alt=media");
}

/// <summary>Settings for a <see cref="GeminiProvider"/>.</summary>
public sealed class GeminiOptions
{
    /// <summary>The clock for time limits, retry waits and cache expiry. Required: the library never reads the system clock on its own.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>How requests are authenticated. Required.</summary>
    public required GeminiAuth Auth { get; init; }

    /// <summary>Where the methods live. The Gemini API when null.</summary>
    public GeminiEndpoint? Endpoint { get; init; }

    /// <summary>The client to send with. The shared one (redirects off) when null. Tests pass a client over a replay handler.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>The retry policy. <see cref="RetryPolicy.Default"/> when null.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>The limit for one attempt to get response headers. 10 minutes when null.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Supplies the random number for retry jitter; a shared generator when null. Tests pass a fixed value.</summary>
    public Func<double>? Jitter { get; init; }

    /// <summary>
    /// The catalog that says what each model supports (temperature, thinking levels, tools, schema form). Without one, every parameter is sent as given
    /// and the provider's own errors are the only check. Use <see cref="GeminiProvider.GetCatalogLayer"/> to get the shipped entries.
    /// </summary>
    public ModelCatalog? Catalog { get; init; }

    /// <summary>Called with a sentence for each parameter that was stripped before sending, so callers can log it. Never receives a secret.</summary>
    public Action<string>? OnParameterDropped { get; init; }

    /// <summary>The name this provider reports (<c>gemini</c>) and the deployment (<c>direct</c>). Cloud deployments set these.</summary>
    public string Name { get; init; } = "gemini";

    /// <summary>The deployment this provider reports: <c>direct</c> for the Gemini API, <c>vertex</c> for Vertex AI.</summary>
    public string Deployment { get; init; } = "direct";
}
