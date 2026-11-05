using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public class AgentApprovalTests
{
    [Fact]
    public async Task An_approval_survives_a_restart_and_the_approved_call_then_runs()
    {
        string dir = NewTempDirectory();
        string id;
        var danger = FuncTool.Text("danger", "deleted", approval: true);
        using (var a = new AgentEnv(dir))
        {
            a.Rt.Tools.Add(danger);
            a.Provider.Enqueue(Calls(("c1", "danger", """{"x":"everything"}""")));

            AgentResult r = await Within(a.Rt.RunAgentAsync(Def("a", ["danger"]), null, Go()));

            Assert.Equal(AgentStopReason.WaitingForApproval, r.StopReason);
            Assert.Equal(InstanceStates.WaitingForApproval, r.State);
            PendingApproval pending = Assert.Single(r.PendingApprovals);
            Assert.Equal("c1", pending.ToolCallId);
            Assert.Equal("danger", pending.Tool);
            Assert.Equal(0, danger.Calls);
            id = r.Instance.InstanceId;
        }

        // A new process: new runtime, same directory, same tools registered, a fresh script.
        var again = FuncTool.Text("danger", "deleted", approval: true);
        using var b = new AgentEnv(dir, "b");
        b.Rt.Tools.Add(again);
        b.Provider.Enqueue(Say("It is gone."));

        AgentRun loaded = (await b.Rt.GetAgentAsync(id))!;
        Assert.Equal(InstanceStates.WaitingForApproval, loaded.State);
        Assert.Equal("c1", Assert.Single(loaded.PendingApprovals).ToolCallId);
        AgentRun run = await b.Rt.ApproveAsync(id);
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal(1, again.Calls);
        Assert.Equal("It is gone.", done.Text);
        Assert.Equal(InstanceStates.Waiting, done.State);
        Assert.Empty(done.Instance.PendingApprovals);
        ToolCall call = done.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.Equal("deleted", ToolText(call));
        Assert.False(call.IsError);
        // The model saw the real result on its next turn.
        Assert.Equal("deleted", ToolText(b.Provider.Requests[0].Messages.SelectMany(static m => m.ToolCalls).Single()));
        // Saved state agrees.
        Instance saved = (await b.Rt.Store.LoadAsync(id))!;
        Assert.Empty(saved.PendingApprovals);
        Assert.Equal(1, saved.Messages.Count(static m => m.ToolCalls.Count == 1));
    }

    [Fact]
    public async Task A_denied_call_is_not_run_and_the_model_sees_the_reason()
    {
        using var env = new AgentEnv();
        var danger = FuncTool.Text("danger", "deleted", approval: true);
        env.Rt.Tools.Add(danger);
        env.Provider.Enqueue(Calls(("c1", "danger", "{}")), Say("Understood, I will not."));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["danger"]), null, Go());
        AgentResult held = await Within(run.AwaitAsync());
        Assert.Equal(AgentStopReason.WaitingForApproval, held.StopReason);

        await run.DenyAsync("c1", "too risky");
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal(0, danger.Calls);
        Assert.Equal("Understood, I will not.", done.Text);
        ToolCall call = done.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("too risky", ToolText(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ungated_calls_in_a_turn_run_at_once_and_the_gated_one_waits()
    {
        using var env = new AgentEnv();
        var safe = FuncTool.Text("safe", "s");
        var risky = FuncTool.Text("risky", "r", approval: true);
        env.Rt.Tools.Add(safe).Add(risky);
        env.Provider.Enqueue(Calls(("c1", "risky", "{}"), ("c2", "safe", "{}")), Say("done"));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["safe", "risky"]), null, Go());
        AgentResult held = await Within(run.AwaitAsync());

        Assert.Equal(1, safe.Calls);
        Assert.Equal(0, risky.Calls);
        Assert.Equal("c1", Assert.Single(held.PendingApprovals).ToolCallId);

        await run.ApproveAsync();
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal(1, safe.Calls);
        Assert.Equal(1, risky.Calls);
        // Results stay in call order whichever ran first.
        Assert.Equal(["r", "s"], done.Instance.Messages.Single(m => m.ToolCalls.Count == 2).ToolCalls.Select(ToolText));
    }

    [Fact]
    public async Task Deciding_a_call_that_is_not_pending_is_an_error()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("hi"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        await Within(run.AwaitAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.ApproveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.DenyAsync("nope"));
    }

    [Fact]
    public async Task A_message_sent_while_approvals_are_pending_waits_until_they_are_decided()
    {
        using var env = new AgentEnv();
        var risky = FuncTool.Text("risky", "r", approval: true);
        env.Rt.Tools.Add(risky);
        env.Provider.Enqueue(Calls(("c1", "risky", "{}")), Say("both seen"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["risky"]), null, Go());
        await Within(run.AwaitAsync());

        await run.MessageAsync("by the way");
        Assert.Single(env.Provider.Requests);
        Assert.Equal(InstanceStates.WaitingForApproval, run.State);

        await run.ApproveAsync();
        AgentResult done = await Within(run.AwaitAsync());
        Assert.Equal("both seen", done.Text);
        Assert.Equal("by the way", env.Provider.Requests[1].Messages[^1].GetUserText());
    }

    [Fact]
    public async Task A_hook_can_require_approval_for_a_tool_that_does_not_ask()
    {
        using var env = new AgentEnv();
        var plain = FuncTool.Text("plain", "p");
        env.Rt.Tools.Add(plain);
        env.Rt.Hooks.BeforeToolCall((ctx, _) => new ValueTask<HookDecision>(ctx.Call.Tool == "plain" ? HookDecision.RequireApproval("policy says ask") : HookDecision.Continue), "policy");
        env.Provider.Enqueue(Calls(("c1", "plain", "{}")), Say("ok"));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["plain"]), null, Go());
        AgentResult held = await Within(run.AwaitAsync());

        Assert.Equal(AgentStopReason.WaitingForApproval, held.StopReason);
        Assert.Equal(0, plain.Calls);
        Assert.Contains("policy says ask", held.PendingApprovals[0].Reason, StringComparison.Ordinal);
        await run.ApproveAsync();
        await Within(run.AwaitAsync());
        Assert.Equal(1, plain.Calls);
    }
}

