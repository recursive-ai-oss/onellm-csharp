using OneLlm.Messages;
using OneLlm.Providers.OpenAI;
using OneLlm.Providers.Subscriptions;

namespace OneLlm.Providers.Codex;

/// <summary>Opt-in ChatGPT subscription chat over the Codex Responses wire. Reads and refreshes the official CLI's credentials in one explicit directory. Thread-safe.</summary>
/// <remarks>Uses undocumented consumer endpoints which may change. Users are responsible for compliance with their provider's terms. Whole turns are accumulated from the stream-only backend.</remarks>
public sealed class CodexProvider : IChatProvider, IDisposable
{
    private readonly OpenAIWire _wire;
    private readonly HttpClient _transport;
    private readonly SubscriptionCredentials _credentials;

    /// <summary>Creates a provider for a single account; no default registration or environment lookup occurs.</summary>
    /// <param name="configDirectory">The directory used as CODEX_HOME by the CLI, containing auth.json. Authentication failures name this directory, never a token.</param>
    /// <param name="timeProvider">Clock for expiry and retries.</param>
    /// <param name="options">Optional transport overrides, primarily for testing.</param>
    /// <exception cref="ArgumentException">The directory is empty or an endpoint is insecure.</exception>
    public CodexProvider(string configDirectory, TimeProvider timeProvider, SubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= new();
        var credentials = new SubscriptionCredentials(configDirectory, false, timeProvider, options);
        _credentials = credentials;
        _transport = new HttpClient(new SubscriptionTransport(credentials, false, options)) { Timeout = Timeout.InfiniteTimeSpan };
        _wire = new OpenAIWire("codex", "codex", OpenAIProtocol.Responses, ChatDialect.OpenAI, new EndpointAdapter(credentials), new OpenAIOptions { HttpClient = _transport }, timeProvider, scrubStreamErrors: true);
        SubscriptionCatalog.Register();
    }

    /// <summary>The live set of credentials used by this provider, for scrubbing opt-in raw logs. Values are wrapped in Secret and never print as plaintext.</summary>
    public IReadOnlyList<Secret> Secrets => _credentials.Secrets;

    /// <inheritdoc />
    public string Name => "codex";

    /// <inheritdoc />
    public string Deployment => "subscription";

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var accumulator = new MessageAccumulator();
        await foreach (StreamEvent e in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            accumulator.Add(e);
        }
        return new ChatResponse { Message = accumulator.ToMessage(), Id = accumulator.ResponseId, Model = accumulator.Model, Finish = accumulator.Finish, FinishReasonRaw = accumulator.FinishReasonRaw, Warnings = accumulator.Warnings, Cost = Cost.Zero };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => _wire.StreamAsync(request, cancellationToken);

    /// <summary>Disposes this provider's transport, not a caller-supplied HTTP client.</summary>
    public void Dispose() => _transport.Dispose();

    private sealed class EndpointAdapter(SubscriptionCredentials credentials) : OpenAIEndpoint
    {
        public override Uri GetUri(string path, string? model) => new("https://chatgpt.com/backend-api/codex/responses");
        public override void Authenticate(HttpRequestMessage request) { }
        public override IEnumerable<Secret> Secrets => credentials.Secrets;
    }
}
