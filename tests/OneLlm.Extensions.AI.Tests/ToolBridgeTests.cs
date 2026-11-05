using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;

namespace OneLlm.Extensions.AI.Tests;

public class ToolBridgeTests
{
    private const string Schema = """{"type":"object","properties":{"city":{"type":"string","description":"A city"},"days":{"type":"integer","minimum":1}},"required":["city"],"additionalProperties":false}""";

    private sealed class EchoTool : ITool
    {
        public ToolSpec Definition { get; } = new("echo", "Echoes the city.", (JsonObject)JsonNode.Parse(Schema)!)
        {
            OutputSchema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"said":{"type":"string"}}}""")!,
            RequiresApproval = true,
        };

        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            if (inputs["city"]!.GetValue<string>() == "fail")
            {
                return ValueTask.FromResult(ToolResult.Error("no such city"));
            }

            return ValueTask.FromResult(ToolResult.FromJson(new JsonObject { ["said"] = inputs["city"]!.GetValue<string>(), ["call"] = context.ToolCallId.StartsWith("meai-", StringComparison.Ordinal) }));
        }
    }

    [Fact]
    public async Task A_tool_as_a_function_keeps_its_schema_and_runs()
    {
        AIFunction function = new EchoTool().AsAIFunction(Kit.Time);
        Assert.Equal("echo", function.Name);
        Assert.Equal("Echoes the city.", function.Description);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Schema), JsonNode.Parse(function.JsonSchema.GetRawText())));
        Assert.NotNull(function.ReturnJsonSchema);

        object? result = await function.InvokeAsync(new AIFunctionArguments { ["city"] = "Paris" });
        JsonElement json = Assert.IsType<JsonElement>(result);
        Assert.Equal("Paris", json.GetProperty("said").GetString());
        Assert.True(json.GetProperty("call").GetBoolean());
    }

    [Fact]
    public async Task A_tool_error_is_an_exception_the_function_caller_reports()
    {
        AIFunction function = new EchoTool().AsAIFunction(Kit.Time);
        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(new AIFunctionArguments { ["city"] = "fail" }));
        Assert.Equal("no such city", e.Message);
    }

    [Fact]
    public async Task A_function_as_a_tool_keeps_its_schema_and_runs()
    {
        AIFunction function = AIFunctionFactory.Create(
            (string city, int days) => $"{city}:{days}",
            "forecast",
            "Gets a forecast.");
        ITool tool = function.AsTool();
        Assert.Equal("forecast", tool.Definition.Name);
        Assert.Equal("Gets a forecast.", tool.Definition.Description);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(function.JsonSchema.GetRawText()), tool.Definition.InputSchema));

        ToolResult r = await tool.InvokeAsync(
            new JsonObject { ["city"] = "Rome", ["days"] = 2 },
            new ToolContext("i", "t", Kit.Time),
            CancellationToken.None);
        Assert.False(r.IsError);
        Assert.Equal("Rome:2", Assert.IsType<TextPart>(Assert.Single(r.Parts)).Text);
    }

    [Fact]
    public void Schemas_are_equal_after_a_round_trip_in_both_directions()
    {
        // tool -> function -> tool
        ITool back = new EchoTool().AsAIFunction(Kit.Time).AsTool();
        Assert.True(JsonNode.DeepEquals(new EchoTool().Definition.InputSchema, back.Definition.InputSchema));
        Assert.True(JsonNode.DeepEquals(new EchoTool().Definition.OutputSchema, back.Definition.OutputSchema));
        Assert.Equal("echo", back.Definition.Name);

        // function -> tool -> function
        AIFunction original = AIFunctionFactory.Create((string city, int days) => $"{city}:{days}", "forecast", "Gets a forecast.");
        AIFunction again = original.AsTool().AsAIFunction(Kit.Time);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.JsonSchema.GetRawText()), JsonNode.Parse(again.JsonSchema.GetRawText())));
        Assert.Equal(original.Name, again.Name);
        Assert.Equal(original.Description, again.Description);
    }

    [Fact]
    public void An_approval_wrapper_becomes_a_tool_that_requires_approval()
    {
        AIFunction inner = AIFunctionFactory.Create((string city) => city, "lookup", "Looks up.");
        Assert.True(new ApprovalRequiredAIFunction(inner).AsTool().Definition.RequiresApproval);
        Assert.False(inner.AsTool().Definition.RequiresApproval);
    }

    [Fact]
    public void A_name_the_tool_contract_refuses_is_reported()
    {
        AIFunction bad = AIFunctionFactory.Create(() => 1, "has.dot", "x");
        Assert.Throws<ArgumentException>(() => bad.AsTool());
    }

    [Fact]
    public void A_tool_function_has_no_capabilities_unless_the_caller_grants_them()
    {
        ToolContext? seen = null;
        AIFunction function = new CaptureTool(c => seen = c).AsAIFunction(Kit.Time);
        function.InvokeAsync(new AIFunctionArguments { ["city"] = "x" }).AsTask().GetAwaiter().GetResult();
        Assert.Same(NoCapabilities.Instance, seen!.Capabilities);
    }

    private sealed class CaptureTool(Action<ToolContext> capture) : ITool
    {
        public ToolSpec Definition { get; } = new("capture", "Captures.", (JsonObject)JsonNode.Parse(Schema)!);

        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            capture(context);
            return ValueTask.FromResult(ToolResult.FromText("ok"));
        }
    }
}

/// <summary>OneLLM models through the runtime, as Microsoft.Extensions.AI clients: priced, with usage and cost on the response.</summary>
public class RuntimeAsClientTests
{
    private const string Yaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing:
              asOf: "2026-11-01"
              source: https://example.test/prices
              inputPerMillion: 1.00
              outputPerMillion: 2.00
          - name: embed-1
            pricing: { asOf: "2026-11-01", source: https://example.test/prices, inputPerMillion: 0.10 }
        """;

