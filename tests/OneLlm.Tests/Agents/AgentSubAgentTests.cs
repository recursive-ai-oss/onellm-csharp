using System.Text.RegularExpressions;
using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public partial class AgentSubAgentTests
{
    [GeneratedRegex(@"Instance Id: (\S+)")]
    private static partial Regex InstanceIdPattern();

    private static string ChildIdFrom(ChatRequest req)
    {
        string text = string.Concat(req.Messages.SelectMany(static m => m.ToolCalls).Where(static c => c.Tool == "run_agent_async").Select(ToolText));
        return InstanceIdPattern().Match(text).Groups[1].Value;
    }

    /// <summary>Routes a request to the parent's or the child's script by a marker in the system prompt.</summary>
    private static void Route(ScriptedProvider p, Func<ChatRequest, ScriptStep> parent, Queue<ScriptStep> child)
    {
        p.Fallback = req =>
        {
            if ((req.System ?? "").Contains("CHILD", StringComparison.Ordinal))
            {
                lock (child)
                {
                    return child.Dequeue();
                }
            }

            return parent(req);
        };
    }

    [Fact]
    public async Task A_parent_spawns_a_child_awaits_it_and_the_cost_rolls_up()
    {
        using var env = new AgentEnv();
        var seenRoots = new List<string>();
        string? seenCaller = null;
        env.Rt.Tools.Add(new FuncTool("whoami", (_, ctx, _) =>
        {
            seenRoots.AddRange(ctx.Capabilities.FileRoots);
            seenCaller = ctx.CallerInstanceId;
            return Task.FromResult(ToolResult.FromText("me"));
        }));
        env.Rt.AgentDefinitions.Add("child", Def("child", ["whoami", "task_complete"], system: "CHILD agent."));
        string rootA = Path.Combine(Path.GetTempPath(), "rootA"), rootB = Path.Combine(Path.GetTempPath(), "rootB");
        env.Rt.Capabilities = new TestCapabilities(rootA, rootB);
        var child = new Queue<ScriptStep>([Calls(("k1", "whoami", "{}")), Complete("child result")]);
        Route(
            env.Provider,
            req => req.Messages.Count(static m => m.ToolCalls.Count > 0) switch
            {
                0 => Calls(("p1", "run_agent_async", """{"agentSwarmUri":"child","prompt":"do the child work"}""")),
                1 => Calls(("p2", "await_agent", $$"""{"ids":["{{ChildIdFrom(req)}}"]}""")),
                _ => Complete("parent done"),
            },
            child);

        // The parent holds only rootA, although the runtime allows both.
        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("parent", ["run_agent_async", "await_agent", "task_complete"]), null, Go(caps: new TestCapabilities(rootA))));

        Assert.Equal("parent done", r.Text);
        // A child gets at most what its parent holds.
        Assert.Equal([Path.GetFullPath(rootA)], seenRoots);
        Message awaited = r.Instance.Messages.First(static m => m.ToolCalls.Any(static c => c.Tool == "await_agent"));
        string awaitText = ToolText(awaited.ToolCalls[0]);
        Assert.Contains("## Agent Result", awaitText, StringComparison.Ordinal);
        Assert.Contains("child result", awaitText, StringComparison.Ordinal);
        Assert.Contains("**Cost:** $0.002400", awaitText, StringComparison.Ordinal);

        string childId = ChildIdFrom(env.Provider.Requests.Last(static q => !(q.System ?? "").Contains("CHILD", StringComparison.Ordinal)));
        Instance savedChild = (await env.Rt.Store.LoadAsync(childId))!;
        Assert.Equal(r.Instance.InstanceId, savedChild.SpawnerInstanceId);
        Assert.Equal(r.Instance.InstanceId, savedChild.CallerInstanceId);
        Assert.Equal("p1", savedChild.CallerToolCallId);
        Assert.Equal("do the child work", savedChild.DelegationPrompt);
        Assert.Equal(r.Instance.InstanceId, seenCaller);
        Assert.Equal(InstanceStates.Stopped, savedChild.State);

        // 3 parent turns and 2 child turns, 0.0012 each.
        Assert.Equal(0.0036m, r.Cost.Total);
        Assert.Equal(0.0060m, r.SubtreeCost);
        Assert.Equal(0.0060m, await env.Rt.GetSubtreeCostAsync(r.Instance.InstanceId));
        Assert.Equal(0.0024m, await env.Rt.GetSubtreeCostAsync(childId));
    }

    [Fact]
    public async Task A_child_can_not_hold_more_than_a_live_parent_even_when_it_asks_for_more()
    {
        using var env = new AgentEnv();
        var gate = new TaskCompletionSource();
        var inGate = new TaskCompletionSource();
        env.Rt.Tools.Add(new FuncTool("hold", async (_, _, _) =>
        {
            inGate.TrySetResult();
            await gate.Task;
            return ToolResult.FromText("held");
        }));
        ICapabilities? childCaps = null;
        env.Rt.Tools.Add(new FuncTool("peek", (_, ctx, _) =>
        {
            childCaps = ctx.Capabilities;
            return Task.FromResult(ToolResult.FromText("peeked"));
        }));
        string a = Path.Combine(Path.GetTempPath(), "capA"), b = Path.Combine(Path.GetTempPath(), "capB");
        env.Rt.Capabilities = new TestCapabilities(a, b) { ShellEnabled = true };
        env.Provider.Fallback = req => (req.System ?? "").Contains("CHILD", StringComparison.Ordinal)
            ? req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Say("done") : Calls(("k1", "peek", "{}"))
            : req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Say("done") : Calls(("p1", "hold", "{}"));

        AgentRun parent = await env.Rt.StartAgentAsync(Def("parent", ["hold"]), null, Go(caps: new TestCapabilities(a)));
        await Within(inGate.Task);
        // The child is started by the host with a wide request and the parent's id as spawner.
        AgentRun child = await env.Rt.StartAgentAsync(
            Def("child", ["peek"], system: "CHILD"),
            null,
            new AgentStartOptions { Prompt = "x", Capabilities = new TestCapabilities(a, b) { ShellEnabled = true }, SpawnerInstanceId = parent.InstanceId });
        await Within(child.AwaitAsync());
        gate.SetResult();
        await Within(parent.AwaitAsync());

        Assert.NotNull(childCaps);
        Assert.Equal([Path.GetFullPath(a)], childCaps!.FileRoots);
        Assert.False(childCaps.ShellEnabled);
    }

    [Fact]
    public async Task Run_agent_async_reads_a_definition_file_only_under_the_callers_file_roots()
    {
        using var env = new AgentEnv();
        string root = Path.Combine(AgentEnv.NewTempDirectory(), "root");
        string outside = Path.Combine(AgentEnv.NewTempDirectory(), "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        const string Yaml = "name: filed\nmodel: demo\nsystemPrompt: CHILD\n";
        File.WriteAllText(Path.Combine(root, "inside.yaml"), Yaml);
        File.WriteAllText(Path.Combine(outside, "secret.yaml"), Yaml);
        File.CreateSymbolicLink(Path.Combine(root, "link.yaml"), Path.Combine(outside, "secret.yaml"));
        env.Rt.Capabilities = new TestCapabilities(root, outside);
        env.Provider.Fallback = _ => Say("child says hi");
        var tool = new RunAgentAsyncTool();

        async Task<ToolResult> Call(string reference) => await ToolInvoker.InvokeAsync(
            tool,
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse($$"""{"agentSwarmUri":"{{reference.Replace("\\", "\\\\", StringComparison.Ordinal)}}","prompt":"hello"}""")!,
            new ToolContext("parent", "tc1", env.Clock) { Runtime = env.Rt, Capabilities = new TestCapabilities(root) },
            CancellationToken.None);

        ToolResult ok = await Call(Path.Combine(root, "inside.yaml"));
        Assert.False(ok.IsError);
        Assert.Contains("Agent started", ToolText(ok), StringComparison.Ordinal);

        ToolResult dotdot = await Call(Path.Combine(root, "..", "outside", "secret.yaml"));
        Assert.True(dotdot.IsError);
        Assert.Contains("Failed to start agent", ToolText(dotdot), StringComparison.Ordinal);

        ToolResult linked = await Call(Path.Combine(root, "link.yaml"));
        Assert.True(linked.IsError);

        ToolResult absolute = await Call(Path.Combine(outside, "secret.yaml"));
        Assert.True(absolute.IsError);

        // The child started above keeps saving its instance in the background; let it finish before the
        // environment deletes its directory.
        string childId = ToolText(ok).Split("Instance Id: ", 2)[1].Split('\n', 2)[0].Trim();
        AgentRun? child = await env.Rt.GetAgentAsync(childId);
        if (child is not null)
        {
            await child.AwaitAsync();
        }
    }

    [Fact]
    public async Task Await_agent_returns_a_finished_agents_result_and_lists_the_ones_still_running()
    {
        using var env = new AgentEnv();
        var gate = new TaskCompletionSource();
        var inGate = new TaskCompletionSource();
        env.Rt.Tools.Add(new FuncTool("hold", async (_, _, _) =>
        {
            inGate.TrySetResult();
            await gate.Task;
            return ToolResult.FromText("held");
        }));
        env.Provider.Fallback = req => (req.System ?? "").Contains("SLOW", StringComparison.Ordinal)
            ? req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Complete("slow done") : Calls(("s1", "hold", "{}"))
            : Complete("quick done");

        AgentRun slow = await env.Rt.StartAgentAsync(Def("slow", ["hold", "task_complete"], system: "SLOW"), null, Go());
        AgentRun quick = await env.Rt.StartAgentAsync(Def("quick", ["task_complete"], system: "QUICK"), null, Go());
        await Within(inGate.Task);
        await Within(quick.AwaitAsync());

        ToolResult one = await AwaitTool(env, [slow.InstanceId, quick.InstanceId], "one");
        string text = ToolText(one);
        Assert.Contains("(first to finish)", text, StringComparison.Ordinal);
        Assert.Contains("quick done", text, StringComparison.Ordinal);
        Assert.Contains("### Still running", text, StringComparison.Ordinal);
        Assert.Contains(slow.InstanceId, text, StringComparison.Ordinal);

        gate.SetResult();
        ToolResult all = await AwaitTool(env, [slow.InstanceId, quick.InstanceId], "all");
        Assert.Contains("slow done", ToolText(all), StringComparison.Ordinal);
        Assert.DoesNotContain("Still running", ToolText(all), StringComparison.Ordinal);
    }

    private static async Task<ToolResult> AwaitTool(AgentEnv env, string[] ids, string mode) => await Within(ToolInvoker.InvokeAsync(
        new AwaitAgentTool(),
        new System.Text.Json.Nodes.JsonObject { ["ids"] = new System.Text.Json.Nodes.JsonArray([.. ids.Select(static i => (System.Text.Json.Nodes.JsonNode?)i)]), ["mode"] = mode },
        new ToolContext("caller", "tc", env.Clock) { Runtime = env.Rt },
        CancellationToken.None).AsTask());

    [Fact]
    public async Task Message_agent_delivers_to_a_waiting_instance_and_resumes_it()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("first"), Say("after the message"));
        AgentRun target = await env.Rt.StartAgentAsync(Def("t", []), null, Go());
        await Within(target.AwaitAsync());
        Assert.Equal(InstanceStates.Waiting, target.State);

        ToolResult sent = await ToolInvoker.InvokeAsync(
            new MessageAgentTool(),
            new System.Text.Json.Nodes.JsonObject { ["agentId"] = target.InstanceId, ["prompt"] = "keep going" },
            new ToolContext("sender", "tc9", env.Clock) { Runtime = env.Rt },
            CancellationToken.None);

        Assert.False(sent.IsError);
        Assert.Contains($"Agent messaged: {target.InstanceId}", ToolText(sent), StringComparison.Ordinal);
        AgentResult after = await Within(target.AwaitAsync());
        Assert.Equal("after the message", after.Text);
        Message delivered = env.Provider.Requests[1].Messages[^1];
        Assert.Equal("keep going", delivered.GetUserText());
        Assert.Equal("agent", delivered.Source!.Kind);
        Assert.Equal("sender", delivered.Source.InstanceId);
    }

    [Fact]
    public async Task Message_agent_with_no_arguments_gives_the_pointer_text_and_unknown_ids_fail()
    {
        using var env = new AgentEnv();
        ToolResult empty = await ToolInvoker.InvokeAsync(new MessageAgentTool(), new System.Text.Json.Nodes.JsonObject(), new ToolContext("s", "t", env.Clock) { Runtime = env.Rt }, CancellationToken.None);
        Assert.True(empty.IsError);
        Assert.Contains("run_agent_async", ToolText(empty), StringComparison.Ordinal);

        ToolResult missing = await ToolInvoker.InvokeAsync(
            new MessageAgentTool(),
            new System.Text.Json.Nodes.JsonObject { ["agentId"] = "nope", ["prompt"] = "hi" },
            new ToolContext("s", "t", env.Clock) { Runtime = env.Rt },
            CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("nope", ToolText(missing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_and_query_tools_report_instances_in_the_store()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("hello"));
        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("lister", []), null, Go()));
        var ctx = new ToolContext("x", "t", env.Clock) { Runtime = env.Rt };

        ToolResult list = await ToolInvoker.InvokeAsync(new ListAgentInstancesTool(), new System.Text.Json.Nodes.JsonObject { ["name"] = "list" }, ctx, CancellationToken.None);
        Assert.Contains(r.Instance.InstanceId, list.Parts.OfType<JsonPart>().Single().Value.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, list.Parts.OfType<JsonPart>().Single().Value["count"]!.GetValue<int>());
    }
}

