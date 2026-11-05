using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;

namespace OneLlm.Providers.Anthropic;

/// <summary>Settings of an <see cref="AnthropicProvider"/>. Every member has a default, so <c>new AnthropicOptions()</c> is a working configuration.</summary>
public sealed record AnthropicOptions
{
    /// <summary>The first API version that rejects <c>temperature</c>, <c>top_p</c> and <c>top_k</c> on every model.</summary>
    internal const string SamplingRemovedFrom = "2026-10-21";

    /// <summary>
    /// The <c>anthropic-version</c> header. Default <c>2026-10-21</c>, the version the API reference documents. From that version on the API
    /// returns a 400 for <c>temperature</c> and <c>top_p</c>, so the provider leaves them out (and says so in <see cref="ChatResponse.Warnings"/>);
    /// choose <c>2023-06-01</c> to send them.
    /// </summary>
    public string ApiVersion { get; init; } = "2026-10-21";

    /// <summary>Values for the <c>anthropic-beta</c> header, for features still in beta (a hosted tool, fast mode, ...). Empty sends no header.</summary>
    public IReadOnlyList<string> Beta { get; init; } = [];

    /// <summary>The API address. Default <c>https://api.anthropic.com/</c>. Must be https; plain http is accepted only for loopback test servers.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>The client to send with. Default <see cref="SharedHttpClient.Instance"/>, which does not follow redirects. Tests pass a client over a replay handler.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>Retry settings. Default <see cref="RetryPolicy.Default"/>: two retries on 429, 5xx and overload, honouring <c>Retry-After</c>.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>The limit for one attempt to get response headers. Default 10 minutes; for a stream it is the wait for the first byte.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The catalog that tells the provider what each model supports and allows. Default: the Anthropic file shipped with OneLLM.</summary>
    public ModelCatalog? Catalog { get; init; }

    /// <summary>
    /// The most output tokens asked for when a request leaves <see cref="ChatRequest.MaxOutputTokens"/> unset: the model's maximum, but at
    /// most this many, because very long non-streaming requests risk running into HTTP time limits. Default 64,000.
    /// </summary>
    public int DefaultMaxTokensCap { get; init; } = 64_000;

    /// <summary>The <c>max_tokens</c> sent for a model the catalog does not know, when the request sets none. Default 4,096.</summary>
    public int UnknownModelMaxTokens { get; init; } = 4_096;

    /// <summary>
    /// Fetches the bytes of an asset that has neither inline data nor a public URL (an asset kept in a store). Without it, such an asset fails the
    /// request with a <see cref="BadRequestException"/> that names the asset.
    /// </summary>
    public Func<AssetRef, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? AssetResolver { get; init; }

    /// <summary>Supplies the random number in [0, 1) for retry jitter. Default a shared random source; tests pass a fixed value.</summary>
    public Func<double>? Jitter { get; init; }
}
