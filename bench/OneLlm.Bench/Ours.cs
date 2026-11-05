using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Bench;

/// <summary>OneLLM's provider clients pointed at the mock server, and the helpers the benchmarks share to drive them.</summary>
public sealed class Ours
{
    public AnthropicProvider Anthropic { get; } = AnthropicProvider.Create(new Secret("bench-key"), TimeProvider.System, new AnthropicOptions { BaseAddress = new Uri(Mock.Url + "/") });

    public OpenAIProvider Responses { get; } = new(new OpenAIOptions { ApiKey = new Secret("bench-key"), BaseUrl = new Uri(Mock.Url + "/v1/") }, TimeProvider.System, OpenAIApi.Responses);

    public OpenAIProvider ChatCompletions { get; } = new(new OpenAIOptions { ApiKey = new Secret("bench-key"), BaseUrl = new Uri(Mock.Url + "/v1/") }, TimeProvider.System, OpenAIApi.ChatCompletions);

    public GeminiProvider Gemini { get; } = new(new GeminiOptions { Time = TimeProvider.System, Auth = new GeminiApiKeyAuth(new Secret("bench-key")), Endpoint = new GeminiDirectEndpoint(new Uri(Mock.Url + "/")) });

    /// <summary>The request every provider sends: the history, a model name and a small output limit.</summary>
    public static ChatRequest Request(List<Turn> history)
    {
        var messages = new List<Message>(history.Count);
        foreach (Turn t in history)
        {
            messages.Add(t.User ? Message.FromUser(t.Text) : Message.FromAssistant(t.Text));
        }

        return new ChatRequest { Model = "mock-model", Messages = messages, MaxOutputTokens = 256 };
    }

    /// <summary>Reads a whole stream, counting the bytes of text deltas.</summary>
    public static async Task<long> Stream(IChatProvider provider, ChatRequest request)
    {
        long n = 0;
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            if (e.Kind == StreamEventKind.TextDelta)
            {
                n += e.Utf8.Length;
            }
        }

        return n;
    }

    /// <summary>Reads until the first text delta and stops, abandoning the rest of the stream.</summary>
    public static async Task<long> First(IChatProvider provider, ChatRequest request)
    {
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            if (e.Kind == StreamEventKind.TextDelta)
            {
                return e.Utf8.Length;
            }
        }

        return 0;
    }
}
