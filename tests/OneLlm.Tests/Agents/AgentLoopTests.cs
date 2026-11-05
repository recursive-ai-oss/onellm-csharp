using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Http;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

/// <summary>Row 6: the loop's rules, one scenario each, on the scripted provider and a fake clock.</summary>
public class AgentLoopTests
{
    private static ToolResult Text(string s) => ToolResult.FromText(s);

    [Fact]
    public async Task A_loop_ending_tool_stops_the_agent_with_its_typed_result()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Complete("all good"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["task_complete"]), null, Go()));

        Assert.Equal(AgentStopReason.Completed, r.StopReason);
        Assert.Equal(InstanceStates.Stopped, r.State);
        Assert.Equal("all good", r.Text);
        Assert.Equal("task_complete", r.CompletedBy);
        Assert.Equal("all good", r.Output!["result"]!.GetValue<string>());
        Assert.Equal(1, r.Turns);
        // 1000 input at $1/M plus 100 output at $2/M.
        Assert.Equal(0.0012m, r.Cost.Total);
        Assert.Equal(0.0012m, r.SubtreeCost);
        ToolCall call = Assert.Single(r.Instance.Messages[^1].ToolCalls);
        Assert.Equal("Task completion event sent successfully", ToolText(call));
        Assert.Single(env.Provider.Requests);
    }

    [Fact]
    public async Task Any_tool_flagged_ends_loop_stops_the_run_and_its_sibling_calls_still_run()
    {
        using var env = new AgentEnv();
        var finish = new FuncTool("finish", (_, _, _) => Task.FromResult(new ToolResult([new TextPart("finished")]) { EndsLoop = true, TypedResult = new JsonObject { ["n"] = 7 } }), endsLoop: true);
        var other = FuncTool.Text("other", "ok");
        env.Rt.Tools.Add(finish).Add(other);
        env.Provider.Enqueue(Calls(("c1", "other", "{}"), ("c2", "finish", "{}")));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["finish", "other"]), null, Go()));

        Assert.Equal(AgentStopReason.Completed, r.StopReason);
        Assert.Equal(1, other.Calls);
        Assert.Equal(["ok", "finished"], r.Instance.Messages[^1].ToolCalls.Select(ToolText));
        Assert.Single(env.Provider.Requests);
    }

    [Fact]
    public async Task A_turn_without_tool_calls_leaves_the_agent_waiting()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("What next?"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["task_complete"]), null, Go()));

        Assert.Equal(AgentStopReason.Waiting, r.StopReason);
        Assert.Equal(InstanceStates.Waiting, r.State);
        Assert.Equal("What next?", r.Text);
        Assert.Null(r.CompletedBy);
    }

    [Fact]
    public async Task Reaching_max_iterations_ends_the_run_in_error_with_the_runtimes_message()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("again", "more"));
        env.Provider.Enqueue(Calls(("c1", "again", "{}")), Calls(("c2", "again", "{}")), Calls(("c3", "again", "{}")));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["again"], "maxIterations: 2"), null, Go()));

        Assert.Equal(AgentStopReason.MaxIterations, r.StopReason);
        Assert.Equal(InstanceStates.Error, r.State);
        Assert.Equal(2, env.Provider.Requests.Count);
        var error = (IDictionary<string, object?>)r.Instance.ExtensionData!["error"]!;
        Assert.Equal("max iterations (2) reached", error["message"]);
        Assert.Contains("max iterations (2) reached", r.Warnings);
        // The saved instance carries the same state.
        Instance saved = (await env.Rt.Store.LoadAsync(r.Instance.InstanceId))!;
        Assert.Equal(InstanceStates.Error, saved.State);
    }

    [Fact]
    public async Task Injected_initial_calls_run_before_the_first_model_turn()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(new FuncTool("lookup", (i, _, _) => Task.FromResult(Text("found " + i["x"]!.GetValue<string>()))));
        env.Provider.Enqueue(Say("ready"));
        AgentDefinition def = Def("a", ["lookup"], "initialPrompts:\n  - toolCalls:\n      - tool: lookup\n        inputs: { x: \"{{ .inputs.who }}\" }\n");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, new Dictionary<string, object?> { ["who"] = "ada" }, Go()));

        Message first = r.Instance.Messages[0];
        ToolCall injected = Assert.Single(first.ToolCalls);
        Assert.True(injected.Injected);
        Assert.Equal("runtime", injected.InjectedBy);
        Assert.Equal("found ada", ToolText(injected));
        // The model's first request already carries the injected call with its result, then the user's prompt.
        ChatRequest req = Assert.Single(env.Provider.Requests);
        Assert.Equal("found ada", ToolText(req.Messages[0].ToolCalls[0]));
        Assert.Equal("go", req.Messages[^1].GetUserText());
    }

    [Fact]
    public async Task Parallel_calls_run_together_and_return_results_in_call_order()
    {
        using var env = new AgentEnv();
        var aStarted = new TaskCompletionSource();
        var bStarted = new TaskCompletionSource();
        // Each waits for the other to start: this only finishes when both run at once. B finishes first.
        env.Rt.Tools.Add(new FuncTool("a", async (_, _, ct) =>
        {
            aStarted.TrySetResult();
            await bStarted.Task;
            await Task.Delay(30, ct);
            return Text("A");
        }));
        env.Rt.Tools.Add(new FuncTool("b", async (_, _, _) =>
        {
            bStarted.TrySetResult();
            await aStarted.Task;
            return Text("B");
        }));
        env.Provider.Enqueue(Calls(("c1", "a", "{}"), ("c2", "b", "{}")), Say("fine"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["a", "b"]), null, Go()));

        Assert.Equal(InstanceStates.Waiting, r.State);
        Message turn = r.Instance.Messages.Single(m => m.ToolCalls.Count == 2);
        Assert.Equal(["c1", "c2"], turn.ToolCalls.Select(static c => c.Id));
        Assert.Equal(["A", "B"], turn.ToolCalls.Select(ToolText));
    }

    [Fact]
    public async Task A_tool_that_is_not_parallel_safe_makes_the_whole_turn_run_in_order()
    {
        using var env = new AgentEnv();
        var log = new List<string>();
        env.Rt.Tools.Add(new FuncTool("first", async (_, _, ct) =>
        {
            lock (log) { log.Add("first:start"); }
            await Task.Delay(40, ct);
            lock (log) { log.Add("first:end"); }
            return Text("1");
        }, parallelSafe: false));
        env.Rt.Tools.Add(new FuncTool("second", (_, _, _) =>
        {
            lock (log) { log.Add("second:start"); }
            return Task.FromResult(Text("2"));
        }));
        env.Provider.Enqueue(Calls(("c1", "first", "{}"), ("c2", "second", "{}")), Say("ok"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["first", "second"]), null, Go()));

        Assert.Equal(["first:start", "first:end", "second:start"], log);
        Assert.Equal(["1", "2"], r.Instance.Messages.Single(m => m.ToolCalls.Count == 2).ToolCalls.Select(ToolText));
    }

    [Fact]
    public async Task A_tool_that_throws_gives_the_model_an_error_result_and_the_loop_goes_on()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(new FuncTool("boom", (_, _, _) => throw new InvalidOperationException("disk on fire")));
        env.Provider.Enqueue(Calls(("c1", "boom", "{}")), Say("I saw the error"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["boom"]), null, Go()));

        Assert.Equal(InstanceStates.Waiting, r.State);
        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("disk on fire", ToolText(call), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", ToolText(call), StringComparison.Ordinal);
        // The second request carries the error result back.
        Assert.True(env.Provider.Requests[1].Messages.SelectMany(static m => m.ToolCalls).Single().IsError);
    }

    [Fact]
    public async Task A_tool_that_runs_past_its_timeout_gives_an_error_result()
    {
        using var env = new AgentEnv();
        var started = new TaskCompletionSource();
        env.Rt.Tools.Add(new FuncTool("slow", async (_, ctx, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.TimeProvider, ct);
            return Text("never");
        }, timeout: TimeSpan.FromSeconds(5)));
        env.Provider.Enqueue(Calls(("c1", "slow", "{}")), Say("noted"));

        Task<AgentResult> run = env.Rt.RunAgentAsync(Def("a", ["slow"]), null, Go());
        await Within(started.Task);
        AgentResult r = await env.Pump(run, TimeSpan.FromSeconds(6));

        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("5", ToolText(call), StringComparison.Ordinal);
        Assert.Equal(InstanceStates.Waiting, r.State);
    }

    [Fact]
    public async Task A_transient_provider_failure_is_retried_then_the_run_succeeds()
    {
        using var env = new AgentEnv();
        env.Rt.AgentRetry = new RetryPolicy { MaxRetries = 2, BaseDelay = TimeSpan.FromSeconds(1) };
        env.Provider.Enqueue(
            ScriptStep.Fail(new TransportException("scripted", "connection reset")),
            ScriptStep.Fail(new RateLimitException("scripted", "slow down", TimeSpan.FromSeconds(3))),
            Say("third time"));

        AgentResult r = await env.Pump(env.Rt.RunAgentAsync(Def("a", []), null, Go()), TimeSpan.FromSeconds(2));

        Assert.Equal("third time", r.Text);
        Assert.Equal(3, env.Provider.Requests.Count);
        Assert.Equal(InstanceStates.Waiting, r.State);
    }

    [Fact]
    public async Task A_provider_failure_after_the_retries_puts_the_instance_in_error_with_a_typed_exception()
    {
        using var env = new AgentEnv();
        env.Rt.AgentRetry = new RetryPolicy { MaxRetries = 1, BaseDelay = TimeSpan.FromSeconds(1) };
        env.Provider.Enqueue(
            ScriptStep.Fail(new TransportException("scripted", "down")),
            ScriptStep.Fail(new TransportException("scripted", "still down")));

        Task<AgentResult> run = env.Rt.RunAgentAsync(Def("a", []), null, Go());
        TransportException ex = await Assert.ThrowsAsync<TransportException>(() => env.Pump(run, TimeSpan.FromSeconds(2)));

        Assert.Contains("still down", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, env.Provider.Requests.Count);
        InstanceSummary saved = await FirstInstanceAsync(env);
        Assert.Equal(InstanceStates.Error, saved.State);
    }

    [Fact]
    public async Task A_permanent_provider_error_is_not_retried()
    {
        using var env = new AgentEnv();
        env.Rt.AgentRetry = new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.FromMilliseconds(1) };
        env.Provider.Enqueue(ScriptStep.Fail(new AuthException("scripted", "bad key")));

        await Assert.ThrowsAsync<AuthException>(() => Within(env.Rt.RunAgentAsync(Def("a", []), null, Go())));

        Assert.Single(env.Provider.Requests);
    }

    [Fact]
    public async Task Cancelling_mid_turn_stops_the_agent_and_keeps_what_it_had()
    {
        using var env = new AgentEnv();
        var inTool = new TaskCompletionSource();
        env.Rt.Tools.Add(FuncTool.Text("quick", "done quickly"));
        env.Rt.Tools.Add(new FuncTool("hang", async (_, _, ct) =>
        {
            inTool.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Text("never");
        }));
        env.Provider.Enqueue(Calls(("c1", "quick", "{}"), ("c2", "hang", "{}")));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["quick", "hang"]), null, Go());
        await Within(inTool.Task);
        Task<AgentResult> waiter = run.AwaitAsync();
        await Within(run.CancelAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(waiter));
        Instance saved = (await env.Rt.Store.LoadAsync(run.InstanceId))!;
        Assert.Equal(InstanceStates.Stopped, saved.State);
        Assert.True(saved.ExtensionData is { } d && d.TryGetValue("cancelled", out object? flag) && flag is true);
        // The partial turn is kept: the finished call has its result, the interrupted one says it was cancelled.
        Message turn = saved.Messages.Single(m => m.ToolCalls.Count == 2);
        Assert.Equal("done quickly", ToolText(turn.ToolCalls[0]));
        Assert.True(turn.ToolCalls[1].IsError);
        Assert.Contains("cancel", ToolText(turn.ToolCalls[1]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_message_to_a_running_agent_is_read_at_the_next_turn_boundary()
    {
        using var env = new AgentEnv();
        var release = new TaskCompletionSource();
        var inTool = new TaskCompletionSource();
        env.Rt.Tools.Add(new FuncTool("gate", async (_, _, _) =>
        {
            inTool.TrySetResult();
            await release.Task;
            return Text("gate open");
        }));
        env.Provider.Enqueue(Calls(("c1", "gate", "{}")), Say("got it"));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["gate"]), null, Go());
        await Within(inTool.Task);
        await run.MessageAsync("actually, use the blue one");
        // Not read yet: the model has been called once.
        Assert.Single(env.Provider.Requests);
        release.SetResult();
        AgentResult r = await Within(run.AwaitAsync());

        Assert.Equal("got it", r.Text);
        ChatRequest second = env.Provider.Requests[1];
        Assert.Equal("actually, use the blue one", second.Messages[^1].GetUserText());
        // The tool result belongs to the turn before the steering message.
        Assert.Equal("gate open", ToolText(second.Messages[^2].ToolCalls[0]));
        Assert.Equal("api", second.Messages[^1].Source!.Kind);
    }

    [Fact]
    public async Task A_message_to_a_waiting_agent_resumes_it()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("first answer"), Say("second answer"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        AgentResult first = await Within(run.AwaitAsync());
        Assert.Equal(InstanceStates.Waiting, first.State);

        await run.MessageAsync("and another thing");
        AgentResult second = await Within(run.AwaitAsync());

        Assert.Equal("second answer", second.Text);
        Assert.Equal(InstanceStates.Waiting, second.State);
        Assert.Equal(["go", "and another thing"], env.Provider.Requests[1].Messages.Where(static m => m.User.Count > 0).Select(static m => m.GetUserText()));
    }

    [Fact]
    public async Task A_message_to_a_stopped_agent_resumes_it_after_a_restart()
    {
        string dir = AgentEnv.NewTempDirectory();
        string id;
        using (var a = new AgentEnv(dir))
        {
            a.Provider.Enqueue(Complete("v1"));
            AgentResult r = await Within(a.Rt.RunAgentAsync(Def("a", ["task_complete"]), null, Go()));
            id = r.Instance.InstanceId;
            Assert.Equal(InstanceStates.Stopped, r.State);
        }

        using var b = new AgentEnv(dir, "b");
        b.Provider.Enqueue(Say("back again"));
        AgentRun run = await b.Rt.MessageAgentAsync(id, "one more question");
        AgentResult result = await Within(run.AwaitAsync());

        Assert.Equal("back again", result.Text);
        Assert.Equal(InstanceStates.Waiting, result.State);
        Assert.Equal("one more question", b.Provider.Requests[0].Messages[^1].GetUserText());
    }

    [Fact]
    public async Task Every_step_is_saved_and_recorded()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("t", "r"));
        env.Provider.Enqueue(Calls(("c1", "t", "{}")), Complete());

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", ["t", "task_complete"]), null, Go()));

        List<string> kinds = OneLlm.Records.RecordReader.ReadFile(env.Records.PathOf(r.Instance.InstanceId)).Select(static e => e.Kind).ToList();
        Assert.Contains("inference", kinds);
        Assert.Equal(2, kinds.Count(static k => k == "inference"));
        Assert.Contains("toolCall", kinds);
        Assert.Contains("taskCompletion", kinds);
        Assert.Equal("run", kinds[0]);
        Assert.Equal("run", kinds[^1]);
    }

    [Fact]
    public async Task An_unknown_tool_name_gets_an_error_result_not_a_crash()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Calls(("c1", "no_such_tool", "{}")), Say("oops"));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("a", []), null, Go()));

        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("no_such_tool", ToolText(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_definition_that_references_an_unregistered_tool_fails_to_load_with_its_position()
    {
        using var env = new AgentEnv();
        BadDefinitionException ex = await Assert.ThrowsAsync<BadDefinitionException>(() => env.Rt.StartAgentAsync(Def("a", ["not_registered"]), null, Go()));
        Assert.Contains("not_registered", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Line > 0);
    }

    private static async Task<InstanceSummary> FirstInstanceAsync(AgentEnv env)
    {
        await foreach (InstanceSummary s in env.Rt.Store.ListAsync(new InstanceQuery()))
        {
            return s;
        }

        throw new Xunit.Sdk.XunitException("No instance was saved.");
    }
}
