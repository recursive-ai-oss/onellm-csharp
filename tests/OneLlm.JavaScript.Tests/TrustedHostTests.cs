using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Secrets;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.JavaScript.Tests;

public sealed class TrustedHostTests
{
    [Fact]
    public async Task Trusted_tool_executes_imports_reads_outside_and_preserves_caller()
    {
        using var root = new Scratch();
        string tools = Path.Combine(root.Root, "tools");
        Directory.CreateDirectory(tools);
        string outside = Path.Combine(root.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.WriteAllText(Path.Combine(tools, "shared.mjs"), "export const value = 42;");
        string path = Path.Combine(tools, "entry.js");
        File.WriteAllText(path, """
            import { value } from './shared.mjs';
            export const tool = { name: 'trusted_entry', description: 'trusted example' };
            export async function invoke(input, ctx) {
              const process = await ctx.exec('printf process');
              const direct = await ctx.exec('/bin/sh', ['-c', 'printf direct; exit 7']);
              const nested = await ctx.callTool('identity_echo', {});
              return { value, file: ctx.readFile(input.path), process, direct, nested, identity: ctx.identity };
            }
            """);
        var registry = new ToolRegistry();
        var caller = new ToolContext("calling-instance", "call", TimeProvider.System) { Services = new Marker() };
        registry.Add(new IdentityTool(caller));
        var options = new JsSandboxOptions
        {
            TrustedHost = true,
            ResolveHostContext = _ => new JsHostContext { Tools = registry, Identity = new JsonObject { ["person"] = "person-a" } },
        };
        ITool tool = JsTools.Load(path, options);
        ToolResult result = await tool.InvokeAsync(new JsonObject { ["path"] = outside }, caller, CancellationToken.None);
        Assert.False(result.IsError, Js.Text(result));
        JsonNode node = JsonNode.Parse(Js.Text(result))!;
        Assert.Equal(42, node["value"]!.GetValue<int>());
        Assert.Equal("outside", node["file"]!.GetValue<string>());
        Assert.Equal("process", node["process"]!["stdout"]!.GetValue<string>());
        Assert.Equal(0, node["process"]!["exitCode"]!.GetValue<int>());
        Assert.Equal(7, node["direct"]!["exitCode"]!.GetValue<int>());
        Assert.Equal("direct", node["direct"]!["stdout"]!.GetValue<string>());
        Assert.Contains("calling-instance", node["nested"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("person-a", node["identity"]!["person"]!.GetValue<string>());
    }

    [Fact]
    public async Task Trusted_definition_hook_imports_and_executes_without_grants()
    {
        using var env = new HookEnv();
        env.Rt.Capabilities = Capabilities.None;
        env.Rt.DefinitionHooks = new JsHookSource(new JsSandboxOptions { TrustedHost = true }, env.Rt.TimeProvider);
        env.Hook("helper.mjs", "export const prefix = 'module-';");
        env.Hook("open-hook.js", """
            import { prefix } from './helper.mjs';
            export async function beforeInference(snapshot, ctx) {
              const process = await ctx.exec('printf hook');
              return { action: 'modify', system: prefix + process.stdout };
            }
            """);
        env.Provider.Enqueue(HookEnv.Say("done"));
        await HookEnv.Run(env, env.Def([], "open-hook.js"));
        Assert.Equal("module-hook", env.Provider.Requests[0].System);
    }

    [Fact]
    public async Task Trusted_network_needs_neither_requires_nor_grants()
    {
        using var server = new StubServer(_ => new StubResponse(200, "reachable"));
        var sandbox = new JsSandbox(new JsSandboxOptions { TrustedHost = true });
        JsRunResult result = await sandbox.RunSnippetAsync("return (await ctx.fetch(input.url)).text;", new JsonObject { ["url"] = server.BaseUri.ToString() }, Js.Context(), ToolCapability.None);
        Assert.Equal("reachable", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task Default_mode_has_no_exec_and_refuses_network_and_ungranted_shell()
    {
        var sandbox = new JsSandbox();
        await Assert.ThrowsAsync<JsSandboxException>(() => sandbox.RunSnippetAsync("return ctx.exec('printf forbidden');", null, Js.Context(), ToolCapability.None));
        await Assert.ThrowsAsync<JsSandboxException>(() => sandbox.RunSnippetAsync("return ctx.fetch('https://example.invalid/');", null, Js.Context(), ToolCapability.Network));
        var registry = new ToolRegistry();
        Assert.True(registry.TryGet("execute_terminal_command", out ITool shell));
        ToolResult result = await ToolInvoker.InvokeAsync(shell, new JsonObject { ["command"] = "printf forbidden" }, Js.Context(), CancellationToken.None);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Secret_scope_is_ordered_opaque_and_scrubs_results_errors_and_logs()
    {
        const string value = "synthetic-private-token-123456";
        var store = new MemorySecretStore();
        store.Set("person-a", "api", new Secret(value));
        store.Set("person-b", "other", new Secret("synthetic-inaccessible-token"));
        var owners = new[] { "person-a" };
        var scope = new SecretScope(store, owners);
        owners[0] = "person-b"; // The scope must snapshot the host's choice.
        Assert.Equal(value, (await scope.GetAsync("api"))!.Value);
        Assert.Null(await scope.GetAsync("other"));
        var logs = new Logs();
        var context = Js.Context(log: logs);
        var sandbox = new JsSandbox(new JsSandboxOptions { ResolveHostContext = _ => new JsHostContext { Secrets = scope } });
        JsRunResult result = await sandbox.RunSnippetAsync("const s = await ctx.secret('api'); ctx.log(s); console.log(s); return {s};", null, context, ToolCapability.None);
        Assert.DoesNotContain(value, result.Value!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(value, string.Join("\n", logs.Lines), StringComparison.Ordinal);
        Assert.Contains(Secret.Mask, result.Value.ToJsonString(), StringComparison.Ordinal);
        JsSandboxException failure = await Assert.ThrowsAsync<JsSandboxException>(() => sandbox.RunSnippetAsync("throw new Error(await ctx.secret('api'));", null, context, ToolCapability.None));
        Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<JsSandboxException>(() => sandbox.RunSnippetAsync("return await ctx.secret('other');", null, context, ToolCapability.None));
    }

    [Fact]
    public async Task Scope_never_queries_unreadable_owners_and_honors_cancellation()
    {
        var store = new RecordingStore();
        var scope = new SecretScope(store, ["allowed"]);
        Assert.Null(await scope.GetAsync("unavailable"));
        Assert.Equal(["allowed"], store.Owners);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => scope.GetAsync("name", cancelled.Token).AsTask());
        var memory = new MemorySecretStore();
        memory.Set("a", "same", new Secret("first"));
        memory.Set("b", "same", new Secret("second"));
        Assert.Equal("second", (await new SecretScope(memory, ["b", "a"]).GetAsync("same"))!.Value);
    }

    private sealed class RecordingStore : ISecretStore
    {
        public List<string> Owners { get; } = [];
        public ValueTask<Secret?> GetAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Owners.Add(ownerId);
            return ValueTask.FromResult<Secret?>(null);
        }
    }

    [Fact]
    public async Task Resolved_secret_is_absent_from_persisted_tool_records()
    {
        const string value = "synthetic-record-token-987654";
        using var env = new HookEnv();
        var store = new MemorySecretStore();
        store.Set("person-a", "api", new Secret(value));
        string path = env.Hook("secret-tool.js", """
            export const tool = { name: 'secret_test', description: 'Uses a scoped secret' };
            export async function invoke(input, ctx) {
              const secret = await ctx.secret('api');
              ctx.log(secret);
              ctx.addCost({ counter: 'apiCalls', quantity: 1, unitPrice: 0, label: secret, source: secret });
              console.log(secret);
              if (input.fail) throw new Error(secret);
              return { secret };
            }
            """);
        env.Rt.Tools.Add(JsTools.Load(path, new JsSandboxOptions { ResolveHostContext = _ => new JsHostContext { Secrets = new SecretScope(store, ["person-a"]) } }));
        env.Provider.Enqueue(HookEnv.Calls("c1", "secret_test", "{}"), HookEnv.Calls("c2", "secret_test", "{\"fail\":true}"), HookEnv.Say("done"));
        OneLlm.Agents.AgentResult result = await HookEnv.Run(env, env.Def(["secret_test"]));
        string record = File.ReadAllText(env.Records.PathOf(result.Instance.InstanceId));
        Assert.Contains("toolCall", record, StringComparison.Ordinal);
        Assert.Contains(Secret.Mask, record, StringComparison.Ordinal);
        Assert.DoesNotContain(value, record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_and_loop_accident_limits_remain_active()
    {
        var sandbox = new JsSandbox(new JsSandboxOptions { TrustedHost = true, ExecTimeLimit = TimeSpan.FromMilliseconds(100), TimeLimit = TimeSpan.FromSeconds(3), Fuel = 10_000 });
        JsRunResult result = await sandbox.RunSnippetAsync("return await ctx.exec('sleep 30');", null, Js.Context(), ToolCapability.None);
        Assert.True(result.Value!["timedOut"]!.GetValue<bool>());
        JsSandboxException failure = await Assert.ThrowsAsync<JsSandboxException>(() => sandbox.RunSnippetAsync("while (true) {}", null, Js.Context(), ToolCapability.None));
        Assert.Equal(JsSandboxFailure.Fuel, failure.Kind);
    }

    [Fact]
    public async Task Callback_endpoint_authenticates_and_keeps_caller_identity()
    {
        var registry = new ToolRegistry();
        ToolContext caller = Js.Context();
        registry.Add(new IdentityTool(caller));
        var sandbox = new JsSandbox(new JsSandboxOptions { TrustedHost = true, EnableToolEndpoint = true, ResolveHostContext = _ => new JsHostContext { Tools = registry } });
        // curl is a process fixture, not a library dependency. The bearer token travels only via the child environment.
        const string command = "curl -s -o /dev/null -w '%{http_code}' -X POST --data '{}' \"$ONELLM_TOOL_ENDPOINT\"; curl -sf -H \"Authorization: Bearer $ONELLM_TOOL_TOKEN\" -H 'Content-Type: application/json' --data '{\"name\":\"identity_echo\",\"inputs\":{}}' \"$ONELLM_TOOL_ENDPOINT\"";
        JsRunResult result = await sandbox.RunSnippetAsync("return await ctx.exec(input.command);", new JsonObject { ["command"] = command }, caller, ToolCapability.None);
        string output = result.Value!["stdout"]!.GetValue<string>();
        Assert.StartsWith("401", output, StringComparison.Ordinal);
        Assert.Contains("inst", output, StringComparison.Ordinal);
        Assert.Equal(0, result.Value["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public async Task Callback_endpoint_stops_when_the_process_exits()
    {
        var sandbox = new JsSandbox(new JsSandboxOptions { TrustedHost = true, EnableToolEndpoint = true });
        JsRunResult result = await sandbox.RunSnippetAsync("return await ctx.exec('printf \"$ONELLM_TOOL_ENDPOINT\"');", null, Js.Context(), ToolCapability.None);
        string url = result.Value!["stdout"]!.GetValue<string>();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
    }

    private sealed class Marker : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class IdentityTool(ToolContext expected) : ITool
    {
        public ToolSpec Definition { get; } = new("identity_echo", "Reports caller", JsonNode.Parse("""{"type":"object"}""")!.AsObject());
        public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            Assert.Same(expected, context);
            return ValueTask.FromResult(ToolResult.FromText(context.InstanceId));
        }
    }

    private sealed class Logs : IToolLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string toolCallId, ToolLogLevel level, string message) => Lines.Add(message);
    }
}

public sealed class JsToolWatcherTests
{
    [Fact]
    public async Task Files_register_replace_survive_bad_edits_and_unregister()
    {
        using var root = new Scratch();
        var registry = new ToolRegistry();
        await using var watcher = new JsToolWatcher(registry, [root.Root], pollInterval: TimeSpan.FromMilliseconds(25));
        int errors = 0;
        watcher.Error += (_, _) => Interlocked.Increment(ref errors);
        string path = Path.Combine(root.Root, "watched.js");
        File.WriteAllText(path, "export const tool = { name: 'watched_test', description: 'v1' }; export function invoke() { return 1; }");
        await Until(() => registry.TryGet("watched_test", out _));
        long addedVersion = registry.Version;
        registry.TryGet("watched_test", out ITool first);
        File.WriteAllText(path, "export const tool = { name: 'watched_test', description: 'v2' }; export function invoke() { return 2; }");
        await Until(() => registry.TryGet("watched_test", out ITool current) && current.Definition.Description == "v2");
        Assert.True(registry.Version > addedVersion);
        File.WriteAllText(path, "invalid javascript {{{");
        await Until(() => Volatile.Read(ref errors) > 0);
        Assert.True(registry.TryGet("watched_test", out _));
        File.WriteAllText(Path.Combine(root.Root, "second.js"), "export const tool = { name: 'second_test', description: 'valid' }; export function invoke() {}");
        await Until(() => registry.TryGet("second_test", out _));
        File.Delete(path);
        await Until(() => !registry.TryGet("watched_test", out _));
        Assert.True(registry.TryGet("second_test", out _));
        Assert.NotSame(first, registry.All.Single(t => t.Definition.Name == "second_test"));
        var replacement = new CountingTool("second_test", "replacement");
        registry.Add(replacement);
        await watcher.DisposeAsync();
        Assert.True(registry.TryGet("second_test", out ITool retained));
        Assert.Same(replacement, retained);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }
}
