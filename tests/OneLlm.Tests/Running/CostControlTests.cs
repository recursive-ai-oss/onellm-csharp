using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.Tests.Running;

/// <summary>Spend limits are read at each call, and cost hooks see simple runs as well as agent runs.</summary>
public sealed class CostControlTests
{
    // demo-1: $1/M input, $2/M output, 1000 max output tokens, so any call's worst case is just over $0.002.

    [Fact]
    public async Task Raising_the_runtime_spend_cap_after_a_call_takes_effect()
    {
        using var h = new Harness();
        h.Runtime.SpendCap = 0.001m;
        h.Provider.EnqueueText("never sent");
        await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }));

        h.Runtime.SpendCap = 10m;
        AskResult r = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });
        Assert.Equal("never sent", r.Text);
    }

    [Fact]
    public async Task Setting_the_runtime_spend_cap_after_a_call_takes_effect()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("first");
        await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        h.Runtime.SpendCap = 0.001m;
        h.Provider.EnqueueText("never sent");
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }));
        Assert.Equal(BudgetKind.SpendCap, e.Kind);
    }

    [Fact]
    public async Task Setting_max_cost_per_call_after_a_call_takes_effect()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("first");
        await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        h.Runtime.MaxCostPerCall = 0.001m;
        h.Provider.EnqueueText("never sent");
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }));
        Assert.Equal(BudgetKind.PerCall, e.Kind);
    }

    [Fact]
    public async Task On_cost_fires_for_a_plain_ask()
    {
        using var h = new Harness();
        var seen = new List<CostHookContext>();
        h.Runtime.Hooks.OnCost((c, _) => { seen.Add(c); return ValueTask.CompletedTask; });
        h.Provider.Enqueue(ScriptStep.TextWithUsage("ok", 1000, 500));

        AskResult r = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        CostHookContext c = Assert.Single(seen);
        Assert.Equal("inference", c.Source);
        Assert.Equal(r.Cost.Total, c.Cost.Total);
    }

    [Fact]
    public async Task Inference_hooks_fire_for_a_plain_ask()
    {
        using var h = new Harness();
        int before = 0, after = 0;
        h.Runtime.Hooks.BeforeInference((_, _) => { before++; return ValueTask.FromResult(HookDecision.Continue); });
        h.Runtime.Hooks.AfterInference((_, _) => { after++; return ValueTask.FromResult(HookDecision.Continue); });
        h.Provider.EnqueueText("ok");

        await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        Assert.Equal(1, before);
        Assert.Equal(1, after);
    }

    private static AskRequest Hi => new() { Model = "demo", Prompt = "hi" };

    [Fact]
    public async Task Lowering_and_clearing_the_runtime_spend_cap_take_effect_and_spent_keeps_adding_up()
    {
        using var h = new Harness();
        h.Runtime.SpendCap = 10m;
        h.Provider.Enqueue(ScriptStep.TextWithUsage("a", 1000, 500));
        await h.Runtime.AskAsync(Hi);
        Assert.Equal(0.002m, h.Runtime.Spent);

        h.Runtime.SpendCap = 0.003m; // 0.002 spent + a worst case of just over 0.002
        await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(Hi));

        h.Runtime.SpendCap = null;
        h.Provider.Enqueue(ScriptStep.TextWithUsage("b", 1000, 500));
        await h.Runtime.AskAsync(Hi);
        Assert.Equal(0.004m, h.Runtime.Spent);
    }

    [Fact]
    public async Task Setting_the_instance_spend_cap_after_a_call_takes_effect()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("first");
        await h.Runtime.AskAsync(Hi);

        h.Runtime.InstanceSpendCap = 0.001m;
        h.Provider.EnqueueText("never sent");
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(Hi));
        Assert.Equal(BudgetKind.SpendCap, e.Kind);
    }

    [Fact]
    public async Task The_runtime_cap_applies_to_embeddings_as_it_changes()
    {
        using var h = new Harness();
        h.Runtime.SpendCap = 10m;
        h.Provider.Enqueue(ScriptStep.TextWithUsage("a", 1000, 500));
        await h.Runtime.AskAsync(Hi);
        h.Runtime.SpendCap = 0.001m;
        await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.EmbedAsync("embed-1", ["x"]));
    }

    [Fact]
    public async Task On_cost_fires_for_a_stream_a_chat_turn_and_each_script_prompt()
    {
        using var h = new Harness();
        var seen = new List<CostHookContext>();
        h.Runtime.Hooks.OnCost((c, _) => { seen.Add(c); return ValueTask.CompletedTask; });

        h.Provider.Enqueue(ScriptStep.TextWithUsage("s", 1000, 500));
        StreamingAnswer stream = h.Runtime.StreamAsync(Hi);
        await foreach (StreamEvent _ in stream) { }
        AskResult streamed = await stream.Result;
        Assert.Single(seen);
        Assert.Equal(streamed.Cost.Total, seen[0].Cost.Total);

        h.Provider.Enqueue(ScriptStep.TextWithUsage("c", 1000, 500));
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo" });
        AskResult turn = await chat.SendAsync("hello");
        Assert.Equal(2, seen.Count);
        Assert.Equal(turn.Cost.Total, seen[1].Cost.Total);

        h.Provider.Enqueue(ScriptStep.TextWithUsage("1", 1000, 500), ScriptStep.TextWithUsage("2", 2000, 500));
        AgentDefinition def = AgentDefinition.Parse("model: demo\ninitialPrompts:\n  - user: [ { text: one } ]\n  - user: [ { text: two } ]\n", "t.flow", DefinitionLoadOptions.Lenient);
        ScriptResult script = await h.Runtime.RunScriptAsync(def);
        Assert.Equal(4, seen.Count);
        Assert.Equal(script.Cost.Total, seen[2].Cost.Total + seen[3].Cost.Total);
        Assert.All(seen, static c => Assert.Equal("inference", c.Source));
    }

    [Fact]
    public async Task Before_inference_can_modify_the_request()
    {
        using var h = new Harness();
        h.Runtime.Hooks.BeforeInference((c, _) => ValueTask.FromResult(HookDecision.ModifyRequest(c.Request with { System = "changed" }, "why")));
        h.Provider.EnqueueText("ok");

        AskResult r = await h.Runtime.AskAsync(Hi);

        Assert.Equal("changed", Assert.Single(h.Provider.Requests).System);
        Assert.Contains(h.ReadRecords(r.Instance.InstanceId), e => e.Kind == "hook");
    }

    [Fact]
    public async Task Before_inference_block_sends_nothing_and_fails_the_ask()
    {
        using var h = new Harness();
        h.Runtime.Hooks.BeforeInference((_, _) => ValueTask.FromResult(HookDecision.Block("no")));
        h.Provider.EnqueueText("never sent");

        await Assert.ThrowsAsync<HookBlockedException>(() => h.Runtime.AskAsync(Hi));

        Assert.Empty(h.Provider.Requests);
        Assert.Equal(0m, h.Runtime.Spent);
    }

    [Fact]
    public async Task Before_inference_respond_answers_without_calling_the_model()
    {
        using var h = new Harness();
        h.Runtime.Hooks.BeforeInference((_, _) => ValueTask.FromResult(HookDecision.Respond(new Message { Assistant = [new TextPart("from hook")] }, "cached")));

        AskResult r = await h.Runtime.AskAsync(Hi);

        Assert.Equal("from hook", r.Text);
        Assert.Empty(h.Provider.Requests);
        Assert.Equal(0m, r.Cost.Total);
        Assert.Equal(2, r.Instance.Messages.Count);
        Assert.Contains(h.ReadRecords(r.Instance.InstanceId), e => e.Kind == "hook");
    }

    [Fact]
    public async Task After_inference_can_modify_and_block()
    {
        using var h = new Harness();
        h.Runtime.Hooks.AfterInference((c, _) => ValueTask.FromResult(HookDecision.ModifyMessage(new Message { Assistant = [new TextPart("edited")] })));
        h.Provider.Enqueue(ScriptStep.TextWithUsage("raw", 1000, 500));
        AskResult r = await h.Runtime.AskAsync(Hi);
        Assert.Equal("edited", r.Text);
        Assert.Equal("edited", r.Instance.Messages[^1].GetAssistantText());
        Assert.Equal(0.002m, r.Instance.Messages[^1].Usage!.Cost);

        using var b = new Harness();
        b.Runtime.Hooks.AfterInference((_, _) => ValueTask.FromResult(HookDecision.Block("no")));
        b.Provider.Enqueue(ScriptStep.TextWithUsage("raw", 1000, 500));
        await Assert.ThrowsAsync<HookBlockedException>(() => b.Runtime.AskAsync(Hi));
        // The call was made and paid for before the hook looked at it.
        Assert.Equal(0.002m, b.Runtime.Spent);
    }

    [Fact]
    public async Task A_throwing_interceptor_blocks_the_ask_and_a_throwing_observer_does_not()
    {
        using var h = new Harness();
        h.Runtime.Hooks.BeforeInference((_, _) => throw new InvalidOperationException("boom"));
        h.Provider.EnqueueText("never sent");
        await Assert.ThrowsAsync<HookBlockedException>(() => h.Runtime.AskAsync(Hi));
        Assert.Empty(h.Provider.Requests);

        using var o = new Harness();
        o.Runtime.Hooks.OnCost((_, _) => throw new InvalidOperationException("boom"));
        o.Runtime.Hooks.OnMessage((_, _) => throw new InvalidOperationException("boom"));
        o.Provider.EnqueueText("fine");
        AskResult r = await o.Runtime.AskAsync(Hi);
        Assert.Equal("fine", r.Text);
    }

    [Fact]
    public async Task Message_and_state_hooks_fire_on_a_plain_ask()
    {
        using var h = new Harness();
        var messages = new List<string>();
        var states = new List<string>();
        h.Runtime.Hooks.OnMessage((c, _) => { messages.Add(c.Message.GetAssistantText() + c.Message.GetUserText()); return ValueTask.CompletedTask; });
        h.Runtime.Hooks.OnStateChange((c, _) => { states.Add(c.To); return ValueTask.CompletedTask; });
        h.Provider.EnqueueText("ok");

        await h.Runtime.AskAsync(Hi);

        Assert.Equal(["hi", "ok"], messages);
        Assert.Equal(["running", "stopped"], states);
    }

    [Fact]
    public async Task Stream_event_hook_sees_the_events_of_a_streaming_ask()
    {
        using var h = new Harness();
        int events = 0;
        h.Runtime.Hooks.Add(new CountingStreamHook(() => events++));
        h.Provider.Enqueue(ScriptStep.Text("he", "llo"));

        StreamingAnswer stream = h.Runtime.StreamAsync(Hi);
        await foreach (StreamEvent _ in stream) { }

        Assert.True(events > 0);
        Assert.Equal("hello", (await stream.Result).Text);
    }

    private sealed class CountingStreamHook(Action count) : IHook
    {
        public string Name => "count";

        public HookPoints Points => HookPoints.StreamEvent;

        public void OnStreamEvent(HookContext context, in StreamEvent e) => count();
    }

    [Fact]
    public async Task With_no_hooks_registered_a_plain_ask_builds_no_hook_set()
    {
        using var h = new Harness();
        Assert.Same(OneLlm.Hooks.HookSet.Empty, OneLlm.Hooks.HookSet.ForRuntime(h.Runtime));
        h.Provider.EnqueueText("warm");
        await h.Runtime.AskAsync(Hi);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            _ = OneLlm.Hooks.HookSet.ForRuntime(h.Runtime);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
