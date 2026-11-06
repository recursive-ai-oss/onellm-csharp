using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.JavaScript.Tests;

internal static class Js
{
    public static ToolContext Context(ICapabilities? caps = null, TimeProvider? time = null, IToolLog? log = null) =>
        new("inst", "call", time ?? TimeProvider.System) { Capabilities = caps ?? NoCapabilities.Instance, LogSink = log };

    public static string Text(ToolResult r) => string.Concat(r.Parts.Select(p => p switch { TextPart t => t.Text, JsonPart j => j.Value.ToJsonString(), _ => "" }));

    public static string SamplesDirectory([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "samples", "js-tools"));

    public const ToolCapability All = ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.Network;

    public static Task<JsRunResult> Run(string code, ICapabilities? caps = null, JsSandboxOptions? options = null, ToolCapability allowed = All, JsonNode? input = null) =>
        new JsSandbox(options).RunSnippetAsync(code, input, Context(caps), allowed, "snippet.js");
}

public class SnippetTests
{
    [Fact]
    public async Task A_snippet_returns_a_value_prints_and_reads_its_input()
    {
        JsRunResult r = await Js.Run("console.log('hi', {a: 1}); const x = await Promise.resolve(input.n * 2); return { x };", input: JsonNode.Parse("""{"n":21}"""));
        Assert.Equal("hi {\"a\":1}", r.Output);
        Assert.Equal("""{"x":42}""", r.Value!.ToJsonString());
    }

    [Fact]
    public async Task Nothing_returned_means_no_value()
    {
        JsRunResult r = await Js.Run("let a = 1;");
        Assert.False(r.HasValue);
        Assert.Equal("", r.Output);
    }

    [Fact]
    public async Task A_run_leaves_nothing_behind_for_the_next_one()
    {
        await Js.Run("globalThis.leak = 5; Array.prototype.poison = 1;");
        JsRunResult r = await Js.Run("return [typeof globalThis.leak, [].poison === undefined];");
        Assert.Equal("""["undefined",true]""", r.Value!.ToJsonString());
    }

    [Fact]
    public async Task Output_over_the_cap_keeps_the_head_and_the_tail()
    {
        JsRunResult r = await Js.Run("for (let i = 0; i < 2000; i++) console.log('line ' + i);", options: new JsSandboxOptions { MaxOutputChars = 200 });
        Assert.StartsWith("line 0\n", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("line 1999", r.Output, StringComparison.Ordinal);
        Assert.Contains("characters of output omitted", r.Output, StringComparison.Ordinal);
        Assert.True(r.Output.Length < 400);
    }

    [Fact]
    public async Task Deterministic_mode_repeats_time_and_random_numbers()
    {
        var options = new JsSandboxOptions { Deterministic = true, FixedTime = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero), RandomSeed = 7 };
        const string code = "return [Date.now(), new Date().toISOString(), Math.random(), Math.random()];";
        string a = (await Js.Run(code, options: options)).Value!.ToJsonString();
        string b = (await Js.Run(code, options: options)).Value!.ToJsonString();
        Assert.Equal(a, b);
        Assert.Contains("2030-05-06T07:08:09.000Z", a, StringComparison.Ordinal);
        string other = (await Js.Run(code, options: new JsSandboxOptions { Deterministic = true, FixedTime = options.FixedTime, RandomSeed = 8 })).Value!.ToJsonString();
        Assert.NotEqual(a, other);
    }

