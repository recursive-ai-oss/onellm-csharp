using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Testing;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

/// <summary>Agents stay inside their own grants, tools and subtree: other instances, resumed runs, written child definitions, injected calls, ask_user and the shell gate.</summary>
public class AgentIsolationTests
{
    private static string Text(AgentResult r, string tool) =>
        string.Join("\n", r.Instance.Messages.SelectMany(static m => m.ToolCalls).Where(c => c.Tool == tool).Select(static c => string.Concat(c.Result.Select(static p => p switch { TextPart t => t.Text, JsonPart j => j.Value.ToJsonString(), _ => "" }))));

    [Fact]
    public async Task An_agent_can_not_read_message_wait_for_or_list_an_instance_it_did_not_start()
    {
        using var env = new AgentEnv();
        env.Rt.Tools.Add(FuncTool.Text("peek", "ok"));
        env.Provider.Enqueue(Say("y at rest"));
        AgentResult y = await Within(env.Rt.RunAgentAsync(Def("y", ["peek"]), null, Go("secret task")));
        string yid = y.Instance.InstanceId;
        int yMessages = y.Instance.Messages.Count;

        env.Provider.Enqueue(
            Calls(
                ("q", "query_agent", $$"""{"instanceId":"{{yid}}","contains":"secret"}"""),
                ("m", "message_agent", $$"""{"agentId":"{{yid}}","prompt":"run something"}"""),
                ("a", "await_agent", $$"""{"ids":["{{yid}}"]}"""),
                ("mon", "monitor_agent", $$"""{"instanceId":"{{yid}}","onFinish":true}"""),
                ("l", "list_agent_instances", "{}")),
            Say("done"));
        AgentResult x = await Within(env.Rt.RunAgentAsync(Def("x", ["query_agent", "message_agent", "await_agent", "monitor_agent", "list_agent_instances"]), null, Go("probe", Capabilities.ReadOnlyFiles(env.Directory))));

        Assert.Contains("not one of your sub-agents", Text(x, "query_agent"), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", Text(x, "query_agent").Replace("not one of your sub-agents", ""), StringComparison.Ordinal);
        Assert.Contains("not one of your sub-agents", Text(x, "message_agent"), StringComparison.Ordinal);
        Assert.Contains("There is no agent instance", Text(x, "await_agent"), StringComparison.Ordinal);
        Assert.Contains("not one of your sub-agents", Text(x, "monitor_agent"), StringComparison.Ordinal);
        Assert.DoesNotContain(yid, Text(x, "list_agent_instances").Replace(x.Instance.InstanceId, ""), StringComparison.Ordinal);
        Assert.Equal(yMessages, (await env.Rt.Store.LoadAsync(yid))!.Messages.Count);
    }

    [Fact]
    public async Task A_host_can_let_agents_reach_any_instance()
    {
        using var env = new AgentEnv();
        env.Rt.AgentsMayReachAnyInstance = true;
        env.Provider.Enqueue(Say("y at rest"));
        AgentResult y = await Within(env.Rt.RunAgentAsync(Def("y", []), null, Go("hello there")));
        env.Provider.Enqueue(Calls(("q", "query_agent", $$"""{"instanceId":"{{y.Instance.InstanceId}}","contains":"hello there"}""")), Say("done"));

        AgentResult x = await Within(env.Rt.RunAgentAsync(Def("x", ["query_agent"]), null, Go()));

        Assert.Contains("hello there", Text(x, "query_agent"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_started_agent_keeps_its_narrower_grants_when_another_runtime_resumes_it()
    {
        string store = NewTempDirectory();
        string root = NewTempDirectory();
        string id;
        ICapabilities? first = null, second = null;
        using (var a = new AgentEnv(store))
        {
            a.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell + Capabilities.AnyPublicHosts;
            a.Rt.Tools.Add(new FuncTool("peek", (_, ctx, _) =>
            {
                first = ctx.Capabilities;
                return Task.FromResult(ToolResult.FromText("ok"));
            }));
            a.Provider.Enqueue(Calls(("c1", "peek", "{}")), Say("at rest"));
            AgentResult r = await Within(a.Rt.RunAgentAsync(Def("a", ["peek"]), null, Go(caps: Capabilities.ReadOnlyFiles(root))));
            id = r.Instance.InstanceId;
        }

        using var b = new AgentEnv(store, "b");
        b.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell + Capabilities.AnyPublicHosts;
        b.Rt.Tools.Add(new FuncTool("peek", (_, ctx, _) =>
        {
            second = ctx.Capabilities;
            return Task.FromResult(ToolResult.FromText("ok"));
        }));
        b.Provider.Enqueue(Calls(("c2", "peek", "{}")), Say("again"));
        AgentRun run = (await b.Rt.GetAgentAsync(id))!;
        await run.MessageAsync("once more");
        await Within(run.AwaitAsync());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first!.ShellEnabled);
        Assert.False(second!.ShellEnabled);
        Assert.False(second.NetworkEnabled);
        Assert.Empty(second.WriteRoots);
        Assert.Single(second.ReadRoots);
    }

    [Fact]
    public async Task Saved_grants_never_widen_a_narrower_resuming_runtime()
    {
        string store = NewTempDirectory();
        string root = NewTempDirectory();
        string id;
        using (var a = new AgentEnv(store))
        {
            a.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell;
            a.Provider.Enqueue(Say("at rest"));
            id = (await Within(a.Rt.RunAgentAsync(Def("a", []), null, Go()))).Instance.InstanceId;
        }

        ICapabilities? seen = null;
        using var b = new AgentEnv(store, "b");
        b.Rt.Capabilities = Capabilities.ReadOnlyFiles(root);
        b.Rt.Tools.Add(new FuncTool("peek", (_, ctx, _) =>
        {
            seen = ctx.Capabilities;
            return Task.FromResult(ToolResult.FromText("ok"));
        }));
        b.Provider.Enqueue(Say("again"));
        AgentRun run = (await b.Rt.GetAgentAsync(id))!;
        await run.MessageAsync("hi");
        AgentResult r = await Within(run.AwaitAsync());

        Assert.Equal("again", r.Text);
        Assert.Null(seen);
        var saved = (IDictionary<string, object?>)(await b.Rt.Store.LoadAsync(id))!.ExtensionData![GrantSummary.Key]!;
        Assert.True(saved.ContainsKey("readRoots"));
    }

    [Fact]
    public async Task A_child_definition_a_model_wrote_gets_no_tool_its_parent_lacks()
    {
        using var env = new AgentEnv(records: false);
        string root = NewTempDirectory();
        env.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell;
        string child = Path.Combine(root, "child.yaml");
        await File.WriteAllTextAsync(child, "name: child\nmodel: demo\nsystemPrompt: CHILD\nreferences:\n  - tools: [execute_terminal_command, task_complete]\n");
        string fine = Path.Combine(root, "fine.yaml");
        await File.WriteAllTextAsync(fine, "name: fine\nmodel: demo\nsystemPrompt: CHILD\nreferences:\n  - tools: [task_complete]\n");
        env.Provider.Fallback = req => (req.System ?? "").Contains("CHILD", StringComparison.Ordinal)
            ? Complete("child done", "k")
            : req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Say("parent done") : Calls(("p1", "run_agent_async", $$"""{"agentSwarmUri":"{{child}}","prompt":"x"}"""), ("p2", "run_agent_async", $$"""{"agentSwarmUri":"{{fine}}","prompt":"x"}"""));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("parent", ["run_agent_async"]), null, Go()));

        ToolCall refused = r.Instance.Messages.SelectMany(static m => m.ToolCalls).Single(static c => c.Id == "p1");
        Assert.True(refused.IsError);
        Assert.Contains("execute_terminal_command", ToolText(refused), StringComparison.Ordinal);
        ToolCall started = r.Instance.Messages.SelectMany(static m => m.ToolCalls).Single(static c => c.Id == "p2");
        Assert.False(started.IsError);
    }

    [Fact]
    public async Task A_host_registered_child_may_use_tools_its_parent_lacks()
    {
        using var env = new AgentEnv(records: false);
        env.Rt.Tools.Add(FuncTool.Text("extra", "x"));
        env.Rt.AgentDefinitions.Add("reg", AgentDefinition.Parse("name: reg\nmodel: demo\nsystemPrompt: CHILD\nreferences:\n  - tools: [extra, task_complete]\n", "reg.yaml"));
        env.Provider.Fallback = req => (req.System ?? "").Contains("CHILD", StringComparison.Ordinal)
            ? Complete("child done", "k")
            : req.Messages.Any(static m => m.ToolCalls.Count > 0) ? Say("parent done") : Calls(("p1", "run_agent_async", """{"agentSwarmUri":"reg","prompt":"x"}"""));

        AgentResult r = await Within(env.Rt.RunAgentAsync(Def("parent", ["run_agent_async"]), null, Go()));

        Assert.False(r.Instance.Messages.SelectMany(static m => m.ToolCalls).Single().IsError);
    }

    [Fact]
    public async Task An_injected_call_to_a_tool_that_needs_approval_waits_for_approval()
    {
        using var env = new AgentEnv();
        var danger = FuncTool.Text("danger", "deleted", approval: true);
        env.Rt.Tools.Add(danger);
        env.Provider.Enqueue(Say("after"));
        AgentDefinition def = Def("a", ["danger"], "initialPrompts:\n  - toolCalls:\n      - tool: danger\n");

        AgentRun run = await env.Rt.StartAgentAsync(def, null, Go("task"));
        AgentResult held = await Within(run.AwaitAsync());

        Assert.Equal(AgentStopReason.WaitingForApproval, held.StopReason);
        Assert.Equal(0, danger.Calls);
        Assert.Contains(held.Instance.Messages, static m => m.GetUserText() == "task");

        await run.ApproveAsync();
        AgentResult done = await Within(run.AwaitAsync());
        Assert.Equal(1, danger.Calls);
        Assert.Equal("after", done.Text);
    }

    [Fact]
    public async Task An_injected_ask_user_leaves_the_instance_waiting_for_the_answer()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Say("thanks"));
        AgentDefinition def = Def("a", ["ask_user"], "initialPrompts:\n  - toolCalls:\n      - tool: ask_user\n        inputs: { question: \"Which?\" }\n");

