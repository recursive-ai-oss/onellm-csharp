using System.Net.Http.Headers;
using OneLlm.Auth;
using OneLlm.Http;

namespace OneLlm.Providers.Anthropic;

/// <summary>Settings of a <see cref="VertexAnthropicDeployment"/>.</summary>
public sealed record VertexAnthropicOptions
{
    /// <summary>The <c>anthropic_version</c> in the body. Default <c>vertex-2023-10-16</c>, the value the Vertex AI documentation gives for Claude.</summary>
    public string AnthropicVersion { get; init; } = "vertex-2023-10-16";

    /// <summary>Beta feature names sent in the body's <c>anthropic_beta</c> array. Empty sends none.</summary>
    public IReadOnlyList<string> Beta { get; init; } = [];

    /// <summary>
    /// The API address. Default <c>https://{location}-aiplatform.googleapis.com/</c>, or <c>https://aiplatform.googleapis.com/</c> for the <c>global</c>
    /// location. Must be https; plain http only for loopback test servers. Set it for a multi-region or private endpoint.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Exact Vertex model ids by model name, checked before the rule that turns <c>claude-x-YYYYMMDD</c> into <c>claude-x@YYYYMMDD</c>.</summary>
    public IReadOnlyDictionary<string, string>? ModelIds { get; init; }
}

/// <summary>
/// Claude on Vertex AI through <c>rawPredict</c> and <c>streamRawPredict</c>, authenticated with an OAuth access token. The usual source is a
/// service account key (<see cref="GoogleServiceAccount"/>); any token source works. Streaming responses are standard server-sent events.
/// </summary>
/// <remarks>
/// Not offered on Vertex and so refused with a <see cref="BadRequestException"/> before anything is sent: message batches and token counting
/// (the counter falls back to a local estimate).
/// </remarks>
public sealed class VertexAnthropicDeployment : IAnthropicDeployment
{
    private readonly string _projectId;
    private readonly string _location;
    private readonly AccessTokenCache _tokens;
    private readonly Uri _baseAddress;
    private readonly VertexAnthropicOptions _options;

    /// <summary>Creates a deployment authenticated by a service account.</summary>
    /// <param name="account">The service account key.</param>
    /// <param name="location">The location, for example <c>us-east5</c> or <c>global</c>.</param>
    /// <param name="time">The clock for token times. Required.</param>
    /// <param name="projectId">The Google Cloud project; the key file's <c>project_id</c> when null.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    /// <param name="tokenHttp">The client for the token request; the shared client when null.</param>
    /// <exception cref="ArgumentException">No project is known, the location is not a plain location name, or the base address is not https.</exception>
    public VertexAnthropicDeployment(GoogleServiceAccount account, string location, TimeProvider time, string? projectId = null, VertexAnthropicOptions? options = null, HttpClient? tokenHttp = null)
        : this(projectId ?? account?.ProjectId ?? throw new ArgumentException("The project id is not given and the key file has none.", nameof(projectId)), location, (account ?? throw new ArgumentNullException(nameof(account))).CreateTokenCache(time, tokenHttp), options)
    {
    }

    /// <summary>Creates a deployment authenticated by tokens from a delegate.</summary>
    /// <param name="projectId">The Google Cloud project.</param>
    /// <param name="location">The location, for example <c>us-east5</c> or <c>global</c>.</param>
    /// <param name="tokenSource">Fetches an access token with its expiry. Called again shortly before the token expires.</param>
    /// <param name="time">The clock. Required.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    public VertexAnthropicDeployment(string projectId, string location, Func<CancellationToken, ValueTask<AccessToken>> tokenSource, TimeProvider time, VertexAnthropicOptions? options = null)
        : this(projectId, location, new AccessTokenCache(tokenSource, time), options)
    {
    }

    private VertexAnthropicDeployment(string projectId, string location, AccessTokenCache tokens, VertexAnthropicOptions? options)
    {
        VertexNames.Require(projectId, nameof(projectId));
        VertexNames.Require(location, nameof(location));
        _projectId = projectId;
        _location = location;
        _tokens = tokens;
        _options = options ?? new VertexAnthropicOptions();
        _baseAddress = VertexNames.BaseAddress(_options.BaseAddress, location);
    }

    /// <inheritdoc />
    public string Name => "vertex";

    /// <inheritdoc />
    public IReadOnlyList<Secret> Secrets => _tokens.Secrets;

    /// <inheritdoc />
    public async ValueTask PrepareAsync(AnthropicRequestInfo info, CancellationToken cancellationToken)
    {
        if (info.Operation != AnthropicOperation.Messages)
        {
            throw new BadRequestException(
                AnthropicErrors.Provider,
                info.Operation == AnthropicOperation.CountTokens
                    ? "token counting is not offered for Claude on Vertex AI (the request was not sent)."
                    : "message batches are not offered for Claude on Vertex AI (the request was not sent).");
        }

        await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public HttpRequestMessage CreateRequest(in AnthropicRequestInfo info, ReadOnlyMemory<byte> body)
    {
        if (info.Operation != AnthropicOperation.Messages)
        {
            throw new BadRequestException(AnthropicErrors.Provider, "this operation is not offered for Claude on Vertex AI.");
        }

        Secret token = _tokens.Current ?? throw new AuthException(AnthropicErrors.Provider, "the Vertex access token expired while the request was being retried.", null);
        string path = $"v1/projects/{Uri.EscapeDataString(_projectId)}/locations/{Uri.EscapeDataString(_location)}/publishers/anthropic/models/{ResolveModelId(info)}:{(info.Stream ? "streamRawPredict" : "rawPredict")}";
        ReadOnlyMemory<byte> payload = MessagesBodyRewriter.Rewrite(body, _options.AnthropicVersion, _options.Beta, dropStream: false);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, path)) { Content = new ReadOnlyMemoryContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return request;
    }

    // Vertex names a dated model "claude-sonnet-4-5@20250929"; a model without a date suffix keeps its name.
    private string ResolveModelId(in AnthropicRequestInfo info)
    {
        string? resolved = info.Model;
        string requested = info.RequestedModel ?? resolved ?? throw new BadRequestException(AnthropicErrors.Provider, "the request names no model.");
        if (_options.ModelIds is { } map && (map.TryGetValue(requested, out string? id) || (resolved is not null && map.TryGetValue(resolved, out id))))
        {
            return Uri.EscapeDataString(id);
        }

        string name = resolved ?? requested;
        if (name.Length > 9 && name[^9] == '-' && name.AsSpan(name.Length - 8).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            name = name[..^9] + "@" + name[^8..];
        }

        // '@' stays literal in the path, as in the documented examples.
        return Uri.EscapeDataString(name).Replace("%40", "@", StringComparison.Ordinal);
    }
}
