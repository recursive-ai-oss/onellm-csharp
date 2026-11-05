using System.Text.Json.Nodes;
using OneLlm.Caching;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Tests.Caching;

public class ResultCacheTests
{
    private static ChatRequest Request(string text = "hello") =>
        new() { Model = "m", System = "be brief", Messages = [Message.FromUser(text)], Temperature = 0.2 };

    private static ChatResponse Response(string text) => new() { Message = Message.FromAssistant(text), Cost = new Cost([CostLine.Create("inputTokens", 1, 1m, 5m)]) };

    [Fact]
    public void The_key_changes_with_anything_that_can_change_the_answer()
    {
        string baseKey = ResultCache.KeyFor("p", Request());
        Assert.Equal(64, baseKey.Length);
        Assert.Equal(baseKey, ResultCache.KeyFor("p", Request()));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("q", Request()));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request("hello!")));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { Model = "n" }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { System = null }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { Temperature = 0.3 }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { MaxOutputTokens = 10 }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { Tools = [new ToolDefinition("t", "d", new JsonObject())] }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { ResponseFormat = ResponseFormat.FromSchema("s", new JsonObject { ["type"] = "object" }) }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { ExtraParameters = new JsonObject { ["seed"] = 1 } }));
        Assert.NotEqual(baseKey, ResultCache.KeyFor("p", Request() with { StopSequences = ["END"] }));
    }

    [Fact]
    public void The_key_ignores_settings_that_only_steer_provider_side_caching_and_does_not_confuse_field_boundaries()
    {
        string baseKey = ResultCache.KeyFor("p", Request());
        Assert.Equal(baseKey, ResultCache.KeyFor("p", Request() with { Cache = new CachePolicy { Key = "k" }, UserId = "u" }));
        Assert.NotEqual(ResultCache.KeyFor("ab", new ChatRequest { Model = "c" }), ResultCache.KeyFor("a", new ChatRequest { Model = "bc" }));
        Assert.NotEqual(ResultCache.KeyFor("p", new ChatRequest { Model = "m", System = null }), ResultCache.KeyFor("p", new ChatRequest { Model = "m", System = "" }));
    }

    [Fact]
    public void Asset_bytes_are_part_of_the_key()
    {
        ChatRequest With(string data) => new() { Model = "m", Messages = [new Message { User = [new AssetPart(new AssetRef { ContentType = "image/png", Data = data })] }] };
        Assert.NotEqual(ResultCache.KeyFor("p", With("AAAA")), ResultCache.KeyFor("p", With("AAAB")));
    }

    [Fact]
    public void The_least_recently_used_answer_is_dropped_at_capacity()
    {
        var cache = new ResultCache(2);
        cache.Set("a", Response("A"));
        cache.Set("b", Response("B"));
        Assert.True(cache.TryGet("a", out _));      // a is now the most recent
        cache.Set("c", Response("C"));              // drops b
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out ChatResponse? a));
        Assert.Equal("A", a!.Message.GetAssistantText());
        Assert.Equal(2, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    private sealed class CountingProvider : IChatProvider
    {
        public string Name => "fake";

        public string Deployment => "direct";

        public int Sends { get; private set; }

        public int Streams { get; private set; }

        public Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Sends++;
            return Task.FromResult(Response("answer " + Sends));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task A_repeated_request_is_answered_from_the_cache_at_zero_cost_and_a_different_one_is_sent()
    {
        var inner = new CountingProvider();
        var cached = new CachingChatProvider(inner, new ResultCache());

        ChatResponse first = await cached.SendAsync(Request());
        ChatResponse second = await cached.SendAsync(Request());
        ChatResponse other = await cached.SendAsync(Request("something else"));

        Assert.Equal(2, inner.Sends);
        Assert.Equal("answer 1", second.Message.GetAssistantText());
        Assert.Equal(5m, first.Cost!.Total);
        Assert.Equal(0m, second.Cost!.Total);
        Assert.Equal("answer 2", other.Message.GetAssistantText());
    }

    [Fact]
    public async Task Streaming_passes_through_uncached()
    {
        var inner = new CountingProvider();
        var cached = new CachingChatProvider(inner, new ResultCache());
        await foreach (StreamEvent unused in cached.StreamAsync(Request()))
        {
        }

        await foreach (StreamEvent unused in cached.StreamAsync(Request()))
        {
        }

        Assert.Equal(2, inner.Streams);
        Assert.Equal("fake", cached.Name);
    }
}
