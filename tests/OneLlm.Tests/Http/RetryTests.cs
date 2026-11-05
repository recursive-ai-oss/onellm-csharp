using System.Net;
using OneLlm.Http;

namespace OneLlm.Tests.Http;

public class RetryTests
{
    // A handler that answers from a script and counts calls.
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int i = Calls++;
            return steps[Math.Min(i, steps.Length - 1)](request, cancellationToken);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body = "{}", string? retryAfter = null) =>
        (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return Task.FromResult(response);
        };

    private static readonly TimeSpan s_longTimeout = TimeSpan.FromDays(1);

    private static ProviderHttpClient Client(ScriptedHandler handler, ManualTimeProvider time, RetryPolicy? retry = null, Secret? secret = null, TimeSpan? timeout = null) =>
        new("acme", time, new HttpClient(handler), retry ?? new RetryPolicy { JitterFraction = 0 }, secret is null ? null : [secret], timeout ?? s_longTimeout, () => 0.0);

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "http://acme.test/v1/chat") { Content = new StringContent("{}") };

    [Fact]
    public async Task A_429_is_retried_after_the_time_the_provider_asked_for()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler(Respond((HttpStatusCode)429, "slow down", retryAfter: "7"), Respond(HttpStatusCode.OK, "done"));
        Task<HttpResponseMessage> send = Client(handler, time).SendAsync(Request);

        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        Assert.False(send.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(7), time.CreatedDueTimes.Last(d => d != s_longTimeout));
        time.Advance(TimeSpan.FromSeconds(7));

        using HttpResponseMessage ok = await send;
        Assert.Equal("done", await ok.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Retry_after_may_be_a_date()
    {
        var time = new ManualTimeProvider();
        string date = time.GetUtcNow().AddSeconds(12).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable, retryAfter: date), Respond(HttpStatusCode.OK));
        Task<HttpResponseMessage> send = Client(handler, time).SendAsync(Request);
        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), time.CreatedDueTimes.Last(d => d != s_longTimeout));
        time.Advance(TimeSpan.FromSeconds(12));
        (await send).Dispose();
    }

    [Fact]
    public async Task Without_retry_after_the_wait_is_the_backoff_and_it_doubles()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler(Respond(HttpStatusCode.InternalServerError), Respond(HttpStatusCode.BadGateway), Respond(HttpStatusCode.OK));
        Task<HttpResponseMessage> send = Client(handler, time, new RetryPolicy { JitterFraction = 0, MaxRetries = 2 }).SendAsync(Request);

        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        time.Advance(TimeSpan.FromMilliseconds(500));
        while (handler.Calls < 2)
        {
            await Task.Delay(5);
        }

        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        (await send).Dispose();
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], time.CreatedDueTimes.Where(d => d != s_longTimeout));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task A_4xx_that_means_the_request_is_wrong_is_never_retried(int status)
    {
        var handler = new ScriptedHandler(Respond((HttpStatusCode)status, "no"));
        ProviderException e = await Assert.ThrowsAnyAsync<ProviderException>(() => Client(handler, new ManualTimeProvider()).SendAsync(Request));
        Assert.Equal(status, e.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task Retries_stop_after_the_limit_and_the_last_error_is_thrown()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler(Respond(HttpStatusCode.InternalServerError, "boom"));
        Task<HttpResponseMessage> send = Client(handler, time, new RetryPolicy { MaxRetries = 2, JitterFraction = 0 }).SendAsync(Request);
        for (int i = 0; i < 2; i++)
        {
            await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
            time.Advance(TimeSpan.FromSeconds(30));
            while (handler.Calls < i + 2)
            {
                await Task.Delay(5);
            }
        }

        ProviderException e = await Assert.ThrowsAsync<ProviderException>(() => send);
        Assert.Equal(500, e.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_wait_longer_than_the_limit_is_not_waited_out()
    {
        var handler = new ScriptedHandler(Respond((HttpStatusCode)429, "later", retryAfter: "3600"));
        var e = await Assert.ThrowsAsync<RateLimitException>(() => Client(handler, new ManualTimeProvider()).SendAsync(Request));
        Assert.Equal(TimeSpan.FromHours(1), e.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancelling_while_waiting_between_retries_stops_the_call()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));
        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> send = Client(handler, time).SendAsync(Request, cts.Token);
        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_sends_nothing()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler, new ManualTimeProvider()).SendAsync(Request, cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_request_that_never_answers_times_out_as_a_transport_error()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        Task<HttpResponseMessage> send = Client(handler, time, RetryPolicy.None, timeout: TimeSpan.FromSeconds(30)).SendAsync(Request);
        await time.WaitForPendingAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        TransportException e = await Assert.ThrowsAsync<TransportException>(() => send);
        Assert.True(e.IsTimeout);
    }

    [Fact]
    public async Task A_dropped_connection_is_a_transient_transport_error_that_is_retried()
    {
        var time = new ManualTimeProvider();
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("connection reset"), Respond(HttpStatusCode.OK));
        Task<HttpResponseMessage> send = Client(handler, time).SendAsync(Request);
        await time.WaitForPendingAsync(ignoreDueTime: s_longTimeout);
        time.Advance(TimeSpan.FromSeconds(1));
        (await send).Dispose();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_key_is_removed_from_error_text_and_from_transport_errors()
    {
        string key = "fake-" + new string('k', 6) + "-1234567890-" + new string('z', 6);
        var secret = new Secret(key);
        var echo = new ScriptedHandler(Respond(HttpStatusCode.BadRequest, $"{{\"error\":\"invalid key {key} supplied\"}}"));
        ProviderException e = await Assert.ThrowsAsync<BadRequestException>(() => Client(echo, new ManualTimeProvider(), secret: secret).SendAsync(Request));
        Assert.DoesNotContain(key, e.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", e.Message, StringComparison.Ordinal);

        var drop = new ScriptedHandler((_, _) => throw new HttpRequestException($"cannot reach host with {key}"));
        ProviderException t = await Assert.ThrowsAsync<TransportException>(() => Client(drop, new ManualTimeProvider(), RetryPolicy.None, secret).SendAsync(Request));
        Assert.DoesNotContain(key, t.Message, StringComparison.Ordinal);
        Assert.Null(t.InnerException);
    }

    [Fact]
    public async Task A_provider_can_map_its_own_error_bodies()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.BadRequest, "prompt is too long: 300000 tokens > 200000"));
        var client = new ProviderHttpClient("acme", new ManualTimeProvider(), new HttpClient(handler), RetryPolicy.None, errorMapper: (in ProviderErrorContext c) =>
            c.Body.Contains("too long", StringComparison.Ordinal)
                ? new ContextOverflowException(c.Provider, c.Body, (int)c.Status) { RequestedTokens = 300000, ContextWindow = 200000 }
                : ProviderErrors.FromStatus(c.Provider, c.Status, c.Body));
        ContextOverflowException e = await Assert.ThrowsAsync<ContextOverflowException>(() => client.SendAsync(Request));
        Assert.Equal(300000, e.RequestedTokens);
    }

    [Fact]
    public async Task Bodies_over_the_limit_are_refused()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK, new string('x', 1000)));
        ProviderHttpClient client = Client(handler, new ManualTimeProvider());
        using HttpResponseMessage response = await client.SendAsync(Request);
        await Assert.ThrowsAsync<MalformedResponseException>(() => client.ReadBodyAsync(response, 100));
        Assert.Equal(1000, (await client.ReadBodyAsync(response, 1000)).Length);
    }

    [Theory]
    [InlineData(0, 0.0, 500)]
    [InlineData(0, 0.999, 625)]
    [InlineData(1, 0.5, 1125)]
    [InlineData(3, 0.0, 4000)]
    [InlineData(10, 0.0, 30000)] // capped
    [InlineData(10, 0.999, 30000)] // jitter cannot pass the cap
    [InlineData(1000, 0.5, 30000)] // no overflow
    public void Backoff_doubles_jitter_stays_inside_its_fraction_and_the_cap_holds(int attempt, double jitter, double expectedMs)
    {
        TimeSpan? delay = RetryPolicy.Default.GetDelay(attempt, null, jitter);
        Assert.NotNull(delay);
        Assert.Equal(expectedMs, delay.Value.TotalMilliseconds, 1.0);
    }

    [Fact]
    public void Jitter_never_moves_the_wait_outside_its_bounds()
    {
        var policy = new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromMinutes(5), JitterFraction = 0.25 };
        for (int attempt = 0; attempt < 6; attempt++)
        {
            double floor = 1000 * Math.Pow(2, attempt);
            for (double j = 0; j < 1; j += 0.05)
            {
                double ms = policy.GetDelay(attempt, null, j)!.Value.TotalMilliseconds;
                Assert.InRange(ms, floor, floor * 1.25 + 0.001);
            }
        }
    }

    [Fact]
    public void A_provider_request_longer_than_the_backoff_wins_and_longer_than_the_limit_means_no_retry()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Default.GetDelay(0, TimeSpan.FromSeconds(10), 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPolicy.Default.GetDelay(0, TimeSpan.FromMilliseconds(100), 0));
        Assert.Null(RetryPolicy.Default.GetDelay(0, TimeSpan.FromSeconds(61), 0));
    }

    [Fact]
    public async Task The_shared_client_does_not_follow_redirects()
    {
        using var listener = new HttpListener();
        int port = ReservePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task serve = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = $"http://127.0.0.1:{port}/elsewhere";
            ctx.Response.Close();
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        request.Headers.TryAddWithoutValidation("x-api-key", "not-a-real-key");
        using HttpResponseMessage response = await SharedHttpClient.Instance.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        await serve;
    }

    private static int ReservePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
