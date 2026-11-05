using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.Tests.Gemini;

internal static partial class GeminiCases
{
    private static IEnumerable<Case> Streaming()
    {
        const string stream = "https://ai.google.dev/api/generate-content#method:-models.streamgeneratecontent";
        yield return new Case("stream", "text", stream, async p =>
        {
            var acc = new MessageAccumulator();
            List<StreamEvent> events = await Collect(p, new ChatRequest { Model = Flash, Messages = [User("Greet the world.")] }, acc);
            Assert.Equal(StreamEventKind.MessageStart, events[0].Kind);
            Assert.Equal(StreamEventKind.Done, events[^1].Kind);
            Assert.Equal(3, events.Count(e => e.Kind == StreamEventKind.TextDelta));
            Message m = acc.ToMessage();
            Assert.Equal("Hello, world!", m.GetAssistantText());
            Assert.Equal(FinishReason.Stop, acc.Finish);
            Assert.Equal(9, m.Usage!.InputTokens);
            Assert.Equal(5, m.Usage.OutputTokens);
        }, new Reply(
            200,
            Sse:
            [
                Chunk("""{"text":"Hel"}"""),
                Chunk("""{"text":"lo, wor"}"""),
                Chunk("""{"text":"ld!"}""", finish: "STOP", usage: """{"promptTokenCount":9,"candidatesTokenCount":5,"totalTokenCount":14}"""),
            ]));

        yield return new Case("stream", "escaped-text", stream, async p =>
        {
            var acc = new MessageAccumulator();
            await Collect(p, new ChatRequest { Model = Flash, Messages = [User("Say something with accents and quotes.")] }, acc);
            Assert.Equal("café \"ok\" \n日本", acc.ToMessage().GetAssistantText());
        }, new Reply(
            200,
            Sse:
            [
                Chunk("""{"text":"caf\u00e9 \"ok\" "}"""),
                Chunk("""{"text":"\n\u65e5\u672c"}""", finish: "STOP"),
            ]));

        yield return new Case("stream", "thinking-summary-and-signature", ThinkRef, async p =>
        {
            var acc = new MessageAccumulator();
            await Collect(p, new ChatRequest { Model = "gemini-3.8-flash", Messages = [User("Greet me.")], Reasoning = new ReasoningOptions { Effort = "low", Summary = "auto" } }, acc);
            Message m = acc.ToMessage();
            var thought = Assert.IsType<ReasoningPart>(m.Assistant[0]);
            Assert.Equal("Considering the greeting. Keep it short.", thought.Display);
            Assert.Equal("thought", thought.Data!["on"]!.GetValue<string>());
            var sig = Assert.IsType<ReasoningPart>(m.Assistant[1]);
            Assert.Equal("c2lnLXN0cmVhbQ==", sig.Data!["signature"]!.GetValue<string>());
            Assert.Equal("text", sig.Data["on"]!.GetValue<string>());
            Assert.Equal("Hi there.", Assert.IsType<TextPart>(m.Assistant[2]).Text);
            Assert.Equal(7, m.Usage!.OutputTokens);
            Assert.Equal(3, m.Usage.Counters!["reasoningTokens"]);
        }, new Reply(
            200,
            Sse:
            [
                Chunk("""{"text":"Considering the greeting.","thought":true}""", model: "gemini-3.8-flash"),
                Chunk("""{"text":" Keep it short.","thought":true}""", model: "gemini-3.8-flash"),
                Chunk("""{"text":"Hi","thoughtSignature":"c2lnLXN0cmVhbQ=="}""", model: "gemini-3.8-flash"),
                Chunk("""{"text":" there."}""", finish: "STOP", usage: """{"promptTokenCount":6,"candidatesTokenCount":4,"thoughtsTokenCount":3,"totalTokenCount":13}""", model: "gemini-3.8-flash"),
            ]));

        yield return new Case("stream", "function-call", FnRef, async p =>
        {
            var acc = new MessageAccumulator();
            await Collect(p, new ChatRequest { Model = Flash, Messages = [User("Weather in Paris?")], Tools = [Weather()] }, acc);
            Message m = acc.ToMessage();
            ToolCall call = Assert.Single(m.ToolCalls);
            Assert.Equal("call-abc", call.Id);
            Assert.Equal("get_weather", call.Tool);
            Assert.Equal("Paris", call.Inputs!["city"]!.GetValue<string>());
            Assert.Equal(FinishReason.ToolCalls, acc.Finish);
        }, new Reply(
            200,
            Sse:
            [
                Chunk("""{"functionCall":{"id":"call-abc","name":"get_weather","args":{"city":"Paris"}}}""", finish: "STOP", usage: """{"promptTokenCount":20,"candidatesTokenCount":6,"totalTokenCount":26}"""),
            ]));

        yield return new Case("stream", "error-event-midway", stream, async p =>
        {
            var seen = new List<string>();
            var e = await Assert.ThrowsAsync<OverloadedException>(async () =>
            {
                await foreach (StreamEvent ev in p.StreamAsync(new ChatRequest { Model = Flash, Messages = [User("Tell a long story.")] }))
                {
                    if (ev.Kind == StreamEventKind.TextDelta)
                    {
                        seen.Add(ev.GetText());
                    }
                }
            });
            Assert.Equal(["Once upon"], seen);
            Assert.Equal("UNAVAILABLE", e.Code);
            Assert.Equal(503, e.StatusCode);
        }, new Reply(
            200,
            Sse:
            [
                Chunk("""{"text":"Once upon"}"""),
                """data: {"error":{"code":503,"message":"The model is overloaded. Please try again later.","status":"UNAVAILABLE"}}""" + "\n\n",
            ]));

        yield return new Case("stream", "prompt-blocked", stream, async p =>
        {
            var e = await Assert.ThrowsAsync<ContentFilterException>(async () =>
            {
                await foreach (StreamEvent ev in p.StreamAsync(new ChatRequest { Model = Flash, Messages = [User("A request the filter refuses.")] }))
                {
                    _ = ev;
                }
            });
            Assert.Equal("PROHIBITED_CONTENT", e.Code);
        }, new Reply(200, Sse: ["""data: {"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},"usageMetadata":{"promptTokenCount":7,"totalTokenCount":7}}""" + "\n\n"]));
    }

