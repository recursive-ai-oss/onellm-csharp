using System.Net.Http.Headers;

namespace OneLlm.Providers.Anthropic;

/// <summary>The Messages API operations a deployment has to address.</summary>
public enum AnthropicOperation
{
    /// <summary>Create a message (<c>POST /v1/messages</c>), answered whole or as a stream.</summary>
    Messages = 0,

    /// <summary>Count the input tokens of a message (<c>POST /v1/messages/count_tokens</c>).</summary>
    CountTokens = 1,

    /// <summary>Create a message batch (<c>POST /v1/messages/batches</c>).</summary>
    CreateBatch = 2,

    /// <summary>Read a message batch (<c>GET /v1/messages/batches/{id}</c>).</summary>
    GetBatch = 3,

    /// <summary>Cancel a message batch (<c>POST /v1/messages/batches/{id}/cancel</c>).</summary>
    CancelBatch = 4,

    /// <summary>Read the results of a finished batch (<c>GET /v1/messages/batches/{id}/results</c>), one JSON object per line.</summary>
    BatchResults = 5,
}

/// <summary>What a deployment is told about the request it has to address and sign.</summary>
/// <param name="Operation">The operation.</param>
/// <param name="Model">The model the request is for, when it has one (cloud deployments put it in the address).</param>
/// <param name="Stream">True when the answer is read as a server-sent event stream.</param>
/// <param name="BatchId">The batch the operation is about, for the batch operations that name one.</param>
public readonly record struct AnthropicRequestInfo(AnthropicOperation Operation, string? Model, bool Stream, string? BatchId);

/// <summary>
/// Where an Anthropic provider sends its requests and how it authenticates them: the endpoint and the credentials, nothing else.
/// The request and response bodies are the same Messages API shapes on every deployment, so the codec does not change when a
/// cloud deployment (Bedrock with request signing, Vertex with OAuth tokens) is added by implementing this.
/// </summary>
public interface IAnthropicDeployment
{
    /// <summary>The deployment's name, as <see cref="IProvider.Deployment"/> reports it: <c>direct</c> for Anthropic's own API.</summary>
    string Name { get; }

    /// <summary>Keys and tokens the deployment holds, so the HTTP layer can remove them from anything that reaches an error.</summary>
    IReadOnlyList<Secret> Secrets { get; }

    /// <summary>Builds one request attempt. It is called again for each retry, because a request message cannot be sent twice.</summary>
    /// <param name="info">Which operation, model and batch the request is for.</param>
    /// <param name="body">The JSON body, empty for requests without one.</param>
    /// <returns>The request, addressed and authenticated, with the body set.</returns>
    HttpRequestMessage CreateRequest(in AnthropicRequestInfo info, ReadOnlyMemory<byte> body);
}

/// <summary>
/// Anthropic's own API: <c>https://api.anthropic.com</c> with an <c>x-api-key</c> header, the <c>anthropic-version</c> header and optional
/// <c>anthropic-beta</c> values.
/// </summary>
public sealed class AnthropicDirectDeployment : IAnthropicDeployment
{
    private readonly Secret _apiKey;
    private readonly Uri _baseAddress;
    private readonly string _version;
    private readonly string? _betaHeader;

    /// <summary>Creates the deployment.</summary>
    /// <param name="apiKey">The API key, sent as <c>x-api-key</c>. Never put into an error message or a log.</param>
    /// <param name="options">Version, beta and address settings; the defaults when null.</param>
    /// <exception cref="ArgumentException">The key is empty, or the base address is neither https nor a loopback http address.</exception>
    public AnthropicDirectDeployment(Secret apiKey, AnthropicOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (apiKey.IsEmpty)
        {
            throw new ArgumentException("The Anthropic API key is empty.", nameof(apiKey));
        }

        options ??= new AnthropicOptions();
        _apiKey = apiKey;
        _version = options.ApiVersion;
        _betaHeader = options.Beta.Count == 0 ? null : string.Join(',', options.Beta);
        Uri address = options.BaseAddress ?? new Uri("https://api.anthropic.com/");

        // A key must never travel in clear text; plain http is accepted only for loopback test servers.
        if (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        {
            throw new ArgumentException("The Anthropic base address must be https (http is allowed only for loopback addresses).", nameof(options));
        }

        _baseAddress = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
    }

    /// <inheritdoc />
    public string Name => "direct";

    /// <inheritdoc />
    public IReadOnlyList<Secret> Secrets => [_apiKey];

    /// <inheritdoc />
    public HttpRequestMessage CreateRequest(in AnthropicRequestInfo info, ReadOnlyMemory<byte> body)
    {
        string path = info.Operation switch
        {
            AnthropicOperation.Messages => "v1/messages",
            AnthropicOperation.CountTokens => "v1/messages/count_tokens",
            AnthropicOperation.CreateBatch => "v1/messages/batches",
            AnthropicOperation.GetBatch => "v1/messages/batches/" + Uri.EscapeDataString(info.BatchId ?? ""),
            AnthropicOperation.CancelBatch => "v1/messages/batches/" + Uri.EscapeDataString(info.BatchId ?? "") + "/cancel",
            AnthropicOperation.BatchResults => "v1/messages/batches/" + Uri.EscapeDataString(info.BatchId ?? "") + "/results",
            _ => throw new ArgumentOutOfRangeException(nameof(info)),
        };

        bool hasBody = info.Operation is AnthropicOperation.Messages or AnthropicOperation.CountTokens or AnthropicOperation.CreateBatch;
        bool isPost = hasBody || info.Operation == AnthropicOperation.CancelBatch;
        var request = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, new Uri(_baseAddress, path));
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey.Value);
        request.Headers.TryAddWithoutValidation("anthropic-version", _version);
        if (_betaHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", _betaHeader);
        }

        if (info.Stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        if (hasBody)
        {
            request.Content = new ReadOnlyMemoryContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }
}