/// <summary>Monitors: the four conditions, OR-combined, firing once, never killing the agent.</summary>
public class AgentMonitorTests
{
    private sealed class Gated : IDisposable
    {
        public Gated(AgentEnv env)
        {
            Env = env;
            env.Rt.Tools.Add(new FuncTool("gate", async (_, _, _) =>
            {
                Entered.TrySetResult();
                await Open.Task;
                return ToolResult.FromText("through");
            }));
        }

        public AgentEnv Env { get; }

        public TaskCompletionSource Entered { get; } = new();

        public TaskCompletionSource Open { get; } = new();

        public void Dispose() => Open.TrySetResult();
    }

    private static async Task<(AgentRun Run, LiveAgent Agent)> StartTargetAsync(AgentEnv env, int turns)
    {
        // Each turn calls the gate tool; the last one completes.
        for (int i = 0; i < turns; i++)
        {
            env.Provider.Enqueue(Calls(($"g{i}", "gate", "{}")));
        }

        env.Provider.Enqueue(Complete("finished"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("target", ["gate", "task_complete"]), null, Go());
        LiveAgent agent = (await env.Rt.AgentHost.GetAsync(run.InstanceId, CancellationToken.None))!;
        return (run, agent);
    }

    private static List<string> Fired(AgentEnv env)
    {
        var fired = new List<string>();
        env.Rt.AgentEvents += e =>
        {
            if (e.Kind == AgentEventKind.MonitorFired)
            {
                lock (fired)
                {
                    fired.Add(e.Detail ?? "");
                }
            }
        };
        return fired;
    }

    [Theory]
    [InlineData("1000", null)]
    [InlineData("1%", null)]
    public async Task A_context_threshold_fires_when_the_context_reaches_it(string spec, string? unused)
    {
        _ = unused;
        using var env = new AgentEnv();
        using var g = new Gated(env);
        var fired = Fired(env);
        (AgentRun run, LiveAgent agent) = await StartTargetAsync(env, 3);
        await Within(g.Entered.Task);

        AgentMonitor m = env.Rt.AgentHost.Arm(null, agent, ContextThreshold.Parse(spec), 0, false, null);
        g.Open.SetResult();
        MonitorFire fire = await Within(m.FiredTask.Task);
        await Within(run.AwaitAsync());

        Assert.Equal("contextAt", fire.Condition);
        Assert.False(m.IsArmed);
        Assert.Single(fired);
    }

    [Fact]
    public async Task A_turn_count_fires_after_that_many_more_turns_and_only_once()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        var fired = Fired(env);
        (AgentRun run, LiveAgent agent) = await StartTargetAsync(env, 3);
        await Within(g.Entered.Task);

        AgentMonitor m = env.Rt.AgentHost.Arm(null, agent, null, 2, false, null);
        g.Open.SetResult();
        MonitorFire fire = await Within(m.FiredTask.Task);
        await Within(run.AwaitAsync());

        Assert.Equal("turns", fire.Condition);
        Assert.Single(fired);
    }

