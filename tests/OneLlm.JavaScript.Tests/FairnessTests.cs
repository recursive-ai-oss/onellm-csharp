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
        JsRunResult r = await sb.CallAsync("export function f() { return 7; }", "f.js", "f", [], passCtx: false, As("hook"), ToolCapability.None, null, default, interceptor: true);
        Assert.Equal("7", r.Value!.ToJsonString());

        await stop.CancelAsync();
        await Task.WhenAll(hogs);
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
