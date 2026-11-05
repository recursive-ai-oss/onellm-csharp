using System.Diagnostics;
using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public class AgentContextStrategyTests
{
    private static void AssertPaired(ChatRequest request)
    {
        foreach (Message m in request.Messages.Where(static m => m.ToolCalls.Count > 0))
        {
            foreach (ToolCall c in m.ToolCalls)
            {
                Assert.True(c.Result.Count > 0, $"call {c.Id} has a result");
            }
        }
    }

    [Fact]
    public async Task Clearing_tool_results_keeps_every_call_paired_and_the_unchanged_prefix_identical()
    {
        using var env = new AgentEnv();
        int n = 0;
        env.Rt.Tools.Add(new FuncTool("t", (_, _, _) => Task.FromResult(ToolResult.FromText("big result number " + Interlocked.Increment(ref n)))));
        for (int i = 1; i <= 7; i++)
        {
            env.Provider.Enqueue(Calls(($"c{i}", "t", "{}")));
        }

        env.Provider.Enqueue(Say("finished"));
        AgentDefinition def = Def("a", ["t"], "contextStrategy: { type: clearToolResults, triggerTokens: 500, keepLast: 2 }");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, null, Go()));

        Assert.Equal(InstanceStates.Waiting, r.State);
        Assert.NotEmpty(r.Instance.Compactions);
        Assert.All(r.Instance.Compactions, static c => Assert.Equal("clearToolResults", c.Strategy));
        Assert.All(r.Instance.Compactions, static c => Assert.NotEmpty(c.ReplacedMessageIds!));

        IReadOnlyList<ChatRequest> requests = env.Provider.Requests;
        Assert.Equal(8, requests.Count);
        foreach (ChatRequest q in requests)
        {
            AssertPaired(q);
        }

        // Between two requests, the only change allowed is appending, or turning a still-full result into the note.
        for (int i = 0; i + 1 < requests.Count; i++)
        {
            List<string> a = [.. requests[i].Messages.Select(Sig)];
            List<string> b = [.. requests[i + 1].Messages.Select(Sig)];
            int d = 0;
            while (d < a.Count && a[d] == b[d])
            {
                d++;
            }

            if (d == a.Count)
            {
                continue;
            }

            Assert.DoesNotContain(ContextStrategies.ClearedNote, a[d], StringComparison.Ordinal);
            Assert.Contains(ContextStrategies.ClearedNote, b[d], StringComparison.Ordinal);
        }

        // Results of the latest two tool turns are still whole.
        List<Message> toolTurns = [.. r.Instance.Messages.Where(static m => m.ToolCalls.Count > 0)];
        Assert.Equal(ContextStrategies.ClearedNote, ToolText(toolTurns[0].ToolCalls[0]));
        Assert.StartsWith("big result", ToolText(toolTurns[^1].ToolCalls[0]), StringComparison.Ordinal);
        Assert.StartsWith("big result", ToolText(toolTurns[^2].ToolCalls[0]), StringComparison.Ordinal);
        // The note is written once: clearing again changes nothing.
        Assert.Equal(7, toolTurns.Count);
    }

    [Fact]
    public void Clearing_does_not_touch_a_call_waiting_for_approval()
    {
        List<string> replaced = ContextStrategies.ClearToolResults(
            new Instance
            {
                InstanceId = "x",
                Messages =
                [
                    new Message { Id = "m1", ToolCalls = [new ToolCall("c1", "t") { Result = [new TextPart("a")] }] },
                    new Message { Id = "m2", ToolCalls = [new ToolCall("c2", "t") { Result = [new TextPart("b")] }] },
                    new Message { Id = "m3", ToolCalls = [new ToolCall("c3", "t") { Result = [new TextPart("c")] }] },
                ],
            },
            keepLast: 1,
            skipCallIds: new HashSet<string> { "c1" });
        Assert.Equal(["m2"], replaced);
    }

    [Fact]
    public async Task Compacting_summarizes_older_turns_into_one_message_and_records_what_it_replaced_and_cost()
    {
        using var env = new AgentEnv();
        int agentCalls = 0;
        env.Rt.Tools.Add(new FuncTool("t", async (_, ctx, ct) =>
        {
            int k = Interlocked.Increment(ref agentCalls);
            await ctx.Runtime!.MessageAgentAsync(ctx.InstanceId, "follow-up " + k, ct);
            return ToolResult.FromText("result " + k);
        }));
        int calls = 0;
        env.Provider.Fallback = req =>
        {
            if ((req.System ?? "").Contains("You compress", StringComparison.Ordinal))
            {
                return Say("SUMMARY: the user wanted things done.");
            }

            int k = Interlocked.Increment(ref calls);
            return k <= 5 ? Calls(($"c{k}", "t", "{}")) : Say("all done");
        };
        AgentDefinition def = Def("a", ["t"], "contextStrategy: { type: compact, triggerTokens: 500, keepLast: 2 }");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, null, Go()));

        Assert.Equal(InstanceStates.Waiting, r.State);
        Compaction first = r.Instance.Compactions[0];
        Assert.Equal("compact", first.Strategy);
        Assert.NotEmpty(first.ReplacedMessageIds!);
        Assert.NotNull(first.SummaryMessageId);
        Assert.Equal(0.0012m, first.Cost);

        // The kept history starts with the summary; every call in it still has its result.
        Message head = r.Instance.Messages[0];
        Assert.Contains(head.Id, r.Instance.Compactions.Select(static c => c.SummaryMessageId));
        Assert.StartsWith("[Summary of the earlier conversation]", head.GetUserText(), StringComparison.Ordinal);
        foreach (Message m in r.Instance.Messages.Where(static m => m.ToolCalls.Count > 0))
        {
            Assert.All(m.ToolCalls, static c => Assert.NotEmpty(c.Result));
        }

        // The agent's requests: each either extends the one before, or starts from a fresh summary (a compaction).
        List<ChatRequest> agentRequests = [.. env.Provider.Requests.Where(static q => !(q.System ?? "").Contains("You compress", StringComparison.Ordinal))];
        int resets = 0;
        for (int i = 0; i + 1 < agentRequests.Count; i++)
        {
            AssertPaired(agentRequests[i]);
            List<string> a = [.. agentRequests[i].Messages.Select(Sig)];
            List<string> b = [.. agentRequests[i + 1].Messages.Select(Sig)];
            bool appended = b.Count >= a.Count && a.SequenceEqual(b.Take(a.Count));
            if (!appended)
            {
                resets++;
                Assert.Contains("[Summary of the earlier conversation]", b[0], StringComparison.Ordinal);
            }
        }

        Assert.Equal(r.Instance.Compactions.Count, resets);
        // The summary call is an instance of its own below this one, so its cost is in the subtree.
        Assert.Equal(r.Instance.Compactions.Sum(static c => c.Cost ?? 0m) + 0.0012m * agentRequests.Count, r.SubtreeCost);
    }

    [Fact]
    public async Task The_provider_native_strategy_is_skipped_with_a_warning_when_the_provider_cannot_do_it()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("t", "r"));
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Calls(("c2", "t", "{}")), Say("end"));
        AgentDefinition def = Def("a", ["t"], "contextStrategy: { type: providerNative, triggerTokens: 100 }");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, null, Go()));

        Assert.Empty(r.Instance.Compactions);
        Assert.Contains(r.Warnings, static w => w.Contains("providerNative", StringComparison.Ordinal));
    }
}

