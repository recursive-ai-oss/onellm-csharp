using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using OneChatResponse = OneLlm.Providers.ChatResponse;

namespace OneLlm.Extensions.AI.Tests;

internal sealed class FakeChatClient : IChatClient
{
    public Func<List<ChatMessage>, ChatOptions?, MeaiChatResponse> OnResponse { get; set; } = (_, _) => new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "pong"));

    public Func<List<ChatMessage>, ChatOptions?, IEnumerable<ChatResponseUpdate>>? OnStream { get; set; }

    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> copy = [.. messages];
        Calls.Add((copy, options));
        return Task.FromResult(OnResponse(copy, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> copy = [.. messages];
        Calls.Add((copy, options));
        foreach (ChatResponseUpdate u in OnStream!(copy, options))
        {
            await Task.Yield();
            yield return u;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        long tokens = 0;
        foreach (string v in values)
        {
            result.Add(new Embedding<float>(new float[] { v.Length, 1 }));
            tokens += v.Length;
        }

        result.Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens };
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Any Microsoft.Extensions.AI client registered as a OneLLM provider, priced from a user catalog entry.</summary>
[Collection("llm-default")]
public class ClientAsProviderTests
{
    private const string Yaml = """
        name: fake
        modelPatterns: ["fake-*"]
        models:
          - name: fake-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing:
              asOf: "2026-11-01"
              source: https://example.test/prices
              inputPerMillion: 1.00
              outputPerMillion: 2.00
              cacheReadPerMillion: 0.10
          - name: fake-embed
            pricing: { asOf: "2026-11-01", source: https://example.test/prices, inputPerMillion: 0.10 }
        """;

    private static (Runtime Runtime, FakeChatClient Client) Make()
    {
        var client = new FakeChatClient();
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.User, Yaml)]),
            TimeProvider = Kit.Time,
            Providers = new ProviderRegistry(_ => null),
        };
        rt.Providers.AddChatClient("fake", client, new FakeEmbeddings());
        return (rt, client);
    }

    private static MeaiChatResponse Pong(long input = 1000, long output = 500, long cached = 0) =>
        new(new ChatMessage(ChatRole.Assistant, "pong")) { ModelId = "fake-1", ResponseId = "r1", FinishReason = ChatFinishReason.Stop, Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, CachedInputTokenCount = cached } };

    [Fact]
    public async Task Llm_ask_runs_through_the_client_and_is_priced_from_the_catalog_entry()
    {
        (Runtime rt, FakeChatClient client) = Make();
        client.OnResponse = (_, _) => Pong();
        Runtime previous = Llm.Default;
        Llm.Default = rt;
        try
        {
            AskResult r = await Llm.GenerateAsync("fake-1", "ping", []);
            Assert.Equal("pong", r.Text);
            Assert.Equal(FinishReason.Stop, r.Finish);
            Assert.Equal(1000, r.Usage!.InputTokens);
            Assert.Equal(500, r.Usage.OutputTokens);
            // 1000 input tokens at $1 per million plus 500 output tokens at $2 per million.
            Assert.Equal(0.002m, r.Cost.Total);
            Assert.Equal("ping", client.Calls[0].Messages.Single().Text);
            Assert.Equal("pong", await Llm.AskAsync("fake-1", "again"));
        }
        finally
        {
            Llm.Default = previous;
        }
    }

    [Fact]
    public async Task Cached_input_tokens_are_priced_at_the_cache_read_rate_not_twice()
    {
        (Runtime rt, FakeChatClient client) = Make();
        // The client reports 1000 input tokens of which 600 were read from its cache.
        client.OnResponse = (_, _) => Pong(input: 1000, output: 0, cached: 600);
        AskResult r = await rt.AskAsync(new AskRequest { Model = "fake-1", Prompt = "ping" });
        Assert.Equal(400, r.Usage!.InputTokens);
        Assert.Equal(600, r.Usage.CacheReadInputTokens);
        Assert.Equal(0.0004m + 0.00006m, r.Cost.Total);
    }

    [Fact]
    public async Task Streaming_updates_become_stream_events_and_the_same_message()
    {
        (Runtime rt, FakeChatClient client) = Make();
        client.OnStream = (_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, "Hel") { ResponseId = "r2", ModelId = "fake-1" },
            new ChatResponseUpdate(ChatRole.Assistant, "lo") { ResponseId = "r2" },
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 2000, OutputTokenCount = 1000 })]) { FinishReason = ChatFinishReason.Stop },
        ];
        StreamingAnswer answer = rt.StreamAsync(new AskRequest { Model = "fake-1", Prompt = "hi" });
        var text = new List<string>();
        await foreach (string t in answer.TextAsync())
        {
            text.Add(t);
        }

        Assert.Equal(["Hel", "lo"], text);
        AskResult r = await answer.Result;
        Assert.Equal("Hello", r.Text);
        Assert.Equal(FinishReason.Stop, r.Finish);
        Assert.Equal(0.004m, r.Cost.Total);
    }

    [Fact]
    public async Task A_tool_round_trip_carries_calls_arguments_results_and_errors()
    {
        (Runtime rt, FakeChatClient client) = Make();
        IChatProvider provider = rt.GetChatProvider(rt.Resolve("fake-1"));
        ToolDefinition weather = new("get_weather", "Gets the weather.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"},"days":{"type":"integer"}},"required":["city"]}""")!) { Strict = true };

        client.OnResponse = (_, _) => new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Checking."),
            new FunctionCallContent("c1", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris", ["days"] = 3 }),
            new FunctionCallContent("c2", "get_weather", new Dictionary<string, object?> { ["city"] = "Rome" }),
        ])) { ModelId = "fake-1", FinishReason = ChatFinishReason.ToolCalls };

        Message ask = Message.FromUser("Weather?");
        OneChatResponse first = await provider.SendAsync(new ChatRequest { Model = "fake-1", Messages = [ask], Tools = [weather] });

        // The tool definition went out as a declaration with the strict flag and the schema unchanged.
        ChatOptions sent = client.Calls[0].Options!;
        AIFunctionDeclaration declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(sent.Tools!));
        Assert.Equal("get_weather", declaration.Name);
        Assert.True((bool)declaration.AdditionalProperties["strictJsonSchema"]!);
        Assert.Equal(FinishReason.ToolCalls, first.Finish);
        Assert.Equal("Checking.", first.Message.GetAssistantText());
        Assert.Equal(["c1", "c2"], first.Message.ToolCalls.Select(c => c.Id));
        Assert.Equal(3, first.Message.ToolCalls[0].Inputs!["days"]!.GetValue<int>());
        Assert.All(first.Message.ToolCalls, c => Assert.Equal(1, c.After));

        ToolCall[] done =
        [
            new(first.Message.ToolCalls[0].Id, "get_weather") { Inputs = first.Message.ToolCalls[0].Inputs, After = 1, Result = [new TextPart("sunny")] },
            new(first.Message.ToolCalls[1].Id, "get_weather") { Inputs = first.Message.ToolCalls[1].Inputs, After = 1, Result = [new TextPart("no data")], IsError = true },
        ];
        client.OnResponse = (_, _) => Pong();
        await provider.SendAsync(new ChatRequest { Model = "fake-1", Messages = [ask, new Message { Assistant = first.Message.Assistant, ToolCalls = done }], Tools = [weather] });

        List<ChatMessage> second = client.Calls[1].Messages;
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], second.Select(m => m.Role));
        Assert.Equal(["c1", "c2"], second[2].Contents.OfType<FunctionResultContent>().Select(c => c.CallId));
        FunctionResultContent[] results = [.. second[2].Contents.OfType<FunctionResultContent>()];
        Assert.Equal("sunny", results[0].Result);
        Assert.True((bool)results[1].AdditionalProperties![OneLlmProperties.IsError]!);
    }

    [Fact]
    public async Task Options_and_response_format_reach_the_client()
    {
        (Runtime rt, FakeChatClient client) = Make();
        IChatProvider provider = rt.GetChatProvider(rt.Resolve("fake-1"));
        JsonObject schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}""")!;
        await provider.SendAsync(new ChatRequest
        {
            Model = "fake-1",
            System = "Be brief.",
            Messages = [Message.FromUser("x")],
            Temperature = 0.5,
            TopP = 0.25,
            MaxOutputTokens = 77,
            StopSequences = ["END"],
            ToolChoice = ToolChoice.Tool("get_weather"),
            ResponseFormat = ResponseFormat.FromSchema("out", schema),
            Reasoning = new OneLlm.Providers.ReasoningOptions { Effort = "high" },
            ServiceTier = "flex",
            ExtraParameters = (JsonObject)JsonNode.Parse("""{"top_k":5}""")!,
        });
        (List<ChatMessage> messages, ChatOptions? options) = client.Calls[0];
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("Be brief.", messages[0].Text);
        Assert.Equal(0.5f, options!.Temperature);
        Assert.Equal(0.25f, options.TopP);
        Assert.Equal(77, options.MaxOutputTokens);
        Assert.Equal(["END"], options.StopSequences);
        Assert.Equal("get_weather", Assert.IsType<RequiredChatToolMode>(options.ToolMode).RequiredFunctionName);
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.Equal("out", format.SchemaName);
        Assert.Equal(JsonValueKind.Object, format.Schema!.Value.ValueKind);
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
        Assert.Equal("flex", options.AdditionalProperties![OneLlmProperties.ServiceTier]);
        Assert.Equal(5, ((JsonNode)options.AdditionalProperties[OneLlmProperties.ExtraParameters]!)["top_k"]!.GetValue<int>());
    }

    [Fact]
    public async Task Embeddings_come_from_the_generator_and_are_priced()
    {
        (Runtime rt, _) = Make();
        EmbedResult r = await rt.EmbedAsync("fake-embed", ["abcd", "xy"]);
        Assert.Equal(2, r.Embeddings.Count);
        Assert.Equal(4f, r.Embeddings[0].Span[0]);
        Assert.True(r.Cost.Total > 0m);
    }

    [Fact]
    public async Task A_client_that_fails_surfaces_its_exception()
    {
        (Runtime rt, FakeChatClient client) = Make();
        client.OnResponse = (_, _) => throw new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.AskAsync(new AskRequest { Model = "fake-1", Prompt = "x" }));
    }

    [Fact]
    public async Task Messages_survive_a_round_trip_with_their_lossy_edges()
    {
        var call = new ToolCall("c1", "lookup")
        {
            Inputs = (JsonObject)JsonNode.Parse("""{"q":"x"}""")!,
            Result = [new JsonPart(JsonNode.Parse("""{"hit":true}""")!)],
            Injected = true,
            InjectedBy = "initialPrompts[0]",
            When = "{{ .inputs.go }}",
            Routine = "tools/lookup",
            After = 2,
        };
        var turn = new Message
        {
            Assistant = [new ReasoningPart("anthropic") { Display = "thinking", Data = (JsonObject)JsonNode.Parse("""{"signature":"sig"}""")! }, new TextPart("a"), new TextPart("b")],
            ToolCalls = [call],
            When = "{{ .inputs.go }}",
        };
        var list = new List<ChatMessage>();
        ContentMapper.AppendMessages(turn, list);
        Assert.Equal([ChatRole.Assistant, ChatRole.Tool], list.Select(m => m.Role));
        // The call sits where the model made it: after two parts.
        Assert.IsType<FunctionCallContent>(list[0].Contents[2]);

        (_, List<Message> back) = ContentMapper.ToMessages(list, null);
        Message again = Assert.Single(back);
        Assert.Equal(Kit.Body(turn), Kit.Body(again));
        Assert.Equal(turn.When, again.When);
        _ = await Task.FromResult(0);
    }
}
