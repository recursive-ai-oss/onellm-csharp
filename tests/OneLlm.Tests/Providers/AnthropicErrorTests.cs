using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using static OneLlm.Tests.Providers.AnthropicKit;

namespace OneLlm.Tests.Providers;

/// <summary>Typed error mapping and retries for the Anthropic provider, from error fixtures.</summary>
public class AnthropicErrorTests
{
    private const string RequestId = "[scrubbed]";

    private static async Task<T> Fails<T>(string fixture) where T : ProviderException
    {
        (AnthropicProvider p, var h) = Open(fixture);
        T e = await Assert.ThrowsAsync<T>(() => p.SendAsync(Hi()));
        h.AssertAllUsed();
        Assert.Equal("anthropic", e.Provider);
        return e;
    }

    [Fact]
    public async Task Invalid_request_is_a_bad_request()
    {
        BadRequestException e = await Fails<BadRequestException>("errors/invalid-request");
        Assert.Equal(400, e.StatusCode);
        Assert.Equal("invalid_request_error", e.Code);
        Assert.Equal(RequestId, e.RequestId);
        Assert.False(e.IsTransient);
        Assert.Contains("must be non-empty", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_error_is_an_auth_error_and_never_shows_the_key()
    {
        (AnthropicProvider p, var h) = Open("errors/authentication");
        AuthException e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(Hi()));
        h.AssertAllUsed();
        Assert.Equal(401, e.StatusCode);
        Assert.Equal("authentication_error", e.Code);
        Assert.Equal(RequestId, e.RequestId);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task Permission_error_is_an_auth_error()
    {
        AuthException e = await Fails<AuthException>("errors/permission");
        Assert.Equal(403, e.StatusCode);
        Assert.Equal("permission_error", e.Code);
    }

    [Fact]
    public async Task Not_found_and_too_large_are_bad_requests_with_their_status()
    {
        Assert.Equal(404, (await Fails<BadRequestException>("errors/not-found")).StatusCode);
        BadRequestException large = await Fails<BadRequestException>("errors/too-large");
        Assert.Equal(413, large.StatusCode);
        Assert.Equal("request_too_large", large.Code);
    }

    [Fact]
    public async Task Billing_error_is_a_plain_provider_error_and_not_transient()
    {
        ProviderException e = await Fails<ProviderException>("errors/billing");
        Assert.Equal(402, e.StatusCode);
        Assert.Equal("billing_error", e.Code);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task Server_errors_are_provider_errors_and_transient()
    {
        ProviderException api = await Fails<ProviderException>("errors/api-error");
        Assert.Equal(500, api.StatusCode);
        Assert.Equal("api_error", api.Code);
        Assert.True(api.IsTransient);
        ProviderException timeout = await Fails<ProviderException>("errors/timeout");
        Assert.Equal(504, timeout.StatusCode);
        Assert.True(timeout.IsTransient);
    }

    [Fact]
    public async Task Overloaded_is_overloaded_and_transient()
    {
        OverloadedException e = await Fails<OverloadedException>("errors/overloaded");
        Assert.Equal(529, e.StatusCode);
        Assert.Equal("overloaded_error", e.Code);
        Assert.True(e.IsTransient);
    }

    [Fact]
    public async Task Context_overflow_carries_both_numbers()
    {
        ContextOverflowException e = await Fails<ContextOverflowException>("errors/context-overflow");
        Assert.Equal(210000, e.RequestedTokens);
        Assert.Equal(200000, e.ContextWindow);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task A_spend_cap_429_is_quota_exhausted_and_not_transient()
    {
        RateLimitException e = await Fails<RateLimitException>("errors/spend-cap");
        Assert.True(e.IsQuotaExhausted);
        Assert.False(e.IsTransient);
        Assert.Null(e.RetryAfter);
        Assert.Equal(429, e.StatusCode);
    }

    [Fact]
    public async Task A_429_without_retry_after_is_not_waited_out_when_retries_are_off()
    {
        RateLimitException e = await Fails<RateLimitException>("errors/rate-limit-no-retry-after");
        Assert.False(e.IsQuotaExhausted);
        Assert.True(e.IsTransient);
        Assert.Equal("rate_limit_error", e.Code);
    }

    [Fact]
    public async Task A_user_set_spend_limit_is_a_bad_request_that_keeps_the_message()
    {
        BadRequestException e = await Fails<BadRequestException>("errors/user-spend-limit");
        Assert.Contains("specified API usage limits", e.Message, StringComparison.Ordinal);
    }

    // ---- retries ----

    private static readonly TimeSpan s_attemptTimeout = TimeSpan.FromMinutes(10); // the provider's default per-attempt limit

    [Fact]
    public async Task A_429_with_retry_after_waits_the_stated_seconds_then_succeeds()
    {
        var time = new ManualTimeProvider();
        (AnthropicProvider p, var h) = Open("errors/rate-limit-retry", new RetryPolicy { JitterFraction = 0 }, time);
        Task<ChatResponse> send = p.SendAsync(Hi());
        await time.WaitForPendingAsync(ignoreDueTime: s_attemptTimeout);
        Assert.False(send.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(7), time.CreatedDueTimes.Last(d => d != s_attemptTimeout));
        time.Advance(TimeSpan.FromSeconds(7));

        ChatResponse r = await send;
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Overload_is_retried_with_backoff_then_succeeds()
    {
        var time = new ManualTimeProvider();
        (AnthropicProvider p, var h) = Open("errors/overloaded-retry", new RetryPolicy { JitterFraction = 0 }, time);
        Task<ChatResponse> send = p.SendAsync(Hi());
        await time.WaitForPendingAsync(ignoreDueTime: s_attemptTimeout);
        time.Advance(TimeSpan.FromSeconds(30));
        ChatResponse r = await send;
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
    }

    // ---- streams ----

    [Fact]
    public async Task An_error_event_in_the_middle_of_a_stream_throws_the_typed_error_after_the_events_before_it()
    {
        (AnthropicProvider p, var h) = Open("errors/stream-mid-error");
        var kinds = new List<StreamEventKind>();
        var text = new List<string>();
        OverloadedException e = await Assert.ThrowsAsync<OverloadedException>(async () =>
        {
            await foreach (StreamEvent ev in p.StreamAsync(Hi()))
            {
                kinds.Add(ev.Kind);
                if (ev.Kind == StreamEventKind.TextDelta)
                {
                    text.Add(ev.GetText());
                }
            }
        });
        h.AssertAllUsed();
        Assert.Equal("overloaded_error", e.Code);
        Assert.Equal(["Partial "], text);
        Assert.DoesNotContain(StreamEventKind.Done, kinds);
    }

    [Fact]
    public async Task A_stream_that_ends_before_message_stop_is_malformed()
    {
        (AnthropicProvider p, var h) = Open("errors/stream-truncated");
        MalformedResponseException e = await Assert.ThrowsAsync<MalformedResponseException>(async () =>
        {
            await foreach (StreamEvent unused in p.StreamAsync(Hi()))
            {
                _ = unused;
            }
        });
        h.AssertAllUsed();
        Assert.Contains("message_stop", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_is_scrubbed_from_error_text()
    {
        // A server that echoes the key in its message must not leak it through the exception.
        var secret = new Secret("test-" + Guid.NewGuid().ToString("N"));
        var handler = new EchoHandler(secret.Value);
        var p = AnthropicProvider.Create(secret, TimeProvider.System, new AnthropicOptions
        {
            Http = new HttpClient(handler),
            BaseAddress = new Uri("http://localhost/"),
            Retry = RetryPolicy.None,
        });
        BadRequestException e = await Assert.ThrowsAsync<BadRequestException>(() => p.SendAsync(Hi()));
        Assert.DoesNotContain(secret.Value, e.Message, StringComparison.Ordinal);
        Assert.Contains(Secret.Mask, e.Message, StringComparison.Ordinal);
    }

    private sealed class EchoHandler(string key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent($$$"""{"type":"error","error":{"type":"invalid_request_error","message":"bad key {{{key}}}"}}""", System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
