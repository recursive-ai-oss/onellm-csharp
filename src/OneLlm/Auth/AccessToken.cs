using OneLlm.Http;

namespace OneLlm.Auth;

/// <summary>A bearer token and the moment it stops being accepted.</summary>
/// <param name="Token">The token. It is held as a <see cref="Secret"/> so it never reaches a log or an error message.</param>
/// <param name="ExpiresAt">When the issuer will refuse it.</param>
public readonly record struct AccessToken(Secret Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps one bearer token and fetches a new one shortly before it expires. Used by every deployment that authenticates with a short-lived
/// token (Vertex AI with a service account, Azure with Microsoft Entra ID, any caller-supplied token source). Thread-safe: concurrent
/// callers that find the token stale share one fetch.
/// </summary>
public sealed class AccessTokenCache
{
    /// <summary>How long before expiry a token is replaced. Default five minutes, so a token never expires while a long request is in flight.</summary>
    public static TimeSpan DefaultRefreshBefore { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest one fetch may take. A fetch is shared by every caller that finds the token stale and is not cancelled by any one of them.</summary>
    private static readonly TimeSpan s_fetchTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, ValueTask<AccessToken>> _source;
    private readonly TimeProvider _time;
    private readonly TimeSpan _refreshBefore;
    private readonly Lock _gate = new();
    private Task<Secret>? _inflight;

    // A reference, so a reader never sees half of a replaced token.
    private volatile Held? _current;

    /// <summary>Creates the cache.</summary>
    /// <param name="source">Fetches a fresh token. It is not called again until the token is near expiry.</param>
    /// <param name="time">The clock that decides when a token is stale. Required: the library never reads the system clock on its own.</param>
    /// <param name="refreshBefore">How long before expiry to fetch a new token; <see cref="DefaultRefreshBefore"/> when null.</param>
    public AccessTokenCache(Func<CancellationToken, ValueTask<AccessToken>> source, TimeProvider time, TimeSpan? refreshBefore = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _refreshBefore = refreshBefore ?? DefaultRefreshBefore;
        Secrets = new LiveSecrets();
    }

    /// <summary>The tokens this cache has handed out, for scrubbing error text. Pass it to the HTTP client of the provider.</summary>
    public LiveSecrets Secrets { get; }

    /// <summary>The token if one is held and has not expired, otherwise null. Never fetches. Retries of a request use this, after <see cref="GetAsync"/> ran once for the call.</summary>
    public Secret? Current
    {
        get
        {
            Held? held = _current;
            return held is not null && held.Token.ExpiresAt > _time.GetUtcNow() ? held.Token.Token : null;
        }
    }

    /// <summary>Returns a token that is valid for at least the refresh margin, fetching one when needed.</summary>
    /// <param name="cancellationToken">Stops this caller waiting. A fetch already under way finishes for the others.</param>
    /// <returns>The token.</returns>
    /// <exception cref="AuthException">The source returned an empty token.</exception>
    public ValueTask<Secret> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<Secret> pending;
        lock (_gate)
        {
            if (_current is { } fresh && fresh.Token.ExpiresAt - _refreshBefore > _time.GetUtcNow())
            {
                return ValueTask.FromResult(fresh.Token.Token);
            }

            pending = _inflight ??= FetchAsync();
        }

        return new ValueTask<Secret>(pending.WaitAsync(cancellationToken));
    }

    private async Task<Secret> FetchAsync()
    {
        // Leave the caller's lock before running the source, so a source that completes at once cannot clear the slot before it is filled.
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(s_fetchTimeout, _time);
            AccessToken token = await _source(timeout.Token).ConfigureAwait(false);
            if (token.Token is null || token.Token.IsEmpty)
            {
                throw new AuthException("auth", "the token source returned an empty token.", null);
            }

            Secrets.Add(token.Token);
            _current = new Held(token);
            return token.Token;
        }
        finally
        {
            lock (_gate)
            {
                _inflight = null;
            }
        }
    }

    private sealed record Held(AccessToken Token);
}
