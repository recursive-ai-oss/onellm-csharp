using System.Text.Json.Nodes;
using OneLlm.Definitions;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Builtins;

public class NetworkToolTests
{
    private const string BraveJson = """
        {"query":{"original":"dotnet aot"},
         "web":{"results":[
           {"title":"First","url":"https://a.example/1","description":"About AOT","age":"2 days ago","extra_snippets":["more one","more two"]},
           {"title":"Second","url":"https://b.example/2","description":"Other"}]},
         "videos":{"results":[{"title":"Vid","url":"https://v.example/3","description":"A video"}]}}
        """;

    [Fact]
    public async Task Http_request_returns_status_headers_and_body_from_a_granted_host()
    {
        using var server = new StubServer(r => new StubResponse(200, $"{r.Method} {r.Target} [{r.Body}]", "application/json"));
        Capabilities caps = Capabilities.Network("127.0.0.1");
        string url = Kit.Js(server.BaseUri + "api?q=1");
        string get = Kit.Text(await Kit.Run("http_request", Kit.One("url", url), caps));
        Assert.StartsWith("HTTP 200 OK\nContent-Type: application/json", get, StringComparison.Ordinal);
        Assert.EndsWith("\n\nGET /api?q=1 []", get, StringComparison.Ordinal);
        string post = Kit.Text(await Kit.Run("http_request", $$$"""{"url":{{{url}}},"method":"post","body":"hi","headers":{"X-Test":"1"}}""", caps));
        Assert.EndsWith("POST /api?q=1 [hi]", post, StringComparison.Ordinal);
        Assert.Equal("1", server.Requests[^1].Headers["X-Test"]);
    }

    [Fact]
    public async Task Http_request_to_an_ungranted_host_or_through_a_redirect_is_refused()
    {
        using var hidden = new StubServer(_ => new StubResponse(200, "secret"));
        using var server = new StubServer(_ => new StubResponse(302, "", Headers: new() { ["Location"] = $"http://localhost:{hidden.Port}/" }));
        string url = Kit.Js(server.BaseUri.ToString());
        ToolResult off = await Kit.Run("http_request", Kit.One("url", url), Capabilities.Files("."));
        Assert.True(off.IsError);
        Assert.Contains("Network access to 127.0.0.1 is not available", Kit.Text(off), StringComparison.Ordinal);
        ToolResult redirected = await Kit.Run("http_request", Kit.One("url", url), Capabilities.Network("127.0.0.1"));
        Assert.True(redirected.IsError);
        Assert.Empty(hidden.Requests);
    }

