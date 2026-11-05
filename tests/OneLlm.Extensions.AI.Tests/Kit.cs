using System.Text.Json;
using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using OneChatResponse = OneLlm.Providers.ChatResponse;

namespace OneLlm.Extensions.AI.Tests;

internal sealed class FixedTime : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>Providers whose HTTP client answers from the recorded fixtures, one fixture per provider instance.</summary>
internal static class Kit
{
    public static readonly TimeProvider Time = new FixedTime();

    private static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "providers", relative + ".json");

    public static (IChatProvider Provider, ReplayHandler Handler) Anthropic(string fixture)
    {
        var handler = ReplayHandler.FromFile(Path("anthropic/direct/" + fixture));
        var options = new AnthropicOptions { Http = new HttpClient(handler), BaseAddress = new Uri("http://localhost/"), Retry = RetryPolicy.None };
        return (AnthropicProvider.Create(new Secret("test-" + Guid.NewGuid().ToString("N")), Time, options), handler);
    }

    public static (IChatProvider Provider, ReplayHandler Handler) OpenAI(string fixture)
    {
        var handler = ReplayHandler.FromFile(Path("openai/direct/" + fixture));
        var options = new OpenAIOptions
        {
            ApiKey = new Secret("test-key"),
            HttpClient = new HttpClient(handler),
            Catalog = ModelCatalog.Create([OpenAICatalog.Shipped()]),
            Retry = RetryPolicy.None,
        };
        return (new OpenAIProvider(options, Time), handler);
    }

    public static (IChatProvider Provider, ReplayHandler Handler) Gemini(string fixture)
    {
        var handler = ReplayHandler.FromFile(Path("gemini/direct/" + fixture));
        var options = new GeminiOptions
        {
            Time = Time,
            Auth = new GeminiApiKeyAuth(new Secret("test-key-0000")),
            Http = new HttpClient(handler),
            Catalog = ModelCatalog.Create([GeminiProvider.GetCatalogLayer()]),
            Retry = new RetryPolicy { JitterFraction = 0 },
            Timeout = TimeSpan.FromDays(1),
            Jitter = () => 0.0,
        };
        return (new GeminiProvider(options), handler);
    }

    public static string Json(Message m) => JsonSerializer.Serialize(m, OneLlmJsonContext.Default.Message);

    /// <summary>The turn without the fields a chat response carries beside it (usage, id, time), which the bridge puts on the response instead.</summary>
    public static string Body(Message m) => Json(new Message { Assistant = m.Assistant, ToolCalls = m.ToolCalls, User = m.User });

    public static IEnumerable<FunctionCallContent> Calls(MeaiChatResponse r) => r.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>();

    public static async Task<MeaiChatResponse> Collect(IAsyncEnumerable<ChatResponseUpdate> updates, List<ChatResponseUpdate>? seen = null)
    {
        var all = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate u in updates)
        {
            all.Add(u);
        }

        seen?.AddRange(all);
        return all.ToChatResponse();
    }

    /// <summary>Asserts the bridged response says what the provider's own response says.</summary>
    public static void AssertSame(MeaiChatResponse bridged, OneChatResponse direct)
    {
        Assert.Equal(direct.Message.GetAssistantText(), bridged.Text);
        Assert.Equal(direct.Id, bridged.ResponseId);
        Assert.Equal(ChatMapping_ToFinish(direct), bridged.FinishReason);
        Assert.Equal(direct.Usage!.TotalInputTokens, bridged.Usage!.InputTokenCount);
        Assert.Equal(direct.Usage.OutputTokens, bridged.Usage.OutputTokenCount);
        Assert.Equal(direct.Usage.CacheReadInputTokens, bridged.Usage.CachedInputTokenCount);
        FunctionCallContent[] calls = [.. Calls(bridged)];
        Assert.Equal(direct.Message.ToolCalls.Select(c => c.Id), calls.Select(c => c.CallId));
        Assert.Equal(direct.Message.ToolCalls.Select(c => c.Tool), calls.Select(c => c.Name));
        // The turn comes back from the bridged messages unchanged: parts, calls, their order and every extension field.
        Assert.Equal(Body(direct.Message), Body(ContentMapper.ToTurn(bridged.Messages)));
    }

    private static ChatFinishReason? ChatMapping_ToFinish(OneChatResponse r) => ChatMapping.ToFinish(r.Finish, r.FinishReasonRaw);
}
