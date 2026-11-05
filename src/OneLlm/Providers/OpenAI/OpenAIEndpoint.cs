using System.Net.Http.Headers;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Where an OpenAI-shaped API lives and how requests to it are authenticated. The OpenAI provider and the OpenAI-compatible provider
/// build every URL and sign every request through this seam, so another deployment of the same wire (Azure OpenAI, say) is a new
/// endpoint and not a new codec.
/// </summary>
public abstract class OpenAIEndpoint
{
    /// <summary>The deployment name providers report: <c>direct</c> for an API reached by its own base URL.</summary>
    public virtual string Deployment => "direct";

    /// <summary>Builds the URL for an API path.</summary>
    /// <param name="path">The path below the API root, such as <c>responses</c>, <c>chat/completions</c>, <c>embeddings</c>, <c>files</c> or <c>batches/{id}</c>. It has no leading slash and may carry a query.</param>
    /// <param name="model">The model or deployment the request is for, for endpoints that route by it. Null for calls that name none.</param>
    /// <returns>The absolute URL.</returns>
    public abstract Uri GetUri(string path, string? model);

    /// <summary>Adds credentials to a request about to be sent.</summary>
    /// <param name="request">The request.</param>
    public abstract void Authenticate(HttpRequestMessage request);

    /// <summary>The secrets this endpoint sends, so error text and logs can have them removed.</summary>
    public virtual IEnumerable<Secret> Secrets => [];

    /// <summary>True when the endpoint is on this machine (a loopback address or <c>localhost</c>).</summary>
    public virtual bool IsLoopback => false;

    /// <summary>The OpenAI API at its default address, or any server that takes a bearer key at a base URL.</summary>
    /// <param name="apiKey">The key sent as a bearer token. Null sends none, for local servers.</param>
    /// <param name="baseUrl">The API root including its version segment. Defaults to <c>https://api.openai.com/v1</c>.</param>
    /// <returns>The endpoint.</returns>
    public static OpenAIEndpoint Direct(Secret? apiKey, Uri? baseUrl = null) => new DirectEndpoint(apiKey, baseUrl ?? new Uri("https://api.openai.com/v1/"));

    private sealed class DirectEndpoint : OpenAIEndpoint
    {
        private readonly Secret? _key;
        private readonly Uri _root;

        public DirectEndpoint(Secret? key, Uri root)
        {
            _key = key is { IsEmpty: false } ? key : null;
            // A base URL without a trailing slash would lose its last segment when a relative path is joined to it.
            string text = root.AbsoluteUri;
            _root = text.EndsWith('/') ? root : new Uri(text + "/");
        }

        public override Uri GetUri(string path, string? model) => new(_root, path);

        public override void Authenticate(HttpRequestMessage request)
        {
            if (_key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key.Value);
            }
        }

        public override IEnumerable<Secret> Secrets => _key is null ? [] : [_key];

        public override bool IsLoopback => _root.IsLoopback;
    }
}

/// <summary>Settings shared by the OpenAI provider and the OpenAI-compatible provider.</summary>
public sealed class OpenAIOptions
{
    /// <summary>The API key, sent as a bearer token to the default endpoint. Ignored when <see cref="Endpoint"/> is set.</summary>
    public Secret? ApiKey { get; init; }

    /// <summary>The API root. Defaults to <c>https://api.openai.com/v1</c>. Ignored when <see cref="Endpoint"/> is set.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>A custom endpoint (another deployment of the same wire). Overrides <see cref="ApiKey"/> and <see cref="BaseUrl"/>.</summary>
    public OpenAIEndpoint? Endpoint { get; init; }

    /// <summary>The HTTP client. Defaults to the shared one, which does not follow redirects.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Retry settings. Defaults to <see cref="Http.RetryPolicy.Default"/>.</summary>
    public Http.RetryPolicy? Retry { get; init; }

    /// <summary>The time one attempt may take to produce response headers. Defaults to ten minutes.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// The catalog consulted for per-model rules: which parameters a model rejects, its reasoning efforts, whether it takes prompt cache
    /// options. Without one every parameter is sent as asked.
    /// </summary>
    public Catalog.ModelCatalog? Catalog { get; init; }

    /// <summary>
    /// Whether the Responses API may keep the response on OpenAI's side. Defaults to false: the request carries the whole conversation and
    /// reasoning items travel as encrypted content, so nothing is stored and the provider stays stateless.
    /// </summary>
    public bool Store { get; init; }
}
