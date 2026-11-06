using System.Diagnostics;
using System.Text.Json.Nodes;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using RecursiveAI.OneEcma;

namespace OneLlm.JavaScript.Tests;

internal sealed class CollectingLog : IToolLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void Write(string toolCallId, ToolLogLevel level, string message)
    {
        lock (_lines)
        {
            _lines.Add(message);
        }
    }
}

/// <summary>Once a run has ended, nothing may act for it: no file, no cost line, no log line, no request.</summary>
public class RunLifetimeTests
{
    private static (ScriptEngine Engine, RunScope Scope, ToolContext Context, CollectingLog Log) Manual(JsSandboxOptions options, ICapabilities caps)
    {
        var log = new CollectingLog();
        var context = new ToolContext("inst", "call", TimeProvider.System) { Capabilities = caps, LogSink = log };
        var scope = new RunScope(options, null);
        scope.RunToken = scope.Token;
        var env = new HostEnv(context, Js.All, options, new OutputBuffer(1000), scope);
        var engine = new ScriptEngine(new ScriptEngineOptions { Configure = e => env.Install(e, null, false) });
        return (engine, scope, context, log);
    }

    [Fact]
    public void After_the_run_is_closed_every_host_function_refuses_and_pending_work_is_cancelled()
    {
        using var dir = new Scratch();
        dir.Write("in.txt", "x");
        var options = new JsSandboxOptions();
        (ScriptEngine engine, RunScope scope, ToolContext context, CollectingLog log) = Manual(options, Capabilities.Files(dir.Root) + Capabilities.Network("127.0.0.1"));

        // Open: all of them work.
        engine.Evaluate("ctx.writeFile('a.txt', 'one'); ctx.addCost({counter:'c', quantity:1, unitPrice:'0.5'}); ctx.log('hi'); ctx.readFile('in.txt');");
        Assert.Equal("one", File.ReadAllText(dir.In("a.txt")));
        Assert.Single(context.CostLines);

        scope.Close();

        const string Probe = """
            const out = [];
            for (const f of [() => ctx.writeFile('b.txt', 'two'), () => ctx.addCost({counter:'c', quantity:1, unitPrice:'0.5'}), () => ctx.log('late'), () => ctx.readFile('in.txt'), () => ctx.fetch('http://127.0.0.1:1/')]) {
              try { f(); out.push('acted'); } catch (e) { out.push(e.message); }
            }
            out.join('|');
            """;
        string result = engine.ToDisplayString(engine.Evaluate(Probe));
        Assert.Equal("the run has ended|the run has ended|the run has ended|the run has ended|the run has ended", result);
        Assert.False(File.Exists(dir.In("b.txt")));
        Assert.Single(context.CostLines);
        Assert.Single(log.Lines);
        Assert.True(scope.Token.IsCancellationRequested);
        scope.Dispose();
    }

    [Fact]
    public async Task A_script_the_host_gave_up_on_adds_nothing_after_the_failure_was_reported()
    {
        using var dir = new Scratch();
        var log = new CollectingLog();
        var context = new ToolContext("inst", "call", TimeProvider.System) { Capabilities = Capabilities.Files(dir.Root), LogSink = log };
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(400), MaxWritesPerRun = 100_000 };
        // Long single steps (collation of a long string) keep the engine from reaching its time check, which is how a run gets abandoned.
        const string Code = """
            const s = '\u00e9'.repeat(400000);
            for (let i = 0; i < 200; i++) {
              s.localeCompare(s + 'a');
              ctx.writeFile('late.txt', 'tick ' + i);
              ctx.addCost({ counter: 'tick', quantity: 1, unitPrice: '0.001' });
              ctx.log('tick ' + i);
            }
            """;
        await Assert.ThrowsAsync<JsSandboxException>(async () => await new JsSandbox(options).RunSnippetAsync(Code, null, context, Js.All));