    private static IEnumerable<Case> Functions()
    {
        yield return new Case("functions", "call-and-result", FnRef, async p =>
        {
            var request = new ChatRequest { Model = "gemini-3.8-flash", Messages = [User("Weather in Paris?")], Tools = [Weather()], ToolChoice = ToolChoice.Auto };
            ChatResponse first = await p.SendAsync(request);
            Assert.Equal(FinishReason.ToolCalls, first.Finish);
            // The signature comes back as its own reasoning part just before the call it belongs to.
            var sig = Assert.IsType<ReasoningPart>(Assert.Single(first.Message.Assistant));
            Assert.Equal("functionCall", sig.Data!["on"]!.GetValue<string>());
            ToolCall call = Assert.Single(first.Message.ToolCalls);
            Assert.Equal(1, call.After);
            Assert.Equal("call-abc", call.Id);

            Message turn = AssistantWithResults(first.Message, ("call-abc", [new JsonPart(JsonNode.Parse("""{"temp_c":18}""")!)]));
            ChatResponse second = await p.SendAsync(new ChatRequest { Model = "gemini-3.8-flash", Messages = [User("Weather in Paris?"), turn], Tools = [Weather()] });
            Assert.Equal("It is 18 degrees in Paris.", second.Message.GetAssistantText());
            Assert.Equal(FinishReason.Stop, second.Finish);
        },
        new Reply(200, Answer("""{"functionCall":{"id":"call-abc","name":"get_weather","args":{"city":"Paris"}},"thoughtSignature":"c2lnLWZj"}""", model: "gemini-3.8-flash")),
        new Reply(200, Answer("""{"text":"It is 18 degrees in Paris."}""", model: "gemini-3.8-flash", id: "resp-0002")));

        yield return new Case("functions", "parallel-calls-without-ids", FnRef, async p =>
        {
            var request = new ChatRequest { Model = Flash, Messages = [User("Weather in Paris and Rome?")], Tools = [Weather()] };
            ChatResponse first = await p.SendAsync(request);
            Assert.Equal(2, first.Message.ToolCalls.Count);
            Assert.StartsWith(GeminiRequestWriterConstants.SyntheticPrefix, first.Message.ToolCalls[0].Id, StringComparison.Ordinal);
            Assert.NotEqual(first.Message.ToolCalls[0].Id, first.Message.ToolCalls[1].Id);
            Assert.Equal("Rome", first.Message.ToolCalls[1].Inputs!["city"]!.GetValue<string>());

            // Results go back in call order; the made-up ids are not sent, so the API pairs them by name and position.
            Message turn = AssistantWithResults(
                first.Message,
                (first.Message.ToolCalls[0].Id, [new TextPart("Sunny, 18 C")]),
                (first.Message.ToolCalls[1].Id, [new TextPart("no data for Rome")]));
            turn = new Message { Assistant = turn.Assistant, ToolCalls = [turn.ToolCalls[0], new ToolCall(turn.ToolCalls[1].Id, turn.ToolCalls[1].Tool) { Inputs = turn.ToolCalls[1].Inputs, After = turn.ToolCalls[1].After, Result = turn.ToolCalls[1].Result, IsError = true }] };
            ChatResponse second = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("Weather in Paris and Rome?"), turn], Tools = [Weather()] });
            Assert.Equal("Paris is sunny; Rome failed.", second.Message.GetAssistantText());
        },
        new Reply(200, Answer("""{"functionCall":{"name":"get_weather","args":{"city":"Paris"}}},{"functionCall":{"name":"get_weather","args":{"city":"Rome"}}}""", id: "resp-0003")),
        new Reply(200, Answer("""{"text":"Paris is sunny; Rome failed."}""", id: "resp-0004")));

        yield return new Case("functions", "forced-choice", FnRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("Anything.")],
                Tools = [Weather()],
                ToolChoice = ToolChoice.Tool("get_weather"),
                ParallelToolCalls = false,
            });
            Assert.Equal("get_weather", Assert.Single(r.Message.ToolCalls).Tool);
        }, new Reply(200, Answer("""{"functionCall":{"id":"call-1","name":"get_weather","args":{"city":"Oslo"}}}""")));
    }

    private static IEnumerable<Case> Hosted()
    {
        yield return new Case("hosted", "google-search", "https://ai.google.dev/gemini-api/docs/google-search", async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-3.8-flash",
                Messages = [User("Pourquoi le ciel est-il bleu ?")],
                HostedTools = [new HostedToolDefinition("google_search")],
            });
            var text = Assert.IsType<TextPart>(r.Message.Assistant[0]);
            Assert.Equal("Le ciel est bleu \u00e0 cause de Rayleigh. Fin.", text.Text);
            // Byte offsets in the answer (UTF-8) become character positions in the text.
            Assert.Equal(3, text.Annotations!.Count);
            Assert.Equal(0, text.Annotations[0].Start);
            Assert.Equal(37, text.Annotations[0].End);
            Assert.Equal("https://example.com/sky", text.Annotations[0].Url);
            Assert.Equal("url_citation", text.Annotations[0].Type);
            Assert.Equal("https://example.org/optics", text.Annotations[1].Url);
            Assert.Equal(38, text.Annotations[2].Start);
            Assert.Equal(42, text.Annotations[2].End);
            var call = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[^1]);
            Assert.Equal("google_search", call.Tool);
            Assert.Equal("pourquoi ciel bleu", call.Inputs!["queries"]![0]!.GetValue<string>());
            Assert.Null(call.Data);
            Assert.Equal(1, r.Usage!.Counters!["toolCalls.google_search"]);
            Assert.Equal(1, r.Usage.Counters["groundedPrompts"]);
        }, new Reply(200, Answer(
            """{"text":"Le ciel est bleu \u00e0 cause de Rayleigh. Fin."}""",
            model: "gemini-3.8-flash",
            extraCandidate: """ "groundingMetadata":{"webSearchQueries":["pourquoi ciel bleu"],"searchEntryPoint":{"renderedContent":"<div class=\"chip\">search</div>"},"groundingChunks":[{"web":{"uri":"https://example.com/sky","title":"Sky colour"}},{"web":{"uri":"https://example.org/optics","title":"Optics notes"}}],"groundingSupports":[{"segment":{"startIndex":0,"endIndex":38,"text":"Le ciel est bleu \u00e0 cause de Rayleigh."},"groundingChunkIndices":[0,1],"confidenceScores":[0.97,0.88]},{"segment":{"startIndex":39,"endIndex":43,"text":"Fin."},"groundingChunkIndices":[1]}]}"""))); 

        yield return new Case("hosted", "code-execution", "https://ai.google.dev/gemini-api/docs/code-execution", async p =>
        {
            var request = new ChatRequest { Model = Flash, Messages = [User("What is 2 + 2? Use code.")], HostedTools = [new HostedToolDefinition("code_execution")] };
            ChatResponse r = await p.SendAsync(request);
            var call = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
            Assert.Equal("code_execution", call.Tool);
            Assert.Equal("completed", call.Status);
            Assert.Equal("print(2 + 2)", call.Inputs!["code"]!.GetValue<string>());
            Assert.Equal("4\n", call.Result!["output"]!.GetValue<string>());
            Assert.Equal(2, call.Data!["replay"]!.AsArray().Count);
            Assert.Equal("The answer is 4.", r.Message.GetAssistantText());

            // Next turn: the code and its result are sent back as the two parts they came as.
            ChatResponse second = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("What is 2 + 2? Use code."), r.Message, User("And doubled?")],
                HostedTools = [new HostedToolDefinition("code_execution")],
            });
            Assert.Equal("Doubled it is 8.", second.Message.GetAssistantText());
        },
        new Reply(200, Answer("""{"executableCode":{"language":"PYTHON","code":"print(2 + 2)"}},{"codeExecutionResult":{"outcome":"OUTCOME_OK","output":"4\n"}},{"text":"The answer is 4."}""")),
        new Reply(200, Answer("""{"text":"Doubled it is 8."}""", id: "resp-0002")));

        yield return new Case("hosted", "url-context", "https://ai.google.dev/gemini-api/docs/url-context", async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("Summarize https://example.com/page")],
                HostedTools = [new HostedToolDefinition("url_context")],
            });
            var call = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[^1]);
            Assert.Equal("url_context", call.Tool);
            Assert.Equal("completed", call.Status);
            Assert.Equal("https://example.com/page", call.Inputs!["urls"]![0]!.GetValue<string>());
            // Pages fetched by the tool are billed as input.
            Assert.Equal(15 + 500, r.Usage!.InputTokens);
            Assert.Equal(500, r.Usage.Counters!["toolUsePromptTokens"]);
        }, new Reply(200, Answer(
            """{"text":"The page describes an example."}""",
            usage: """{"promptTokenCount":15,"toolUsePromptTokenCount":500,"candidatesTokenCount":6,"totalTokenCount":521}""",
            extraCandidate: """ "urlContextMetadata":{"urlMetadata":[{"retrievedUrl":"https://example.com/page","urlRetrievalStatus":"URL_RETRIEVAL_STATUS_SUCCESS"}]}""")));

        yield return new Case("hosted", "server-side-tool-parts", GenRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-3.8-flash",
                Messages = [User("Find the capital of Norway.")],
                HostedTools = [new HostedToolDefinition("google_search")],
            });
            var call = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
            Assert.Equal("google_search", call.Tool);
            Assert.Equal("search-1", call.Id);
            Assert.Equal("completed", call.Status);
            Assert.Equal("Oslo is the capital.", r.Message.GetAssistantText());
        }, new Reply(200, Answer(
            """{"toolCall":{"toolType":"GOOGLE_SEARCH_WEB","id":"search-1","args":{"queries":["capital of Norway"]}}},{"toolResponse":{"toolType":"GOOGLE_SEARCH_WEB","id":"search-1","response":{"results":[{"title":"Norway","url":"https://example.com/no"}]}}},{"text":"Oslo is the capital."}""",
            model: "gemini-3.8-flash")));
    }
}

/// <summary>The prefix of made-up call ids, as the tests need to name it.</summary>
internal static class GeminiRequestWriterConstants
{
    public const string SyntheticPrefix = "gemini-call-";
}
