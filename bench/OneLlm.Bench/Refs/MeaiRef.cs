using Microsoft.Extensions.AI;


namespace OneLlm.Bench.Refs;

/// <summary>Microsoft.Extensions.AI's <c>IChatClient</c>, over its OpenAI adapter, pointed at the mock server.</summary>
public sealed class MeaiRef
{
    private readonly IChatClient _client;

    public MeaiRef(OpenAI.Chat.ChatClient chat) => _client = chat.AsIChatClient();

    public async Task<string> Send(List<Turn> history)
    {
        ChatResponse r = await _client.GetResponseAsync(Build(history));
        return r.Text;
    }

    public async Task<long> Stream(List<Turn> history)
    {
        long n = 0;
        await foreach (ChatResponseUpdate u in _client.GetStreamingResponseAsync(Build(history)))
        {
            n += u.Text.Length;
        }

        return n;
    }

    public async Task<long> First(List<Turn> history)
    {
        await foreach (ChatResponseUpdate u in _client.GetStreamingResponseAsync(Build(history)))
        {
            if (u.Text.Length > 0)
            {
                return u.Text.Length;
            }
        }

        return 0;
    }

    private static List<ChatMessage> Build(List<Turn> history)
    {
        var list = new List<ChatMessage>(history.Count);
        foreach (Turn t in history)
        {
            list.Add(new ChatMessage(t.User ? ChatRole.User : ChatRole.Assistant, t.Text));
        }

        return list;
    }
}
