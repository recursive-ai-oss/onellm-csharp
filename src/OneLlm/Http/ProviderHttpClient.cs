using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OneLlm.Http;

/// <summary>What a provider-specific error mapper is given about a failed response.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The start of the response body (at most 64 KiB), with the client's secrets already removed.</param>
/// <param name="Headers">The response headers.</param>
/// <param name="RetryAfter">The parsed <c>Retry-After</c>, when the response had one.</param>
public readonly record struct ProviderErrorContext(string Provider, HttpStatusCode Status, string Body, HttpResponseHeaders Headers, TimeSpan? RetryAfter);

/// <summary>Turns a failed response into the typed error to throw. Providers pass one to read their own error bodies.</summary>
/// <param name="context">The failed response.</param>
/// <returns>The error. Its message must be built from <see cref="ProviderErrorContext.Body"/> only, as that is the scrubbed text.</returns>
public delegate ProviderException ProviderErrorMapper(in ProviderErrorContext context);

/// <summary>
/// Sends HTTP requests for one provider: applies a time limit, retries transient failures by a <see cref="RetryPolicy"/> (honouring
/// <c>Retry-After</c>), removes the client's secrets from anything that reaches an error, and throws typed errors.
/// </summary>
/// <remarks>
/// The time limit covers the wait for response headers. For a non-streaming call that is the whole generation, since servers answer when it
/// finishes; for a stream it is the wait for the first byte. Idle limits while reading a stream are the caller's. Thread-safe.
/// </remarks>
public sealed class ProviderHttpClient
{
    private const int MaxErrorBodyBytes = 64 * 1024;
    private const int MaxEchoedBody = 400;

    private readonly string _provider;
    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;
    private readonly TimeProvider _time;
    private readonly IEnumerable<Secret> _secrets;
    private readonly TimeSpan _timeout;
    private readonly Func<double> _jitter;
    private readonly ProviderErrorMapper? _mapper;

    /// <summary>Creates the client.</summary>
    /// <param name="provider">The provider's name, used in errors.</param>
    /// <param name="time">The clock used for time limits and waits between retries.</param>
    /// <param name="http">The client to send with; <see cref="SharedHttpClient.Instance"/> when null. Pass a client over a test handler to replay fixtures.</param>
    /// <param name="retry">The retry policy; <see cref="RetryPolicy.Default"/> when null.</param>
    /// <param name="secrets">Keys and tokens to remove from error text.</param>
    /// <param name="timeout">The limit for one attempt to get response headers. Default 10 minutes.</param>
    /// <param name="jitter">Supplies the random number in [0, 1) for retry jitter; a shared <see cref="Random"/> when null. Tests pass a fixed value.</param>
    /// <param name="errorMapper">Provider-specific error mapping; the status-based default when null.</param>
    public ProviderHttpClient(
        string provider,
        TimeProvider time,
        HttpClient? http = null,
        RetryPolicy? retry = null,
        IEnumerable<Secret>? secrets = null,
        TimeSpan? timeout = null,
        Func<double>? jitter = null,
        ProviderErrorMapper? errorMapper = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _http = http ?? SharedHttpClient.Instance;
        _retry = retry ?? RetryPolicy.Default;
        // A live set is kept by reference so tokens minted later are scrubbed too; anything else is copied.
        _secrets = secrets as LiveSecrets ?? (secrets is null ? [] : [.. secrets]);
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        // Jitter only spreads out retries; it needs no unpredictability.
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
        _mapper = errorMapper;
    }

    /// <summary>The provider's name.</summary>
    public string Provider => _provider;

    /// <summary>Removes the client's secrets from text that came from outside.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cleaned text.</returns>
    public string Scrub(string text) => Secret.ScrubAll(text, _secrets);

    /// <summary>Sends a request, retrying transient failures.</summary>
    /// <param name="createRequest">Builds a fresh request for each attempt (a request message cannot be sent twice).</param>
    /// <param name="cancellationToken">Cancels the call, including a wait between retries.</param>
    /// <returns>The successful response with its headers read and its body not yet read. The caller disposes it.</returns>
    /// <exception cref="ProviderException">The request failed for good; the subclass says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createRequest);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderException failure;
            TimeSpan? retryAfter = null;
            HttpResponseMessage? response = null;
            try
            {
                using var timeoutSource = new CancellationTokenSource(_timeout, _time);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
                using HttpRequestMessage request = createRequest();
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    HttpResponseMessage ok = response;
                    response = null;
                    return ok;
                }

                retryAfter = ParseRetryAfter(response);
                string body = await ReadErrorBodyAsync(response, linked.Token).ConfigureAwait(false);
                var context = new ProviderErrorContext(_provider, response.StatusCode, Scrub(body), response.Headers, retryAfter);
                failure = _mapper is not null ? _mapper(context) : DefaultMapper(context);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new TransportException(_provider, "the request timed out.", isTimeout: true);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                // The inner exception is not kept: its text can carry whatever a server or a proxy sent back.
                failure = new TransportException(_provider, Scrub("the request failed: " + e.Message));
            }
            finally
            {
                response?.Dispose();
            }

            if (!failure.IsTransient || attempt >= _retry.MaxRetries)
            {
                throw failure;
            }

            TimeSpan? asked = retryAfter ?? (failure as RateLimitException)?.RetryAfter ?? (failure as OverloadedException)?.RetryAfter;
            TimeSpan? delay = _retry.GetDelay(attempt, asked, _jitter());
            if (delay is null)
            {
                throw failure;
            }

            await Task.Delay(delay.Value, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a response body into memory, failing if it is larger than a limit.</summary>
    /// <param name="response">The response.</param>
    /// <param name="maxBytes">The most bytes to accept.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body.</returns>
    /// <exception cref="MalformedResponseException">The body is larger than <paramref name="maxBytes"/>.</exception>
    public async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new MalformedResponseException(_provider, $"the response is larger than {maxBytes} bytes.", (int)response.StatusCode);
        }

        using var buffer = new MemoryStream();
        Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new MalformedResponseException(_provider, $"the response is larger than {maxBytes} bytes.", (int)response.StatusCode);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.ToArray();
    }

    private static ProviderException DefaultMapper(in ProviderErrorContext context)
    {
        string text = context.Body.Length <= MaxEchoedBody ? context.Body : context.Body[..MaxEchoedBody];
        return ProviderErrors.FromStatus(context.Provider, context.Status, $"HTTP {(int)context.Status}: {text}", context.RetryAfter);
    }

    // Error bodies are small; at most 64 KiB of one is read for the message.
    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            byte[] buffer = new byte[MaxErrorBodyBytes];
            int total = 0;
            int read;
            while (total < buffer.Length && (read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
    }

    // Retry-After is seconds or an HTTP date (RFC 9110 section 10.2.3). retry-after-ms is a header some providers add, in milliseconds.
    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out IEnumerable<string>? ms)
            && double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double millis)
            && millis >= 0)
        {
            return TimeSpan.FromMilliseconds(millis);
        }

        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            TimeSpan wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
