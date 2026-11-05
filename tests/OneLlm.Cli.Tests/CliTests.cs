using System.Text.Json.Nodes;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;

namespace OneLlm.Cli.Tests;

public class ParsingAndGrantTests
{
    private static readonly string[] s_valued = ["input", "root"];
    private static readonly string[] s_optional = ["allow-shell", "allow-net"];
    private static readonly string[] s_flags = ["yes"];

    [Fact]
    public void Options_come_as_name_value_or_name_equals_value_and_repeat()
    {
        CliArgs a = CliArgs.Parse(["x.yaml", "--input", "a=1", "--input=b=2", "--yes", "--root", "r"], s_valued, s_optional, s_flags);
        Assert.Equal(["x.yaml"], a.Positional);
        Assert.Equal(["a=1", "b=2"], a.GetAll("input"));
        Assert.True(a.Has("yes"));
        Assert.Equal("r", a.Get("root"));
    }

    [Fact]
    public void A_bare_allow_flag_has_an_empty_value_and_an_unknown_option_is_a_usage_error()
    {
        CliArgs a = CliArgs.Parse(["--allow-shell", "file"], s_valued, s_optional, s_flags);
        Assert.Equal("", a.Get("allow-shell"));
        Assert.Equal(["file"], a.Positional);
        Assert.Throws<UsageException>(() => CliArgs.Parse(["--nope"], s_valued, s_optional, s_flags));
        Assert.Throws<UsageException>(() => CliArgs.Parse(["--input"], s_valued, s_optional, s_flags));
        Assert.Throws<UsageException>(() => CliArgs.Parse(["--yes=1"], s_valued, s_optional, s_flags));
    }

    private static Capabilities Grant(params string[] args) =>
        Grants.FromFlags(Path.GetTempPath(), CliArgs.Parse(args, s_valued, s_optional, s_flags), static (_, _) => ValueTask.FromResult(false));

    [Fact]
    public void With_no_flags_only_files_are_granted()
    {
        Capabilities caps = Grant();
        Assert.False(caps.ShellEnabled);
        Assert.False(caps.NetworkEnabled);
        Assert.NotEmpty(caps.ReadRoots);
    }

    [Fact]
    public void Shell_flags_grant_everything_a_list_or_a_question()
    {
        Assert.True(Grant("--allow-shell").ShellAccess.AllowsCommand("rm", []));

        Capabilities listed = Grant("--allow-shell=git,dotnet");
        Assert.True(listed.ShellAccess.AllowsCommand("git", ["status"]));
        Assert.False(listed.ShellAccess.AllowsCommand("rm", ["-rf", "x"]));

        Capabilities ask = Grant("--allow-shell=ask");
        Assert.True(ask.ShellAccess.RequiresApproval);
    }

    [Fact]
    public void Network_flags_grant_public_hosts_or_a_list()
    {
        Assert.True(Grant("--allow-net").NetworkEnabled);

        Capabilities listed = Grant("--allow-net=example.com");
        Assert.True(listed.IsNetworkAllowed(new Uri("https://example.com/x")));
        Assert.False(listed.IsNetworkAllowed(new Uri("https://other.example.org/")));
    }

    [Fact]
    public void Since_reads_durations_and_dates()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddDays(-7), CliApp.ParseSince("7d", now));
        Assert.Equal(now.AddHours(-24), CliApp.ParseSince("24h", now));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), CliApp.ParseSince("2026-11-01", now));
        Assert.Throws<UsageException>(() => CliApp.ParseSince("soon", now));
    }
}

public class ExampleAgentTests
{
    private static readonly string[] s_expected =
        ["agent-writer", "cs-tool-writer", "decider-writer", "hook-writer", "js-tool-writer", "orchestrator", "provider-writer", "script-writer"];

    // The examples that build or run code say so: they need a shell, and the person running them grants it.
    private static readonly HashSet<string> s_needShell = new(["cs-tool-writer", "hook-writer", "provider-writer"], StringComparer.Ordinal);

    public static TheoryData<string> Names => new(s_expected);

    [Fact]
    public void All_eight_examples_are_embedded_and_each_has_a_description_and_a_stop()
    {
        Assert.Equal(s_expected, Examples.Names);
        foreach (string name in s_expected)
        {
            var def = Examples.Load("examples/" + name);
            Assert.Equal(name, def.Name);
            Assert.False(string.IsNullOrWhiteSpace(def.Description));
            Assert.NotNull(def.MaxIterations);
            Assert.Contains("task_complete", def.References.SelectMany(static r => r.Tools ?? []));
        }
    }