    [Fact]
    public async Task The_default_clock_is_the_contexts_clock()
    {
        var clock = new FixedClock(new DateTimeOffset(2041, 1, 2, 3, 4, 5, TimeSpan.Zero));
        JsRunResult r = await new JsSandbox().RunSnippetAsync("return new Date().toISOString();", null, Js.Context(time: clock), Js.All);
        Assert.Equal("\"2041-01-02T03:04:05.000Z\"", r.Value!.ToJsonString());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class SandboxRefusalTests
{
    [Fact]
    public async Task The_script_sees_no_host_objects_beyond_ctx_and_console()
    {
        JsRunResult r = await Js.Run("""
            const names = ['process', 'require', 'module', 'Deno', 'Bun', 'window', 'global', 'fetch', 'XMLHttpRequest', 'setTimeout', 'setInterval', 'Buffer', 'import', 'print', '$262', 'Environment', 'System', 'DotNet'];
            const found = names.filter(n => typeof globalThis[n] !== 'undefined');
            return { found, ctx: Object.keys(ctx).sort(), g: Object.getOwnPropertyNames(globalThis).filter(k => !/^[A-Z]/.test(k)).sort() };
            """);
        JsonNode v = r.Value!;
        Assert.Empty(v["found"]!.AsArray());
        Assert.Equal(["instanceId", "toolCallId"], v["ctx"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        string[] g = v["g"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("ctx", g);
        Assert.Contains("console", g);
        Assert.DoesNotContain("process", g);
    }

    [Fact]
    public async Task Constructor_chains_do_not_reach_the_host()
    {
        JsRunResult r = await Js.Run("""
            const F = (() => {}).constructor;
            const viaFunction = F('return typeof process + typeof require + typeof fetch')();
            const viaObject = ({}).constructor.constructor('return typeof globalThis.process')();
            let host = 'none';
            try { ctx.log.constructor('return process')(); } catch (e) { host = e.name; }
            const err = new Error('x');
            return [viaFunction, viaObject, host, typeof err.stack, Object.getPrototypeOf(ctx.log) === Function.prototype];
            """);
        string json = r.Value!.ToJsonString();
        Assert.StartsWith("""["undefinedundefinedundefined","undefined",""", json, StringComparison.Ordinal);
        Assert.EndsWith("""true]""", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dynamic_import_of_files_and_modules_fails()
    {
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("await import('./other.js'); return 1;"));
        Assert.Equal(JsSandboxFailure.Script, e.Kind);
    }

    [Fact]
    public async Task Files_outside_the_grant_are_refused_by_every_route()
    {
        using var dir = new Scratch();
        string secret = Path.Combine(dir.Outside, "secret.txt");
        File.WriteAllText(secret, "hidden");
        dir.Write("ok.txt", "visible");
        Capabilities caps = Capabilities.Files(dir.Root);

        Assert.Equal("\"visible\"", (await Js.Run("return await ctx.readFile('ok.txt');", caps)).Value!.ToJsonString());

        foreach (string attempt in new[] { secret, "../outside/secret.txt", dir.Root + "/../outside/secret.txt", "/etc/hostname" })
        {
            JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run($"return ctx.readFile({System.Text.Json.JsonSerializer.Serialize(attempt)});", caps));
            Assert.Contains("not available", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", e.Message, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run($"ctx.writeFile('../outside/new.txt', 'x');", caps));
        Assert.False(File.Exists(Path.Combine(dir.Outside, "new.txt")));
    }

    [Fact]
    public async Task A_symlink_out_of_the_grant_is_followed_and_refused()
    {
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Outside, "secret.txt"), "hidden");
        if (!Scratch.TryLink(dir.In("link"), dir.Outside, directory: true))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(dir.Root);
        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("return ctx.readFile('link/secret.txt');", caps));
        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("ctx.writeFile('link/new.txt', 'x');", caps));
        Assert.False(File.Exists(Path.Combine(dir.Outside, "new.txt")));
    }

    [Fact]
    public async Task Writes_inside_the_grant_work_and_a_read_only_grant_refuses_them()
    {
        using var dir = new Scratch();
        await Js.Run("ctx.writeFile('out/a.txt', 'héllo');", Capabilities.Files(dir.Root));
        Assert.Equal("héllo", File.ReadAllText(dir.In("out/a.txt")));
        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("ctx.writeFile('b.txt', 'x');", Capabilities.ReadOnlyFiles(dir.Root)));
        Assert.False(File.Exists(dir.In("b.txt")));
    }

    [Fact]
    public async Task A_capability_the_tool_did_not_declare_is_refused_even_when_the_host_granted_it()
    {
        using var dir = new Scratch();
        dir.Write("ok.txt", "visible");
        Capabilities caps = Capabilities.Files(dir.Root);
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("return ctx.readFile('ok.txt');", caps, allowed: ToolCapability.None));
        Assert.Contains("'fileRead' in the tool's requires list", e.Message, StringComparison.Ordinal);
        e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("return await ctx.fetch('http://127.0.0.1:1/');", caps, allowed: ToolCapability.FileRead));
        Assert.Contains("'network' in the tool's requires list", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_with_egress_off_is_refused_and_sends_nothing()
    {
        using var server = new StubServer(_ => new StubResponse(200, "hello"));
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run($"return await ctx.fetch('{server.BaseUri}');", Capabilities.Files(".")));
        Assert.Contains("Network access to 127.0.0.1 is not available", e.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Fetch_on_a_granted_host_works_and_a_redirect_to_an_ungranted_one_is_refused()
    {
        using var hidden = new StubServer(_ => new StubResponse(200, "secret"));
        using var server = new StubServer(r => r.Target == "/hop"
            ? new StubResponse(302, "", Headers: new() { ["Location"] = $"http://localhost:{hidden.Port}/" })
            : new StubResponse(200, "hello", Headers: new() { ["X-Seen"] = "yes" }));
        Capabilities caps = Capabilities.Network("127.0.0.1");

        JsRunResult r = await Js.Run($"const res = await ctx.fetch('{server.BaseUri}', {{ method: 'POST', body: 'b', headers: {{ 'X-Mine': '1' }} }}); return [res.status, res.ok, res.text, res.headers['x-seen']];", caps);
        Assert.Equal("""[200,true,"hello","yes"]""", r.Value!.ToJsonString());
        Assert.Equal("POST", server.Requests[0].Method);
        Assert.Equal("1", server.Requests[0].Headers["X-Mine"]);

        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run($"return await ctx.fetch('{server.BaseUri}hop');", caps));
        Assert.Empty(hidden.Requests);
    }

    [Fact]
    public async Task An_infinite_loop_is_stopped_by_the_time_limit()
    {
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(300) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("while (true) {}", options: options));
        Assert.Equal(JsSandboxFailure.Time, e.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_catch_block_cannot_swallow_a_limit()
    {
        var options = new JsSandboxOptions { Fuel = 100_000 };
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("try { while (true) {} } catch (e) { return 'caught'; } finally { }", options: options));
        Assert.Equal(JsSandboxFailure.Fuel, e.Kind);
        Assert.Contains("100000 steps", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catastrophic_regex_is_stopped()
    {
        var options = new JsSandboxOptions { Fuel = 5_000_000, TimeLimit = TimeSpan.FromSeconds(5) };
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("return /^(a+)+$/.test('a'.repeat(40) + 'b');", options: options));
        Assert.True(e.Kind is JsSandboxFailure.Fuel or JsSandboxFailure.Time, e.Kind.ToString());
    }

    [Fact]
    public async Task A_memory_hog_is_stopped_by_the_allocation_budget()
    {
        var options = new JsSandboxOptions { MaxAllocatedBytes = 32L * 1024 * 1024, Fuel = 5_000_000_000 };
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("const keep = []; while (true) keep.push({ a: 1, b: [1, 2, 3] });", options: options));
        Assert.Equal(JsSandboxFailure.Memory, e.Kind);

        e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("let s = 'x'; while (true) { s = s + s; }", options: new JsSandboxOptions { MaxAllocatedBytes = 64L * 1024 * 1024 }));
        Assert.True(e.Kind is JsSandboxFailure.Memory or JsSandboxFailure.Script, e.Kind.ToString());
    }

    [Theory]
    [InlineData("new Array(1e9).fill(0);")]
    [InlineData("Array.from({ length: 1e9 });")]
    [InlineData("let a = [1]; while (true) a = a.concat(a);")]
    [InlineData("const keep = []; for (let i = 0; ; i++) keep.push(i);")]
    public async Task Array_storage_the_engine_does_not_count_is_caught_by_the_host_backstop(string code)
    {
        var options = new JsSandboxOptions { MaxHostAllocatedBytes = 128L * 1024 * 1024, TimeLimit = TimeSpan.FromSeconds(20) };
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run(code, options: options));
        Assert.Equal(JsSandboxFailure.Memory, e.Kind);
    }