public class AgentOverheadTests
{
    [Fact]
    public void With_no_hooks_registered_no_hook_point_allocates()
    {
        HookSet empty = HookSet.Empty;
        HookPoints[] points = [HookPoints.BeforeInference, HookPoints.AfterInference, HookPoints.BeforeToolCall, HookPoints.AfterToolCall, HookPoints.OnMessage, HookPoints.OnStateChange, HookPoints.OnCost, HookPoints.BeforeCompaction, HookPoints.StreamEvent];
        bool any = false;
        // Warm up so JIT work is not counted.
        foreach (HookPoints p in points)
        {
            any |= empty.Has(p);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            foreach (HookPoints p in points)
            {
                any |= empty.Has(p);
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(any);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task A_runtime_with_no_hooks_gives_every_agent_the_shared_empty_set()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("x"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        await Within(run.AwaitAsync());
        LiveAgent agent = (await env.Rt.AgentHost.GetAsync(run.InstanceId, CancellationToken.None))!;
        Assert.Same(HookSet.Empty, agent.Setup.Hooks);
    }

    /// <summary>
    /// Indicative loop cost per turn on a scripted provider, with and without an observer. Runs only when ONELLM_INDICATIVE is set,
    /// and writes its numbers to the file that variable names. Not a benchmark: a machine-shared smoke measurement.
    /// </summary>
    [Fact]
    public async Task Indicative_loop_overhead_per_turn()
    {
        string? outPath = Environment.GetEnvironmentVariable("ONELLM_INDICATIVE");
        if (string.IsNullOrEmpty(outPath))
        {
            return;
        }

        const int Turns = 400;
        async Task<(double Ms, long Bytes)> Run(bool withHook)
        {
            using var env = new AgentEnv(records: false);
            env.Rt.Tools.Add(FuncTool.Text("t", "r"));
            if (withHook)
            {
                env.Rt.Hooks.OnMessage((_, _) => ValueTask.CompletedTask, "obs");
            }

            AgentDefinition def = Def("a", ["t"], "maxIterations: 1000");
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Turns; i++)
            {
                env.Provider.Enqueue(Calls(($"d{i}", "t", "{}")));
            }

            env.Provider.Enqueue(Say("end"));
            var sw = Stopwatch.StartNew();
            await env.Rt.RunAgentAsync(def, null, Go());
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds / (Turns + 1), (GC.GetAllocatedBytesForCurrentThread() - b0) / (Turns + 1));
        }

        _ = await Run(false);
        var plain = await Run(false);
        var hooked = await Run(true);
        File.WriteAllText(
            outPath,
            $"indicative; {Turns} tool turns on the scripted provider, memory store, fake clock\n" +
            $"no hooks:  {plain.Ms:F3} ms/turn (thread alloc {plain.Bytes} B/turn)\n" +
            $"1 observer: {hooked.Ms:F3} ms/turn (thread alloc {hooked.Bytes} B/turn)\n");
        Assert.Skip("indicative run recorded");
    }
}