    private static string[] Flags(string name) => s_needShell.Contains(name) ? ["--allow-shell=dotnet,git"] : [];

    /// <summary>The scripted model looks at the folder once, then finishes; the orchestrator starts a sub-agent and waits for it first.</summary>
    private static void Script(ScriptedProvider p)
    {
        p.Fallback = req =>
        {
            int turns = req.Messages.Count(static m => m.ToolCalls.Count > 0);
            if ((req.System ?? "").Contains("You coordinate", StringComparison.Ordinal))
            {
                string started = string.Concat(req.Messages.SelectMany(static m => m.ToolCalls).Where(static c => c.Tool == "run_agent_async")
                    .Select(static c => string.Concat(c.Result.OfType<TextPart>().Select(static t => t.Text))));
                System.Text.RegularExpressions.Match id = System.Text.RegularExpressions.Regex.Match(started, @"Instance Id: (\S+)");
                return turns switch
                {
                    0 => CliEnv.Calls(("s1", "run_agent_async", """{"agentSwarmUri":"examples/script-writer","prompt":"Write a flow that summarizes a diff."}""")),
                    1 => CliEnv.Calls(("w1", "await_agent", $$"""{"ids":["{{id.Groups[1].Value}}"]}""")),
                    _ => CliEnv.Complete("orchestrated"),
                };
            }

            return turns == 0 ? CliEnv.Calls(("l1", "list_files", "{}")) : CliEnv.Complete("done: " + (req.System is null ? "" : "ok"));
        };
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Each_example_runs_end_to_end_on_a_scripted_model(string name)
    {
        using var env = new CliEnv();
        File.WriteAllText(Path.Combine(env.Root, "readme.txt"), "hello");
        Script(env.Provider);

        int code = await env.RunAsync("", ["run", "examples/" + name, "--model", "demo", "--prompt", "do the task", .. Flags(name)]);

        Assert.True(code == 0, env.Err);
        Assert.Contains(name == "orchestrator" ? "orchestrated" : "done", env.Out);
        Assert.Contains("list_files", env.Err);
        Assert.Contains("stopped", env.Err);
    }

    [Fact]
    public async Task The_orchestrator_starts_a_sub_agent_linked_to_it()
    {
        using var env = new CliEnv();
        Script(env.Provider);

        int code = await env.RunAsync("", "run", "examples/orchestrator", "--model", "demo", "--prompt", "split the job");
        Assert.True(code == 0, env.Err);
        Assert.Contains("orchestrated", env.Out);

        int listed = await env.RunAsync("", "instances", "list");
        Assert.Equal(0, listed);
        string[] rows = env.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(rows.Any(static r => r.Contains("script-writer", StringComparison.Ordinal) && r.Contains("stopped", StringComparison.Ordinal)), env.Out);
        Assert.True(rows.Any(static r => r.Contains("orchestrator", StringComparison.Ordinal)), env.Out);
    }

    [Theory]
    [InlineData("cs-tool-writer")]
    [InlineData("hook-writer")]
    [InlineData("provider-writer")]
    public async Task Examples_that_need_a_shell_do_not_load_without_the_flag(string name)
    {
        using var env = new CliEnv();
        Script(env.Provider);

        int code = await env.RunAsync("", "run", "examples/" + name, "--model", "demo", "--prompt", "x");

        Assert.Equal(ExitCodes.Setup, code);
        Assert.Contains("shell", env.Err, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Provider.Requests);
    }

    [Fact]
    public async Task An_unknown_example_is_a_usage_error_that_lists_the_known_ones()
    {
        using var env = new CliEnv();
        int code = await env.RunAsync("", "run", "examples/nope");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("cs-tool-writer", env.Err);
    }
}

public class VerbTests
{
    private sealed class HoldTool : ITool
    {
        public int Runs { get; private set; }

        public ToolSpec Definition { get; } = new("hold", "Needs approval.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!) { RequiresApproval = true };

        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(ToolResult.FromText("held ok"));
        }
    }

