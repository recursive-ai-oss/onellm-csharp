using System.Net.Http.Headers;
using OneLlm.Auth;
using OneLlm.Http;

namespace OneLlm.Providers.Anthropic;

/// <summary>Settings of a <see cref="BedrockDeployment"/>.</summary>
public sealed record BedrockOptions
{
    /// <summary>The <c>anthropic_version</c> in the body. Default <c>bedrock-2023-05-31</c>, the value the Bedrock documentation gives for Claude on InvokeModel.</summary>
    public string AnthropicVersion { get; init; } = "bedrock-2023-05-31";

    /// <summary>Beta feature names sent in the body's <c>anthropic_beta</c> array (Bedrock takes betas in the body, not in a header). Empty sends none.</summary>
    public IReadOnlyList<string> Beta { get; init; } = [];

    /// <summary>The runtime address. Default <c>https://bedrock-runtime.{region}.amazonaws.com/</c>. Must be https; plain http only for loopback test servers. Use it for a VPC endpoint.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    /// The inference profile prefix to put before a base model id: <c>us</c>, <c>eu</c>, <c>apac</c>, <c>jp</c> or <c>global</c>. Newer Claude models are not
    /// available on demand under their base id (Bedrock answers 400) and need a profile. Null sends the base id. A model id written with its
    /// own profile prefix, or as an ARN, is used as given.
    /// </summary>
    public string? InferenceProfile { get; init; }

    /// <summary>Exact Bedrock model ids, ARNs or profile ids by model name, checked before any rule. Use it for a model the built-in table does not know.</summary>
    public IReadOnlyDictionary<string, string>? ModelIds { get; init; }
}

/// <summary>
/// Claude on Amazon Bedrock through <c>InvokeModel</c> and <c>InvokeModelWithResponseStream</c>, carrying the Messages API body. Requests are
/// signed with AWS Signature Version 4 (<see cref="AwsSigV4Signer"/>) from explicit credentials or the standard environment variables, or
/// authenticated with a Bedrock API key (a bearer token). Streaming responses arrive in the AWS event stream encoding and are presented to
/// the codec as server-sent events.
/// </summary>
/// <remarks>
/// Not offered on Bedrock InvokeModel, and so refused with a <see cref="BadRequestException"/> before anything is sent: message batches (use
/// Bedrock batch inference jobs) and token counting (the counter falls back to a local estimate). Anthropic's documentation also lists URL
/// image and document sources, the Files API and the top-level <c>cache_control</c> as unavailable here.
/// </remarks>
public sealed class BedrockDeployment : IAnthropicDeployment
{
    // Base ids of the models the Bedrock documentation's table lists; a name not here becomes "anthropic.<name>".
    private static readonly Dictionary<string, string> s_baseIds = new(StringComparer.Ordinal)
    {
        ["claude-opus-4-6"] = "anthropic.claude-opus-4-6-v1",
        ["claude-opus-4-5-20251101"] = "anthropic.claude-opus-4-5-20251101-v1:0",
        ["claude-opus-4-1-20250805"] = "anthropic.claude-opus-4-1-20250805-v1:0",
        ["claude-sonnet-4-6"] = "anthropic.claude-sonnet-4-6",
        ["claude-sonnet-4-5-20250929"] = "anthropic.claude-sonnet-4-5-20250929-v1:0",
        ["claude-sonnet-4-20250514"] = "anthropic.claude-sonnet-4-20250514-v1:0",
        ["claude-haiku-4-5-20251001"] = "anthropic.claude-haiku-4-5-20251001-v1:0",
    };

    private readonly Uri _baseAddress;
    private readonly BedrockOptions _options;
    private readonly AwsSigV4Signer? _signer;
    private readonly Secret? _bearer;
    private readonly IReadOnlyList<Secret> _secrets;

