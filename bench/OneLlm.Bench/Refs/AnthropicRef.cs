using Anthropic;
using Anthropic.Models.Messages;

namespace OneLlm.Bench.Refs;

/// <summary>The Anthropic .NET SDK, driven only through its documented client API, pointed at the mock server.</summary>
public sealed class AnthropicRef
{
    private readonly AnthropicClient _client = new() { ApiKey = "bench-key", BaseUrl = Mock.Url };

    public async Task<string> Send(List<Turn> history)
    {
        Message m = await _client.Messages.Create(Build(history));
        return m.ID;
    }

    public async Task<long> Stream(List<Turn> history)
    {
        long n = 0;
        await foreach (RawMessageStreamEvent e in _client.Messages.CreateStreaming(Build(history)))
        {
            if (e.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? d) && d.Delta.TryPickText(out TextDelta? t))
            {
                n += t.Text.Length;
            }
        }

        return n;
    }

    public async Task<long> First(List<Turn> history)
    {
        await foreach (RawMessageStreamEvent e in _client.Messages.CreateStreaming(Build(history)))
        {
            if (e.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? d) && d.Delta.TryPickText(out TextDelta? t))
            {
                return t.Text.Length;
            }
        }

        return 0;
    }

    private static MessageCreateParams Build(List<Turn> history)
    {
        var msgs = new List<MessageParam>(history.Count);
        foreach (Turn t in history)
        {
            msgs.Add(t.User ? new UserMessageParam { Content = t.Text } : new AssistantMessageParam { Content = t.Text });
        }

        return new MessageCreateParams { Model = "mock-model", MaxTokens = 256, Messages = msgs };
    }
}
