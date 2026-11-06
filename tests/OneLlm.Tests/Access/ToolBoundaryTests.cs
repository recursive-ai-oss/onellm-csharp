using System.Net;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;
using OneLlm.Tests.Builtins;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Access;

/// <summary>Boundaries around the grants: shell environment, repository control files, the runtime's own state, address classes, connection pools, glob cost.</summary>
public class ToolBoundaryTests
{
    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "These cases use POSIX programs.");

    private static string Shell(string command, string[]? args = null, string[]? env = null) =>
        new JsonObject
        {
            ["command"] = command,
            ["args"] = args is null ? null : new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["env"] = env is null ? null : new JsonArray([.. env.Select(a => (JsonNode?)JsonValue.Create(a))]),
        }.ToJsonString();

    // ---- shell environment

    [Fact]
    public async Task A_model_cannot_set_environment_variables_for_an_allow_listed_program()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("env");
        foreach (string pair in new[] { "GIT_CONFIG_COUNT=1", "PAGER=sh", "HOME=/tmp", "PYTHONPATH=/x", "MY_VAR=1" })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", Shell("env", env: [pair]), caps);
            Assert.True(r.IsError, pair);
            Assert.Contains("Setting the environment variable", Kit.Text(r), StringComparison.Ordinal);
        }

        // The host can name the variables the model may set.
        ToolResult ok = await Kit.Run("execute_terminal_command", Shell("env", env: ["MY_VAR=hello"]), caps, new BuiltinOptions { ShellModelEnvironmentNames = ["MY_VAR"] });
        Assert.False(ok.IsError, Kit.Text(ok));
        Assert.Contains("MY_VAR=hello", Kit.Text(ok), StringComparison.Ordinal);

        // The same holds when each command needs approval.
        Capabilities approval = Capabilities.Files(s.Root) + Capabilities.ShellWithApproval(static (_, _) => ValueTask.FromResult(true));
        Assert.True((await Kit.Run("execute_terminal_command", Shell("env", env: ["GIT_CONFIG_COUNT=1"]), approval)).IsError);
    }

    [Fact]
    public async Task An_allow_listed_program_starts_from_a_minimal_environment()
    {
        UnixOnly();
        using var s = new Scratch();
        const string name = "ONELLM_BOUNDARY_PROBE";
        Environment.SetEnvironmentVariable(name, "inherited-value");
        try
        {
            Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("env");
            string text = Kit.Text(await Kit.Run("execute_terminal_command", Shell("env"), caps));
            Assert.Contains("PATH=", text, StringComparison.Ordinal);
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);

            string named = Kit.Text(await Kit.Run("execute_terminal_command", Shell("env"), caps, new BuiltinOptions { ShellInheritedEnvironmentNames = [name] }));
            Assert.Contains(name + "=inherited-value", named, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ---- repository control files

    [Theory]
    [InlineData(".git/config")]
    [InlineData("sub/.git/hooks/pre-commit")]
    [InlineData(".GIT/config")]
    [InlineData(".gitmodules")]
    [InlineData(".hg/hgrc")]
    public async Task File_tools_refuse_to_write_repository_control_files(string path)
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        string body = new JsonObject { ["path"] = path, ["content"] = "[core]\n\tfsmonitor = touch pwned\n" }.ToJsonString();
        ToolResult w = await Kit.Run("write_file", body, caps);
        Assert.True(w.IsError);
        Assert.Contains("repository control", Kit.Text(w), StringComparison.Ordinal);
        Assert.False(File.Exists(s.In(path)));

        string patch = $"--- /dev/null\n+++ b/{path}\n@@ -0,0 +1 @@\n+x\n";
        ToolResult p = await Kit.Run("apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString(), caps);
        Assert.True(p.IsError);
        Assert.False(File.Exists(s.In(path)));
    }

    [Fact]
    public async Task Repository_control_files_stay_readable_and_can_be_granted()
    {
        using var s = new Scratch();
        s.Write(".git/config", "[core]\n");
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Contains("[core]", Kit.Text(await Kit.Run("read_file", Kit.One("path", Kit.Js(".git/config")), caps)), StringComparison.Ordinal);
        ToolResult r = await Kit.Run("replace_string", """{"path":".git/config","old_string":"[core]","new_string":"[x]"}""", caps);
        Assert.True(r.IsError);
        Assert.Equal("[core]\n", File.ReadAllText(s.In(".git/config")));

        ToolResult granted = await Kit.Run("write_file", """{"path":".git/info-note","content":"ok"}""", caps.WithRepoControlWrites());
        Assert.False(granted.IsError, Kit.Text(granted));
        // A root that is itself inside a control directory is the host's choice.
        Assert.False((await Kit.Run("write_file", """{"path":"note","content":"ok"}""", Capabilities.Files(s.In(".git")))).IsError);
    }

    [Fact]
    public async Task The_git_config_route_to_running_a_command_is_closed()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("git status");
        string pwn = s.In("PWNED");
        ToolResult init = await Kit.Run("execute_terminal_command", Shell("git", ["init", "-q"]), Capabilities.Files(s.Root) + Capabilities.ShellCommands("git init"));
        Assert.False(init.IsError, Kit.Text(init));
        ToolResult w = await Kit.Run("write_file", new JsonObject { ["path"] = ".git/config", ["content"] = $"[core]\n\tfsmonitor = touch {pwn}\n" }.ToJsonString(), caps);
        Assert.True(w.IsError);
        _ = await Kit.Run("execute_terminal_command", Shell("git", ["status"]), caps);
        Assert.False(File.Exists(pwn));
    }

    // ---- the runtime's own state

    [Fact]
    public async Task File_tools_cannot_rewrite_the_default_state_directory_but_todo_and_memory_still_work()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.True((await Kit.Run("write_file", """{"path":".onellm/instances/a.yaml","content":"x"}""", caps)).IsError);
        Assert.True((await Kit.Run("write_file", """{"path":".onellm/memory.md","content":"x"}""", caps)).IsError);
        Assert.False((await Kit.Run("memory", """{"action":"append","content":"note"}""", caps)).IsError);
        Assert.Equal("note", Kit.Text(await Kit.Run("memory", "{}", caps)));
    }

    [Fact]
    public void A_runtime_protects_its_file_store_and_record_directories_wherever_they_are()
    {
        using var s = new Scratch();
        var rt = new Runtime
        {
            Capabilities = Capabilities.Files(s.Root),
            Store = new FileInstanceStore(s.In("state/runs")),
        };
        rt.Sinks.Add(new JsonlRecordSink(s.In("audit")));
        Assert.Throws<CapabilityMissingException>(() => rt.Capabilities.ResolveWrite("state/runs/x.yaml"));
        Assert.Throws<CapabilityMissingException>(() => rt.Capabilities.ResolveWrite("audit/_runtime.jsonl"));
        Assert.Equal(s.In("state/other.txt"), rt.Capabilities.ResolveWrite("state/other.txt"));
        Assert.Same(rt.Capabilities, rt.Capabilities);

        // Intersections keep every protection.
        ICapabilities child = CapabilityIntersection.Of(rt.Capabilities, Capabilities.Files(s.Root));
        Assert.Throws<CapabilityMissingException>(() => child.ResolveWrite("audit/_runtime.jsonl"));
        // Reading stays possible.
        Assert.Equal(s.In("audit/_runtime.jsonl"), rt.Capabilities.ResolveRead("audit/_runtime.jsonl"));
    }

    // ---- network

    [Theory]
    [InlineData("localhost")]
    [InlineData("*.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.0/8")]
    public void An_intersection_never_reaches_an_address_class_one_side_lacks(string entry)
    {
        Capabilities any = Capabilities.AnyPublicHosts;
        Capabilities narrow = Capabilities.Network(entry, "example.com");
        foreach (Capabilities c in new[] { any.Intersect(narrow), narrow.Intersect(any) })
        {
            Assert.True(c.IsNetworkAllowed(new Uri("https://example.com/")) || c.AnyPublicNetwork == false);
            Assert.False(c.IsNetworkAllowed(new Uri("http://localhost:1/")));
            Assert.False(c.IsNetworkAllowed(new Uri("http://foo.localhost/")));
            Assert.False(c.IsNetworkAllowed(new Uri("http://127.0.0.1/")));
            Assert.False(c.IsNetworkAllowed(new Uri("http://10.1.2.3/")));
            Assert.False(c.IsAddressAllowed(new Uri("http://localhost:1/"), IPAddress.Loopback));
        }

        Assert.True(any.Intersect(narrow).IsNetworkAllowed(new Uri("https://example.com/")));
        Assert.False(CapabilityIntersection.Of(any, Capabilities.Network("localhost")).IsNetworkAllowed(new Uri("http://localhost/")));
    }

    [Theory]
    [InlineData("::127.0.0.1", true)]
    [InlineData("::ffff:0:7f00:1", true)]
    [InlineData("::ffff:0:a00:1", true)]
    [InlineData("64:ff9b:1::1", true)]
    [InlineData("::ffff:0:808:808", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Translated_ipv6_forms_are_judged_by_what_they_reach(string address, bool special) =>
        Assert.Equal(special, EgressPolicy.IsSpecialAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task A_narrower_grant_cannot_use_a_connection_a_wider_grant_opened()
    {
        using var server = new StubServer(_ => new StubResponse(200, "hello"), keepAlive: true);
        string url = $"http://localhost:{server.Port}/";
        Capabilities wide = Capabilities.Network("localhost");
        Capabilities narrow = Capabilities.AnyPublicHosts;

        using (HttpResponseMessage ok = await EgressClient.SendAsync(wide, HttpMethod.Get, new Uri(url), [], null, default))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // The wide grant's connection is idle in a pool now. The narrow grant must still be refused at connect time.
        await Assert.ThrowsAsync<HttpRequestException>(() => EgressClient.SendAsync(narrow, HttpMethod.Get, new Uri(url), [], null, default));
        Assert.Single(server.Requests);

        // Equal policies share a pool; different ones never do.
        Assert.Equal(EgressClient.PolicyKey(Capabilities.Network("a.example", "b.example")), EgressClient.PolicyKey(Capabilities.Network("b.example", "a.example")));
        Assert.NotEqual(EgressClient.PolicyKey(wide), EgressClient.PolicyKey(narrow));
    }

    [Fact]
    public async Task A_redirect_to_another_origin_keeps_only_plain_headers_and_never_a_body()
    {
        using var target = new StubServer(_ => new StubResponse(200, "done"));
        using var origin = new StubServer(r => r.Target == "/post"
            ? new StubResponse(307, "", Headers: new() { ["Location"] = $"http://127.0.0.1:{target.Port}/landed" })
            : new StubResponse(302, "", Headers: new() { ["Location"] = $"http://127.0.0.1:{target.Port}/landed" }));
        Capabilities caps = Capabilities.Network("127.0.0.1");
        KeyValuePair<string, string>[] headers =
        [
            new("Authorization", "Bearer t"), new("X-Session-Id", "s1"), new("X-Custom", "c"), new("Accept", "text/html"),
        ];

        using (HttpResponseMessage r = await EgressClient.SendAsync(caps, HttpMethod.Get, new Uri(origin.BaseUri, "get"), headers, null, default))
        {
            Assert.Equal("done", await r.Content.ReadAsStringAsync());
        }

        Dictionary<string, string> seen = target.Requests.Single().Headers;
        Assert.Equal("text/html", seen["Accept"]);
        Assert.False(seen.ContainsKey("Authorization"));
        Assert.False(seen.ContainsKey("X-Session-Id"));
        Assert.False(seen.ContainsKey("X-Custom"));

        await Assert.ThrowsAsync<HttpRequestException>(() => EgressClient.SendAsync(caps, HttpMethod.Post, new Uri(origin.BaseUri, "post"), headers, "secret body", default));
        Assert.Single(target.Requests);
    }

    // ---- glob

    [Fact]
    public async Task A_pattern_with_many_stars_matches_in_linear_time()
    {
        string name = new('a', 40);
        string pattern = "*a*a*a*a*a*a*a*a*a*a*a*a*b";
        Task<bool> match = Task.Run(() => Glob.Match(pattern, name));
        Assert.Same(match, await Task.WhenAny(match, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(await match);
        Assert.True(Glob.Match("*a*a*a*a*a*a*a*a*a*a*a*a*", name));
        Assert.True(Glob.Match("*a*a*a*a*b", name + "b"));
    }

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "dir/a.cs", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("a*/c", "abc/c", true)]
    [InlineData("a*/c", "ab/d/c", false)]
    [InlineData("*/*.txt", "x/y.txt", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("[a-c]x", "bx", true)]
    [InlineData("[^a-c]x", "bx", false)]
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "aXb", false)]
    [InlineData("**", "abc", true)]
    [InlineData("", "", true)]
    [InlineData("*", "", true)]
    [InlineData("a*", "a", true)]
    [InlineData("*a", "ba", true)]
    [InlineData("*a", "bab", false)]
    public void Glob_keeps_its_meaning(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Glob.Match(pattern, name));

    [Fact]
    public async Task A_hostile_exclusion_pattern_cannot_stall_list_files()
    {
        using var s = new Scratch();
        s.Write(new string('a', 40), "x");
        var run = Task.Run(async () => await Kit.Run("list_files", """{"exclusionPatterns":["*a*a*a*a*a*a*a*a*a*a*b"]}""", Capabilities.Files(s.Root)));
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Contains(new string('a', 40), Kit.Text(await run), StringComparison.Ordinal);
    }

    // ---- links in listings

    [Fact]
    public async Task Listings_say_a_link_is_a_link_without_showing_where_it_points()
    {
        UnixOnly();
        using var s = new Scratch();
        Directory.CreateSymbolicLink(s.In("out-link"), s.Outside);
        string tree = Kit.Text(await Kit.Run("list_files", "{}", Capabilities.Files(s.Root)));
        Assert.Contains("out-link/ (symlink)", tree, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Outside, tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decider_file_that_is_a_link_out_of_the_grants_is_not_read()
    {
        UnixOnly();
        using var s = new Scratch();
        var h = new Deciders.DeciderHarness();
        Directory.CreateDirectory(s.In("dec"));
        File.WriteAllText(Path.Combine(s.Outside, "secret.yaml"), "model: dec-1\nquestions: {}\n");
        File.CreateSymbolicLink(s.In("dec/decider.yaml"), Path.Combine(s.Outside, "secret.yaml"));
        ITool tool = BuiltinTools.Create().Single(static t => t.Definition.Name == "run_decider");
        var ctx = new ToolContext("agent-1", "call-1", TimeProvider.System) { Capabilities = Capabilities.Files(s.Root), Runtime = h.Runtime };
        ToolResult r = await ToolInvoker.InvokeAsync(tool, (JsonObject)JsonNode.Parse("""{"decider":"dec"}""")!, ctx, default);
        Assert.True(r.IsError);
        Assert.Contains("outside the directories granted", Kit.Text(r), StringComparison.Ordinal);
    }
}