    private static (Runtime Runtime, ScriptedProvider Provider) Make()
    {
        var provider = new ScriptedProvider();
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, Yaml)]),
            TimeProvider = Kit.Time,
            Providers = new ProviderRegistry(_ => null),
        };
        rt.Providers.Set("scripted", provider);
        return (rt, provider);
    }

    [Fact]
    public async Task Chat_has_usage_and_cost_in_the_documented_keys()
    {
        (Runtime rt, ScriptedProvider provider) = Make();
        provider.Enqueue(ScriptStep.TextWithUsage("hello", 1000, 500));
        using var client = rt.AsChatClient("demo-1");
        Microsoft.Extensions.AI.ChatResponse r = await client.GetResponseAsync("hi");
        Assert.Equal("hello", r.Text);
        Assert.Equal(1000, r.Usage!.InputTokenCount);
        Assert.Equal(500, r.Usage.OutputTokenCount);
        Assert.Equal(0.002m, r.AdditionalProperties![OneLlmProperties.CostTotal]);
        Cost cost = Assert.IsType<Cost>(r.AdditionalProperties[OneLlmProperties.Cost]);
        Assert.Equal(["inputTokens", "outputTokens"], cost.Lines.Select(l => l.Counter));
        Assert.Equal(1m, cost.Lines[0].UnitPrice);
        Assert.Equal("demo-1", Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata))).DefaultModelId);
    }

    [Fact]
    public async Task Streaming_ends_with_one_usage_content_carrying_the_cost()
    {
        (Runtime rt, ScriptedProvider provider) = Make();
        provider.Enqueue(new ScriptStep { Chunks = ["he", "llo"], Usage = new Usage { InputTokens = 2000, OutputTokens = 1000 } });
        using var client = rt.AsChatClient("demo-1");
        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(u);
        }

        Assert.Equal("hello", string.Concat(updates.Select(u => u.Text)));
        UsageContent usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>());
        Assert.Equal(2000, usage.Details.InputTokenCount);
        Assert.Equal(0.004m, usage.AdditionalProperties![OneLlmProperties.CostTotal]);
        Assert.Equal(1000, updates.ToChatResponse().Usage!.OutputTokenCount);
    }

    [Fact]
    public async Task Model_id_option_picks_another_catalog_model_and_an_unknown_one_fails()
    {
        (Runtime rt, ScriptedProvider provider) = Make();
        provider.Enqueue(ScriptStep.TextWithUsage("x", 10, 10));
        using var client = rt.AsChatClient("demo-1");
        await client.GetResponseAsync("hi", new ChatOptions { ModelId = "demo-1" });
        Assert.Equal("demo-1", provider.Requests[0].Model);
        await Assert.ThrowsAnyAsync<OneLlmException>(() => client.GetResponseAsync("hi", new ChatOptions { ModelId = "no-such-model" }));
    }

    [Fact]
    public async Task Embeddings_report_usage_and_cost()
    {
        (Runtime rt, _) = Make();
        using var generator = rt.AsEmbeddingGenerator("embed-1");
        GeneratedEmbeddings<Embedding<float>> r = await generator.GenerateAsync(["abcd", "xy"]);
        Assert.Equal(2, r.Count);
        Assert.Equal(4f, r[0].Vector.Span[0]);
        Assert.Equal(6, r.Usage!.InputTokenCount);
        Assert.Equal(0.0000006m, r.AdditionalProperties![OneLlmProperties.CostTotal]);
    }

    [Fact]
    public void A_provider_without_the_capability_says_what_is_missing()
    {
        (Runtime rt, _) = Make();
        Assert.Throws<CapabilityMissingException>(() => rt.AsTextToSpeechClient("demo-1"));
    }

    [Fact]
    public void Audio_containers_are_recognised_from_their_first_bytes()
    {
        Assert.Equal("audio/wav", AudioSniffer.ContentTypeOf("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Equal("audio/mpeg", AudioSniffer.ContentTypeOf("ID3\x04\0\0\0\0\0\0"u8));
        Assert.Equal("audio/flac", AudioSniffer.ContentTypeOf("fLaC\0\0\0\0"u8));
        Assert.Equal("audio/ogg", AudioSniffer.ContentTypeOf("OggS\0\0\0\0"u8));
        Assert.Equal("audio/mp4", AudioSniffer.ContentTypeOf("\0\0\0 ftypM4A "u8));
    }
}