    [Fact]
    public async Task Deep_recursion_is_a_catchable_range_error_not_a_crash()
    {
        JsRunResult r = await Js.Run("function f() { return f() + 1; } try { f(); } catch (e) { return e.name; }");
        Assert.Equal("\"RangeError\"", r.Value!.ToJsonString());
    }

    [Fact]
    public async Task Cancelling_the_token_stops_the_run_with_an_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        Task<JsRunResult> run = new JsSandbox(new JsSandboxOptions { TimeLimit = TimeSpan.FromSeconds(30), Fuel = 100_000_000_000 })
            .RunSnippetAsync("while (true) {}", null, Js.Context(), Js.All, "snippet.js", cts.Token);
        await Task.Delay(100);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
    }

    [Fact]
    public async Task A_script_cannot_change_how_its_result_is_read()
    {
        JsRunResult r = await Js.Run("JSON.stringify = () => '{\"forged\":true}'; return { real: 1 };");
        Assert.Equal("""{"real":1}""", r.Value!.ToJsonString());
    }

    [Fact]
    public async Task A_result_that_is_too_large_is_refused()
    {
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("return 'x'.repeat(5000);", options: new JsSandboxOptions { MaxResultChars = 1000 }));
        Assert.Equal(JsSandboxFailure.Result, e.Kind);
    }

    [Fact]
    public async Task Files_larger_than_the_cap_are_not_read()
    {
        using var dir = new Scratch();
        dir.Write("big.txt", new string('a', 5000));
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("return ctx.readFile('big.txt');", Capabilities.Files(dir.Root), new JsSandboxOptions { MaxFileBytes = 1000 }));
        Assert.Contains("reads at most 1000", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_are_positioned_and_carry_no_host_paths()
    {
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("const a = 1;\nconst b = a.nope.deeper;\n"));
        Assert.Equal(JsSandboxFailure.Script, e.Kind);
        Assert.Equal("snippet.js", e.File);
        Assert.Equal(2, e.Line);
        Assert.Contains("snippet.js:2:", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.CurrentDirectory, e.Message, StringComparison.Ordinal);

        e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("let = = ;"));
        Assert.Equal(JsSandboxFailure.Syntax, e.Kind);
        Assert.Equal(1, e.Line);
    }

    [Fact]
    public async Task Cost_lines_are_exact_decimals()
    {
        ToolContext ctx = Js.Context();
        await new JsSandbox().RunSnippetAsync("ctx.addCost({ counter: 'lookups', quantity: 3, unitPrice: '0.0007', label: 'lookup' }); ctx.addCost({ counter: 'x', quantity: 1, unitPrice: 0.1, per: 1, multiplier: 3 });", null, ctx, Js.All);
        Assert.Equal(2, ctx.CostLines.Count);
        Assert.Equal(0.0021m, ctx.CostLines[0].Amount);
        Assert.Equal(0.3m, ctx.CostLines[1].Amount);
        await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await new JsSandbox().RunSnippetAsync("ctx.addCost({ counter: 'x', unitPrice: 'abc' });", null, Js.Context(), Js.All));
    }
}

