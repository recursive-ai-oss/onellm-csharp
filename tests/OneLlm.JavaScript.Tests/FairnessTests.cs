using System.Text.Json.Nodes;
using OneLlm.Hooks;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.JavaScript.Tests;

/// <summary>One caller cannot hold every slot, and hooks and deciders keep slots of their own.</summary>
// Spinning scripts load the CPU, so these run alone, as the memory tests do.
[Collection("MemoryBombs")]
public class FairnessTests
{
    private static ToolContext As(string instance) => new(instance, "call", TimeProvider.System);

    private const string Hog = "const t = Date.now(); while (Date.now() - t < 1200) {} return 1;";

    [Fact]
    public async Task One_caller_cannot_hold_more_than_its_share_and_others_still_run()
    {
        var limiter = new JsRunLimiter(maxConcurrentRuns: 6, maxWait: TimeSpan.Zero, reservedForInterceptors: 1, maxRunsPerCaller: 2);
        var sb = new JsSandbox(new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(5), Fuel = 100_000_000_000 });
        Task<JsRunResult>[] mine = [sb.RunSnippetAsync(Hog, null, As("a"), Js.All), sb.RunSnippetAsync(Hog, null, As("a"), Js.All)];
        while (limiter.ActiveRuns < 2)
        {
            await Task.Delay(5);
        }

