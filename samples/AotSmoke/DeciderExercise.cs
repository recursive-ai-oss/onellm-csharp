using System.Text.Json;
using System.Text.Json.Serialization;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.AotSmoke;

internal sealed record Routing(string Team, bool Escalate);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Routing))]
internal sealed partial class RoutingJson : JsonSerializerContext;

/// <summary>A decider written in C#: the generator types its answers, and its logic turns them into an outcome.</summary>
[Decider("triage.decider.yaml", Name = "triage")]
internal static partial class Triage
{
    private static JsonElement Decide(Answers answers, JsonElement inputs) =>
        JsonSerializer.SerializeToElement(new Routing(answers.Topic.Choice.ToString(), answers.Angry.Probability > 0.4), RoutingJson.Default.Routing);
}

/// <summary>Runs the C# decider against a scripted decisions engine, so the native-AOT publish checks the generated code.</summary>
internal static class DeciderExercise
{
    public static async Task<bool> RunAsync()
    {
        var engine = new ScriptedDecisionProvider("smokedec");
        var runtime = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, """
                name: smokedec
                modelPatterns: ["dec-*"]
                capabilities: { decisions: true }
                models:
                  - name: dec-1
                    kind: decision
                    contextWindow: 64000
                    pricing: { inputPerMillion: 1.00, outputPerMillion: 0 }
                """)]),
            Providers = new ProviderRegistry(),
        };
        runtime.Providers.Set("smokedec", engine);
        engine.Responder = request => new DecisionResponse
        {
            Answers = new System.Text.Json.Nodes.JsonObject
            {
                ["topic"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "choice", ["choice"] = "orders", ["confidence"] = 0.9, ["probabilities"] = new System.Text.Json.Nodes.JsonObject { ["billing"] = 0.1, ["orders"] = 0.9 } },
                ["angry"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "noul", ["noul"] = 0.8 },
            },
            Usage = new Messages.Usage { Model = "dec-1", InputTokens = 1000 },
        };

        DecisionResult result = await runtime.DecideAsync(Triage.Decider, new Dictionary<string, object?> { ["title"] = "Where is my parcel?" });
        Routing routing = result.GetOutputs(RoutingJson.Default.Routing);
        Triage.Answers answers = Triage.Answers.From(result.Answers);
        bool ok = routing is { Team: "Orders", Escalate: true }
            && answers.Topic.Choice == Triage.TopicChoice.Orders
            && result.Cost.Total == 0.001m
            && engine.Requests.Count == 1
            && System.Text.Encoding.UTF8.GetString(engine.Requests[0].State.Span) == """{"title":"Where is my parcel?"}""";

        Llm.Default = runtime;
        Routing viaStatic = await Llm.DecideAsync(Triage.Decider, RoutingJson.Default.Routing, new Dictionary<string, object?> { ["title"] = "x" });
        return ok && viaStatic.Team == "Orders";
    }
}
