using System.Net;
using System.Text;
using OneLlm;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.AotSmoke;

/// <summary>
/// Drives the OpenAI provider end to end against canned HTTP answers, so a NativeAOT build proves the request writers, the decoders, the
/// server-sent events reader and the shipped catalog files work without reflection. Call <see cref="RunAsync"/> from the program.
/// </summary>
internal static class OpenAiExercise
{
    private const string Answer = """
        {"id":"resp_1","object":"response","status":"completed","model":"gpt-6.1-terra","output":[
        {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Thinking."}],"encrypted_content":"abc"},
        {"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello!","annotations":[]}]}],
        "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":4,"cache_write_tokens":0},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":2},"total_tokens":15}}
        """;

    private const string Stream =
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_2\",\"model\":\"gpt-6.1-terra\"}}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hel\"}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"lo\\n\"}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_2\",\"status\":\"completed\",\"model\":\"gpt-6.1-terra\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}\n\n";

    private sealed class Canned : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/embeddings", StringComparison.Ordinal))
            {
                return Json("""{"data":[{"index":0,"embedding":[0.5,0.25]}],"model":"text-embedding-3-small","usage":{"prompt_tokens":2}}""");
            }

            if (path.EndsWith("/input_tokens", StringComparison.Ordinal))
            {
                return Json("""{"object":"response.input_tokens","input_tokens":12}""");
            }

            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                return Json("""{"id":"c1","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""");
            }

            bool wantsStream = request.Headers.Accept.ToString().Contains("event-stream", StringComparison.Ordinal);
            return wantsStream
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") })
                : Json(Answer);
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    public static async Task RunAsync()
    {
        ModelCatalog catalog = ModelCatalog.Create([OpenAICatalog.Shipped()]);
        using var http = new HttpClient(new Canned());
        var options = new OpenAIOptions { ApiKey = new Secret("not-a-real-key"), HttpClient = http, Catalog = catalog };
        var provider = new OpenAIProvider(options, TimeProvider.System);
        var request = new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Say hello.")], Reasoning = new ReasoningOptions { Effort = "low" } };

        ChatResponse whole = await provider.SendAsync(request);
        Check(whole.Message.GetAssistantText() == "Hello!" && whole.Usage!.InputTokens == 6, "responses call");

        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            acc.Add(e);
        }

        Check(acc.ToMessage().GetAssistantText() == "Hello\n", "responses stream");

        // The reasoning item comes back out unchanged on the next turn.
        await provider.SendAsync(request with { Messages = [Message.FromUser("Say hello."), whole.Message, Message.FromUser("Again.")] });

        TokenCount count = await provider.CountTokensAsync(request);
        Check(count.InputTokens == 12, "token count");

        EmbeddingResponse embeddings = await provider.EmbedAsync(new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["a"] });
        Check(embeddings.Embeddings[0].Length == 2, "embeddings");

        var compatible = OpenAICompatibleProvider.FromPreset(catalog, "groq", TimeProvider.System, new Secret("not-a-real-key"), http);
        ChatResponse chat = await compatible.SendAsync(new ChatRequest { Model = "m", Messages = [Message.FromUser("Hi")] });
        Check(chat.Message.GetAssistantText() == "Hi", "compatible chat");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException("OpenAI exercise failed: " + what);
        }
    }
}