public class JsToolTests
{
    private static Task<ToolResult> Call(ITool tool, string json, ICapabilities? caps = null) =>
        ToolInvoker.InvokeAsync(tool, (JsonObject)JsonNode.Parse(json)!, Js.Context(caps), CancellationToken.None).AsTask();

    private static string WriteTool(Scratch dir, string name, string source, string? yaml = null)
    {
        string path = dir.Write(name + ".js", source);
        if (yaml is not null)
        {
            dir.Write(name + ".tool.yaml", yaml);
        }

        return path;
    }

    [Fact]
    public async Task The_sample_tools_load_validate_and_run_through_the_invoker()
    {
        IReadOnlyList<ITool> tools = JsTools.LoadDirectory(Js.SamplesDirectory());
        Assert.Equal(["reshape_json", "convert_units"], tools.Select(t => t.Definition.Name).ToArray());

        ITool convert = tools[1];
        Assert.Equal("number", convert.Definition.InputSchema["properties"]!["value"]!["type"]!.GetValue<string>());
        ToolResult r = await Call(convert, """{"value":5,"from":"km","to":"mi"}""");
        Assert.False(r.IsError, Js.Text(r));
        Assert.Equal("""{"value":3.106855961,"unit":"mi"}""", Js.Text(r));
        Assert.Equal("""{"value":212,"unit":"f"}""", Js.Text(await Call(convert, """{"value":100,"from":"c","to":"f"}""")));
        ToolResult bad = await Call(convert, """{"value":1,"from":"kg","to":"m"}""");
        Assert.True(bad.IsError);
        Assert.Contains("cannot convert 'kg' to 'm'", Js.Text(bad), StringComparison.Ordinal);
        Assert.Contains("unit-converter.js:", Js.Text(bad), StringComparison.Ordinal);

        ToolResult reshaped = await Call(tools[0], """{"items":[{"name":"a","address":{"city":"Oslo"}},{"name":"b"}],"fields":{"n":"name","city":"address.city"}}""");
        Assert.Equal("""[{"n":"a","city":"Oslo"},{"n":"b"}]""", Js.Text(reshaped));
        Assert.Equal(ToolCapability.None, ((ICapabilityTool)convert).Requires);
    }