        AgentRun run = await env.Rt.StartAgentAsync(def, null, Go("task"));
        AgentResult asked = await Within(run.AwaitAsync());

        Assert.Equal(InstanceStates.Waiting, asked.State);
        Assert.Empty(env.Provider.Requests);

        await run.MessageAsync("blue");
        AgentResult done = await Within(run.AwaitAsync());
        Assert.Equal("thanks", done.Text);
        Assert.Equal("blue", ToolText(done.Instance.Messages[0].ToolCalls[0]));
    }

    [Fact]
    public async Task Only_a_person_answers_ask_user_and_other_messages_wait_behind_it()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Calls(("q1", "ask_user", """{"question":"Which colour?"}""")), Say("Blue it is."));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["ask_user"]), null, Go());
        await Within(run.AwaitAsync());
        LiveAgent live = (await env.Rt.AgentHost.GetAsync(run.InstanceId, CancellationToken.None))!;

        await live.DeliverAsync("monitor says hi", new MessageSource { Kind = "runtime", Reason = "monitor" }, CancellationToken.None);
        await live.DeliverAsync("sibling here", new MessageSource { Kind = "agent", InstanceId = "other" }, CancellationToken.None);
        AgentResult still = await Within(run.AwaitAsync());

        Assert.Single(env.Provider.Requests);
        Assert.Equal(InstanceStates.Waiting, still.State);

        await run.MessageAsync("blue");
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal("Blue it is.", done.Text);
        Assert.Equal("blue", ToolText(done.Instance.Messages.SelectMany(static m => m.ToolCalls).Single()));
        Assert.Contains(done.Instance.Messages, static m => m.GetUserText() == "monitor says hi");
    }

    [Fact]
    public async Task A_message_resumes_an_instance_that_ended_in_error()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(ScriptStep.Fail(new IOException("boom")));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", []), null, Go());
        await Assert.ThrowsAnyAsync<Exception>(() => Within(run.AwaitAsync()));
        Assert.Equal(InstanceStates.Error, (await env.Rt.Store.LoadAsync(run.InstanceId))!.State);

        env.Provider.Enqueue(Say("back"));
        await run.MessageAsync("try again");
        AgentResult r = await Within(run.AwaitAsync());

        Assert.Equal("back", r.Text);
        Assert.NotEqual(InstanceStates.Error, r.State);
    }

    [Fact]
    public async Task A_message_through_message_agent_resumes_a_saved_instance_that_ended_in_error()
    {
        string store = NewTempDirectory();
        string id;
        using (var a = new AgentEnv(store))
        {
            a.Provider.Enqueue(ScriptStep.Fail(new IOException("boom")));
            AgentRun run = await a.Rt.StartAgentAsync(Def("a", []), null, Go());
            await Assert.ThrowsAnyAsync<Exception>(() => Within(run.AwaitAsync()));
            id = run.InstanceId;
        }

        using var b = new AgentEnv(store, "b");
        b.Rt.AgentsMayReachAnyInstance = true;
        b.Provider.Enqueue(Calls(("m", "message_agent", $$"""{"agentId":"{{id}}","prompt":"try again"}""")), Say("sent"));
        b.Provider.Fallback = req => (req.System ?? "").Contains("You are an agent.", StringComparison.Ordinal) && req.Messages.Any(static m => m.GetUserText() == "try again") ? Say("back") : Say("sent");
        await Within(b.Rt.RunAgentAsync(Def("b", ["message_agent"]), null, Go("send it")));
        AgentRun revived = (await b.Rt.GetAgentAsync(id))!;
        AgentResult r = await Within(revived.AwaitAsync());

        Assert.NotEqual(InstanceStates.Error, r.State);
    }

    [Fact]
    public async Task Shell_with_loop_approval_waits_for_a_saved_approval_and_runs_after_it()
    {
        string store = NewTempDirectory();
        string root = NewTempDirectory();
        string id;
        using (var a = new AgentEnv(store))
        {
            a.Rt.Capabilities = Capabilities.Files(root) + Capabilities.ShellWithLoopApproval;
            a.Provider.Enqueue(Calls(("c1", "execute_terminal_command", """{"command":"echo","args":["hello-approved"]}""")));
            AgentResult held = await Within(a.Rt.RunAgentAsync(Def("a", ["execute_terminal_command"]), null, Go()));
            Assert.Equal(AgentStopReason.WaitingForApproval, held.StopReason);
            Assert.Equal("c1", Assert.Single(held.PendingApprovals).ToolCallId);
            id = held.Instance.InstanceId;
        }

        using var b = new AgentEnv(store, "b");
        b.Rt.Capabilities = Capabilities.Files(root) + Capabilities.ShellWithLoopApproval;
        b.Provider.Enqueue(Say("ran"));
        AgentRun run = await b.Rt.ApproveAsync(id);
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal("ran", done.Text);
        ToolCall call = done.Instance.Messages.SelectMany(static m => m.ToolCalls).Single();
        Assert.False(call.IsError);
        Assert.Contains("hello-approved", ToolText(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_shell_command_does_not_run()
    {
        using var env = new AgentEnv();
        string root = NewTempDirectory();
        string marker = Path.Combine(root, "marker");
        env.Rt.Capabilities = Capabilities.Files(root) + Capabilities.ShellWithLoopApproval;
        env.Provider.Enqueue(Calls(("c1", "execute_terminal_command", $$"""{"command":"touch","args":["{{marker}}"]}""")), Say("fine"));
        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["execute_terminal_command"]), null, Go());
        await Within(run.AwaitAsync());

        await run.DenyAsync("c1", "no");
        await Within(run.AwaitAsync());

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_resumed_instance_started_with_shell_approval_still_needs_approval_in_a_runtime_that_allows_any_command()
    {
        string store = NewTempDirectory();
        string root = NewTempDirectory();
        string id;
        using (var a = new AgentEnv(store))
        {
            a.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell;
            a.Provider.Enqueue(Say("at rest"));
            id = (await Within(a.Rt.RunAgentAsync(Def("a", ["execute_terminal_command"]), null, Go(caps: Capabilities.Files(root) + Capabilities.ShellWithLoopApproval)))).Instance.InstanceId;
        }

        using var b = new AgentEnv(store, "b");
        b.Rt.Capabilities = Capabilities.Files(root) + Capabilities.Shell;
        b.Provider.Enqueue(Calls(("c1", "execute_terminal_command", """{"command":"echo","args":["x"]}""")));
        AgentRun run = (await b.Rt.GetAgentAsync(id))!;
        await run.MessageAsync("go");
        AgentResult r = await Within(run.AwaitAsync());

        Assert.Equal(AgentStopReason.WaitingForApproval, r.StopReason);
    }
}
