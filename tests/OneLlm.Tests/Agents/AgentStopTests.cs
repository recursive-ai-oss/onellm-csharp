using OneLlm.Agents;
using OneLlm.Instances;
using OneLlm.Messages;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public class AgentStopTests
{
    [Fact]
    public async Task Segment_cancel_keeps_its_existing_idle_behavior()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("idle"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        await Within(run.AwaitAsync());
        await run.CancelAsync();
        Assert.Equal(InstanceStates.Waiting, (await env.Rt.Store.LoadAsync(run.InstanceId))!.State);
    }

    [Fact]
    public async Task Explicit_stop_also_settles_active_work_before_saving()
    {
        using var env = new AgentEnv();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Rt.Tools.Add(new FuncTool("hang", async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OneLlm.Tools.ToolResult.FromText("never");
        }));
        env.Provider.Enqueue(Calls(("call", "hang", "{}")));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["hang"]), null, Go());
        await Within(entered.Task);
        await Within(run.StopAsync());
        Assert.Equal(InstanceStates.Stopped, (await env.Rt.Store.LoadAsync(run.InstanceId))!.State);
        Assert.True(run.Instance.ExtensionData!["agentExplicitlyStopped"] is true);
    }

    [Fact]
    public async Task Explicit_stop_disarms_monitors_drops_late_alerts_and_allows_explicit_resume()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("idle"), Say("resumed"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        await Within(run.AwaitAsync());
        LiveAgent live = (await env.Rt.AgentHost.GetAsync(run.InstanceId, CancellationToken.None))!;
        AgentMonitor monitor = env.Rt.AgentHost.Arm(run.InstanceId, live, ContextThreshold.Parse("400k"), 0, false, null);
        await run.StopAsync();
        await run.StopAsync();
        Assert.Null(env.Rt.AgentHost.FindMonitor(monitor.Id));
        await live.DeliverAsync("late alert", new MessageSource { Kind = "runtime", Reason = "monitor" }, CancellationToken.None);
        Assert.Equal(InstanceStates.Stopped, run.State);
        Assert.Equal(InstanceStates.Stopped, (await env.Rt.Store.LoadAsync(run.InstanceId))!.State);
        Assert.Single(env.Provider.Requests);
        Assert.DoesNotContain(run.Instance.Messages, m => m.GetUserText().Contains("late alert", StringComparison.Ordinal));
        await run.MessageAsync("hello again");
        AgentResult result = await Within(run.AwaitAsync());
        Assert.Equal("resumed", result.Text);
        Assert.Equal(InstanceStates.Waiting, result.State);
    }

    [Fact]
    public async Task Idle_approval_stop_is_durable_and_preserves_the_approval_gate()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("held", "ok", approval: true));
        env.Provider.Enqueue(Calls(("call", "held", "{}")), Say("done"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["held"]), null, Go());
        Assert.Equal(InstanceStates.WaitingForApproval, (await Within(run.AwaitAsync())).State);
        await run.StopAsync();
        Assert.Equal(InstanceStates.Stopped, (await env.Rt.Store.LoadAsync(run.InstanceId))!.State);
        Assert.Single(run.PendingApprovals);
        await run.MessageAsync("continue when approved");
        Assert.Equal(InstanceStates.Stopped, run.State);
        await run.ApproveAsync();
        Assert.Equal(InstanceStates.Waiting, (await Within(run.AwaitAsync())).State);
    }

    [Fact]
    public async Task Saved_explicit_stop_is_not_recovered_and_late_monitor_alerts_stay_suppressed()
    {
        string directory = NewTempDirectory();
        try
        {
            using var first = new AgentEnv(directory);
            first.Provider.Enqueue(Say("idle"));
            AgentRun run = await first.Rt.StartAgentAsync(Def("a", []), null, Go());
            await Within(run.AwaitAsync());
            await run.StopAsync();
            using var second = new AgentEnv(directory, "next-");
            second.Rt.ResumeInterruptedAgents = true;
            Assert.Empty(await second.Rt.ResumeInterruptedAgentsAsync());
            LiveAgent loaded = (await second.Rt.AgentHost.GetAsync(run.InstanceId, CancellationToken.None))!;
            await loaded.DeliverAsync("late", new MessageSource { Kind = "runtime", Reason = "monitor" }, CancellationToken.None);
            Assert.Empty(second.Provider.Requests);
            Assert.Equal(InstanceStates.Stopped, loaded.Instance.State);
        }
        finally
        {
            System.IO.Directory.Delete(directory, true);
        }
    }
}
