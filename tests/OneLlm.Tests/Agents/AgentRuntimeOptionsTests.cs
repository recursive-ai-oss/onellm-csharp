using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Messages;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public class AgentRuntimeOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_children_use_independent_tools_and_runtime_grants_only_when_enabled(bool enabled)
    {
        using var env = new AgentEnv(records: false);
        env.Rt.IndependentSubAgentPolicy = enabled;
        env.Rt.Capabilities = Capabilities.Files(env.Directory) + Capabilities.Shell;
        string path = Path.Combine(env.Directory, "child.yaml");
        await File.WriteAllTextAsync(path, "name: child\nmodel: demo\nsystemPrompt: CHILD\nreferences:\n  - tools: [probe, task_complete]\n");
        ICapabilities? observed = null;
        env.Rt.Tools.Add(new FuncTool("probe", (_, context, _) =>
        {
            observed = context.Capabilities;
            return Task.FromResult(ToolResult.FromText("ok"));
        }));
        env.Provider.Fallback = request => (request.System ?? "").Contains("CHILD", StringComparison.Ordinal)
            ? request.Messages.Any(static m => m.ToolCalls.Count > 0) ? Complete() : Calls(("probe", "probe", "{}"))
            : request.Messages.Any(static m => m.ToolCalls.Count > 0) ? Say("parent done")
            : Calls(("spawn", "run_agent_async", new JsonObject { ["agentSwarmUri"] = path, ["prompt"] = "go" }.ToJsonString()));

        AgentResult result = await Within(env.Rt.RunAgentAsync(Def("parent", ["run_agent_async"]), null, Go(caps: Capabilities.Files(env.Directory))));
        ToolCall spawn = result.Instance.Messages.SelectMany(static m => m.ToolCalls).Single();
        Assert.True(spawn.IsError == !enabled, string.Join(" ", spawn.Result.OfType<TextPart>().Select(static t => t.Text)));
        if (enabled)
        {
            await foreach (var child in env.Rt.Store.ListAsync(new() { CallerInstanceId = result.Instance.InstanceId }))
            {
                await Within((await env.Rt.GetAgentAsync(child.InstanceId))!.AwaitAsync());
            }
            Assert.NotNull(observed);
            Assert.True(observed.ShellEnabled);
            Assert.NotEmpty(observed.WriteRoots);
        }
        else
        {
            Assert.Null(observed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_childs_grants_stay_bounded_by_its_parent_by_default(bool enabled)
    {
        using var env = new AgentEnv(records: false);
        env.Rt.IndependentSubAgentPolicy = enabled;
        env.Rt.Capabilities = Capabilities.Files(env.Directory) + Capabilities.Shell;
        env.Provider.Enqueue(Say("rest"));
        AgentResult parent = await env.Rt.RunAgentAsync(Def("parent", []), null, Go(caps: Capabilities.ReadOnlyFiles(env.Directory)));
        ICapabilities? seen = null;
        env.Rt.Tools.Add(new FuncTool("probe", (_, ctx, _) => { seen = ctx.Capabilities; return Task.FromResult(ToolResult.FromText("ok")); }));
        env.Provider.Enqueue(Calls(("p", "probe", "{}")), Say("done"));
        await env.Rt.RunAgentAsync(Def("child", ["probe"]), null, new AgentStartOptions { Prompt = "go", SpawnerInstanceId = parent.Instance.InstanceId });
        Assert.NotNull(seen);
        Assert.Equal(enabled, seen.ShellEnabled);
        Assert.Equal(enabled, seen.WriteRoots.Count > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_addition_and_removal_are_observed_on_the_next_turn_only_when_enabled(bool enabled)
    {
        using var env = new AgentEnv(records: false);
        env.Rt.RefreshAgentToolsOnRegistryChange = enabled;
        var late = FuncTool.Text("late", "arrived");
        var old = FuncTool.Text("old", "old");
        env.Rt.Tools.AddRoutine("live", old);
        env.Rt.Tools.Add(new FuncTool("change", (_, _, _) =>
        {
            env.Rt.Tools.AddRoutine("live", late);
            Assert.True(env.Rt.Tools.Remove("old"));
            return Task.FromResult(ToolResult.FromText("changed"));
        }));
        env.Provider.Enqueue(Calls(("c", "change", "{}")), Calls(("l", "late", "{}"), ("o", "old", "{}")), Say("done"));
        var definition = OneLlm.Definitions.AgentDefinition.Parse("name: live\nmodel: demo\nreferences:\n  - routine: live\n  - tools: [change]\n");
        AgentResult result = await env.Rt.RunAgentAsync(definition, null, Go());
        Assert.Equal(enabled ? 1 : 0, late.Calls);
        Assert.Equal(enabled ? 0 : 1, old.Calls);
        Assert.Equal(enabled, env.Provider.Requests[1].Tools.Any(static t => t.Name == "late"));
        Assert.Equal(!enabled, env.Provider.Requests[1].Tools.Any(static t => t.Name == "old"));
        Assert.Equal(3, result.Instance.Messages.SelectMany(static m => m.ToolCalls).Count());
    }

    [Fact]
    public async Task Policy_and_refresh_options_do_not_grant_shell_access()
    {
        using var env = new AgentEnv(records: false);
        Assert.False(env.Rt.IndependentSubAgentPolicy);
        Assert.False(env.Rt.RefreshAgentToolsOnRegistryChange);
        Assert.False(env.Rt.ResumeInterruptedAgents);
        await Assert.ThrowsAsync<BadDefinitionException>(() => env.Rt.StartAgentAsync(Def("blocked", ["execute_terminal_command"]), null, Go()));

        env.Rt.IndependentSubAgentPolicy = true;
        env.Rt.RefreshAgentToolsOnRegistryChange = true;
        await Assert.ThrowsAsync<BadDefinitionException>(() => env.Rt.StartAgentAsync(Def("still-blocked", ["execute_terminal_command"]), null, Go()));
    }

    [Fact]
    public void Registry_version_advances_for_changes_but_not_absent_removal()
    {
        var registry = new ToolRegistry();
        long first = registry.Version;
        registry.Add(FuncTool.Text("custom", "ok"));
        Assert.True(registry.Version > first);
        long added = registry.Version;
        registry.Add(FuncTool.Text("custom", "replacement"));
        Assert.True(registry.Version > added);
        Assert.True(registry.Remove("custom"));
        long removed = registry.Version;
        Assert.False(registry.Remove("custom"));
        Assert.Equal(removed, registry.Version);
    }
}