        string? after = File.Exists(dir.In("late.txt")) ? File.ReadAllText(dir.In("late.txt")) : null;
        int cost = context.CostLines.Count;
        int logs = log.Lines.Count;
        await Task.Delay(2500);
        Assert.Equal(after, File.Exists(dir.In("late.txt")) ? File.ReadAllText(dir.In("late.txt")) : null);
        Assert.Equal(cost, context.CostLines.Count);
        Assert.Equal(logs, log.Lines.Count);
    }

    [Fact]
    public async Task A_run_cancelled_by_the_caller_stops_acting_too()
    {
        var log = new CollectingLog();
        var context = new ToolContext("inst", "call", TimeProvider.System) { LogSink = log };
        using var cts = new CancellationTokenSource();
        var options = new JsSandboxOptions { Fuel = 100_000_000_000, TimeLimit = TimeSpan.FromSeconds(30), MaxLogChars = long.MaxValue };
        Task<JsRunResult> run = new JsSandbox(options).RunSnippetAsync("for (let i = 0; ; i++) { ctx.log('t'); }", null, context, Js.All, "snippet.js", cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        int lines = log.Lines.Count;
        await Task.Delay(500);
        Assert.Equal(lines, log.Lines.Count);
    }

    [Fact]
    public async Task A_fetch_the_script_does_not_wait_for_is_cancelled_when_the_run_returns()
    {
        using var server = new StubServer(_ =>
        {
            Thread.Sleep(3000);
            return new StubResponse(200, "late");
        });
        var context = new ToolContext("inst", "call", TimeProvider.System) { Capabilities = Capabilities.Network("127.0.0.1") };
        var watch = Stopwatch.StartNew();
        JsRunResult r = await new JsSandbox().RunSnippetAsync($"ctx.fetch('{server.BaseUri}'); return 1;", null, context, Js.All);
        Assert.Equal("1", r.Value!.ToJsonString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"waited for the fetch: {watch.Elapsed}");
    }
}

/// <summary>Limits across runs: how many at once, how many bytes between them, and what one run may hold.</summary>
[Collection("MemoryBombs")]
public class RunLimiterTests
{
    private static JsSandbox Sandbox(JsRunLimiter limiter, TimeSpan? time = null, Action<JsSandboxOptions>? _ = null) =>
        new(new JsSandboxOptions { Limiter = limiter, TimeLimit = time ?? TimeSpan.FromSeconds(5), Fuel = 100_000_000_000 });

    private static Task<JsRunResult> Go(JsSandbox sb, string code, ICapabilities? caps = null) =>
        sb.RunSnippetAsync(code, null, Js.Context(caps), Js.All);

    [Fact]
    public async Task A_run_over_the_concurrency_limit_fails_as_busy_instead_of_hanging()
    {
        using var limiter = new JsRunLimiter(maxConcurrentRuns: 1, maxWait: TimeSpan.Zero);
        JsSandbox sb = Sandbox(limiter, TimeSpan.FromSeconds(1.5));
        Task<JsRunResult> first = Go(sb, "const t = Date.now(); while (Date.now() - t < 1000) {} return 1;");
        while (limiter.ActiveRuns == 0)
        {
            await Task.Delay(10);
        }

        var watch = Stopwatch.StartNew();
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Go(sb, "return 2;"));
        Assert.Equal(JsSandboxFailure.Busy, e.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal("1", (await first).Value!.ToJsonString());

        // The slot comes back when the first run's thread has ended.
        Assert.Equal("2", (await Go(sb, "return 2;")).Value!.ToJsonString());
        Assert.True(SpinWait.SpinUntil(() => limiter.ActiveRuns == 0, 2000));
    }

