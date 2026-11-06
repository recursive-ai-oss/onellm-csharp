using System.Text.Json;
using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;

namespace OneLlm.JavaScript.Tests;

/// <summary>Boundaries of what a definition or a script can make the host do: cost lines, which files a definition may load, what resume does, what errors reveal.</summary>
public class DefinitionBoundaryTests
{
    private static async Task<JsSandboxException> Fails(string code, ICapabilities? caps = null, JsSandboxOptions? options = null) =>
        await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run(code, caps, options));

    [Theory]
    [InlineData("ctx.addCost({counter:'x', quantity:-1000, unitPrice:'1'});")]
    [InlineData("ctx.addCost({counter:'x', quantity:1, unitPrice:-1});")]
    [InlineData("ctx.addCost({counter:'x', quantity:1, unitPrice:1, multiplier:-2});")]
    [InlineData("ctx.addCost({counter:'x', quantity:NaN, unitPrice:1});")]
    [InlineData("ctx.addCost({counter:'x', quantity:Infinity, unitPrice:1});")]
    [InlineData("ctx.addCost({counter:'x', quantity:'79228162514264337593543950335', unitPrice:'79228162514264337593543950335'});")]
    public async Task Add_cost_refuses_negative_non_finite_and_overflowing_amounts(string code)
    {
        ToolContext context = Js.Context();
        var e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await new JsSandbox().RunSnippetAsync(code, null, context, Js.All));
        Assert.Equal(JsSandboxFailure.Script, e.Kind);
        Assert.Empty(context.CostLines);
        Assert.DoesNotContain("OverflowException", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cost_lines_per_run_and_their_text_are_capped()
    {
        ToolContext context = Js.Context();
        var sandbox = new JsSandbox(new JsSandboxOptions { MaxCostLines = 5 });
        var e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await sandbox.RunSnippetAsync("for (let i = 0; i < 1000; i++) ctx.addCost({counter:'x', quantity:1, unitPrice:'0.001'});", null, context, Js.All));
        Assert.Contains("at most 5 cost lines", e.Message, StringComparison.Ordinal);
        Assert.Equal(5, context.CostLines.Count);

        e = await Fails("ctx.addCost({counter:'x'.repeat(100000), quantity:1, unitPrice:1});");
        Assert.Contains("longer than", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_cost_still_takes_a_plain_line()
    {
        ToolContext context = Js.Context();
        await new JsSandbox().RunSnippetAsync("ctx.addCost({counter:'pages', quantity:3, unitPrice:'0.01'});", null, context, Js.All);
        Assert.Equal(0.03m, context.CostLines.Single().Amount);
    }

    [Fact]
    public async Task Host_failures_reach_the_script_as_plain_errors()
    {
        using var scratch = new Scratch();
        ICapabilities caps = Capabilities.Files(scratch.Root);
        const string code = """
            const out = {};
            try { ctx.readFile('nope.txt'); } catch (e) { out.missing = { name: e.name, message: e.message, stack: String(e.stack) }; }
            try { await ctx.fetch('http://x', { method: 'bad method' }); } catch (e) { out.method = { message: e.message, stack: String(e.stack) }; }
            return out;
            """;
        JsRunResult r = await Js.Run(code, caps, options: null, allowed: Js.All);
        string text = r.Value!.ToJsonString();
        Assert.DoesNotContain(scratch.Root, text, StringComparison.Ordinal);
        foreach (string leak in new[] { "Exception", "OneLlm.", "PathGuard", ".cs:", "System." })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }

        Assert.Contains("no such file", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_nested_past_the_json_limit_is_a_result_failure()
    {
        JsSandboxException e = await Fails("let o = {}; for (let i = 0; i < 200; i++) o = { o }; return o;");
        Assert.Equal(JsSandboxFailure.Result, e.Kind);
        Assert.Contains("nested", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_files_outside_the_read_grants_are_refused_without_echoing_them()
    {
        using var env = new HookEnv();
        using var outside = new Scratch();
        string secret = outside.Write("secret.txt", "TOPSECRET-LINE this is not javascript");
        string valid = outside.Write("hook.js", "export function beforeToolCall(s) { return { action: 'block', reason: 'outside' }; }");

        foreach (string path in new[] { secret, valid, "../" + Path.GetFileName(outside.Root) + "/hook.js" })
        {
            AgentDefinition def = env.Def([], path);
            var e = Assert.ThrowsAny<Exception>(() => env.Rt.DefinitionHooks!.Load(def, env.Rt.Capabilities));
            Assert.IsType<CapabilityMissingException>(e);
            Assert.DoesNotContain("TOPSECRET", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_hook_inside_the_granted_root_still_loads()
    {
        using var env = new HookEnv();
        env.Hook("ok.js", "export function beforeToolCall() { return undefined; }");
        Assert.Single(env.Rt.DefinitionHooks!.Load(env.Def([], "ok.js"), env.Rt.Capabilities));
    }

    [Fact]
    public async Task Resuming_in_a_runtime_without_a_hook_source_fails_closed()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Hook("gate.js", "export function beforeToolCall() { return { action: 'block', reason: 'POLICY-BLOCK' }; }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult first = await HookEnv.Run(env, env.Def(["t"], "gate.js"));
        Assert.Equal(0, tool.Calls);

        // A second runtime on the same store, without the package that runs hooks.
        var second = new Runtime
        {
            Catalog = env.Rt.Catalog,
            GetEnvironmentVariable = _ => null,
            Providers = env.Rt.Providers,
            Store = env.Rt.Store,
            Capabilities = env.Rt.Capabilities,
        };
        second.Tools.Add(tool);
        env.Provider.Enqueue(HookEnv.Calls("c2", "t", "{}"), HookEnv.Say("done"));
        var e = await Assert.ThrowsAsync<CapabilityMissingException>(async () =>
            await second.MessageAgentAsync(first.Instance.InstanceId, "again").WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("Definition hooks", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Resuming_an_instance_with_a_negative_recorded_cost_still_works()
    {
        using var env = new HookEnv();
        env.Provider.Enqueue(HookEnv.Say("one"), HookEnv.Say("two"));
        AgentResult first = await HookEnv.Run(env, env.Def([]));
        Instance saved = (await env.Rt.Store.LoadAsync(first.Instance.InstanceId, default))!;
        saved.Messages.Add(new Message { Assistant = [new TextPart("earlier")], Usage = new Usage { InputTokens = 10, OutputTokens = 1, Cost = -50m } });

        await env.Rt.Store.SaveAsync(saved, default);
        AgentRun run = await env.Rt.MessageAgentAsync(first.Instance.InstanceId, "more").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(run);
    }
}
