using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tools;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

/// <summary>The loop with the capability model, the built-in tools and reference loading.</summary>
public class AgentIntegrationTests
{
    [Fact]
    public async Task Ask_user_leaves_the_instance_waiting_and_the_answer_becomes_the_tool_result()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Calls(("q1", "ask_user", """{"question":"Which colour?","choices":["red","blue"]}""")), Say("Blue it is."));

        AgentRun run = await env.Rt.StartAgentAsync(Def("a", ["ask_user"]), null, Go());
        AgentResult asked = await Within(run.AwaitAsync());

        Assert.Equal(InstanceStates.Waiting, asked.State);
        Assert.Equal(AgentStopReason.Waiting, asked.StopReason);
        Assert.Single(env.Provider.Requests);

        await run.MessageAsync("blue");
        AgentResult done = await Within(run.AwaitAsync());

        Assert.Equal("Blue it is.", done.Text);
        ToolCall call = done.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.Equal("blue", ToolText(call));
        // The model saw the answer in the place it asked, and no separate user message was added for it.
        Assert.Equal("blue", ToolText(env.Provider.Requests[1].Messages.SelectMany(static m => m.ToolCalls).Single()));
        Assert.Equal(1, done.Instance.Messages.Count(static m => m.User.Count > 0));
    }

    [Fact]
    public async Task A_child_gets_the_intersection_of_real_capability_grants()
    {
        using var env = new AgentEnv();
        string a = AgentEnv.NewTempDirectory(), b = AgentEnv.NewTempDirectory();
        env.Rt.Capabilities = Capabilities.Files(a) + Capabilities.Files(b) + Capabilities.Shell;
        ICapabilities? seen = null;
        env.Rt.Tools.Add(new FuncTool("peek", (_, ctx, _) =>
        {
            seen = ctx.Capabilities;
            return Task.FromResult(ToolResult.FromText("ok"));
        }));
        env.Provider.Enqueue(Calls(("c1", "peek", "{}")), Say("done"));

        // The caller asks for one root and shell; shell is not in the parent's grant here.
        await Within(env.Rt.RunAgentAsync(Def("a", ["peek"]), null, Go(caps: Capabilities.Files(a))));

        Assert.NotNull(seen);
        Assert.Single(seen!.WriteRoots);
        Assert.False(seen.ShellEnabled);
        Assert.False(seen.NetworkEnabled);
    }

    [Fact]
    public async Task A_reference_the_grants_do_not_cover_fails_to_load_strictly_and_is_dropped_leniently()
    {
        using var env = new AgentEnv();
        env.Rt.Capabilities = Capabilities.None;
        AgentDefinition def = AgentDefinition.Parse("name: a\nmodel: demo\nreferences:\n  - routine: /agent-tools/os/read-file\n");

        BadDefinitionException ex = await Assert.ThrowsAsync<BadDefinitionException>(() => env.Rt.StartAgentAsync(def, null, Go()));
        Assert.Contains("read", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, ex.Line);
    }

    [Fact]
    public async Task Existing_routine_paths_find_the_builtin_and_agent_tools_under_the_default_grants()
    {
        using var env = new AgentEnv();
        env.Provider.Enqueue(Calls(("c1", "task_complete", """{"result":"r","summary":"s"}""")));
        AgentDefinition def = AgentDefinition.Parse(
            "name: a\nmodel: demo\nreferences:\n  - routine: /agent-tools/os/read-file\n  - routine: /agent-tools/task-complete\n  - routine: /runtime/agents/await-agent\n");

        AgentResult r = await Within(env.Rt.RunAgentAsync(def, null, Go()));

        string[] names = [.. env.Provider.Requests[0].Tools.Select(static t => t.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["await_agent", "read_file", "task_complete"], names);
        Assert.Equal(AgentStopReason.Completed, r.StopReason);
    }
}
