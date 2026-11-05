using System.Net;
using System.Net.Http;
using System.Text;
using OneLlm;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.AotSmoke;

/// <summary>
/// Runs the Gemini provider end to end against an in-memory service: a whole answer, a streamed answer with a function call and a signature,
/// token counting and the shipped catalog. It needs no network and no key, and it must publish with NativeAOT without warnings.
/// </summary>
internal static class GeminiSmoke
{
    public static async Task RunAsync()
    {
        var catalog = ModelCatalog.Create([GeminiProvider.GetCatalogLayer()]);
        ResolvedModel resolved = catalog.Resolve("gemini:gemini-2.5-flash");
        Check(resolved.Model?.Pricing?.AsOf is not null, "the shipped catalog resolves a model with dated prices");

        using var http = new HttpClient(new CannedService());
        var provider = new GeminiProvider(new GeminiOptions
        {
            Time = TimeProvider.System,
            Auth = new GeminiApiKeyAuth(new Secret("smoke-key-0000")),
            Http = http,
            Catalog = catalog,
        });

        ChatRequest request = new() { Model = "gemini-2.5-flash", Messages = [Message.FromUser("Say hello.")] };
        ChatResponse whole = await provider.SendAsync(request);
        Check(whole.Message.GetAssistantText() == "Hello!" && whole.Usage?.InputTokens == 7, "a whole answer parses");

        var accumulator = new MessageAccumulator();
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            accumulator.Add(e);
        }

        Message streamed = accumulator.ToMessage();
        Check(streamed.GetAssistantText() == "Hello!" && streamed.ToolCalls.Count == 1 && streamed.ToolCalls[0].Tool == "get_weather", "a streamed answer builds the message");

        TokenCount count = await provider.CountTokensAsync(request);
        Check(count.InputTokens == 5 && count.Tier == TokenCountTier.Provider, "token counting reports its tier");
        System.Console.WriteLine("gemini smoke ok");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException("gemini smoke failed: " + what);
        }
    }

    private sealed class CannedService : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
            {
                string sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hel\"}]},\"index\":0}],\"responseId\":\"r1\"}\r\n\r\n"
                    + "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"lo!\",\"thoughtSignature\":\"c2ln\"},{\"functionCall\":{\"id\":\"c1\",\"name\":\"get_weather\",\"args\":{\"city\":\"Oslo\"}}}]},\"finishReason\":\"STOP\",\"index\":0}],\"usageMetadata\":{\"promptTokenCount\":7,\"candidatesTokenCount\":3,\"totalTokenCount\":10},\"responseId\":\"r1\"}\r\n\r\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
            }

            string body = path.EndsWith(":countTokens", StringComparison.Ordinal)
                ? "{\"totalTokens\":5}"
                : "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hello!\"}]},\"finishReason\":\"STOP\",\"index\":0}],\"usageMetadata\":{\"promptTokenCount\":7,\"candidatesTokenCount\":2,\"totalTokenCount\":9},\"responseId\":\"r0\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