    [Fact]
    public async Task Http_request_describes_binary_bodies_and_cuts_long_ones()
    {
        using var server = new StubServer(r => r.Target == "/bin"
            ? new StubResponse(200, ContentType: "image/png", Bytes: [1, 2, 3])
            : new StubResponse(200, new string('z', 5000)));
        Capabilities caps = Capabilities.Network("127.0.0.1");
        Assert.Contains("[Binary or non-text body: 3 bytes, image/png]", Kit.Text(await Kit.Run("http_request", Kit.One("url", Kit.Js(server.BaseUri + "bin")), caps)), StringComparison.Ordinal);
        string cut = Kit.Text(await Kit.Run("http_request", Kit.One("url", Kit.Js(server.BaseUri + "long")), caps, new BuiltinOptions { HttpMaxBodyBytes = 1024 }));
        Assert.Contains("[Body truncated: showing the first 1024 bytes.]", cut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_web_prints_numbered_results_and_adds_the_query_price()
    {
        using var server = new StubServer(_ => new StubResponse(200, BraveJson, "application/json"));
        var provider = new BraveSearchProvider(new Secret("test-key-123"), new Uri(server.BaseUri, "res/v1/web/search"));
        var options = new BuiltinOptions { SearchProvider = provider };
        var ctx = new ToolContext("i", "c", TimeProvider.System) { Capabilities = Capabilities.Network("127.0.0.1") };
        ToolResult r = await ToolInvoker.InvokeAsync(Kit.Tool("search_web", options), (JsonObject)JsonNode.Parse("""{"query":"dotnet aot","count":50}""")!, ctx, CancellationToken.None);
        Assert.Equal(
            "Web Search Results for: dotnet aot\nFound 2 results\n\n1. First\n   URL: https://a.example/1\n   Age: 2 days ago\n   About AOT\n   - more one\n   - more two\n\n2. Second\n   URL: https://b.example/2\n   Other\n\n\nVideo Results: 1\n\n1. Vid\n   URL: https://v.example/3\n   A video\n\n",
            Kit.Text(r));
        StubRequest sent = server.Requests.Single();
        Assert.Equal("/res/v1/web/search?q=dotnet%20aot&count=20", sent.Target);
        Assert.Equal("test-key-123", sent.Headers["X-Subscription-Token"]);
        CostLine cost = r.CostLines.Single();
        Assert.Equal("toolCalls.search_web", cost.Counter);
        Assert.Equal(0.005m, cost.Amount);
        Assert.Equal("https://api-dashboard.search.brave.com/documentation/pricing", cost.Source);
        Assert.DoesNotContain("test-key-123", Kit.Text(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_web_needs_the_network_grant_for_the_providers_host()
    {
        using var server = new StubServer(_ => new StubResponse(200, BraveJson, "application/json"));
        var options = new BuiltinOptions { SearchProvider = new BraveSearchProvider(new Secret("k"), new Uri(server.BaseUri, "s")) };
        ToolResult r = await Kit.Run("search_web", """{"query":"x"}""", Capabilities.Files("."), options);
        Assert.True(r.IsError);
        Assert.Empty(server.Requests);
        Assert.Empty(r.CostLines);
    }

    [Fact]
    public async Task Search_web_retries_a_429_and_scrubs_the_key_from_errors()
    {
        int calls = 0;
        using var retry = new StubServer(_ => ++calls == 1
            ? new StubResponse(429, "slow down", Headers: new() { ["Retry-After"] = "0" })
            : new StubResponse(200, BraveJson, "application/json"));
        var ok = new BuiltinOptions { SearchProvider = new BraveSearchProvider(new Secret("k"), new Uri(retry.BaseUri, "s")) };
        ToolResult r = await Kit.Run("search_web", """{"query":"x"}""", Capabilities.Network("127.0.0.1"), ok);
        Assert.False(r.IsError, Kit.Text(r));
        Assert.Equal(2, retry.Requests.Count);

        using var failing = new StubServer(_ => new StubResponse(500, "oops key=hunter2-secret"));
        var bad = new BuiltinOptions { SearchProvider = new BraveSearchProvider(new Secret("hunter2-secret"), new Uri(failing.BaseUri, "s")) };
        ToolResult e = await Kit.Run("search_web", """{"query":"x"}""", Capabilities.Network("127.0.0.1"), bad);
        Assert.True(e.IsError);
        Assert.StartsWith("Error: Brave API error (status 500): ", Kit.Text(e), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-secret", Kit.Text(e), StringComparison.Ordinal);
        Assert.Empty(e.CostLines);
    }

    [Fact]
    public void Search_web_is_left_out_without_a_provider()
    {
        Assert.DoesNotContain(BuiltinTools.Create().Select(t => t.Definition.Name), n => n == "search_web");
        Assert.Contains(BuiltinTools.Create(new BuiltinOptions { SearchProvider = new BraveSearchProvider(new Secret("k")) }).Select(t => t.Definition.Name), n => n == "search_web");
    }

    [Fact]
    public void The_key_never_shows_in_the_providers_text_form()
    {
        var p = new BraveSearchProvider(new Secret("abc-secret-key"));
        Assert.DoesNotContain("abc-secret-key", p.ToString(), StringComparison.Ordinal);
    }
}

public class StateToolTests
{
    [Fact]
    public async Task Todo_keeps_a_list_per_instance_across_calls()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("The todo list is empty.", Kit.Text(await Kit.Run("todo", "{}", caps)));
        string set = Kit.Text(await Kit.Run("todo", """{"todos":[{"content":"write","status":"completed"},{"content":"test","status":"in_progress"},{"content":"ship","status":"pending"}]}""", caps));
        Assert.Equal("Todo list (1 of 3 done):\n1. [x] write\n2. [~] test\n3. [ ] ship", set);
        Assert.Equal(set, Kit.Text(await Kit.Run("todo", "{}", caps)));
        Assert.True(File.Exists(s.In(".onellm/todo-inst-1.json")));
        ToolResult bad = await Kit.Run("todo", """{"todos":[{"content":"x","status":"later"}]}""", caps);
        Assert.True(bad.IsError);
        Assert.Equal(set, Kit.Text(await Kit.Run("todo", "{}", caps)));
    }

    [Fact]
    public async Task Memory_reads_appends_writes_and_clears()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("(memory is empty)", Kit.Text(await Kit.Run("memory", "{}", caps)));
        await Kit.Run("memory", """{"action":"append","content":"fact one"}""", caps);
        await Kit.Run("memory", """{"action":"append","content":"fact two"}""", caps);
        Assert.Equal("fact one\nfact two", Kit.Text(await Kit.Run("memory", """{"action":"read"}""", caps)));
        await Kit.Run("memory", """{"action":"write","content":"fresh"}""", caps);
        Assert.Equal("fresh", Kit.Text(await Kit.Run("memory", "{}", caps)));
        await Kit.Run("memory", """{"action":"clear"}""", caps);
        Assert.Equal("(memory is empty)", Kit.Text(await Kit.Run("memory", "{}", caps)));
        ToolResult big = await Kit.Run("memory", $$"""{"action":"write","content":"{{new string('x', 70000)}}"}""", caps);
        Assert.True(big.IsError);
    }

    [Fact]
    public async Task The_state_files_follow_the_write_grant()
    {
        using var s = new Scratch();
        ToolResult r = await Kit.Run("memory", """{"action":"append","content":"x"}""", Capabilities.ReadOnlyFiles(s.Root));
        Assert.True(r.IsError);
        Assert.False(Directory.Exists(s.In(".onellm")));
        var escape = new BuiltinOptions { StateDirectory = "../outside/state" };
        Assert.True((await Kit.Run("todo", """{"todos":[]}""", Capabilities.Files(s.Root), escape)).IsError);
        Assert.False(Directory.Exists(Path.Combine(s.Outside, "state")));
    }

    [Fact]
    public async Task Ask_user_returns_a_result_the_loop_can_recognize()
    {
        ToolResult r = await Kit.Run("ask_user", """{"question":"Which branch?","choices":["main","dev"]}""", Capabilities.None);
        Assert.False(r.IsError);
        Assert.True(AskUser.TryGetRequest(r, out AskUserRequest req));
        Assert.Equal("Which branch?", req.Question);
        Assert.Equal(["main", "dev"], req.Choices);
        Assert.False(AskUser.TryGetRequest(ToolResult.FromText("hello"), out _));
    }
}

public class ReferenceGrantTests
{
    private const string Agent = """
        model: test-model
        systemPrompt: hi
        references:
          - routine: /agent-tools/os/read-file
          - routine: /agent-tools/os/execute-terminal-command
        """;

    private static ToolReferenceResolver Resolver(ICapabilities caps) => new(BuiltinTools.Create(), caps);

    [Fact]
    public void Strict_load_fails_on_a_reference_whose_grant_is_missing_and_names_it()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root));
        var e = Assert.Throws<BadDefinitionException>(() => AgentDefinition.Parse(Agent, "a.yaml", r.Strict));
        Assert.Contains("execute-terminal-command", e.Message, StringComparison.Ordinal);
        Assert.Equal(6, e.Line);
    }

    [Fact]
    public void Strict_load_succeeds_when_the_grants_cover_the_references()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root) + Capabilities.Shell);
        Assert.Equal(2, AgentDefinition.Parse(Agent, "a.yaml", r.Strict).References.Count);
    }

    [Fact]
    public void Lenient_load_strips_the_ungranted_reference_and_warns()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root));
        AgentDefinition d = AgentDefinition.Parse(Agent, "a.yaml", r.Lenient);
        Assert.Equal("/agent-tools/os/read-file", Assert.Single(d.References).Routine);
        Assert.Contains("execute-terminal-command", Assert.Single(d.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_source_or_unregistered_tool_is_not_granted_and_a_definition_cannot_grant()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root));
        Assert.False(r.IsGranted(new ToolReference { Routine = "/somewhere/else/tool" }));
        Assert.False(r.IsGranted(new ToolReference { Tools = ["no_such_tool"] }));
        Assert.False(r.IsGranted(new ToolReference()));
        Assert.True(r.IsGranted(new ToolReference { Tools = ["read_file", "todo"] }));
        Assert.False(r.IsGranted(new ToolReference { Tools = ["read_file", "http_request"] }));
        Assert.False(r.IsGranted(new ToolReference { Routine = "/agent-tools/os/read-file", Tools = ["execute_terminal_command"] }));
        // Extension keys on a reference are carried, not interpreted: they cannot grant anything.
        var sneaky = new ToolReference { Tools = ["execute_terminal_command"], ExtensionData = new() { ["grant"] = "shell", ["capabilities"] = "all" } };
        Assert.False(r.IsGranted(sneaky));
    }

    [Fact]
    public void Groups_need_every_tool_granted_but_the_usable_part_is_available()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root));
        var group = new ToolReference { Swarm = "/agent-tools/os" };
        ReferenceCheck check = r.Check(group);
        Assert.False(check.IsGranted);
        Assert.Equal(["read_file", "write_file", "replace_string", "list_files", "search_files_regex"], check.GrantedTools.Select(t => t.Definition.Name));
        Assert.Contains(check.Problems, p => p.Contains("execute_terminal_command", StringComparison.Ordinal) && p.Contains("shell access", StringComparison.Ordinal));
        Assert.Equal(5, r.ToolsFor([group]).Count);
    }

    [Fact]
    public void Web_references_name_the_package_to_add_and_mapped_sources_resolve()
    {
        using var s = new Scratch();
        ToolReferenceResolver r = Resolver(Capabilities.Files(s.Root) + Capabilities.AnyPublicHosts);
        ReferenceCheck web = r.Check(new ToolReference { Routine = "/agent-tools/web/fetch-webpage-markdown" });
        Assert.Contains(web.Problems, p => p.Contains("RecursiveAI.OneLlm.Web", StringComparison.Ordinal));
        r.MapSource("/team/standard-toolset", "read_file", "list_files");
        Assert.True(r.IsGranted(new ToolReference { Swarm = "/team/standard-toolset" }));
    }

    [Fact]
    public void Routine_paths_map_to_the_tool_names_definitions_use()
    {
        Assert.Equal(["read_file"], BuiltinTools.ToolsForRoutine("/agent-tools/os/read-file")!);
        Assert.Equal(["search_files_regex"], BuiltinTools.ToolsForRoutine("agent-tools/os/regex-file-search")!);
        Assert.Equal(["execute_terminal_command"], BuiltinTools.ToolsForRoutine("execute_terminal_command")!);
        Assert.Equal(["search_web"], BuiltinTools.ToolsForRoutine("/agent-tools/web/search-web")!);
        Assert.Null(BuiltinTools.ToolsForRoutine("/team/projects/board/board-status"));
    }
}
