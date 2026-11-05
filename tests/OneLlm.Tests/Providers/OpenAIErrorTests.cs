using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAIErrorTests
{
    private const string Dir = "openai/direct/errors/";

    private static ChatRequest Ask() => new() { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Hi.")] };

    private static async Task<T> Fails<T>(string fixture)
        where T : ProviderException
    {
        using var fx = OpenAIFx.Open(Dir + fixture + ".json");
        T e = await Assert.ThrowsAsync<T>(() => OpenAIFx.OpenAI(fx).SendAsync(Ask()));
        Assert.Equal("openai", e.Provider);
        fx.AssertAllUsed();
        return e;
    }

    [Fact]
    public async Task An_invalid_key_is_an_auth_error_and_the_text_is_the_servers_message()
    {
        AuthException e = await Fails<AuthException>("auth-401");
        Assert.Equal(401, e.StatusCode);
        Assert.Contains("Incorrect API key", e.Message, StringComparison.Ordinal);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task A_forbidden_region_is_an_auth_error()
    {
        Assert.Equal(403, (await Fails<AuthException>("forbidden-403")).StatusCode);
    }

    [Fact]
    public async Task A_rate_limit_carries_the_retry_after_and_is_not_waited_out_when_it_is_too_long()
    {
        RateLimitException e = await Fails<RateLimitException>("rate-429-long");
        Assert.Equal(TimeSpan.FromHours(1), e.RetryAfter);
        Assert.False(e.IsQuotaExhausted);
    }

    [Fact]
    public async Task Out_of_quota_is_a_rate_limit_that_waiting_will_not_fix()
    {
        RateLimitException e = await Fails<RateLimitException>("quota-429");
        Assert.True(e.IsQuotaExhausted);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task A_context_overflow_reports_the_window_and_the_size_requested()
    {
        ContextOverflowException e = await Fails<ContextOverflowException>("overflow-400");
        Assert.Equal(400000, e.ContextWindow);
        Assert.Equal(412345, e.RequestedTokens);
    }

    [Fact]
    public async Task A_policy_rejection_is_a_content_filter_error()
    {
        await Fails<ContentFilterException>("content-filter-400");
    }

    [Fact]
    public async Task An_unsupported_parameter_and_an_unknown_model_are_bad_requests()
    {
        Assert.Equal(400, (await Fails<BadRequestException>("bad-request-400")).StatusCode);
        Assert.Equal(404, (await Fails<BadRequestException>("not-found-404")).StatusCode);
    }

    [Fact]
    public async Task Server_errors_are_transient()
    {
        ProviderException e = await Fails<ProviderException>("server-500");
        Assert.Equal(500, e.StatusCode);
        Assert.True(e.IsTransient);
        OverloadedException o = await Fails<OverloadedException>("overloaded-503");
        Assert.True(o.IsTransient);
    }

    [Fact]
    public async Task A_body_that_is_not_json_still_gives_a_typed_error_with_a_bounded_message()
    {
        ProviderException e = await Fails<ProviderException>("not-json-502");
        Assert.Equal(502, e.StatusCode);
        Assert.Contains("Bad gateway", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_never_appears_in_an_error()
    {
        using var fx = OpenAIFx.Open(Dir + "auth-401.json");
        var secret = new Secret("sk-synthetic");
        var p = new OpenAIProvider(new OpenAIOptions { ApiKey = secret, HttpClient = fx.Client, Retry = RetryPolicy.None, Catalog = OpenAIFx.Catalog }, new ManualTimeProvider());
        AuthException e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(Ask()));
        // The recorded error text quotes the key back, as the API does; it must not survive.
        Assert.DoesNotContain(secret.Value, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Value, e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_429_with_retry_after_is_retried_after_that_long()
    {
        var time = new ManualTimeProvider();
        using var fx = OpenAIFx.Open(Dir + "retry-after.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx, retry: new RetryPolicy { JitterFraction = 0 }, time: time);
        Task<ChatResponse> call = p.SendAsync(Ask());
        await time.WaitForPendingAsync(ignoreDueTime: TimeSpan.FromMinutes(10));
        Assert.False(call.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(7), time.CreatedDueTimes.Last(d => d != TimeSpan.FromMinutes(10)));
        time.Advance(TimeSpan.FromSeconds(7));
        ChatResponse r = await call;
        Assert.Equal("Back again.", r.Message.GetAssistantText());
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task An_error_event_in_the_middle_of_a_stream_throws_after_the_text_that_came_first()
    {
        using var fx = OpenAIFx.Open(Dir + "stream-error-event.json");
        var seen = new List<string>();
        ProviderException e = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (StreamEvent ev in OpenAIFx.OpenAI(fx).StreamAsync(Ask()))
            {
                if (ev.Kind == StreamEventKind.TextDelta)
                {
                    seen.Add(ev.GetText());
                }
            }
        });
        Assert.Equal(["Par"], seen);
        Assert.Contains("server had an error", e.Message, StringComparison.Ordinal);
        Assert.True(e.IsTransient);
    }

    [Fact]
    public async Task A_failed_response_event_maps_its_code()
    {
        using var fx = OpenAIFx.Open(Dir + "stream-failed.json");
        await Assert.ThrowsAsync<RateLimitException>(async () =>
        {
            await foreach (StreamEvent unused in OpenAIFx.OpenAI(fx).StreamAsync(Ask()))
            {
            }
        });
    }

    [Fact]
    public async Task A_stream_that_ends_early_is_a_malformed_response()
    {
        using var fx = OpenAIFx.Open(Dir + "stream-truncated.json");
        await Assert.ThrowsAsync<MalformedResponseException>(async () =>
        {
            await foreach (StreamEvent unused in OpenAIFx.OpenAI(fx).StreamAsync(Ask()))
            {
            }
        });
    }

    [Fact]
    public async Task An_error_object_in_a_chat_completions_stream_throws()
    {
        using var fx = OpenAIFx.Open(Dir + "chat-stream-error.json");
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (StreamEvent unused in OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions).StreamAsync(new ChatRequest { Model = "gpt-4.1", Messages = [Message.FromUser("Hi.")] }))
            {
            }
        });
    }
}