public class AgentHookTests
{
    private static ValueTask<HookDecision> Done(HookDecision d) => new(d);

    [Fact]
    public async Task Block_before_a_tool_call_returns_an_error_result_and_skips_the_tool()
    {
        using var env = new AgentEnv();
        var t = FuncTool.Text("t", "ran");
        env.Rt.Tools.Add(t);
        env.Rt.Hooks.BeforeToolCall((_, _) => Done(HookDecision.Block("not today")), "gate");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal(0, t.Calls);
        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("not today", ToolText(call), StringComparison.Ordinal);
        Assert.Contains("hook", HookKinds(env, r), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Modify_before_a_tool_call_changes_the_inputs_the_tool_sees()
    {
        using var env = new AgentEnv();
        string? seen = null;
        env.Rt.Tools.Add(new FuncTool("t", (i, _, _) =>
        {
            seen = i["x"]!.GetValue<string>();
            return Task.FromResult(ToolResult.FromText("ok"));
        }));
        env.Rt.Hooks.BeforeToolCall((_, _) => Done(HookDecision.ModifyInputs(new JsonObject { ["x"] = "rewritten" }, "redact")), "rewriter");
        env.Provider.Enqueue(Calls(("c1", "t", """{"x":"secret"}""")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal("rewritten", seen);
        Assert.Contains("hook", HookKinds(env, r), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Modify_after_a_tool_call_changes_the_result_the_model_sees()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("t", "card 4111-1111"));
        env.Rt.Hooks.AfterToolCall((_, _) => Done(HookDecision.ModifyResult(ToolResult.FromText("card [redacted]"))), "scrub");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal("card [redacted]", ToolText(env.Provider.Requests[1].Messages.SelectMany(static m => m.ToolCalls).Single()));
        Assert.Equal("card [redacted]", ToolText(r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0]));
    }

    [Fact]
    public async Task Respond_at_a_tool_call_short_circuits_the_tool_with_the_hooks_result()
    {
        using var env = new AgentEnv();
        var t = FuncTool.Text("t", "real");
        env.Rt.Tools.Add(t);
        env.Rt.Hooks.BeforeToolCall((_, _) => Done(HookDecision.Respond(ToolResult.FromText("cached"), "from cache")), "cache");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal(0, t.Calls);
        Assert.Equal("cached", ToolText(r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0]));
    }

    [Fact]
    public async Task Modify_before_inference_changes_the_request_the_provider_gets()
    {
        using var env = new AgentEnv();
        env.Rt.Hooks.BeforeInference((ctx, _) => Done(HookDecision.ModifyRequest(ctx.Request with { System = "Be terse." }, "style")), "style");
        env.Provider.Enqueue(Say("ok"));

        await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        Assert.Equal("Be terse.", env.Provider.Requests[0].System);
    }

    [Fact]
    public async Task Respond_before_inference_answers_without_calling_the_provider()
    {
        using var env = new AgentEnv();
        var canned = new Message { Id = "x", Assistant = [new TextPart("canned answer")] };
        env.Rt.Hooks.BeforeInference((_, _) => Done(HookDecision.Respond(canned, "offline")), "canned");

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        Assert.Empty(env.Provider.Requests);
        Assert.Equal("canned answer", r.Text);
        Assert.Equal(InstanceStates.Waiting, r.State);
    }

    [Fact]
    public async Task Block_before_inference_ends_the_run_in_error_with_a_typed_exception()
    {
        using var env = new AgentEnv();
        env.Rt.Hooks.BeforeInference((_, _) => Done(HookDecision.Block("over policy")), "policy");

        HookBlockedException ex = await Assert.ThrowsAsync<HookBlockedException>(() => Within(env.Rt.RunAgentAsync(Def("a", []), null, Go())));

        Assert.Equal("policy", ex.Hook);
        Assert.Equal("over policy", ex.Reason);
        Assert.Empty(env.Provider.Requests);
    }

    [Fact]
    public async Task Modify_after_inference_replaces_the_message_that_is_kept()
    {
        using var env = new AgentEnv();
        env.Rt.Hooks.AfterInference((_, _) => Done(HookDecision.ModifyMessage(new Message { Id = "y", Assistant = [new TextPart("edited")] }, "edit")), "edit");
        env.Provider.Enqueue(Say("original"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        Assert.Equal("edited", r.Text);
        // The usage of the real call stays on the kept message.
        Assert.NotNull(r.Instance.Messages[^1].Usage);
    }

    [Fact]
    public async Task An_interceptor_that_throws_blocks_the_call()
    {
        using var env = new AgentEnv();
        var t = FuncTool.Text("t", "ran");
        env.Rt.Tools.Add(t);
        env.Rt.Hooks.BeforeToolCall((_, _) => throw new InvalidOperationException("hook bug"), "buggy");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal(0, t.Calls);
        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("buggy", ToolText(call), StringComparison.Ordinal);
        Assert.Contains("error", HookKinds(env, r), StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_observer_that_throws_does_not_stop_the_run()
    {
        using var env = new AgentEnv();
        int seen = 0;
        env.Rt.Hooks.OnMessage((_, _) =>
        {
            seen++;
            throw new InvalidOperationException("observer bug");
        }, "noisy");
        env.Rt.Hooks.OnStateChange((_, _) => throw new InvalidOperationException("observer bug 2"), "noisy2");
        env.Rt.Hooks.OnCost((_, _) => throw new InvalidOperationException("observer bug 3"), "noisy3");
        env.Provider.Enqueue(Say("fine"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        Assert.Equal("fine", r.Text);
        Assert.Equal(InstanceStates.Waiting, r.State);
        Assert.True(seen >= 2);
    }

    [Fact]
    public async Task Host_hooks_run_in_registration_order_and_before_the_tool_hooks_that_follow_them()
    {
        using var env = new AgentEnv();
        var order = new List<string>();
        env.Rt.Tools.Add(FuncTool.Text("t", "r"));
        env.Rt.Hooks.BeforeToolCall((_, _) => { order.Add("one"); return Done(HookDecision.Continue); }, "one");
        env.Rt.Hooks.BeforeToolCall((_, _) => { order.Add("two"); return Done(HookDecision.Continue); }, "two");
        env.Rt.Hooks.AfterToolCall((_, _) => { order.Add("after"); return Done(HookDecision.Continue); }, "after");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Say("ok"));

        await Within(env.Rt.RunAgentAsync(Def("a", ["t"]), null, Go()));

        Assert.Equal(["one", "two", "after"], order);
    }

    [Fact]
    public async Task A_stream_event_hook_observes_events_and_the_message_is_the_same()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("streamed"));
        int events = 0;
        env.Rt.Hooks.Add(new CountingStreamHook(() => events++));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        Assert.True(events >= 3);
        Assert.Equal("streamed", r.Text);
    }

    [Fact]
    public async Task A_before_compaction_hook_can_block_the_compaction()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("t", "result"));
        env.Rt.Hooks.BeforeCompaction((_, _) => Done(HookDecision.Block("keep everything")), "keeper");
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Calls(("c2", "t", "{}")), Calls(("c3", "t", "{}")), Calls(("c4", "t", "{}")), Say("end"));
        AgentDefinition def = Def("a", ["t"], "contextStrategy: { type: clearToolResults, triggerTokens: 100, keepLast: 1 }");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, null, Go()));

        Assert.Empty(r.Instance.Compactions);
        Assert.DoesNotContain(r.Instance.Messages.SelectMany(static m => m.ToolCalls), static c => ToolText(c).Contains("cleared", StringComparison.Ordinal));
    }

    private static List<string> HookKinds(AgentEnv env, AgentResult r) =>
        [.. OneLlm.Records.RecordReader.ReadFile(env.Records.PathOf(r.Instance.InstanceId)).Select(static e => e.Kind)];

    private sealed class CountingStreamHook(Action onEvent) : IHook
    {
        public string Name => "counter";

        public HookPoints Points => HookPoints.StreamEvent;

        public void OnStreamEvent(HookContext context, in StreamEvent e) => onEvent();
    }
}
