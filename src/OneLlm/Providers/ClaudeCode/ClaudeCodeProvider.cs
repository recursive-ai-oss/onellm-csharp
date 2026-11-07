using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Subscriptions;

namespace OneLlm.Providers.ClaudeCode;

/// <summary>Opt-in Claude subscription chat over the Messages wire. Reads and refreshes the official CLI's file credentials in one explicit directory. Thread-safe.</summary>
/// <remarks>Uses undocumented consumer endpoints which may change. Users are responsible for compliance with their provider's terms. Does not read the macOS Keychain.</remarks>
public sealed class ClaudeCodeProvider : IChatProvider, IDisposable
{
    private readonly AnthropicProvider _inner;
    private readonly HttpClient _transport;
    private readonly SubscriptionCredentials _credentials;

    /// <summary>Creates a provider for a single account; no default registration or environment lookup occurs.</summary>
    /// <param name="configDirectory">The directory used as CLAUDE_CONFIG_DIR by the CLI, containing .credentials.json. Authentication failures name this directory, never a token.</param>
    /// <param name="timeProvider">Clock for expiry and retries.</param>
    /// <param name="options">Optional transport overrides, primarily for testing.</param>
    /// <exception cref="ArgumentException">The directory is empty or an endpoint is insecure.</exception>
    public ClaudeCodeProvider(string configDirectory, TimeProvider timeProvider, SubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= new();
        var credentials = new SubscriptionCredentials(configDirectory, true, timeProvider, options);
        _credentials = credentials;
        _transport = new HttpClient(new SubscriptionTransport(credentials, true, options)) { Timeout = Timeout.InfiniteTimeSpan };
        _inner = new AnthropicProvider(new DeploymentAdapter(credentials), timeProvider, new AnthropicOptions { ApiVersion = "2023-06-01", Http = _transport });
        SubscriptionCatalog.Register();
    }

    /// <summary>The live set of credentials used by this provider, for scrubbing opt-in raw logs. Values are wrapped in Secret and never print as plaintext.</summary>
    public IReadOnlyList<Secret> Secrets => _credentials.Secrets;

    /// <inheritdoc />
    public string Name => "claude-code";

    /// <inheritdoc />
    public string Deployment => "subscription";

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        (await _inner.SendAsync(ForWire(request), cancellationToken).ConfigureAwait(false)) with { Cost = Cost.Zero };

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => _inner.StreamAsync(ForWire(request), cancellationToken);

    private static ChatRequest ForWire(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Resolve Messages-specific model rules against the ordinary Anthropic catalog without the routing namespace.
        return request.Model.StartsWith("claude-code/", StringComparison.Ordinal) ? request with { Model = request.Model[12..] } : request;
    }

    /// <summary>Disposes this provider's transport, not a caller-supplied HTTP client.</summary>
    public void Dispose() => _transport.Dispose();

    private sealed class DeploymentAdapter(SubscriptionCredentials credentials) : IAnthropicDeployment
    {
        public string Name => "subscription";
        public IReadOnlyList<Secret> Secrets => credentials.Secrets;
        public HttpRequestMessage CreateRequest(in AnthropicRequestInfo info, ReadOnlyMemory<byte> body) =>
            new(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new ByteArrayContent(body.ToArray()) };
    }
}