    private static string IdFrom(string err) =>
        System.Text.RegularExpressions.Regex.Match(err, @"^instance (\S+)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;

    private static void WriteAgent(CliEnv env, string tools, string file = "agent.yaml")
    {
        File.WriteAllText(
            Path.Combine(env.Root, file),
            $"name: probe\nmodel: demo\nmaxIterations: 5\nsystemPrompt: You probe.\nreferences:\n  - tools: [{tools}]\n");
    }

    [Fact]
    public async Task No_arguments_is_a_usage_error_and_help_and_unknown_verbs_behave()
    {
        using var env = new CliEnv();
        Assert.Equal(ExitCodes.Usage, await env.RunAsync(""));
        Assert.Equal(ExitCodes.Ok, await env.RunAsync("", "--help"));
        Assert.Contains("Exit codes", env.Out);
        Assert.Equal(ExitCodes.Ok, await env.RunAsync("", "run", "--help"));
        Assert.Contains("--allow-shell", env.Out);
        Assert.Equal(ExitCodes.Usage, await env.RunAsync("", "frobnicate"));
        Assert.Equal(ExitCodes.Usage, await env.RunAsync("", "run"));
        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "run", "missing.yaml"));
    }

    [Fact]
    public async Task A_tool_that_needs_a_shell_or_the_network_does_not_load_without_its_flag()
    {
        using var env = new CliEnv();
        WriteAgent(env, "execute_terminal_command, task_complete", "shell.yaml");
        WriteAgent(env, "http_request, task_complete", "net.yaml");

        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "run", "shell.yaml"));
        Assert.Contains("shell", env.Err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "run", "net.yaml"));
        Assert.Contains("network", env.Err, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Provider.Requests);

        env.Provider.Enqueue(CliEnv.Complete("fine"));
        Assert.Equal(ExitCodes.Ok, await env.RunAsync("", "run", "shell.yaml", "--allow-shell=echo", "--prompt", "go"));
        env.Provider.Enqueue(CliEnv.Complete("fine"));
        Assert.Equal(ExitCodes.Ok, await env.RunAsync("", "run", "net.yaml", "--allow-net=example.com", "--prompt", "go"));
    }

    [Fact]
    public async Task A_file_outside_the_root_is_refused_to_the_tool_not_read()
    {
        using var env = new CliEnv();
        string outside = Path.Combine(Path.GetTempPath(), "onellm-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "SECRET-CONTENT");
        try
        {
            WriteAgent(env, "read_file, task_complete");
            env.Provider.Enqueue(CliEnv.Calls(("r1", "read_file", new JsonObject { ["path"] = outside }.ToJsonString())), CliEnv.Complete("done"));

            int code = await env.RunAsync("", "run", "agent.yaml", "--prompt", "go");

            Assert.Equal(0, code);
            Assert.DoesNotContain("SECRET-CONTENT", env.Out + env.Err);
            string saved = string.Concat(Directory.EnumerateFiles(Path.Combine(env.Root, ".onellm", "instances")).Select(File.ReadAllText));
            Assert.DoesNotContain("SECRET-CONTENT", saved);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("y\n", true)]
    [InlineData("\n", false)]
    [InlineData("", false)]
    public async Task Approvals_are_asked_at_the_terminal(string typed, bool runs)
    {
        using var env = new CliEnv();
        var hold = new HoldTool();
        env.Extra = rt => rt.Tools.Add(hold);
        WriteAgent(env, "hold, task_complete");
        env.Provider.Enqueue(CliEnv.Calls(("h1", "hold", "{}")), CliEnv.Complete("finished"));

        int code = await env.RunAsync(typed, "run", "agent.yaml", "--prompt", "go");

        Assert.Equal(0, code);
        Assert.Contains("Approve hold", env.Err);
        Assert.Equal(runs ? 1 : 0, hold.Runs);
    }

    [Fact]
    public async Task Yes_approves_without_asking()
    {
        using var env = new CliEnv();
        var hold = new HoldTool();
        env.Extra = rt => rt.Tools.Add(hold);
        WriteAgent(env, "hold, task_complete");
        env.Provider.Enqueue(CliEnv.Calls(("h1", "hold", "{}")), CliEnv.Complete("finished"));

        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go", "--yes"));
        Assert.Equal(1, hold.Runs);
        Assert.DoesNotContain("Approve hold", env.Err);
    }

    private const string ShoutTool = "export const tool = { name: 'shout', description: 'Upper-cases text.', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } };\nexport function invoke({ text }) { return text.toUpperCase(); }\n";

    private static string ShowOf(string output, string tool) =>
        output.Split('\n').First(l => l.StartsWith("[tool] " + tool, StringComparison.Ordinal));

    [Fact]
    public async Task A_JavaScript_tool_file_named_with_tools_runs_for_an_agent()
    {
        using var env = new CliEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, "tools"));
        File.WriteAllText(Path.Combine(env.Root, "tools", "shout.js"), ShoutTool);
        WriteAgent(env, "shout, task_complete");
        env.Provider.Enqueue(CliEnv.Calls(("s1", "shout", """{"text":"hello"}""")), CliEnv.Complete("ok"));

        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go", "--tools", "tools"));
        string id = IdFrom(env.Err);
        await env.RunAsync("", "instances", "show", id);
        Assert.Contains("HELLO", ShowOf(env.Out, "shout"));

        // Without --tools the reference does not load.
        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go"));
        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "run", "agent.yaml", "--tools", "nowhere"));
    }

    [Fact]
    public async Task A_JavaScript_hook_listed_in_the_definition_blocks_a_tool_call()
    {
        using var env = new CliEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, "hooks"));
        File.WriteAllText(Path.Combine(env.Root, "hooks", "gate.js"), "export function beforeToolCall(s) { return s.call.tool === 'shout' ? { action: 'block', reason: 'not today' } : undefined; }\n");
        File.WriteAllText(Path.Combine(env.Root, "shout.js"), ShoutTool);
        File.WriteAllText(
            Path.Combine(env.Root, "agent.yaml"),
            "name: probe\nmodel: demo\nsystemPrompt: You probe.\nreferences:\n  - tools: [shout, task_complete]\nhooks:\n  - file: hooks/gate.js\n    points: [beforeToolCall]\n");
        env.Provider.Enqueue(CliEnv.Calls(("s1", "shout", """{"text":"hello"}""")), CliEnv.Complete("ok"));

        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go", "--tools", "shout.js"));
        string id = IdFrom(env.Err);
        await env.RunAsync("", "instances", "show", id);
        string shown = ShowOf(env.Out, "shout");
        Assert.Contains("not today", shown);
        Assert.DoesNotContain("HELLO", shown);
    }

    [Fact]
    public async Task The_js_tool_writer_writes_a_tool_file_and_checks_it_with_run_javascript()
    {
        using var env = new CliEnv();
        string write = new JsonObject { ["path"] = "tools/shout.js", ["content"] = ShoutTool }.ToJsonString();
        string check = new JsonObject { ["code"] = "return input.text.toUpperCase();", ["input"] = new JsonObject { ["text"] = "abc" } }.ToJsonString();
        env.Provider.Enqueue(CliEnv.Calls(("w1", "write_file", write)), CliEnv.Calls(("j1", "run_javascript", check)), CliEnv.Complete("tool written"));

        Assert.Equal(0, await env.RunAsync("", "run", "examples/js-tool-writer", "--model", "demo", "--prompt", "write shout"));
        string writerId = IdFrom(env.Err);
        Assert.True(File.Exists(Path.Combine(env.Root, "tools", "shout.js")));
        await env.RunAsync("", "instances", "show", writerId);
        Assert.Contains("ABC", ShowOf(env.Out, "run_javascript"));

        // The file the agent wrote loads as a tool.
        WriteAgent(env, "shout, task_complete");
        env.Provider.Enqueue(CliEnv.Calls(("s1", "shout", """{"text":"hey"}""")), CliEnv.Complete("ok"));
        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go", "--tools", "tools"));
    }

    [Fact]
    public async Task A_flow_runs_scripted_with_inputs_and_prints_the_last_answer()
    {
        using var env = new CliEnv();
        File.WriteAllText(
            Path.Combine(env.Root, "greet.flow"),
            "name: greet\nmodel: demo\nsystemPrompt: You greet.\ninitialPrompts:\n  - user: [ { text: \"Greet {{ .inputs.who }}\" } ]\n");
        env.Provider.EnqueueText("Hello, Ada!");

        int code = await env.RunAsync("", "run", "greet.flow", "--input", "who=Ada");

        Assert.Equal(0, code);
        Assert.Contains("Hello, Ada!", env.Out);
        Assert.Contains("Greet Ada", string.Concat(env.Provider.Requests[0].Messages.Select(static m => m.GetUserText())));
        Assert.Contains("script", env.Err);
    }

    [Fact]
    public async Task A_decider_directory_is_run_as_a_decider_and_errors_are_setup_errors()
    {
        using var env = new CliEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, "triage"));
        File.WriteAllText(
            Path.Combine(env.Root, "triage", "decider.yaml"),
            "model: no-such-model\nstate: \"{{ .inputs.t }}\"\nquestions:\n  q:\n    type: noul\n    instructions: Is it urgent?\n");

        int code = await env.RunAsync("", "run", "triage", "--input", "t=hi");

        Assert.Equal(ExitCodes.Setup, code);
        Assert.Contains("no-such-model", env.Err);
        Assert.Equal(ExitCodes.Usage, await env.RunAsync("", "run", "triage", "--prompt", "x"));
    }

    [Fact]
    public async Task Instances_can_be_listed_shown_forked_and_costed_after_a_run()
    {
        using var env = new CliEnv();
        WriteAgent(env, "task_complete");
        env.Provider.Enqueue(CliEnv.Complete("all done"));
        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go"));
        string id = IdFrom(env.Err);

        Assert.Equal(0, await env.RunAsync("", "instances", "list"));
        Assert.Contains(id, env.Out);
        Assert.Contains("stopped", env.Out);

        Assert.Equal(0, await env.RunAsync("", "instances", "list", "--state-filter", "error"));
        Assert.DoesNotContain(id, env.Out);

        Assert.Equal(0, await env.RunAsync("", "instances", "show", id));
        Assert.Contains("[user] go", env.Out);
        Assert.Contains("task_complete", env.Out);

        Assert.Equal(0, await env.RunAsync("", "cost", id));
        // 1000 input tokens at $1 per million plus 100 output at $2 per million.
        Assert.Contains("$0.001200", env.Out);

        Assert.Equal(0, await env.RunAsync("", "cost", "--since", "1d"));
        Assert.Contains("1 instance: $0.001200", env.Out);

        Assert.Equal(0, await env.RunAsync("", "instances", "fork", id));
        string forked = env.Out.Trim();
        Assert.NotEqual(id, forked);
        Assert.Equal(0, await env.RunAsync("", "instances", "show", forked));

        Assert.Equal(ExitCodes.Failed, await env.RunAsync("", "instances", "show", "nope"));
    }

    [Fact]
    public async Task Records_and_instances_default_to_a_folder_under_the_root()
    {
        using var env = new CliEnv();
        WriteAgent(env, "task_complete");
        env.Provider.Enqueue(CliEnv.Complete("x"));
        await env.RunAsync("", "run", "agent.yaml", "--prompt", "go");

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(env.Root, ".onellm", "instances")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(env.Root, ".onellm", "records"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Resume_sends_a_message_to_a_waiting_agent()
    {
        using var env = new CliEnv();
        WriteAgent(env, "task_complete");
        env.Provider.EnqueueText("What should I do?");
        Assert.Equal(0, await env.RunAsync("", "run", "agent.yaml", "--prompt", "go"));
        Assert.Contains("waiting", env.Err);
        string id = IdFrom(env.Err);

        env.Provider.Enqueue(CliEnv.Complete("thanks"));
        Assert.Equal(0, await env.RunAsync("", "instances", "resume", id, "--prompt", "wrap up"));

        Assert.Contains("stopped", env.Err);
        Assert.Contains("wrap up", string.Concat(env.Provider.Requests[^1].Messages.Select(static m => m.GetUserText())));
    }

    [Fact]
    public async Task Chat_streams_each_line_and_saves_one_instance()
    {
        using var env = new CliEnv();
        env.Provider.EnqueueText("Hi there");
        env.Provider.EnqueueText("Fine, thanks");

        int code = await env.RunAsync("hello\nand you?\n/exit\nignored\n", "chat", "demo");

        Assert.Equal(0, code);
        Assert.Contains("Hi there", env.Out);
        Assert.Contains("Fine, thanks", env.Out);
        Assert.Equal(2, env.Provider.Requests.Count);
        Assert.Single(Directory.GetFiles(Path.Combine(env.Root, ".onellm", "instances")));
    }

    [Fact]
    public async Task Catalog_lists_models_and_shows_each_value_with_its_layer()
    {
        using var env = new CliEnv();

        Assert.Equal(0, await env.RunAsync("", "catalog"));
        Assert.Contains("scripted:demo-1", env.Out);

        Assert.Equal(0, await env.RunAsync("", "catalog", "demo"));
        Assert.Contains("contextWindow: 100000", env.Out);
        Assert.Contains("[shipped]", env.Out);
        Assert.Contains("pricing.inputPerMillion: 1", env.Out);

        Assert.Equal(ExitCodes.Setup, await env.RunAsync("", "catalog", "does-not-exist"));
    }

    [Fact]
    public async Task Examples_lists_and_shows()
    {
        using var env = new CliEnv();
        Assert.Equal(0, await env.RunAsync("", "examples"));
        Assert.Contains("examples/orchestrator", env.Out);
        Assert.Equal(0, await env.RunAsync("", "examples", "show", "agent-writer"));
        Assert.Contains("name: agent-writer", env.Out);
    }

    [Fact]
    public async Task Cancellation_exits_130()
    {
        using var env = new CliEnv();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        env.Token = cts.Token;
        Assert.Equal(ExitCodes.Cancelled, await env.RunAsync("hello\n", "chat", "demo"));
    }
}
