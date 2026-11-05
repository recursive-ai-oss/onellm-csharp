using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.Tests.Gemini;

/// <summary>Conformance cases for chat, streaming, function calling, hosted tools, structured output, thinking, media and safety.</summary>
internal static partial class GeminiCases
{
    private const string GenRef = "https://ai.google.dev/api/generate-content";
    private const string FnRef = "https://ai.google.dev/gemini-api/docs/function-calling";
    private const string ThinkRef = "https://ai.google.dev/gemini-api/docs/thinking";
    private const string Flash = "gemini-3.5-flash-lite";

    // One candidate answer in the shape of GenerateContentResponse.
    internal static string Answer(string parts, string finish = "STOP", string? extraCandidate = null, string usage = """{"promptTokenCount":10,"candidatesTokenCount":4,"totalTokenCount":14}""", string model = Flash, string id = "resp-0001") =>
        $$"""{"candidates":[{"content":{"role":"model","parts":[{{parts}}]},"finishReason":"{{finish}}","index":0{{(extraCandidate is null ? "" : "," + extraCandidate)}}}],"usageMetadata":{{usage}},"modelVersion":"{{model}}","responseId":"{{id}}"}""";

    // A streamed chunk: the same object, written as one server-sent event.
    internal static string Chunk(string parts, string? finish = null, string? usage = null, string? extraCandidate = null, string model = Flash, string id = "resp-0002")
    {
        string fin = finish is null ? "" : $$""","finishReason":"{{finish}}"{{(extraCandidate is null ? "" : "," + extraCandidate)}}""";
        string use = usage is null ? "" : $$""","usageMetadata":{{usage}}""";
        return $$"""data: {"candidates":[{"content":{"role":"model","parts":[{{parts}}]}{{fin}},"index":0}]{{use}},"modelVersion":"{{model}}","responseId":"{{id}}"}""" + "\n\n";
    }

    internal static Message User(string text) => Message.FromUser(text);

    internal static Message AssistantWithResults(Message m, params (string Id, Part[] Result)[] results) => new()
    {
        Assistant = m.Assistant,
        ToolCalls = [.. m.ToolCalls.Select(c => new ToolCall(c.Id, c.Tool) { Inputs = c.Inputs, After = c.After, Result = results.First(r => r.Id == c.Id).Result })],
    };

    internal static ToolDefinition Weather() => new("get_weather", "Gets the weather for a city.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")!);

    internal static async Task<List<StreamEvent>> Collect(GeminiProvider p, ChatRequest r, MessageAccumulator acc)
    {
        var all = new List<StreamEvent>();
        await foreach (StreamEvent e in p.StreamAsync(r))
        {
            acc.Add(e);
            all.Add(e);
        }

        return all;
    }

    internal static string Describe(Message m)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Part p in m.Assistant)
        {
            switch (p)
            {
                case TextPart t:
                    sb.Append($"text:{t.Text}|{t.Annotations?.Count ?? 0};");
                    break;
                case ReasoningPart r:
                    sb.Append($"reasoning:{r.Display}|{r.Data?.ToJsonString()};");
                    break;
                case HostedToolCallPart h:
                    sb.Append($"hosted:{h.Tool}|{h.Status}|{h.Inputs?.ToJsonString()}|{h.Result?.ToJsonString()};");
                    break;
                default:
                    sb.Append(p.Kind).Append(';');
                    break;
            }
        }

        foreach (ToolCall c in m.ToolCalls)
        {
            sb.Append($"call:{c.Id}|{c.Tool}|{c.Inputs?.ToJsonString()}|{c.After};");
        }

        Usage? u = m.Usage;
        sb.Append($"usage:{u?.InputTokens}/{u?.OutputTokens}/{u?.CacheReadInputTokens}");
        return sb.ToString();
    }

    public static IEnumerable<Case> All() =>
    [
        .. Chat(),
        .. Streaming(),
        .. Functions(),
        .. Hosted(),
        .. Structured(),
        .. Thinking(),
        .. Media(),
        .. Safety(),
        .. Resources(),
        .. Errors(),
    ];

    private static IEnumerable<Case> Chat()
    {
        yield return new Case("chat", "basic", GenRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                System = "Be brief.",
                Messages = [User("Say hello in one word.")],
                MaxOutputTokens = 64,
                Temperature = 0.2,
                TopP = 0.9,
                StopSequences = ["END"],
            });
            Assert.Equal("Hello!", r.Message.GetAssistantText());
            Assert.Equal(FinishReason.Stop, r.Finish);
            Assert.Equal("STOP", r.FinishReasonRaw);
            Assert.Equal("resp-0001", r.Id);
            Assert.Equal(Flash, r.Model);
            Assert.Equal(12, r.Usage!.InputTokens);
            Assert.Equal(2, r.Usage.OutputTokens);
            Assert.Equal(Flash, r.Usage.Model);
            // Fields without a neutral place are kept on the message, under the provider's name.
            var gemini = (Dictionary<string, object?>)r.Message.ExtensionData!["gemini"]!;
            Assert.True(gemini.ContainsKey("safetyRatings"));
            Assert.Equal(1L, Convert.ToInt64(((Dictionary<string, object?>)((Dictionary<string, object?>)gemini["unknown"]!)["futureField"]!)["x"]));
        }, new Reply(200, """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello!"}]},"finishReason":"STOP","index":0,"safetyRatings":[{"category":"HARM_CATEGORY_HARASSMENT","probability":"NEGLIGIBLE"}]}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":2,"totalTokenCount":14,"promptTokensDetails":[{"modality":"TEXT","tokenCount":12}]},"modelVersion":"gemini-3.5-flash-lite","futureField":{"x":1},"responseId":"resp-0001"}"""));

        yield return new Case("chat", "implicit-cache-usage", "https://ai.google.dev/gemini-api/docs/caching", async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = "gemini-3.8-flash", Messages = [User("Summarize the document again.")] });
            // The service counts cached tokens inside promptTokenCount; the library reports them apart.
            Assert.Equal(1024, r.Usage!.InputTokens);
            Assert.Equal(3072, r.Usage.CacheReadInputTokens);
            Assert.Equal(30, r.Usage.OutputTokens);
            Assert.Equal(20, r.Usage.Counters!["reasoningTokens"]);
            Assert.Equal(4096, r.Usage.TotalInputTokens);
        }, new Reply(200, Answer("""{"text":"Here is the summary."}""", model: "gemini-3.8-flash", usage: """{"promptTokenCount":4096,"cachedContentTokenCount":3072,"candidatesTokenCount":10,"thoughtsTokenCount":20,"totalTokenCount":4126,"cacheTokensDetails":[{"modality":"TEXT","tokenCount":3072}]}""")));

        yield return new Case("chat", "multi-turn", GenRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("My name is Ada."), Message.FromAssistant("Nice to meet you, Ada."), User("What is my name?")],
            });
            Assert.Equal("Your name is Ada.", r.Message.GetAssistantText());
        }, new Reply(200, Answer("""{"text":"Your name is Ada."}""")));
    }
}
