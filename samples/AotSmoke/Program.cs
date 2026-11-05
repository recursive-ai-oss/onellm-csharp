using System.Text;
using System.Text.Json;
using OneYaml;
using OneLlm.Catalog;
using OneLlm.Messages;

// Exercises the core paths that must stay NativeAOT-safe: building a message from stream events, JSON and YAML round trips
// through the source-generated contexts, and catalog resolution. Exits non-zero if anything differs.

var acc = new MessageAccumulator();
acc.Add(StreamEvent.MessageStart("resp_1", "demo-model"));
acc.Add(StreamEvent.TextDelta(0, "Hello, "u8.ToArray()));
acc.Add(StreamEvent.TextDelta(0, "world."u8.ToArray()));
acc.Add(StreamEvent.ToolCallStart(1, "call_1", "lookup"));
acc.Add(StreamEvent.ToolCallArgumentsDelta(1, """{"q":"x"}"""u8.ToArray()));
acc.Add(StreamEvent.ToolCallEnd(1));
acc.Add(StreamEvent.Done(FinishReason.ToolCalls));
Message built = acc.ToMessage();

string json = JsonSerializer.Serialize(built, OneLlmJsonContext.Default.Message);
Message fromJson = JsonSerializer.Deserialize(json, OneLlmJsonContext.Default.Message)!;
string yaml = YamlSerializer.Serialize(built, OneLlmYamlContext.Default.Message);
Message fromYaml = YamlSerializer.Deserialize(yaml, OneLlmYamlContext.Default.Message)!;

bool ok = JsonSerializer.Serialize(fromJson, OneLlmJsonContext.Default.Message) == json
    && JsonSerializer.Serialize(fromYaml, OneLlmJsonContext.Default.Message) == json
    && fromJson.GetAssistantText() == "Hello, world."
    && fromJson.ToolCalls.Count == 1;

ModelCatalog catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, """
    name: demo
    modelPatterns: ["demo-*"]
    aliases:
      small: demo-small
    models:
      - name: demo-small
        contextWindow: 1000
    """)]);
ok &= catalog.Resolve("small").Name == "demo-small";
ok &= OneLlm.AotSmoke.PricingExercise.Run() == 0;
ok &= await OneLlm.AotSmoke.DefinitionsExercise.RunAsync();
ok &= await OneLlm.AotSmoke.RuntimeExercise.RunAsync();
ok &= await OneLlm.AotSmoke.DeploymentsExercise.RunAsync();

ok &= OneLlm.AotSmoke.ToolExercise.Run() == 0;
ok &= OneLlm.AotSmoke.MediaExercise.Run() == 0;
ok &= await OneLlm.AotSmoke.BuiltinsExercise.RunAsync();
ok &= OneLlm.AotSmoke.AnthropicSmoke.Run() == 0;
await OneLlm.AotSmoke.OpenAiExercise.RunAsync();
await OneLlm.AotSmoke.GeminiSmoke.RunAsync();

Console.WriteLine(ok ? "ok " + OneLlm.AssemblyMarker.Version : "FAILED");
return ok ? 0 : 1;
