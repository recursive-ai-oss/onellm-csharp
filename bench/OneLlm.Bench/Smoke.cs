using System.Globalization;
using OneLlm.Bench.Refs;

namespace OneLlm.Bench;

/// <summary>
/// <c>smoke</c>: runs every client once against the running mock server and checks they all read the same streamed text, so a benchmark
/// that silently does less work than its neighbours is caught before any timing.
/// </summary>
public static class Smoke
{
    public static async Task<int> RunAsync()
    {
        Mock.Configure(events: 100, bytes: 8);
        var ours = new Ours();
        var openai = new OpenAiRef();
        var anthropic = new AnthropicRef();
        var gemini = new GeminiRef();
        var meai = new MeaiRef(openai.Chat);
        List<Turn> h = Mock.History(1_000);
        var req = Ours.Request(h);

        var results = new (string Name, long Chars)[]
        {
            ("ours anthropic", await Ours.Stream(ours.Anthropic, req)),
            ("ours responses", await Ours.Stream(ours.Responses, req)),
            ("ours chat", await Ours.Stream(ours.ChatCompletions, req)),
            ("ours gemini", await Ours.Stream(ours.Gemini, req)),
            ("ref anthropic", await anthropic.Stream(h)),
            ("ref openai responses", await openai.ResponsesStream(h)),
            ("ref openai chat", await openai.ChatStream(h)),
            ("ref meai", await meai.Stream(h)),
            ("ref gemini", await gemini.Stream(h)),
        };
        int bad = 0;
        foreach ((string name, long chars) in results)
        {
            Console.WriteLine($"stream {name,-22} {chars.ToString(CultureInfo.InvariantCulture)} chars");
            bad += chars == 800 ? 0 : 1;
        }

        Mock.Configure(events: 1, bytes: 16);
        foreach (var (name, task) in new (string, Task<object?>)[]
        {
            ("ours anthropic", Cast(ours.Anthropic.SendAsync(req))),
            ("ours responses", Cast(ours.Responses.SendAsync(req))),
            ("ours chat", Cast(ours.ChatCompletions.SendAsync(req))),
            ("ours gemini", Cast(ours.Gemini.SendAsync(req))),
            ("ref anthropic", Cast(anthropic.Send(h))),
            ("ref openai responses", Cast(openai.ResponsesSend(h))),
            ("ref openai chat", Cast(openai.ChatSend(h))),
            ("ref meai", Cast(meai.Send(h))),
            ("ref gemini", Cast(gemini.Send(h))),
        })
        {
            object? r = await task;
            Console.WriteLine($"send   {name,-22} ok ({(r is OneLlm.Providers.ChatResponse c ? c.Message.ToString() : r)})");
        }

        var agent = new AgentBench();
        agent.Setup();
        OneLlm.Agents.AgentResult ar = await agent.Ours_Agent();
        Console.WriteLine($"agent  ours: {ar.Turns} turns, stop {ar.StopReason}; reference: '{await agent.Ref_AgentFramework()}'");
        bad += ar.Turns == 11 ? 0 : 1;
        return bad == 0 ? 0 : 1;
    }

    private static async Task<object?> Cast<T>(Task<T> t) => await t;
}