    [Fact]
    public async Task A_tool_declares_its_capabilities_and_gets_exactly_those()
    {
        using var dir = new Scratch();
        dir.Write("data.txt", "42");
        string path = WriteTool(dir, "reader", """
            export const tool = { name: 'read_it', description: 'reads', requires: ['fileRead'], costHint: '0.002', timeout: 5, requiresApproval: true, parallelSafe: false };
            export async function invoke(inputs, ctx) {
              const text = await ctx.readFile('data.txt');
              if (inputs.write) ctx.writeFile('x.txt', 'no');
              ctx.addCost({ counter: 'reads', quantity: 1, unitPrice: '0.002' });
              return text;
            }
            """);
        ITool tool = JsTools.Load(path);
        Assert.Equal(ToolCapability.FileRead, ((ICapabilityTool)tool).Requires);
        Assert.Equal(0.002m, tool.Definition.CostHint);
        Assert.Equal(TimeSpan.FromSeconds(5), tool.Definition.Timeout);
        Assert.True(tool.Definition.RequiresApproval);
        Assert.False(tool.Definition.ParallelSafe);

        Capabilities caps = Capabilities.Files(dir.Root);
        var ctx = Js.Context(caps);
        ToolResult ok = await ToolInvoker.InvokeAsync(tool, new JsonObject(), ctx, CancellationToken.None);
        Assert.Equal("42", Js.Text(ok));
        Assert.Equal(0.002m, ok.CostLines.Single().Amount);

        ToolResult denied = await Call(tool, """{"write":true}""", caps);
        Assert.True(denied.IsError);
        Assert.Contains("'fileWrite' in the tool's requires list", Js.Text(denied), StringComparison.Ordinal);
        Assert.False(File.Exists(dir.In("x.txt")));

        ToolResult ungranted = await Call(tool, "{}", Capabilities.None);
        Assert.True(ungranted.IsError);
    }

    [Fact]
    public async Task Tool_state_does_not_survive_between_calls()
    {
        using var dir = new Scratch();
        ITool tool = JsTools.Load(WriteTool(dir, "counter", """
            export const tool = { description: 'counts' };
            let n = 0;
            export function invoke() { globalThis.hits = (globalThis.hits ?? 0) + 1; return ++n + ':' + globalThis.hits; }
            """));
        Assert.Equal("counter", tool.Definition.Name);
        Assert.Equal("1:1", Js.Text(await Call(tool, "{}")));
        Assert.Equal("1:1", Js.Text(await Call(tool, "{}")));
    }

