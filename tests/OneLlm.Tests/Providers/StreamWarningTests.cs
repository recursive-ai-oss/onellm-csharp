using System.Net;
using System.Text;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;
using OneLlm.Tests.Gemini;

namespace OneLlm.Tests.Providers;

/// <summary>A stream has no response object to carry notices about the request, so providers send them first, as events.</summary>
public class StreamWarningTests
{
    private const string AnthropicSse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"usage":{"input_tokens":3,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private const string ChatCompletionsSse = """
        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-test","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}

        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]


        """;

    private const string ResponsesSse = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","model":"gpt-test","status":"in_progress","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","status":"in_progress","content":[]}}

        event: response.content_part.added
        data: {"type":"response.content_part.added","item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"Hi"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hi"}]}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","model":"gpt-test","status":"completed","output":[{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hi"}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}


        """;

    private const string GeminiSse = """
        data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hi"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5},"modelVersion":"gemini-test"}


        """;

    private static ModelCatalog Catalog(string provider, string model) => ModelCatalog.Create([CatalogLayer.FromYaml(
        CatalogLayer.Shipped,
        $"name: {provider}\nmodels:\n  - name: {model}\n    maxOutputTokens: 1000\n    supportsTemperature: false\n")]);

    private static HttpClient Canned(string sse) => new(new CannedHandler(sse));

    private static ChatRequest Request(string model) => new()
    {
        Model = model,
        Messages = [Message.FromUser("hi")],
        Temperature = 0.3,
        MaxOutputTokens = 100,
    };

    private static async Task<(List<StreamEventKind> Kinds, MessageAccumulator Accumulator)> Collect(IChatProvider provider, ChatRequest request)
    {
        var kinds = new List<StreamEventKind>();
        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            kinds.Add(e.Kind);
            acc.Add(e);
        }

        return (kinds, acc);
    }

    private static void AssertWarned(List<StreamEventKind> kinds, MessageAccumulator acc)
    {
        Assert.Equal(StreamEventKind.Warning, kinds[0]);
        Assert.True(acc.IsDone);
        Assert.Equal("Hi", acc.ToMessage().GetAssistantText());
        ResponseWarning w = Assert.Single(acc.Warnings);
        Assert.Equal("parameter_stripped", w.Code);
        Assert.Contains("temperature", w.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_streams_a_warning_event_for_a_stripped_parameter()
    {
        var provider = new AnthropicProvider(
            new AnthropicDirectDeployment(new Secret("test-key"), new AnthropicOptions { Http = Canned(AnthropicSse) }),
            TimeProvider.System,
            new AnthropicOptions { Http = Canned(AnthropicSse), Catalog = Catalog("anthropic", "claude-test"), Retry = RetryPolicy.None });

        (List<StreamEventKind> kinds, MessageAccumulator acc) = await Collect(provider, Request("claude-test"));

        AssertWarned(kinds, acc);
    }

    [Theory]
    [InlineData(OpenAIApi.Responses)]
    [InlineData(OpenAIApi.ChatCompletions)]
    public async Task OpenAI_streams_a_warning_event_for_a_stripped_parameter(OpenAIApi api)
    {
        string sse = api == OpenAIApi.Responses ? ResponsesSse : ChatCompletionsSse;
        var provider = new OpenAIProvider(
            new OpenAIOptions { ApiKey = new Secret("test-key"), HttpClient = Canned(sse), Catalog = Catalog("openai", "gpt-test"), Retry = RetryPolicy.None },
            TimeProvider.System,
            api);

        (List<StreamEventKind> kinds, MessageAccumulator acc) = await Collect(provider, Request("gpt-test"));

        AssertWarned(kinds, acc);
    }

    [Fact]
    public async Task OpenAI_compatible_servers_stream_a_warning_event_too()
    {
        var provider = new OpenAICompatibleProvider(
            "openai",
            new OpenAIOptions { BaseUrl = new Uri("http://localhost:11434/v1"), HttpClient = Canned(ChatCompletionsSse), Catalog = Catalog("openai", "gpt-test"), Retry = RetryPolicy.None },
            TimeProvider.System);

        (List<StreamEventKind> kinds, MessageAccumulator acc) = await Collect(provider, Request("gpt-test"));

        AssertWarned(kinds, acc);
    }

    [Fact]
    public async Task Gemini_streams_a_warning_event_for_a_stripped_parameter()
    {
        GeminiProvider provider = GeminiHarness.NewProvider(Canned(GeminiSse), catalog: Catalog("gemini", "gemini-test"));

        (List<StreamEventKind> kinds, MessageAccumulator acc) = await Collect(provider, Request("gemini-test"));

        AssertWarned(kinds, acc);
    }

    [Fact]
    public void The_accumulator_keeps_warnings_in_arrival_order_and_they_do_not_change_the_message()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.Warning(new ResponseWarning("parameter_stripped", "a", "a")));
        acc.Add(StreamEvent.TextDelta(0, "x"u8.ToArray()));
        acc.Add(StreamEvent.Warning(new ResponseWarning("parameter_clamped", "b", "b")));
        acc.Add(StreamEvent.Done(FinishReason.Stop));

        Assert.Equal(["a", "b"], acc.Warnings.Select(w => w.Message).ToArray());
        Assert.Equal("x", acc.ToMessage().GetAssistantText());
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }
}
