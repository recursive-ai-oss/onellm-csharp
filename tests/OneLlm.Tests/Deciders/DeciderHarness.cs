using System.Text;
using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.Tests.Deciders;

/// <summary>A runtime with a scripted decisions engine (<c>dec-1</c>, priced) and a scripted chat model (<c>chat-1</c>).</summary>
internal sealed class DeciderHarness
{
    public const string Yaml = """
        providers:
          - name: fakedec
            modelPatterns: ["dec-*"]
            capabilities: { decisions: true }
            models:
              - name: dec-1
                kind: decision
                contextWindow: 64000
                properties: { maxQuestions: 400 }
                pricing:
                  asOf: "2026-11-01"
                  source: https://example.test/prices
                  inputPerMillion: 1.00
                  outputPerMillion: 0
                  cacheReadPerMillion: 0.10
                  cacheCreationPerMillion: 1.25
          - name: fakechat
            modelPatterns: ["chat-*"]
            models:
              - name: chat-1
                contextWindow: 100000
                pricing:
                  asOf: "2026-11-01"
                  source: https://example.test/prices
                  inputPerMillion: 1.00
                  outputPerMillion: 2.00
        """;

    private int _ids;

    public DeciderHarness(string? instances = null)
    {
        Engine = new ScriptedDecisionProvider("fakedec");
        Chat = new ScriptedProvider("fakechat");
        Runtime = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, Yaml)]),
            TimeProvider = new ManualTimeProvider(),
            IdSource = () => (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Providers = new ProviderRegistry(static _ => null),
        };
        Runtime.Providers.Set("fakedec", Engine);
        Runtime.Providers.Set("fakechat", Chat);
    }

    public ScriptedDecisionProvider Engine { get; }

    public ScriptedProvider Chat { get; }

    public Runtime Runtime { get; }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);
}
