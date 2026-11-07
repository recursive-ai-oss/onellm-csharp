using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.OneLite;
using OneLlm.Providers;
using OneLlm.Testing;
using RecursiveAI.OneLite;

namespace OneLlm.Stores.Tests;

public class AgentRecoveryTests
{
    private const string CatalogYaml = "name: scripted\nmodelPatterns: [demo]\nmodels:\n  - name: demo\n    contextWindow: 10000\n";

    private static Runtime Make(IInstanceStore store, ScriptedProvider provider, bool enabled = false)
    {
        var runtime = new Runtime
        {
            Store = store,
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]),
            Providers = new ProviderRegistry(_ => null),
            ResumeInterruptedAgents = enabled,
        };
        runtime.Providers.Set("scripted", provider);
        return runtime;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_agent_recovers_after_a_simulated_crash_but_not_while_leased(bool oneLite)
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using Database? db = oneLite ? await OneLiteStorage.OpenAsync(Path.Combine(dir.Path, "runs.db")) : null;
        IInstanceStore store = oneLite ? await OneLiteInstanceStore.CreateAsync(db!) : new FileInstanceStore(dir.Path);
        var crashedStore = new CrashStore(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProvider = new ScriptedProvider().Enqueue(new ScriptStep
        {
            Chunks = ["unfinished"],
            BeforeChunk = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
        });
        Runtime old = Make(crashedStore, oldProvider);
        AgentRun running = await old.StartAgentAsync(AgentDefinition.Parse("name: interrupted\nmodel: demo\nreferences:\n  - tools: [task_complete]\n"), options: new() { Prompt = "finish this task" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(InstanceStates.Running, (await store.LoadAsync(running.InstanceId))!.State);

        var provider = new ScriptedProvider().Enqueue(new ScriptStep
        {
            ToolCalls = [new ScriptedToolCall("done", "task_complete", "{\"result\":\"recovered\",\"summary\":\"done\"}")],
        });
        // A new host must be opted in, and must not steal a live host's lease.
        Runtime restarted = Make(store, provider);
        Assert.Empty(await restarted.ResumeInterruptedAgentsAsync());
        restarted.ResumeInterruptedAgents = true;
        Assert.Empty(await restarted.ResumeInterruptedAgentsAsync());

        await crashedStore.CrashAsync();
        await running.CancelAsync();
        Assert.Equal(InstanceStates.Running, (await store.LoadAsync(running.InstanceId))!.State);
        // Reopen the durable store, as a new process would.
        using Database? reopenedDb = oneLite ? await OneLiteStorage.OpenAsync(Path.Combine(dir.Path, "runs.db")) : null;
        IInstanceStore reopened = oneLite ? await OneLiteInstanceStore.CreateAsync(reopenedDb!) : new FileInstanceStore(dir.Path);
        restarted = Make(reopened, provider, enabled: true);
        Assert.NotNull(await restarted.GetAgentAsync(running.InstanceId));
        AgentRun recovered = Assert.Single(await restarted.ResumeInterruptedAgentsAsync());
        AgentResult result = await recovered.AwaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal("recovered", result.Text);
        Assert.Contains(result.Instance.Messages, static m => m.GetUserText() == "finish this task");
        Assert.Single(provider.Requests);
        Assert.Empty(await Make(reopened, provider, enabled: true).ResumeInterruptedAgentsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_starts_running_children_before_their_awaiting_parents(bool oneLite)
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using Database? db = oneLite ? await OneLiteStorage.OpenAsync(Path.Combine(dir.Path, "runs.db")) : null;
        IInstanceStore store = oneLite ? await OneLiteInstanceStore.CreateAsync(db!) : new FileInstanceStore(dir.Path);
        var crashedStore = new CrashStore(store);
        string? childId = null;
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProvider = new ScriptedProvider
        {
            Fallback = request => (request.System ?? "").Contains("CHILD", StringComparison.Ordinal)
                ? new ScriptStep { Chunks = ["unfinished"], BeforeChunk = async (_, token) => { childEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } }
                : new ScriptStep { ToolCalls = [new ScriptedToolCall("wait", "await_agent", "{\"ids\":[\"" + childId + "\"]}")], Expect = _ => parentEntered.TrySetResult() },
        };
        Runtime old = Make(crashedStore, oldProvider);
        AgentRun parent = await old.StartAgentAsync(AgentDefinition.Parse("name: parent\nmodel: demo\nreferences:\n  - tools: [await_agent, task_complete]\n"));
        AgentRun child = await old.StartAgentAsync(AgentDefinition.Parse("name: child\nmodel: demo\nsystemPrompt: CHILD\nreferences:\n  - tools: [task_complete]\n"), options: new() { Prompt = "go", SpawnerInstanceId = parent.InstanceId });
        childId = child.InstanceId;
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await parent.MessageAsync("wait for child");
        await parentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await crashedStore.CrashAsync();
        await parent.CancelAsync();
        await child.CancelAsync();

        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider
        {
            Fallback = request => (request.System ?? "").Contains("CHILD", StringComparison.Ordinal)
                ? new ScriptStep { Chunks = ["child complete"], BeforeChunk = async (_, token) => { resumedChild.TrySetResult(); await releaseChild.Task.WaitAsync(token); } }
                : request.Messages.Any(static m => m.ToolCalls.Count > 0)
                    ? new ScriptStep { ToolCalls = [new ScriptedToolCall("done", "task_complete", "{\"result\":\"parent complete\",\"summary\":\"done\"}")] }
                    : new ScriptStep { ToolCalls = [new ScriptedToolCall("wait", "await_agent", "{\"ids\":[\"" + childId + "\"]}")] },
        };
        Runtime restarted = Make(store, provider, enabled: true);
        IReadOnlyList<AgentRun> recovered = await restarted.ResumeInterruptedAgentsAsync();
        Assert.Equal(2, recovered.Count);
        Assert.Equal(child.InstanceId, recovered[0].InstanceId);
        await resumedChild.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<AgentResult> parentResult = recovered.Single(r => r.InstanceId == parent.InstanceId).AwaitAsync();
        Assert.False(parentResult.IsCompleted);
        releaseChild.SetResult();
        Assert.Equal("parent complete", (await parentResult.WaitAsync(TimeSpan.FromSeconds(10))).Text);
        await recovered[0].AwaitAsync();
    }

    // A dead process can no longer write its in-memory state. Releasing its lease models process exit, not a clean cancellation.
    private sealed class CrashStore(IInstanceStore inner) : IInstanceStore
    {
        private readonly List<IInstanceLease> _leases = [];
        private bool _crashed;

        public async Task CrashAsync()
        {
            _crashed = true;
            foreach (IInstanceLease lease in _leases)
            {
                await lease.DisposeAsync();
            }
        }

        public ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default) => inner.LoadAsync(instanceId, cancellationToken);
        public ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default) => _crashed ? ValueTask.CompletedTask : inner.SaveAsync(instance, cancellationToken);
        public ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default) => inner.DeleteAsync(instanceId, cancellationToken);
        public IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);
        public async ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            IInstanceLease lease = await inner.AcquireAsync(instanceId, cancellationToken);
            lock (_leases) { _leases.Add(lease); }
            return new HeldLease(this, lease);
        }

        private sealed class HeldLease(CrashStore owner, IInstanceLease lease) : IInstanceLease
        {
            public string InstanceId => lease.InstanceId;
            public ValueTask DisposeAsync() => owner._crashed ? ValueTask.CompletedTask : lease.DisposeAsync();
        }
    }
}