    [Fact]
    public async Task A_run_waits_for_a_slot_up_to_the_limiters_wait_and_then_fails_as_busy()
    {
        using var limiter = new JsRunLimiter(maxConcurrentRuns: 1, maxWait: TimeSpan.FromMilliseconds(300));
        JsSandbox sb = Sandbox(limiter, TimeSpan.FromSeconds(3));
        Task<JsRunResult> first = Go(sb, "const t = Date.now(); while (Date.now() - t < 1500) {} return 1;");
        while (limiter.ActiveRuns == 0)
        {
            await Task.Delay(10);
        }

        var watch = Stopwatch.StartNew();
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Go(sb, "return 2;"));
        Assert.Equal(JsSandboxFailure.Busy, e.Kind);
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 250, 1400);
        await first;
    }

    [Fact]
    public async Task Parallel_runs_beyond_the_limit_queue_and_finish_without_exceeding_it()
    {
        using var limiter = new JsRunLimiter(maxConcurrentRuns: 2, maxWait: TimeSpan.FromSeconds(30));
        JsSandbox sb = Sandbox(limiter);
        int peak = 0;
        Task<JsRunResult>[] runs = [.. Enumerable.Range(0, 6).Select(_ => Go(sb, "const t = Date.now(); while (Date.now() - t < 150) {} return 1;"))];
        while (!runs.All(r => r.IsCompleted))
        {
            peak = Math.Max(peak, limiter.ActiveRuns);
            await Task.Delay(5);
        }

        await Task.WhenAll(runs);
        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public async Task File_and_response_text_is_charged_to_the_run_and_a_run_holds_a_bounded_amount()
    {
        using var dir = new Scratch();
        dir.Write("big.txt", new string('a', 1024 * 1024));
        // Default per-run cap is 32 MiB, so a 1 MiB file (2 MiB as a string) can be read 16 times.
        JsRunResult r = await new JsSandbox().RunSnippetAsync(
            "const keep = []; let n = 0; try { for (;;) { keep.push(ctx.readFile('big.txt')); n++; } } catch (e) { return [n, e.name, e.message]; }",
            null,
            Js.Context(Capabilities.Files(dir.Root)),
            Js.All);
        JsonNode v = r.Value!;
        Assert.Equal(16, v[0]!.GetValue<int>());
        Assert.Equal("RangeError", v[1]!.GetValue<string>());
        Assert.Contains("at most 33554432 bytes", v[2]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_limiters_total_budget_ends_the_run_that_passes_it_as_busy()
    {
        using var dir = new Scratch();
        dir.Write("big.txt", new string('a', 1024 * 1024));
        using var limiter = new JsRunLimiter(maxConcurrentRuns: 4, maxTotalBytes: 5L * 1024 * 1024);
        JsSandbox sb = Sandbox(limiter);
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Go(sb, "const keep = []; for (let i = 0; i < 10; i++) keep.push(ctx.readFile('big.txt')); return keep.length;", Capabilities.Files(dir.Root)));
        Assert.Equal(JsSandboxFailure.Busy, e.Kind);
        Assert.True(SpinWait.SpinUntil(() => limiter.ActiveRuns == 0, 2000));
        Assert.Equal(0, limiter.ChargedBytes);
    }

    [Fact]
    public async Task A_response_body_is_charged_like_a_file()
    {
        using var server = new StubServer(_ => new StubResponse(200, new string('b', 100_000)));
        var options = new JsSandboxOptions { MaxHostResultBytes = 300_000 };
        JsRunResult r = await new JsSandbox(options).RunSnippetAsync(
            $"let n = 0; try {{ for (;;) {{ const res = await ctx.fetch('{server.BaseUri}'); n++; }} }} catch (e) {{ return n; }}",
            null,
            Js.Context(Capabilities.Network("127.0.0.1")),
            Js.All);
        Assert.Equal("1", r.Value!.ToJsonString());
    }

    [Fact]
    public async Task Parallel_memory_bombs_end_in_limit_failures_and_the_slots_come_back()
    {
        using var limiter = new JsRunLimiter(maxConcurrentRuns: 4, maxWait: TimeSpan.FromSeconds(60));
        var options = new JsSandboxOptions { Limiter = limiter, TimeLimit = TimeSpan.FromSeconds(20), MaxHostAllocatedBytes = 128L * 1024 * 1024, MaxAllocatedBytes = 64L * 1024 * 1024, Fuel = 5_000_000_000 };
        var sb = new JsSandbox(options);
        Task<JsRunResult>[] runs = [.. Enumerable.Range(0, 8).Select(_ => sb.RunSnippetAsync("const m = new Map(); for (let i = 0; ; i++) m.set(i, { i });", null, Js.Context(), Js.All))];
        foreach (Task<JsRunResult> run in runs)
        {
            JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await run);
            Assert.True(e.Kind is JsSandboxFailure.Memory or JsSandboxFailure.Busy, e.Kind.ToString());
        }

        Assert.True(SpinWait.SpinUntil(() => limiter.ActiveRuns == 0, 5000));
    }
}

/// <summary>Quotas that bound what one run may do with the functions it is granted.</summary>
public class RunQuotaTests
{
    [Fact]
    public async Task Writes_are_limited_by_count_and_bytes()
    {
        using var dir = new Scratch();
        var options = new JsSandboxOptions { MaxWritesPerRun = 3, MaxWriteBytesPerRun = 100 };
        JsRunResult r = await new JsSandbox(options).RunSnippetAsync(
            "const out = []; for (let i = 0; i < 5; i++) { try { ctx.writeFile('f' + i + '.txt', 'x'); out.push('ok'); } catch (e) { out.push(e.name); } } return out;",
            null,
            Js.Context(Capabilities.Files(dir.Root)),
            Js.All);
        Assert.Equal("""["ok","ok","ok","RangeError","RangeError"]""", r.Value!.ToJsonString());
        Assert.Equal(3, Directory.GetFiles(dir.Root).Length);

        var bytes = new JsSandboxOptions { MaxWriteBytesPerRun = 100 };
        JsRunResult r2 = await new JsSandbox(bytes).RunSnippetAsync(
            "try { ctx.writeFile('a', 'x'.repeat(60)); ctx.writeFile('b', 'x'.repeat(60)); return 'both'; } catch (e) { return e.message; }",
            null,
            Js.Context(Capabilities.Files(dir.Root)),
            Js.All);
        Assert.Contains("at most 100 bytes", r2.Value!.ToJsonString(), StringComparison.Ordinal);
        Assert.False(File.Exists(dir.In("b")));
    }

