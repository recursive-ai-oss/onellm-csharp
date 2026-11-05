using System.Globalization;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using OneLlm.Agents;
using OneLlm.Bench.Refs;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;

namespace OneLlm.Bench;

/// <summary>
/// Agent-loop overhead: one run is ten model turns that each call a no-op tool, then one that ends the run. OneLLM drives its scripted
/// provider (no I/O, in-memory instance store); the reference drives its agent over a stub chat client that does the same. Divide the time
/// by 11 for the per-turn cost. <c>Own_*</c> rows compare OneLLM with no hook against one observer hook on the message point.
/// </summary>
[MemoryDiagnoser]
public class AgentBench
{
    private const int Turns = 10;
    private const string CatalogYaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        aliases: { demo: demo-1 }
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing: { asOf: "2026-11-01", source: "https://example.test/prices", inputPerMillion: 1.00, outputPerMillion: 2.00 }
        """;

    private Runtime _plain = null!;
    private Runtime _observed = null!;
    private AgentDefinition _definition = null!;
    private AgentFrameworkRef _framework = null!;
    private int _ids;

    [GlobalSetup]
    public void Setup()
    {
        _definition = AgentDefinition.Parse("name: bench\nmodel: demo\nsystemPrompt: You are an agent.\nreferences:\n  - tools: [noop, task_complete]\n", "bench.yaml");
        _plain = NewRuntime();
        _observed = NewRuntime();
        _observed.Hooks.OnMessage((_, _) => ValueTask.CompletedTask);
        _framework = new AgentFrameworkRef(Turns);
    }

    private Runtime NewRuntime()
    {
        var provider = new ScriptedProvider
        {
            Fallback = request => request.Messages.Count > Turns
                ? new ScriptStep { ToolCalls = [new ScriptedToolCall("done", "task_complete", "{\"result\":\"ok\",\"summary\":\"s\"}")], Usage = new Usage { InputTokens = 1000, OutputTokens = 100 } }
                : new ScriptStep { ToolCalls = [new ScriptedToolCall("c" + request.Messages.Count.ToString(CultureInfo.InvariantCulture), "noop", "{}")], Usage = new Usage { InputTokens = 1000, OutputTokens = 100 } },
        };
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]),
            IdSource = () => (++_ids).ToString(CultureInfo.InvariantCulture),
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
        };
        rt.Providers.Set("scripted", provider);
        rt.Tools.Add(new NoopTool());
        return rt;
    }

    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("agent-loop")]
    public Task<AgentResult> Ours_Agent() => _plain.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" });

    [Benchmark, BenchmarkCategory("agent-loop")]
    public Task<string> Ref_AgentFramework() => _framework.Run();

    [Benchmark, BenchmarkCategory("agent-hooks")]
    public Task<AgentResult> Own_NoHook() => _plain.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" });

    [Benchmark, BenchmarkCategory("agent-hooks")]
    public Task<AgentResult> Own_OneObserver() => _observed.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" });

    private sealed class NoopTool : ITool
    {
        public ToolSpec Definition { get; } = new("noop", "Does nothing.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!);

        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.FromText("ok"));
    }
}
