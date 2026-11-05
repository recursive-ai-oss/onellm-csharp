using OneLlm.Auth;
using OneLlm.Http;

namespace OneLlm.Providers.Gemini;

/// <summary>Authenticates to Vertex AI with an OAuth access token from an <see cref="AccessTokenCache"/>, sent as a bearer token.</summary>
public sealed class GoogleBearerAuth : GeminiAuth
{
    private readonly AccessTokenCache _tokens;

    /// <summary>Creates the authentication.</summary>
    /// <param name="tokens">The token cache; see <see cref="GoogleServiceAccount.CreateTokenCache"/>.</param>
    public GoogleBearerAuth(AccessTokenCache tokens) => _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<KeyValuePair<string, string>>> GetHeadersAsync(CancellationToken cancellationToken)
    {
        Secret token = await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        return [new KeyValuePair<string, string>("Authorization", "Bearer " + token.Value)];
    }

    /// <inheritdoc />
    /// <remarks>The returned set grows as tokens are minted, so the provider's HTTP client scrubs each of them.</remarks>
    public override IEnumerable<Secret> GetSecrets() => _tokens.Secrets;
}

/// <summary>
/// Where Gemini's methods live on Vertex AI: <c>/v1/projects/{project}/locations/{location}/publishers/google/models/{model}:{method}</c>. The
/// request and response shapes are the Gemini API's, except <c>countTokens</c> and <c>embedContent</c>, which take Vertex's bodies (see
/// <see cref="GeminiEndpoint.UsesVertexBodies"/>). What Vertex does not offer at these addresses (files, and batch, whose Vertex forms are
/// different jobs) is refused with a <see cref="BadRequestException"/> before anything is sent.
/// </summary>
public sealed class VertexGeminiEndpoint : GeminiEndpoint
{
    private readonly string _root;
    private readonly string _parent;

    /// <summary>Creates the endpoint.</summary>
    /// <param name="projectId">The Google Cloud project.</param>
    /// <param name="location">The location, for example <c>us-central1</c> or <c>global</c>.</param>
    /// <param name="baseAddress">The API address. Default <c>https://{location}-aiplatform.googleapis.com/</c>, or <c>https://aiplatform.googleapis.com/</c> for <c>global</c>. https, or http for loopback.</param>
    /// <exception cref="ArgumentException">The project or location is empty or has characters a Google Cloud name cannot have, or the address is not https.</exception>
    public VertexGeminiEndpoint(string projectId, string location, Uri? baseAddress = null)
    {
        VertexNames.Require(projectId, nameof(projectId));
        VertexNames.Require(location, nameof(location));
        _root = VertexNames.BaseAddress(baseAddress, location).AbsoluteUri;
        _parent = $"projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}";
    }

    /// <inheritdoc />
    public override bool UsesVertexBodies => true;

    /// <inheritdoc />
    public override Uri Model(string model, string method, bool sse = false)
    {
        if (method is "batchEmbedContents" or "batchGenerateContent")
        {
            throw NotOffered(method);
        }

        return new Uri($"{_root}v1/{_parent}/publishers/google/models/{Uri.EscapeDataString(Id(model))}:{method}{(sse ? "?alt=sse" : "")}");
    }

    /// <inheritdoc />
    public override Uri Resource(string path)
    {
        // Cached content is the one resource family with the same name and shape on Vertex (under the location).
        if (!path.StartsWith("cachedContents", StringComparison.Ordinal))
        {
            throw NotOffered(path);
        }

        return new Uri($"{_root}v1/{_parent}/{path}");
    }

    /// <inheritdoc />
    public override Uri Upload() => throw NotOffered("file upload");

    /// <inheritdoc />
    public override Uri Download(string name) => throw NotOffered("file download");

    /// <inheritdoc />
    public override string ModelResourceName(string model) => $"{_parent}/publishers/google/models/{Id(model)}";

    private static string Id(string model)
    {
        const string prefix = "models/";
        return model.StartsWith(prefix, StringComparison.Ordinal) ? model[prefix.Length..] : model;
    }

    private static BadRequestException NotOffered(string what) =>
        new("gemini", $"'{what}' is not offered through the Vertex AI deployment (the request was not sent).");
}

/// <summary>Builds <see cref="GeminiOptions"/> for the Gemini models on Vertex AI.</summary>
public static class VertexGemini
{
    /// <summary>Options for a service account.</summary>
    /// <param name="account">The service account key.</param>
    /// <param name="location">The location, for example <c>us-central1</c> or <c>global</c>.</param>
    /// <param name="time">The clock. Required.</param>
    /// <param name="projectId">The project; the key file's <c>project_id</c> when null.</param>
    /// <param name="tokenHttp">The client for the token request; the shared client when null.</param>
    /// <returns>Options with the Vertex endpoint, bearer authentication and <c>Deployment = "vertex"</c>. Set the other members (HTTP client, catalog) with a <c>with</c>-style copy of your own.</returns>
    /// <exception cref="ArgumentException">No project is known.</exception>
    public static GeminiOptions Options(GoogleServiceAccount account, string location, TimeProvider time, string? projectId = null, HttpClient? tokenHttp = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        string project = projectId ?? account.ProjectId ?? throw new ArgumentException("The project id is not given and the key file has none.", nameof(projectId));
        return Options(project, location, account.CreateTokenCache(time, tokenHttp), time);
    }

    /// <summary>Options for tokens from a cache (a delegate-backed one, for example).</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="location">The location.</param>
    /// <param name="tokens">The token cache.</param>
    /// <param name="time">The clock. Required.</param>
    /// <returns>Options with the Vertex endpoint, bearer authentication and <c>Deployment = "vertex"</c>.</returns>
    public static GeminiOptions Options(string projectId, string location, AccessTokenCache tokens, TimeProvider time) => new()
    {
        Time = time,
        Auth = new GoogleBearerAuth(tokens),
        Endpoint = new VertexGeminiEndpoint(projectId, location),
        Deployment = "vertex",
    };
}