    [Fact]
    public async Task A_throwing_tool_gives_an_error_result_with_the_position()
    {
        using var dir = new Scratch();
        ITool tool = JsTools.Load(WriteTool(dir, "boom", """
            export const tool = { name: 'boom', description: 'fails' };
            export async function invoke(inputs) {
              await null;
              throw new TypeError('bad input: ' + inputs.what);
            }
            """));
        ToolResult r = await Call(tool, """{"what":"nothing"}""");
        Assert.True(r.IsError);
        string text = Js.Text(r);
        Assert.Contains("error[TypeError]: bad input: nothing", text, StringComparison.Ordinal);
        Assert.Contains("boom.js:4:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(dir.Root, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runaway_tool_is_stopped_at_its_own_timeout()
    {
        using var dir = new Scratch();
        ITool tool = JsTools.Load(WriteTool(dir, "spin", "export const tool = { description: 'spins', timeout: 0.3 };\nexport function invoke() { for (;;) {} }\n"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ToolResult r = await Call(tool, "{}");
        Assert.True(r.IsError);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_tool_that_ends_the_loop_returns_its_typed_result()
    {
        using var dir = new Scratch();
        ITool tool = JsTools.Load(WriteTool(dir, "done", "export const tool = { description: 'ends', endsLoop: true };\nexport function invoke(i) { return { answer: i.n }; }\n"));
        ToolResult r = ToolInvoker.InvokeAsync(tool, new JsonObject { ["n"] = 3 }, Js.Context(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.True(r.EndsLoop, Js.Text(r));
        Assert.Equal("""{"answer":3}""", r.TypedResult!.ToJsonString());
    }

    [Fact]
    public void Invalid_tool_files_fail_to_load_with_file_line_and_column()
    {
        using var dir = new Scratch();
        BadDefinitionException syntax = Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "syn", "export const tool = {\n  description: 'x'\n};\nexport function invoke( {\n")));
        Assert.Equal("syn.js", syntax.File);
        Assert.True(syntax.Line >= 3, syntax.Message);

        Assert.Contains("invoke", Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "noinvoke", "export const tool = { description: 'x' };"))).Message, StringComparison.Ordinal);
        Assert.Contains("description", Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "nodesc", "export function invoke() {}"))).Message, StringComparison.Ordinal);
        Assert.Contains("not a valid tool name", Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "badname", "export const tool = { name: 'has space', description: 'x' };\nexport function invoke() {}"))).Message, StringComparison.Ordinal);
        Assert.Contains("shell", Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "shelly", "export const tool = { description: 'x', requires: ['shell'] };\nexport function invoke() {}"))).Message, StringComparison.Ordinal);
        Assert.Contains("import", Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "imp", "import { x } from './other.js';\nexport const tool = { description: 'x' };\nexport function invoke() {}"))).Message.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Throws<BadDefinitionException>(() => JsTools.Load(dir.In("missing.js")));
    }

    [Fact]
    public void A_yaml_description_beside_the_file_wins_and_a_bad_one_names_its_file()
    {
        using var dir = new Scratch();
        ITool tool = JsTools.Load(WriteTool(dir, "y", "export const tool = { description: 'from js' };\nexport function invoke() {}", "name: from_yaml\ndescription: from yaml\nrequires: [network]\nendsLoop: true\ncostHint: 0.5\n"));
        Assert.Equal("from_yaml", tool.Definition.Name);
        Assert.True(tool.Definition.EndsLoop);
        Assert.Equal(ToolCapability.Network, ((ICapabilityTool)tool).Requires);
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => JsTools.Load(WriteTool(dir, "z", "export function invoke() {}", "name: [unclosed\n")));
        Assert.Equal("z.tool.yaml", e.File);
    }
}

public class RunJavaScriptToolTests
{
    private static Task<ToolResult> Call(string json, ICapabilities caps) =>
        ToolInvoker.InvokeAsync(JsTools.RunJavaScript(), (JsonObject)JsonNode.Parse(json)!, Js.Context(caps), CancellationToken.None).AsTask();

    [Fact]
    public async Task The_snippet_output_and_value_come_back_as_text()
    {
        ToolResult r = await Call("""{"code":"console.log('a'); return [1,2,3].map(x => x * x);"}""", Capabilities.None);
        Assert.False(r.IsError);
        Assert.Equal("a\n=> [1,4,9]", Js.Text(r));
        Assert.Equal("(no output)", Js.Text(await Call("""{"code":"let x = 1;"}""", Capabilities.None)));
        Assert.Equal("=> 6", Js.Text(await Call("""{"code":"return input.a + input.b;","input":{"a":2,"b":4}}""", Capabilities.None)));
    }

    [Fact]
    public async Task It_runs_under_the_callers_grants()
    {
        using var dir = new Scratch();
        dir.Write("n.txt", "7");
        Assert.Equal("=> 8", Js.Text(await Call("""{"code":"return Number(await ctx.readFile('n.txt')) + 1;"}""", Capabilities.Files(dir.Root))));
        ToolResult none = await Call("""{"code":"return await ctx.readFile('n.txt');"}""", Capabilities.None);
        Assert.True(none.IsError);
        Assert.Contains("is not available", Js.Text(none), StringComparison.Ordinal);
        var request = new JsonObject { ["code"] = "return await ctx.readFile(" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir.Outside, "x")) + ");" };
        ToolResult outside = await Call(request.ToJsonString(), Capabilities.Files(dir.Root));
        Assert.True(outside.IsError);
    }

    [Fact]
    public async Task Failures_and_missing_code_are_error_results()
    {
        ToolResult r = await Call("""{"code":"null.x"}""", Capabilities.None);
        Assert.True(r.IsError);
        Assert.StartsWith("Error: ", Js.Text(r), StringComparison.Ordinal);
        Assert.Contains("snippet.js:1", Js.Text(r), StringComparison.Ordinal);
        Assert.True((await Call("{}", Capabilities.None)).IsError);
    }

    [Fact]
    public void The_definition_is_a_valid_schema_with_one_required_argument()
    {
        ITool tool = JsTools.RunJavaScript();
        Assert.Equal("run_javascript", tool.Definition.Name);
        Assert.Equal(["code"], tool.Definition.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }
}
