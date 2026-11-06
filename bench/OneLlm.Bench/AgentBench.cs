using System.Globalization;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using OneLlm.Agents;
using OneLlm.Bench.Refs;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;

namespace OneLlm.Bench;

/// <summary>
/// Agent-loop overhead: one run is ten model turns that each call a no-op tool, then one that ends the run. OneLLM drives a stub provider
/// (no I/O) on a default runtime: in-memory instance store, no record sinks; the reference drives its agent over a stub chat client that does the same. Divide the time
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
    private Runtime _file = null!;
    private string _dir = "";
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
        _dir = Path.Combine(Path.GetTempPath(), "onellm-agent-bench-" + Guid.NewGuid().ToString("N"));
        _file = NewRuntime(new FileInstanceStore(_dir));
        _framework = new AgentFrameworkRef(Turns);
    }

    private Runtime NewRuntime(IInstanceStore? store = null)
    {
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]),
            IdSource = () => (++_ids).ToString(CultureInfo.InvariantCulture),
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
        };
        if (store is not null)
        {
            rt.Store = store;
        }

        rt.Providers.Set("scripted", new LoopStub(Turns));
        rt.Tools.Add(new NoopTool());
        return rt;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Peak.Record();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Benchmark, BenchmarkCategory("agent-loop")]
    public Task<AgentResult> Ours_Agent() => RunReleased(_plain);

    [Benchmark, BenchmarkCategory("agent-loop")]
    public Task<string> Ref_AgentFramework() => _framework.Run();

    [Benchmark, BenchmarkCategory("agent-hooks")]
    public Task<AgentResult> Own_NoHook() => RunReleased(_plain);

    [Benchmark, BenchmarkCategory("agent-hooks")]
    public Task<AgentResult> Own_OneObserver() => RunReleased(_observed);

    [Benchmark, BenchmarkCategory("agent-store")]
    public Task<AgentResult> Own_FileStore() => _file.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" });

    [Benchmark, BenchmarkCategory("agent-store")]
    public Task<AgentResult> Own_MemoryStoreRetained() => _plain.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" });

    // The compared rows take the instance out of the store when the run is over, as the reference's session becomes garbage when its run
    // returns. Without it the default store keeps every finished run, the heap grows with the iteration count, and the timings measure
    // the collector working through that growth; Own_MemoryStoreRetained shows that case.
    private async Task<AgentResult> RunReleased(Runtime runtime)
    {
        AgentResult result = await runtime.RunAgentAsync(_definition, null, new AgentStartOptions { Prompt = "go" }).ConfigureAwait(false);
        await runtime.Store.DeleteAsync(result.Instance.InstanceId).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// A chat provider that answers from memory with no I/O, like the reference's stub client: it asks for the no-op tool until the
    /// conversation holds <c>turns</c> tool calls, then ends the run. It keeps no record of requests, so what is measured is the loop.
    /// </summary>
    private sealed class LoopStub(int turns) : IChatProvider
    {
        public string Name => "scripted";

        public string Deployment => "direct";

        public Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            int results = 0;
            foreach (Message m in request.Messages)
            {
                results += m.ToolCalls.Count;
            }

            ToolCall call = results < turns
                ? new ToolCall("c" + results.ToString(CultureInfo.InvariantCulture), "noop") { Inputs = new JsonObject() }
                : new ToolCall("done", "task_complete") { Inputs = (JsonObject)JsonNode.Parse("{\"result\":\"ok\",\"summary\":\"s\"}")! };
            var message = new Message { ToolCalls = [call], Usage = new Usage { Model = request.Model, InputTokens = 1000, OutputTokens = 100 } };
            return Task.FromResult(new ChatResponse { Message = message, Finish = FinishReason.ToolCalls });
        }

        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopTool : ITool
    {
        public ToolSpec Definition { get; } = new("noop", "Does nothing.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!);

        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.FromText("ok"));
    }
}