    /// <summary>Creates a deployment that signs requests with AWS keys.</summary>
    /// <param name="region">The AWS region, for example <c>us-east-1</c>.</param>
    /// <param name="credentials">The keys. See <see cref="AwsCredentials.FromEnvironment"/>.</param>
    /// <param name="time">The clock for request times. Required.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    /// <exception cref="ArgumentException">The region is not a plain region name, or the base address is not https.</exception>
    public BedrockDeployment(string region, AwsCredentials credentials, TimeProvider time, BedrockOptions? options = null)
        : this(region, time, options)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _signer = new AwsSigV4Signer(credentials, region, "bedrock", time);
        _secrets = credentials.Secrets;
    }

    /// <summary>Creates a deployment that authenticates with a Bedrock API key sent as a bearer token.</summary>
    /// <param name="region">The AWS region.</param>
    /// <param name="apiKey">The Bedrock API key.</param>
    /// <param name="time">The clock. Required.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    /// <exception cref="ArgumentException">The key is empty, the region is not a plain region name, or the base address is not https.</exception>
    public BedrockDeployment(string region, Secret apiKey, TimeProvider time, BedrockOptions? options = null)
        : this(region, time, options)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (apiKey.IsEmpty)
        {
            throw new ArgumentException("The Bedrock API key is empty.", nameof(apiKey));
        }

        _bearer = apiKey;
        _secrets = [apiKey];
    }

    private BedrockDeployment(string region, TimeProvider time, BedrockOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNull(time);
        foreach (char c in region)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                // The region becomes part of a host name, so anything but a plain region name is refused.
                throw new ArgumentException("The AWS region must be a name like us-east-1.", nameof(region));
            }
        }

        _options = options ?? new BedrockOptions();
        Uri address = _options.BaseAddress ?? new Uri($"https://bedrock-runtime.{region}.amazonaws.com/");
        if (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        {
            throw new ArgumentException("The Bedrock address must be https (http is allowed only for loopback addresses).", nameof(options));
        }

        _baseAddress = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
        _secrets = [];
    }

    /// <summary>
    /// Creates a deployment from the environment: the Bedrock API key in <c>AWS_BEARER_TOKEN_BEDROCK</c> when set, otherwise the keys in
    /// <c>AWS_ACCESS_KEY_ID</c>, <c>AWS_SECRET_ACCESS_KEY</c> and <c>AWS_SESSION_TOKEN</c>. Nothing else is read: no profile files, no instance metadata.
    /// </summary>
    /// <param name="region">The AWS region.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Settings; the defaults when null.</param>
    /// <param name="lookup">Reads a variable; the process environment when null.</param>
    /// <returns>The deployment.</returns>
    /// <exception cref="AuthException">Neither a Bedrock API key nor AWS keys are set.</exception>
    public static BedrockDeployment FromEnvironment(string region, TimeProvider time, BedrockOptions? options = null, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        string? bearer = lookup("AWS_BEARER_TOKEN_BEDROCK");
        return !string.IsNullOrWhiteSpace(bearer)
            ? new BedrockDeployment(region, new Secret(bearer), time, options)
            : new BedrockDeployment(region, AwsCredentials.FromEnvironment(lookup), time, options);
    }

    /// <inheritdoc />
    public string Name => "bedrock";

    /// <inheritdoc />
    public IReadOnlyList<Secret> Secrets => _secrets;

    /// <inheritdoc />
    public ProviderErrorMapper? ErrorMapper => BedrockErrors.Mapper;

    /// <inheritdoc />
    public Stream AdaptStream(Stream body) => new BedrockEventStream(body);

    /// <inheritdoc />
    public ValueTask PrepareAsync(AnthropicRequestInfo info, CancellationToken cancellationToken)
    {
        if (info.Operation != AnthropicOperation.Messages)
        {
            throw new BadRequestException(
                AnthropicErrors.Provider,
                info.Operation == AnthropicOperation.CountTokens
                    ? "token counting is not offered through Bedrock InvokeModel (the request was not sent)."
                    : "message batches are not offered through Bedrock InvokeModel; use Bedrock batch inference jobs (the request was not sent).");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public HttpRequestMessage CreateRequest(in AnthropicRequestInfo info, ReadOnlyMemory<byte> body)
    {
        if (info.Operation != AnthropicOperation.Messages)
        {
            throw new BadRequestException(AnthropicErrors.Provider, "this operation is not offered through Bedrock InvokeModel.");
        }

        // The model id sits in the path with its colons and slashes percent-encoded (an id like "...-v1:0" is sent as "...-v1%3A0").
        string path = "model/" + Uri.EscapeDataString(ResolveModelId(info)) + (info.Stream ? "/invoke-with-response-stream" : "/invoke");
        ReadOnlyMemory<byte> payload = MessagesBodyRewriter.Rewrite(body, _options.AnthropicVersion, _options.Beta, dropStream: true);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, path)) { Content = new ReadOnlyMemoryContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (!info.Stream)
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
        }

        if (_bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer.Value);
        }
        else
        {
            _signer!.Sign(request, payload.Span);
        }

        return request;
    }

    // Order: a caller's explicit map, then an id the caller already wrote in Bedrock form, then the table and the profile prefix.
    private string ResolveModelId(in AnthropicRequestInfo info)
    {
        string? resolved = info.Model;
        string requested = info.RequestedModel ?? resolved ?? throw new BadRequestException(AnthropicErrors.Provider, "the request names no model.");
        if (_options.ModelIds is { } map)
        {
            if (map.TryGetValue(requested, out string? id) || (resolved is not null && map.TryGetValue(resolved, out id)))
            {
                return id;
            }
        }

        if (requested.Contains("anthropic.", StringComparison.Ordinal) || requested.StartsWith("arn:", StringComparison.Ordinal))
        {
            return requested;
        }

        string name = resolved ?? requested;
        string baseId = s_baseIds.TryGetValue(name, out string? known) ? known : "anthropic." + name;
        return _options.InferenceProfile is { Length: > 0 } profile ? profile + "." + baseId : baseId;
    }
}
