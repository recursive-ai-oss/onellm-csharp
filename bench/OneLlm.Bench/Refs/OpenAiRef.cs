using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace OneLlm.Bench.Refs;

/// <summary>The OpenAI .NET SDK, driven only through its documented client API, pointed at the mock server.</summary>
public sealed class OpenAiRef
{
    private readonly ChatClient _chat;
    private readonly ResponsesClient _responses;

    public OpenAiRef()
    {
        var client = new OpenAIClient(new ApiKeyCredential("bench-key"), new OpenAIClientOptions { Endpoint = new Uri(Mock.Url + "/v1") });
        _chat = client.GetChatClient("mock-model");
        _responses = client.GetResponsesClient();
    }

    /// <summary>The underlying chat client, for the Microsoft.Extensions.AI adapter.</summary>
    public ChatClient Chat => _chat;

    public async Task<string> ChatSend(List<Turn> history)
    {
        ChatCompletion c = await _chat.CompleteChatAsync(ToChat(history));
        return c.Content[0].Text;
    }

    public async Task<long> ChatStream(List<Turn> history)
    {
        long n = 0;
        await foreach (StreamingChatCompletionUpdate u in _chat.CompleteChatStreamingAsync(ToChat(history)))
        {
            foreach (ChatMessageContentPart p in u.ContentUpdate)
            {
                n += p.Text.Length;
            }
        }

        return n;
    }

    public async Task<long> ChatFirst(List<Turn> history)
    {
        await foreach (StreamingChatCompletionUpdate u in _chat.CompleteChatStreamingAsync(ToChat(history)))
        {
            if (u.ContentUpdate.Count > 0 && u.ContentUpdate[0].Text.Length > 0)
            {
                return u.ContentUpdate[0].Text.Length;
            }
        }

        return 0;
    }

    public async Task<string> ResponsesSend(List<Turn> history)
    {
        var r = await _responses.CreateResponseAsync(ToOptions(history));
        return r.Value.GetOutputText();
    }

    public async Task<long> ResponsesStream(List<Turn> history)
    {
        long n = 0;
        await foreach (StreamingResponseUpdate u in await _responses.CreateResponseStreamingAsync(ToOptions(history, stream: true)))
        {
            if (u is StreamingResponseOutputTextDeltaUpdate d)
            {
                n += d.Delta.Length;
            }
        }

        return n;
    }

    public async Task<long> ResponsesFirst(List<Turn> history)
    {
        await foreach (StreamingResponseUpdate u in await _responses.CreateResponseStreamingAsync(ToOptions(history, stream: true)))
        {
            if (u is StreamingResponseOutputTextDeltaUpdate d)
            {
                return d.Delta.Length;
            }
        }

        return 0;
    }

    private static List<ChatMessage> ToChat(List<Turn> history)
    {
        var list = new List<ChatMessage>(history.Count);
        foreach (Turn t in history)
        {
            list.Add(t.User ? new UserChatMessage(t.Text) : new AssistantChatMessage(t.Text));
        }

        return list;
    }

    private static CreateResponseOptions ToOptions(List<Turn> history, bool stream = false)
    {
        var options = new CreateResponseOptions { Model = "mock-model", StreamingEnabled = stream };
        foreach (Turn t in history)
        {
            options.InputItems.Add(t.User ? ResponseItem.CreateUserMessageItem(t.Text) : ResponseItem.CreateAssistantMessageItem(t.Text));
        }

        return options;
    }
}
