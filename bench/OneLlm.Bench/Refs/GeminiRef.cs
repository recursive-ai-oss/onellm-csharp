using Google.GenAI;
using Google.GenAI.Types;

namespace OneLlm.Bench.Refs;

/// <summary>The Google GenAI .NET SDK, driven only through its documented client API, pointed at the mock server.</summary>
public sealed class GeminiRef
{
    private readonly Client _client = new(apiKey: "bench-key", httpOptions: new HttpOptions { BaseUrl = Mock.Url });

    public async Task<string?> Send(List<Turn> history)
    {
        GenerateContentResponse r = await _client.Models.GenerateContentAsync("mock-model", Build(history));
        return r.Text;
    }

    public async Task<long> Stream(List<Turn> history)
    {
        long n = 0;
        await foreach (GenerateContentResponse r in _client.Models.GenerateContentStreamAsync("mock-model", Build(history)))
        {
            n += r.Text?.Length ?? 0;
        }

        return n;
    }

    public async Task<long> First(List<Turn> history)
    {
        await foreach (GenerateContentResponse r in _client.Models.GenerateContentStreamAsync("mock-model", Build(history)))
        {
            return r.Text?.Length ?? 0;
        }

        return 0;
    }

    private static List<Content> Build(List<Turn> history)
    {
        var list = new List<Content>(history.Count);
        foreach (Turn t in history)
        {
            list.Add(new Content { Role = t.User ? "user" : "model", Parts = [new Part { Text = t.Text }] });
        }

        return list;
    }
}
