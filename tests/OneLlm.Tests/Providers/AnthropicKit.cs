using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;

namespace OneLlm.Tests.Providers;

/// <summary>Shared pieces of the Anthropic tests: a provider over a replayed fixture and the requests the fixtures were written for.</summary>
internal static class AnthropicKit
{
    public const string Model = "claude-sonnet";

    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "providers", "anthropic", "direct", name + ".json");

    // The key is assembled at run time; it is not a real key and never leaves the process.
    public static Secret Key() => new("test-" + Guid.NewGuid().ToString("N"));

    /// <summary>A provider whose HTTP client answers from the fixture. Single-error fixtures use <see cref="RetryPolicy.None"/>.</summary>
    public static (AnthropicProvider Provider, ReplayHandler Handler) Open(
        string fixture,
        RetryPolicy? retry = null,
        TimeProvider? time = null,
        AnthropicOptions? options = null)
    {
        var handler = ReplayHandler.FromFile(FixturePath(fixture));
        AnthropicOptions o = (options ?? new AnthropicOptions()) with
        {
            Http = new HttpClient(handler),
            BaseAddress = new Uri("http://localhost/"),
            Retry = retry ?? RetryPolicy.None,
        };
        return (AnthropicProvider.Create(Key(), time ?? TimeProvider.System, o), handler);
    }

    public static ChatRequest Hi() => new()
    {
        Model = Model,
        Messages = [Message.FromUser("Say hello.")],
        MaxOutputTokens = 1024,
    };

    public static ChatRequest Ask(string text, int maxTokens = 1024) => new()
    {
        Model = Model,
        Messages = [Message.FromUser(text)],
        MaxOutputTokens = maxTokens,
    };

    public static ToolDefinition Weather() => new(
        "get_weather",
        "Get the current weather in a given location",
        (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string","description":"The city and state, e.g. San Francisco, CA"}},"required":["location"]}""")!);

    public static string Json(Message m) => JsonSerializer.Serialize(m, OneLlmJsonContext.Default.Message);

    /// <summary>The assistant turn followed by results for the named calls, as the loop would store it.</summary>
    public static Message WithResults(Message assistant, params (string Id, Part[] Result, bool IsError)[] results)
    {
        var calls = new List<ToolCall>();
        foreach (ToolCall c in assistant.ToolCalls)
        {
            (string Id, Part[] Result, bool IsError) r = results.FirstOrDefault(x => x.Id == c.Id);
            calls.Add(r.Id is null
                ? c
                : new ToolCall(c.Id, c.Tool)
                {
                    Inputs = c.Inputs,
                    After = c.After,
                    ExtensionData = c.ExtensionData,
                    Result = r.Result,
                    IsError = r.IsError,
                });
        }

        return new Message { Assistant = assistant.Assistant, ToolCalls = calls, Usage = assistant.Usage, ExtensionData = assistant.ExtensionData };
    }

    /// <summary>Streams a request and builds the message from the events, the way every consumer does.</summary>
    public static async Task<(ChatResponse Response, List<StreamEventKind> Kinds)> StreamAsync(AnthropicProvider provider, ChatRequest request)
    {
        var acc = new MessageAccumulator();
        var kinds = new List<StreamEventKind>();
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            acc.Add(e);
            kinds.Add(e.Kind);
        }

        return (new ChatResponse
        {
            Message = acc.ToMessage(),
            Id = acc.ResponseId,
            Model = acc.Model,
            Finish = acc.Finish,
            FinishReasonRaw = acc.FinishReasonRaw,
        }, kinds);
    }

    public static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;
}