        JsSandboxException third = await Assert.ThrowsAsync<JsSandboxException>(async () => await sb.RunSnippetAsync("return 1;", null, As("a"), Js.All));
        Assert.Equal(JsSandboxFailure.Busy, third.Kind);
        Assert.Contains("per caller", third.Message, StringComparison.Ordinal);
        Assert.Equal("1", (await sb.RunSnippetAsync("return 1;", null, As("b"), Js.All)).Value!.ToJsonString());
        await Task.WhenAll(mine);
    }

    [Fact]
    public async Task Eight_hogs_then_a_hook_still_runs()
    {
        // Eight tool-style runs from eight callers fill every slot a tool may use; the slots kept for interceptors stay free.
        // The hogs spin until the test cancels them, so the count is checked while they are all certainly still running.
        var limiter = new JsRunLimiter(maxConcurrentRuns: 8, maxWait: TimeSpan.Zero, reservedForInterceptors: 2, maxRunsPerCaller: 3);
        var options = new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(60), Fuel = 100_000_000_000 };
        var sb = new JsSandbox(options);
        using var stop = new CancellationTokenSource();
        List<Task> hogs = [];
        for (int i = 0; i < 8; i++)
        {
            hogs.Add(Swallow(sb.RunSnippetAsync("for (;;) {}", null, As("c" + i), Js.All, "snippet.js", stop.Token)));
        }

        // Six get a slot; the other two are refused at once (no wait), so the count settles at six and stays there.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (limiter.ActiveRuns != 6 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(6, limiter.ActiveRuns);

        using var dir = new Scratch();
        string path = dir.Write("gate.js", "export function beforeToolCall(s) { return { action: 'block', reason: 'no' }; }");
        IHook hook = new JsHookSource(options).LoadFile(path, HookPoints.None, Capabilities.None);
        Assert.Equal(HookPoints.BeforeToolCall, hook.Points);
        JsRunResult r = await sb.CallAsync("export function f() { return 7; }", "f.js", "f", [], passCtx: false, As("hook"), ToolCapability.None, null, default, runClass: RunClass.Hook);
        Assert.Equal("7", r.Value!.ToJsonString());

        await stop.CancelAsync();
        await Task.WhenAll(hogs);
    }

    private static ToolContext Child(string id, string parent) => new(id, "call", TimeProvider.System) { CallerInstanceId = parent };

    private const string Spin = "for (;;) {}";

    [Fact]
    public async Task Sub_agents_share_their_trees_cap_and_a_third_tree_still_runs()
    {
        var limiter = new JsRunLimiter(maxConcurrentRuns: 10, maxWait: TimeSpan.Zero, reservedForInterceptors: 2, maxRunsPerCaller: 3);
        var sb = new JsSandbox(new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(60), Fuel = 100_000_000_000 });
        using var stop = new CancellationTokenSource();
        // The parent and six children all belong to tree "root"; together they get three slots.
        List<Task> tree = [Swallow(sb.RunSnippetAsync(Spin, null, As("root"), Js.All, "snippet.js", stop.Token))];
        for (int i = 0; i < 6; i++)
        {
            tree.Add(Swallow(sb.RunSnippetAsync(Spin, null, Child("kid" + i, "root"), Js.All, "snippet.js", stop.Token)));
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (limiter.ActiveRuns < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(300);
        Assert.Equal(3, limiter.ActiveRuns);
        Assert.Equal("1", (await sb.RunSnippetAsync("return 1;", null, As("other-tree"), Js.All)).Value!.ToJsonString());
        await stop.CancelAsync();
        await Task.WhenAll(tree);
    }

    [Fact]
    public async Task One_trees_spinning_deciders_cannot_make_another_trees_hook_fail_busy()
    {
        var limiter = new JsRunLimiter(maxConcurrentRuns: 8, maxWait: TimeSpan.Zero, reservedForInterceptors: 2, maxRunsPerCaller: 3, maxInterceptorsPerOwner: 2);
        var options = new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(60), Fuel = 100_000_000_000 };
        using var stop = new CancellationTokenSource();
        using var dir = new Scratch();
        string path = dir.Write("spin.js", "export function prepare() { for (;;) {} }");
        // Eight different decider files, one call each, as a model that writes its own deciders would start them.
        List<Task> hogs = [];
        for (int i = 0; i < 8; i++)
        {
            JsDeciderLogic logic = JsDeciderLogic.Load(path, options);
            hogs.Add(SwallowAny(logic.PrepareAsync(System.Text.Json.JsonDocument.Parse("{}").RootElement, stop.Token).AsTask()));
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (limiter.ActiveRuns < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(300);
        Assert.Equal(4, limiter.ActiveRuns);

        // A hook of another tree still gets in, and so do two more, up to its own cap.
        var sb = new JsSandbox(options);
        JsRunResult r = await sb.CallAsync("export function f() { return 7; }", "f.js", "f", [], passCtx: false, As("victim"), ToolCapability.None, null, default, runClass: RunClass.Hook);
        Assert.Equal("7", r.Value!.ToJsonString());
        await stop.CancelAsync();
        await Task.WhenAll(hogs);
    }

    [Fact]
    public async Task A_hook_owner_is_capped_so_one_tree_cannot_fill_the_reserve()
    {
        var limiter = new JsRunLimiter(maxConcurrentRuns: 8, maxWait: TimeSpan.Zero, reservedForInterceptors: 2, maxRunsPerCaller: 3, maxInterceptorsPerOwner: 2);
        var sb = new JsSandbox(new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(60), Fuel = 100_000_000_000 });
        using var stop = new CancellationTokenSource();
        List<Task> hooks = [];
        for (int i = 0; i < 5; i++)
        {
            hooks.Add(Swallow(sb.CallAsync("export function f() { for (;;) {} }", "f.js", "f", [], passCtx: false, Child("kid" + i, "root"), ToolCapability.None, null, stop.Token, runClass: RunClass.Hook)));
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (limiter.ActiveRuns < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(300);
        Assert.Equal(2, limiter.ActiveRuns);
        JsRunResult r = await sb.CallAsync("export function f() { return 7; }", "f.js", "f", [], passCtx: false, As("victim"), ToolCapability.None, null, default, runClass: RunClass.Hook);
        Assert.Equal("7", r.Value!.ToJsonString());
        await stop.CancelAsync();
        await Task.WhenAll(hooks);
    }

    [Fact]
    public async Task Loading_a_file_takes_a_slot_like_a_run()
    {
        var limiter = new JsRunLimiter(maxConcurrentRuns: 8, maxWait: TimeSpan.Zero, maxInterceptorsPerOwner: 1);
        var sb = new JsSandbox(new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(1.5), Fuel = 100_000_000_000 });
        Task<JsSandboxException?> first = Task.Run(() =>
        {
            try
            {
                sb.InspectOwned("for (;;) {}", "a.js", "owner", null);
                return null;
            }
            catch (JsSandboxException e)
            {
                return e;
            }
        });
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (limiter.ActiveRuns < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        JsSandboxException busy = Assert.Throws<JsSandboxException>(() => sb.InspectOwned("export const x = 1;", "b.js", "owner", null));
        Assert.Equal(JsSandboxFailure.Busy, busy.Kind);
        // Another owner still loads.
        sb.InspectOwned("export const x = 1;", "c.js", "someone-else", null);
        Assert.NotNull(await first);
        Assert.True(SpinWait.SpinUntil(() => limiter.ActiveRuns == 0, 5000));
    }

    [Fact]
    public void A_definition_may_not_name_a_flood_of_hook_files()
    {
        var def = OneLlm.Definitions.AgentDefinition.Parse("name: a\nmodel: demo\nsystemPrompt: s\nhooks:\n" + string.Concat(Enumerable.Range(0, JsHookSource.MaxHooksPerDefinition + 1).Select(i => $"  - file: h{i}.js\n")) + "references: []\n", "a.yaml");
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => new JsHookSource().Load(def, Capabilities.None));
        Assert.Contains("at most 16 hook files", e.Message, StringComparison.Ordinal);
    }

    private static async Task SwallowAny(Task run)
    {
        try
        {
            await run;
        }
        catch (Exception e) when (e is JsSandboxException or OperationCanceledException)
        {
            // Refused as busy or cancelled at the end of the test.
        }
    }

    private static async Task Swallow(Task<JsRunResult> run)
    {
        try
        {
            await run;
        }
        catch (JsSandboxException)
        {
            // The extra callers are refused as busy.
        }
        catch (OperationCanceledException)
        {
            // The six that got a slot are cancelled at the end of the test.
        }
    }
}

public class ErrorTextTests
{
    [Fact]
    public async Task A_huge_thrown_message_is_cut_with_a_marker()
    {
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run("throw new Error('y'.repeat(5e6));"));
        Assert.True(e.Message.Length < JsSandboxException.MaxMessageChars + 200, e.Message.Length.ToString());
        Assert.Contains("characters of the error left out", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_javascript_result_for_a_huge_throw_is_bounded()
    {
        ToolResult r = await ToolInvoker.InvokeAsync(JsTools.RunJavaScript(), new JsonObject { ["code"] = "throw 'z'.repeat(5e6);" }, Js.Context(), CancellationToken.None);
        Assert.True(r.IsError);
        Assert.True(Js.Text(r).Length < JsSandboxException.MaxMessageChars + 300);
    }

    [Fact]
    public void A_load_time_timeout_names_the_file_once()
    {
        using var dir = new Scratch();
        string path = dir.Write("spin.js", "export const tool = { description: 'x' };\nexport function invoke() {}\nfor (;;) {}\n");
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => JsTools.Load(path, new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(300) }));
        Assert.DoesNotContain("spin.js: spin.js", e.Message, StringComparison.Ordinal);
        Assert.StartsWith("spin.js", e.Message, StringComparison.Ordinal);
    }
}
