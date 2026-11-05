using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm;
using OneLlm.Catalog;
using OneLlm.Extensions.AI;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;

// Exercises the bridge paths that must stay NativeAOT-safe: a OneLLM model as a chat client (send and stream, with cost), a chat client
// as a OneLLM provider, and tools and functions in both directions. Exits non-zero if anything differs.

TimeProvider clock = TimeProvider.System;
var rt = new Runtime
{
    Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, """
        name: scripted
        modelPatterns: ["demo-*"]
        models:
          - name: demo-1
            contextWindow: 1000
            maxOutputTokens: 100
            pricing: { asOf: "2026-11-01", source: https://example.test/p, inputPerMillion: 1.00, outputPerMillion: 2.00 }
        """)]),
    Providers = new ProviderRegistry(_ => null),
    TimeProvider = clock,
};
var scripted = new ScriptedProvider();
rt.Providers.Set("scripted", scripted);

bool ok = true;

// Out: a OneLLM model as a chat client.
scripted.Enqueue(ScriptStep.TextWithUsage("hello", 1000, 500));
using (var client = rt.AsChatClient("demo-1"))
{
    Microsoft.Extensions.AI.ChatResponse r = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
    ok &= r.Text == "hello" && (decimal)r.AdditionalProperties![OneLlmProperties.CostTotal]! == 0.002m;

    scripted.Enqueue(ScriptStep.Text("he", "llo"));
    var text = new System.Text.StringBuilder();
    await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
    {
        text.Append(u.Text);
    }

    ok &= text.ToString() == "hello";
}

// In: a chat client as a OneLLM provider, priced from the catalog.
var inner = new EchoClient();
var rt2 = new Runtime
{
    Catalog = rt.Catalog,
    Providers = new ProviderRegistry(_ => null),
    TimeProvider = clock,
};
rt2.Providers.AddChatClient("scripted", inner);
AskResult asked = await rt2.AskAsync(new AskRequest { Model = "demo-1", Prompt = "ping" });
ok &= asked.Text == "echo: ping" && asked.Cost.Total == 0.002m;

// Tools: ITool -> AIFunction -> ITool.
AIFunction function = new UpperTool().AsAIFunction(clock);
object? result = await function.InvokeAsync(new AIFunctionArguments { ["word"] = "abc" });
ok &= result is string s && s == "ABC";
ITool back = function.AsTool();
ok &= JsonNode.DeepEquals(back.Definition.InputSchema, new UpperTool().Definition.InputSchema);

Console.WriteLine(ok ? "ok" : "FAILED");
return ok ? 0 : 1;

internal sealed class EchoClient : IChatClient
{
    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "echo: " + messages.Last().Text))
        {
            Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 500 },
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "echo");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class UpperTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "upper",
        "Upper-cases a word.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["word"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("word"),
            ["additionalProperties"] = false,
        });

    public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolResult.FromText(inputs["word"]!.GetValue<string>().ToUpperInvariant()));
}
