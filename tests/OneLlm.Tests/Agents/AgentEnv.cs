using System.Globalization;
using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tests.Running;
using OneLlm.Tools;

namespace OneLlm.Tests.Agents;

/// <summary>A tool made from a delegate, with the metadata flags a test wants to vary.</summary>
internal sealed class FuncTool : ITool
{
    private readonly Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> _run;

    public FuncTool(
        string name,
        Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> run,
        bool endsLoop = false,
        bool approval = false,
        bool parallelSafe = true,
        TimeSpan? timeout = null)
    {
        _run = run;
        Definition = new ToolSpec(name, "Test tool " + name + ".", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""")!)
        {
            EndsLoop = endsLoop,
            RequiresApproval = approval,
            ParallelSafe = parallelSafe,
            Timeout = timeout,
        };
    }

    public ToolSpec Definition { get; }

    public int Calls { get; private set; }

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return await _run(inputs, context, cancellationToken).ConfigureAwait(false);
    }

    public static FuncTool Text(string name, string text, bool approval = false) => new(name, (_, _, _) => Task.FromResult(ToolResult.FromText(text)), approval: approval);
}

/// <summary>A runtime over a scripted provider with a fake clock, an optional file store, and helpers to write agents.</summary>
internal sealed class AgentEnv : IDisposable
{
    private int _ids;

    public AgentEnv(string? storeDirectory = null, string idPrefix = "", bool records = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "onellm-agent-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Provider = new ScriptedProvider();
        Rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, Harness.Yaml)]),
            TimeProvider = Clock,
            IdSource = () => idPrefix + (++_ids).ToString(CultureInfo.InvariantCulture),
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
        };
        Rt.Providers.Set("scripted", Provider);
        if (storeDirectory is not null)
        {
            Rt.Store = new FileInstanceStore(storeDirectory);
        }

        Records = new OneLlm.Records.JsonlRecordSink(Path.Combine(Directory, "records"));
        if (records)
        {
            Rt.Sinks.Add(Records);
        }
    }

    public string Directory { get; }

    public ScriptedProvider Provider { get; }

    public Runtime Rt { get; }

    public OneLlm.Records.JsonlRecordSink Records { get; }

    public ManualTimeProvider Clock { get; } = new();

    public static string NewTempDirectory()
    {
        string d = Path.Combine(Path.GetTempPath(), "onellm-agent-store-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>An agent definition naming the given tools (which must be registered), with extra top-level YAML.</summary>
    public static AgentDefinition Def(string name, string[] tools, string extra = "", string system = "You are an agent.")
    {
        string refs = tools.Length == 0 ? "references: []\n" : $"references:\n  - tools: [{string.Join(", ", tools)}]\n";
        return AgentDefinition.Parse($"name: {name}\nmodel: demo\nsystemPrompt: {system}\n{extra}\n{refs}", name + ".yaml");
    }

    public static AgentStartOptions Go(string prompt = "go", ICapabilities? caps = null) => new() { Prompt = prompt, Capabilities = caps };

    public static ScriptStep Say(string text) => ScriptStep.TextWithUsage(text, 1000, 100);

    public static ScriptStep Calls(params (string Id, string Tool, string Json)[] calls) => new()
    {
        ToolCalls = [.. calls.Select(static c => new ScriptedToolCall(c.Id, c.Tool, c.Json))],
        Usage = new Usage { InputTokens = 1000, OutputTokens = 100 },
    };

    public static ScriptStep Complete(string result = "done", string id = "tc") => Calls((id, "task_complete", $$"""{"result":"{{result}}","summary":"s"}"""));

    /// <summary>Waits for a task with a real-time limit, so a hung loop fails the test instead of the run.</summary>
    public static async Task<T> Within<T>(Task<T> task, int seconds = 20)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException("The agent did not settle within " + seconds + " s.");
        }
    }

    public static async Task Within(Task task, int seconds = 20)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException("The step did not finish within " + seconds + " s.");
        }
    }

    /// <summary>Waits for a task while moving the fake clock forward, for retries and timeouts that wait on time.</summary>
    public async Task<T> Pump<T>(Task<T> task, TimeSpan step)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("The agent did not settle while the clock was moving.");
            }

            Clock.Advance(step);
            await Task.Delay(2);
        }

        return await task;
    }

    public static string Sig(Message m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("U:").Append(m.GetUserText()).Append("|A:").Append(m.GetAssistantText());
        foreach (ToolCall c in m.ToolCalls)
        {
            sb.Append("|C:").Append(c.Id).Append('/').Append(c.Tool).Append('/').Append(c.Inputs?.ToJsonString()).Append("=>")
                .Append(string.Concat(c.Result.OfType<TextPart>().Select(static p => p.Text))).Append(c.IsError ? "!" : "");
        }

        return sb.ToString();
    }

    public static string ToolText(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(static p => p.Text));

    public static string ToolText(ToolCall c) => string.Concat(c.Result.OfType<TextPart>().Select(static p => p.Text));

    public void Dispose()
    {
        // A sub-agent a test leaves running may still hold its record file open for a moment (Windows refuses to delete an open file,
        // and a late write can recreate a directory). Cleanup of a scratch directory is best effort and never fails the test.
        for (int attempt = 0; attempt < 20 && System.IO.Directory.Exists(Directory); attempt++)
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Grants a test chooses: given file roots, shell and network off.</summary>
internal sealed class TestCapabilities(params string[] roots) : ICapabilities
{
    public IReadOnlyList<string> FileRoots { get; } = roots;

    public bool ShellEnabled { get; init; }

    public bool IsNetworkAllowed(Uri uri) => false;
}
