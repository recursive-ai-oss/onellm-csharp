namespace OneLlm.Http;

/// <summary>
/// When and how long to wait before sending a failed request again. Only transient failures are retried: connection errors, time-outs, and
/// the statuses 408, 429, 500, 502, 503, 504 and 529. A 4xx that says the request itself is wrong is never retried.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>A policy that never retries.</summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>The policy used when none is given: 2 retries, 500 ms base delay, 30 s cap, 25% jitter.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Most extra attempts after the first. Default 2. 0 turns retries off.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>The wait before the first retry; it doubles with each further retry. Default 500 ms.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait chosen by backoff. Default 30 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest wait the provider may ask for (<c>Retry-After</c>) that is still waited out. A longer request is not retried: the error is
    /// thrown with <c>RetryAfter</c> set and the caller decides. Default 60 s.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Random extra wait as a fraction of the backoff, 0 to 1: with 0.25 the wait is 100% to 125% of the backoff. Default 0.25.</summary>
    public double JitterFraction { get; init; } = 0.25;

    /// <summary>Works out how long to wait before retry number <paramref name="attempt"/>.</summary>
    /// <param name="attempt">0 for the first retry, 1 for the second, ...</param>
    /// <param name="retryAfter">What the provider asked for, when it said.</param>
    /// <param name="jitter">A random number in [0, 1) that scales <see cref="JitterFraction"/>.</param>
    /// <returns>The wait: the backoff (with jitter, capped at <see cref="MaxDelay"/>) or the provider's request, whichever is longer; or null when the provider asked for more than <see cref="MaxRetryAfter"/> and the request should not be retried.</returns>
    public TimeSpan? GetDelay(int attempt, TimeSpan? retryAfter, double jitter)
    {
        if (retryAfter is { } asked && asked > MaxRetryAfter)
        {
            return null;
        }

        // Doubling is done in milliseconds as a decimal-free loop so a large attempt number cannot overflow.
        double ms = BaseDelay.TotalMilliseconds;
        double cap = MaxDelay.TotalMilliseconds;
        for (int i = 0; i < attempt && ms < cap; i++)
        {
            ms *= 2;
        }

        ms = Math.Min(ms, cap) * (1 + (Math.Clamp(jitter, 0, 1) * Math.Clamp(JitterFraction, 0, 1)));
        ms = Math.Min(ms, cap);
        TimeSpan backoff = TimeSpan.FromMilliseconds(ms);
        return retryAfter is { } wait && wait > backoff ? wait : backoff;
    }
}