    [Fact]
    public async Task On_finish_fires_when_the_agent_rests()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        (AgentRun run, LiveAgent agent) = await StartTargetAsync(env, 1);
        await Within(g.Entered.Task);

        AgentMonitor m = env.Rt.AgentHost.Arm(null, agent, null, 0, true, null);
        Assert.True(m.IsArmed);
        g.Open.SetResult();
        MonitorFire fire = await Within(m.FiredTask.Task);
        await Within(run.AwaitAsync());

        Assert.Equal("onFinish", fire.Condition);
        Assert.Equal(InstanceStates.Stopped, fire.Snapshot.State);
    }

    [Fact]
    public async Task Elapsed_time_fires_on_the_clock_and_never_stops_the_agent()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        (AgentRun run, LiveAgent agent) = await StartTargetAsync(env, 1);
        await Within(g.Entered.Task);

        AgentMonitor m = env.Rt.AgentHost.Arm(null, agent, null, 0, false, TimeSpan.FromMinutes(30));
        Assert.True(m.IsArmed);
        env.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(m.IsArmed);
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        MonitorFire fire = await Within(m.FiredTask.Task);

        Assert.Equal("elapsed", fire.Condition);
        // The agent is still running its tool, and finishes normally once released.
        Assert.True(agent.IsRunning);
        g.Open.SetResult();
        AgentResult r = await Within(run.AwaitAsync());
        Assert.Equal("finished", r.Text);
    }

    [Fact]
    public async Task The_first_of_several_conditions_wins_and_disarms_the_rest()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        var fired = Fired(env);
        (AgentRun run, LiveAgent agent) = await StartTargetAsync(env, 2);
        await Within(g.Entered.Task);

        AgentMonitor m = env.Rt.AgentHost.Arm(null, agent, ContextThreshold.Parse("1000"), 2, true, TimeSpan.FromHours(1));
        g.Open.SetResult();
        MonitorFire fire = await Within(m.FiredTask.Task);
        await Within(run.AwaitAsync());
        env.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal("contextAt", fire.Condition);
        Assert.Single(fired);
    }

    [Fact]
    public async Task A_fired_monitor_sends_the_watcher_a_runtime_message_that_resumes_it()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        env.Provider.Fallback = req => (req.System ?? "").Contains("WATCHER", StringComparison.Ordinal)
            ? Say("noted")
            : req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Complete("finished") : Calls(("g0", "gate", "{}"));
        AgentRun watcher = await env.Rt.StartAgentAsync(Def("watcher", [], system: "WATCHER"), null, Go());
        await Within(watcher.AwaitAsync());
        Assert.Equal(InstanceStates.Waiting, watcher.State);

        AgentRun target = await env.Rt.StartAgentAsync(Def("target", ["gate", "task_complete"]), null, Go());
        LiveAgent agent = (await env.Rt.AgentHost.GetAsync(target.InstanceId, CancellationToken.None))!;
        await Within(g.Entered.Task);
        AgentMonitor m = env.Rt.AgentHost.Arm(watcher.InstanceId, agent, null, 0, true, null);
        g.Open.SetResult();
        await Within(m.FiredTask.Task);
        await Within(target.AwaitAsync());

        // The alert starts the watcher's next segment.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (env.Provider.Requests.Count(static r => (r.System ?? "").Contains("WATCHER", StringComparison.Ordinal)) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        ChatRequest second = env.Provider.Requests.Where(static r => (r.System ?? "").Contains("WATCHER", StringComparison.Ordinal)).ElementAt(1);
        Message alert = second.Messages[^1];
        Assert.Contains(m.Id, alert.GetUserText(), StringComparison.Ordinal);
        Assert.Equal("runtime", alert.Source!.Kind);
    }

    [Fact]
    public async Task Monitor_agent_tool_reports_a_snapshot_when_given_no_conditions()
    {
        using var env = new AgentEnv();
        using var g = new Gated(env);
        (AgentRun run, _) = await StartTargetAsync(env, 1);
        await Within(g.Entered.Task);

        ToolResult r = await ToolInvoker.InvokeAsync(
            new MonitorAgentTool(),
            new System.Text.Json.Nodes.JsonObject { ["instanceId"] = run.InstanceId },
            new ToolContext("other", "t", env.Clock) { Runtime = env.Rt },
            CancellationToken.None);

        Assert.False(r.IsError);
        Assert.Contains(run.InstanceId, string.Concat(r.Parts.OfType<TextPart>().Select(static p => p.Text)) + string.Concat(r.Parts.OfType<JsonPart>().Select(static p => p.Value.ToJsonString())), StringComparison.Ordinal);
        g.Open.SetResult();
        await Within(run.AwaitAsync());
    }
}