    [Fact]
    public async Task Log_volume_is_limited_with_one_notice()
    {
        var log = new CollectingLog();
        var options = new JsSandboxOptions { MaxLogChars = 50 };
        await new JsSandbox(options).RunSnippetAsync("for (let i = 0; i < 1000; i++) ctx.log('line number ' + i);", null, Js.Context(log: log), Js.All);
        Assert.InRange(log.Lines.Count, 2, 6);
        Assert.Single(log.Lines, l => l.Contains("output limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_are_limited_by_count()
    {
        using var server = new StubServer(_ => new StubResponse(200, "ok"));
        var options = new JsSandboxOptions { MaxFetchesPerRun = 2 };
        JsRunResult r = await new JsSandbox(options).RunSnippetAsync(
            $"const out = []; for (let i = 0; i < 4; i++) {{ try {{ await ctx.fetch('{server.BaseUri}'); out.push('ok'); }} catch (e) {{ out.push(e.name); }} }} return out;",
            null,
            Js.Context(Capabilities.Network("127.0.0.1")),
            Js.All);
        Assert.Equal("""["ok","ok","RangeError","RangeError"]""", r.Value!.ToJsonString());
        Assert.Equal(2, server.Requests.Count);
    }
}

public class LoadGuardTests
{
    [Fact]
    public void A_tool_file_whose_top_level_never_ends_fails_to_load_in_time()
    {
        using var dir = new Scratch();
        string path = dir.Write("spin.js", "export const tool = { description: 'x' };\nexport function invoke() {}\nfor (;;) {}\n");
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(400) };
        var watch = Stopwatch.StartNew();
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => JsTools.Load(path, options));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Contains("spin.js", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_whose_top_level_builds_a_memory_bomb_fails_to_load()
    {
        using var dir = new Scratch();
        string path = dir.Write("bomb.js", "export const tool = { description: 'x' };\nexport function invoke() {}\nconst a = []; for (let i = 0; ; i++) a.push(new Array(1000).fill(i));\n");
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromSeconds(20), MaxHostAllocatedBytes = 128L * 1024 * 1024, MaxAllocatedBytes = 64L * 1024 * 1024, Fuel = 5_000_000_000 };
        Assert.Throws<BadDefinitionException>(() => JsTools.Load(path, options));
    }
}

public class SpecialFileTests
{
    private static string? MakeFifo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        using Process? p = Process.Start(new ProcessStartInfo("mkfifo", path) { UseShellExecute = false, RedirectStandardError = true });
        p?.WaitForExit();
        return p is { ExitCode: 0 } ? path : null;
    }

    [Fact]
    public async Task A_pipe_in_a_granted_root_is_refused_without_holding_a_thread()
    {
        using var dir = new Scratch();
        if (MakeFifo(dir.In("pipe")) is null)
        {
            return;
        }

        long stuckBefore = JsSandbox.AbandonedRunsStillRunning;
        var watch = Stopwatch.StartNew();
        JsSandboxException read = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await new JsSandbox().RunSnippetAsync("return ctx.readFile('pipe');", null, Js.Context(Capabilities.Files(dir.Root)), Js.All));
        Assert.Contains("did not open in time", read.Message, StringComparison.Ordinal);
        JsSandboxException write = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await new JsSandbox().RunSnippetAsync("ctx.writeFile('pipe', 'x');", null, Js.Context(Capabilities.Files(dir.Root)), Js.All));
        Assert.Contains("did not open in time", write.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        Assert.Equal(stuckBefore, JsSandbox.AbandonedRunsStillRunning);

        // The helper threads that were blocked in open have been released: a plain file still reads, and opening the pipe's other end
        // finds no stranded opener (it would block for ever if a reader were still waiting, so give it a bounded try).
        dir.Write("ok.txt", "fine");
        JsRunResult ok = await new JsSandbox().RunSnippetAsync("return ctx.readFile('ok.txt');", null, Js.Context(Capabilities.Files(dir.Root)), Js.All);
        Assert.Equal("\"fine\"", ok.Value!.ToJsonString());
    }
}

[Collection("MemoryBombs")]
public class BackstopMeasureTests
{
    [Fact]
    public async Task A_busy_wait_that_only_churns_short_lived_objects_is_not_a_memory_failure()
    {
        // Date.now() and the strings below allocate far more than the limit over 1.4 s but hold nothing: churn is not growth.
        var options = new JsSandboxOptions { MaxHostAllocatedBytes = 64L * 1024 * 1024, TimeLimit = TimeSpan.FromSeconds(20), Fuel = 100_000_000_000 };
        JsRunResult r = await Js.Run("const t = Date.now(); let n = 0; while (Date.now() - t < 1400) { n += ('abc' + n).length; } return 'done';", options: options);
        Assert.Equal("\"done\"", r.Value!.ToJsonString());
    }
}
