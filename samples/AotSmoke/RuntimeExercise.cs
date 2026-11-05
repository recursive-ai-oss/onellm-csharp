using System.Text.Json.Serialization;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.AotSmoke;

internal sealed record Receipt(string Shop, decimal Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Receipt))]
internal sealed partial class ReceiptJson : JsonSerializerContext;

/// <summary>Exercises the runtime and the happy-path statics against the scripted provider, so the native-AOT publish checks them.</summary>
internal static class RuntimeExercise
{
    public static async Task<bool> RunAsync()
    {
        var provider = new ScriptedProvider();
        provider.EnqueueText("plain answer")
            .EnqueueText("""{"shop":"Corner","total":4.5}""")
            .Enqueue(ScriptStep.Text("one"), ScriptStep.TextWithUsage("two", 100, 10), ScriptStep.Text("s1", "s2"));

        var runtime = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, """
                name: scripted
                modelPatterns: ["demo-*"]
                models:
                  - name: demo-1
                    maxOutputTokens: 100
                    pricing: { inputPerMillion: 1.00, outputPerMillion: 2.00 }
                """)]),
            Providers = new ProviderRegistry(),
        };
        runtime.Providers.Set("scripted", provider);
        Llm.Default = runtime;

        string text = await Llm.AskAsync("demo-1", "say something");
        Receipt receipt = await Llm.AskAsync("demo-1", "read the receipt", ReceiptJson.Default.Receipt);

        AgentDefinition script = AgentDefinition.Parse(
            "model: demo-1\nsystemPrompt: 'Help {{ .inputs.who }}.'\ninitialPrompts:\n  - user: [ { text: 'Hi {{ .inputs.who }}' } ]\n  - user: [ { text: 'Again, after {{ (index .turns 0).text }}' } ]\n",
            "smoke.flow");
        ScriptResult result = await runtime.RunScriptAsync(script, new Dictionary<string, object?> { ["who"] = "Ada" });

        StreamingAnswer stream = runtime.StreamAsync(new AskRequest { Model = "demo-1", Prompt = "stream" });
        string streamed = "";
        await foreach (string piece in stream.TextAsync())
        {
            streamed += piece;
        }

        return text == "plain answer"
            && receipt == new Receipt("Corner", 4.5m)
            && result.Text == "two"
            && result.Cost.Total == 0.00012m
            && result.Instance.RenderedSystemPrompt == "Help Ada."
            && provider.Requests[3].Messages[2].GetUserText() == "Again, after one"
            && streamed == "s1s2"
            && (await stream.Result).Text == "s1s2";
    }
}
