using System.Net;
using System.Net.Http;
using System.Text;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;

namespace OneLlm.AotSmoke;

/// <summary>
/// Exercises the Anthropic provider under NativeAOT against a canned HTTP handler: request body writing (tools, thinking, caching), whole and
/// streamed response decoding, thinking replay, and the shipped catalog.
/// </summary>
internal static class AnthropicSmoke
{
    private const string Whole =
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"thinking","thinking":"hm","signature":"sig"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"calc","input":{"x":1}}],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":4,"cache_creation":{"ephemeral_5m_input_tokens":1,"ephemeral_1h_input_tokens":3}}}""";

    private const string Stream =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"calc\",\"input\":{},\"toolset_name\":\"t\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"x\\\":1}\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private sealed class Canned : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Bodies.Add(body);
            bool stream = body.Contains("\"stream\":true", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? Stream : Whole, Encoding.UTF8, stream ? "text/event-stream" : "application/json"),
            };
        }
    }

    /// <summary>Runs the exercise.</summary>
    /// <returns>0 when every step gave the expected result.</returns>
    public static int Run() => RunAsync().GetAwaiter().GetResult() ? 0 : 1;

    private static async Task<bool> RunAsync()
    {
        var handler = new Canned();
        var provider = AnthropicProvider.Create(new Secret("not-a-key"), TimeProvider.System, new AnthropicOptions
        {
            Http = new HttpClient(handler),
            BaseAddress = new Uri("http://localhost/"),
            Retry = RetryPolicy.None,
        });
        var request = new ChatRequest
        {
            Model = "claude-sonnet",
            Messages = [Message.FromUser("hello")],
            System = "terse",
            MaxOutputTokens = 256,
            Cache = new CachePolicy(),
            Reasoning = new ReasoningOptions { Effort = "high" },
            Tools = [new ToolDefinition("calc", "adds", new System.Text.Json.Nodes.JsonObject { ["type"] = "object" })],
        };

        ChatResponse whole = await provider.SendAsync(request).ConfigureAwait(false);
        bool ok = whole.Finish == FinishReason.ToolCalls
            && whole.Message.ToolCalls.Count == 1
            && whole.Usage!.GetCounter("cacheCreation1hInputTokens") == 3
            && whole.Message.Assistant[0] is ReasoningPart;

        // The thinking block and the call go back with a result on the next turn.
        var withResult = new Message
        {
            Assistant = whole.Message.Assistant,
            ToolCalls = [new ToolCall("toolu_1", "calc") { Inputs = whole.Message.ToolCalls[0].Inputs, After = 2, Result = [new TextPart("2")] }],
        };
        await provider.SendAsync(request with { Messages = [Message.FromUser("hello"), withResult] }).ConfigureAwait(false);

        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in provider.StreamAsync(request).ConfigureAwait(false))
        {
            acc.Add(e);
        }

        Message streamed = acc.ToMessage();
        ok &= streamed.GetAssistantText() == "Hi"
            && streamed.ToolCalls[0].Inputs!["x"]!.GetValue<int>() == 1
            && streamed.ToolCalls[0].ExtensionData!["toolset_name"] as string == "t"
            && handler.Bodies[1].Contains("\"signature\":\"sig\"", StringComparison.Ordinal)
            && handler.Bodies[0].Contains("\"model\":\"claude-sonnet-5-5\"", StringComparison.Ordinal);

        ok &= AnthropicCatalog.Default.Resolve("claude-haiku").Model!.Pricing!.AsOf is not null;
        return ok;
    }
}
