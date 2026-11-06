using System.Diagnostics;

namespace OneLlm.JavaScript.Tests;

/// <summary>Scripts that try to make the host allocate far more than the engine's budget, one per way a script can grow memory.</summary>
internal static class MemoryCases
{
    public static readonly (string Name, string Code)[] All =
    [
        ("array-fill-1e9", "new Array(1e9).fill(0);"),
        ("array-from-length-1e9", "Array.from({ length: 1e9 });"),
        ("array-from-map-1e9", "Array.from({ length: 1e9 }, (_, i) => i);"),
        ("array-push-loop", "const keep = []; for (let i = 0; ; i++) keep.push(i);"),
        ("array-concat-doubling", "let a = [1]; while (true) a = a.concat(a);"),
        ("array-spread-holes", "const a = [...new Array(1e8)];"),
        ("array-length-assign", "const a = []; a.length = 4e9; a.fill(0);"),
        ("array-unshift-loop", "const a = []; for (let i = 0; ; i++) a.unshift(i);"),
        ("array-splice-grow", "const a = [1]; while (true) a.splice(0, 0, ...a);"),
        ("array-flat-grow", "let a = [1]; while (true) a = [a, a].flat();"),
        ("array-keys-spread", "const a = [...new Array(1e8).keys()];"),
        ("string-split", "const a = 'a,'.repeat(8e6).split(',');"),
        ("object-hog", "const keep = []; while (true) keep.push({ a: 1, b: [1, 2, 3] });"),
        ("object-many-properties", "const o = {}; for (let i = 0; ; i++) o['k' + i] = i;"),
        ("closure-hog", "const keep = []; for (let i = 0; ; i++) keep.push(() => i);"),
        ("typed-array-huge", "new Float64Array(2 ** 28);"),
        ("typed-array-uint8-2gb", "new Uint8Array(2 ** 31 - 1);"),
        ("typed-array-loop", "const keep = []; while (true) keep.push(new Uint8Array(1e7));"),
        ("typed-array-sort", "new Float64Array(5e7).sort();"),
        ("array-buffer-huge", "new ArrayBuffer(2 ** 31);"),
        ("array-buffer-resize", "const b = new ArrayBuffer(1, { maxByteLength: 2 ** 31 - 1 }); b.resize(2 ** 31 - 2);"),
        ("array-buffer-loop", "const keep = []; while (true) keep.push(new ArrayBuffer(1e7));"),
        ("join-large", "const a = new Array(4e6).fill('abcdefghijklmnop'); a.join(',');"),
        ("join-loop", "const a = new Array(1e6).fill('abcdefghijklmnop'); const keep = []; while (true) keep.push(a.join(','));"),
        ("json-stringify-large", "const a = new Array(4e6).fill({ k: 'abcdefgh', n: 1 }); JSON.stringify(a);"),
        ("json-stringify-loop", "const a = new Array(1e5).fill({ k: 'abcdefgh', n: 1 }); const keep = []; while (true) keep.push(JSON.stringify(a));"),
        ("string-doubling", "let s = 'x'; while (true) { s = s + s; }"),
        ("string-repeat-loop", "const keep = []; while (true) keep.push('x'.repeat(1e7) + keep.length);"),
        ("map-growth", "const m = new Map(); for (let i = 0; ; i++) m.set(i, i);"),
        ("set-growth", "const s = new Set(); for (let i = 0; ; i++) s.add(i);"),
        ("weakmap-growth", "const m = new WeakMap(); const keep = []; for (let i = 0; ; i++) { const k = {}; keep.push(k); m.set(k, i); }"),
        ("finalization-registry", "const r = new FinalizationRegistry(() => {}); for (let i = 0; ; i++) r.register({}, i);"),
        ("symbol-registry", "for (let i = 0; ; i++) Symbol.for('k' + i);"),
        ("bigint-squaring", "let b = 3n; while (true) b = b * b;"),
        ("bigint-squaring-26", "let b = 3n; for (let i = 0; i < 26; i++) b = b * b; b > 0n;"),
        ("bigint-shift", "1n << 4000000000n;"),
        ("bigint-pow", "2n ** 3000000000n;"),
        ("bigint-asuint", "BigInt.asUintN(2 ** 31, -1n);"),
        ("promise-queue", "for (;;) Promise.resolve().then(() => {});"),
        ("iterator-gather", "const s = new Set(); for (let i = 0; i < 3e6; i++) s.add(i); const keep = []; while (true) keep.push([...s]);"),
    ];

    public static string Code(string name) => All.First(c => c.Name == name).Code;
}

// Memory bombs run alone: no other test class runs beside them, and their budgets are small so a run peaks well under 600 MiB
// (safe on a 7 GB CI runner).
[CollectionDefinition("MemoryBombs", DisableParallelization = true)]
public class MemoryBombsDefinition;

[Collection("MemoryBombs")]
public class MemoryTests
{
    private static JsSandboxOptions Options(bool backstop) => new()
    {
        TimeLimit = TimeSpan.FromSeconds(30),
        Fuel = 5_000_000_000,
        MaxAllocatedBytes = 64L * 1024 * 1024,
        MaxHostAllocatedBytes = backstop ? 256L * 1024 * 1024 : 0,
    };

    // Too slow for a regression run: they end at the 30 s time limit (or run past it, see below). They stay in the probe list.
    private static readonly HashSet<string> Slow = ["array-unshift-loop", "bigint-squaring", "bigint-squaring-26"];

    // Growth the engine's own allocation budget does not stop (it is charged nothing, or too little): only the host backstop ends these.
    private static readonly HashSet<string> NeedsBackstop = ["symbol-registry", "promise-queue"];

    public static IEnumerable<TheoryDataRow<string>> Names() => MemoryCases.All.Where(c => !Slow.Contains(c.Name)).Select(c => new TheoryDataRow<string>(c.Name));

    public static IEnumerable<TheoryDataRow<string>> EngineStopsNames() => MemoryCases.All.Where(c => !Slow.Contains(c.Name) && !NeedsBackstop.Contains(c.Name)).Select(c => new TheoryDataRow<string>(c.Name));

    // The engine's own budget plus the host backstop: every case ends in a limit report, never an out-of-memory crash.
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_memory_pattern_ends_in_a_limit_report(string name)
    {
        var options = Options(true);
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run(MemoryCases.Code(name), options: options));
        Assert.True(e.Kind is JsSandboxFailure.Memory or JsSandboxFailure.Script, $"{name}: {e.Kind}");
    }

    // The engine's own limits, with the host backstop off (the test-only setting is MaxHostAllocatedBytes = 0): what OneEcma 1.0.1 stops by itself.
    [Theory]
    [MemberData(nameof(EngineStopsNames))]
    public async Task The_engine_alone_stops_these_patterns(string name)
    {
        var options = Options(false);
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run(MemoryCases.Code(name), options: options));
        Assert.True(e.Kind is JsSandboxFailure.Memory or JsSandboxFailure.Script, $"{name}: {e.Kind}");
    }

    // Measurement probe, not a regression test: runs one case named by ONELLM_MEMCASE with the host backstop on or off
    // (ONELLM_JS_BACKSTOP=off) and appends one line to ONELLM_MEMLOG. Run once per process so the peak working set is the case's own.
    [Fact]
    public async Task Probe()
    {
        string? name = Environment.GetEnvironmentVariable("ONELLM_MEMCASE");
        string? log = Environment.GetEnvironmentVariable("ONELLM_MEMLOG");
        if (name is null || log is null)
        {
            return;
        }

        bool off = Environment.GetEnvironmentVariable("ONELLM_JS_BACKSTOP") == "off";
        // ONELLM_MEMLIMITS=wide: the engine's own budget with a long clock and a large fuel (so slow patterns show how they end); otherwise the product defaults.
        bool wide = Environment.GetEnvironmentVariable("ONELLM_MEMLIMITS") == "wide";
        var options = new JsSandboxOptions
        {
            TimeLimit = wide ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10),
            Fuel = wide ? 5_000_000_000 : 200_000_000,
            MaxHostAllocatedBytes = off ? 0 : 1024L * 1024 * 1024,
        };
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long before = self.PeakWorkingSet64;
        long start = Stopwatch.GetTimestamp();
        string outcome;
        try
        {
            await Js.Run(MemoryCases.Code(name), options: options);
            outcome = "completed";
        }
        catch (JsSandboxException e)
        {
            string text = e.Message.Split('\n')[0];
            outcome = $"{e.Kind}: {text[..Math.Min(90, text.Length)]}";
        }

        self.Refresh();
        long ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        File.AppendAllText(log, $"{name}\t{(off ? "budget-only" : "with-backstop")}\t{outcome}\t{ms} ms\tpeak {(self.PeakWorkingSet64 - before) >> 20} MiB over start\n");
    }
}

[Collection("MemoryBombs")]
public class UnstoppableScriptTests
{
    // Explaining an uncaught error replays the run. The replay has its own work budget, so a script that does a lot and then fails ends within
    // its limits instead of costing a second unbounded run.
    [Theory]
    [InlineData("let s = 0; for (let i = 0; i < 4e6; i++) { s += i; } null.x;")]
    [InlineData("const keep = []; for (let i = 0; i < 2e6; i++) keep.push({ i, s: String(i) }); throw new Error('late ' + keep.length);")]
    [InlineData("function f(n) { return n === 0 ? undefined.x : f(n - 1) + 1; } f(900);")]
    [InlineData("const a = new Array(3e6).fill(1); const o = {}; for (let i = 0; i < 1e5; i++) o['k' + i] = a.length; o.missing.deep;")]
    public async Task A_failing_script_that_did_a_lot_of_work_ends_within_its_limits(string code)
    {
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromSeconds(20), Fuel = 200_000_000, MaxAllocatedBytes = 128L * 1024 * 1024 };
        long start = Stopwatch.GetTimestamp();
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () => await Js.Run(code, options: options));
        Assert.True(e.Kind is JsSandboxFailure.Script or JsSandboxFailure.Memory or JsSandboxFailure.Fuel, e.Kind.ToString());
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(20));
    }

    // Squaring a BigInt in a loop reaches single operations that take minutes. The engine polls the clock inside them; if it ever could not,
    // the host would stop waiting at the time limit and count the engine it left behind (JsSandbox.AbandonedRuns).
    [Fact]
    public async Task A_bigint_multiplication_loop_ends_at_the_time_limit()
    {
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromSeconds(1), Fuel = 5_000_000_000, MaxAllocatedBytes = 1L << 40, MaxHostAllocatedBytes = 0 };
        long before = JsSandbox.AbandonedRuns;
        long start = Stopwatch.GetTimestamp();
        JsSandboxException e = await Assert.ThrowsAsync<JsSandboxException>(async () =>
            await Js.Run("let b = 3n; while (true) b = b * b;", options: options));
        TimeSpan took = Stopwatch.GetElapsedTime(start);
        Assert.Equal(JsSandboxFailure.Time, e.Kind);
        Assert.True(took < TimeSpan.FromSeconds(3), $"took {took}");
        // Since OneEcma 1.1.0 the engine polls the clock inside BigInt multiplication, so it stops by itself within the grace and nothing is abandoned.
        Assert.Equal(before, JsSandbox.AbandonedRuns);
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_wait_for_an_engine_that_cannot_stop()
    {
        using var cts = new CancellationTokenSource();
        var options = new JsSandboxOptions { TimeLimit = TimeSpan.FromSeconds(60), Fuel = 5_000_000_000, MaxAllocatedBytes = 1L << 40, MaxHostAllocatedBytes = 0 };
        Task<JsRunResult> run = new JsSandbox(options).RunSnippetAsync("let b = 3n; while (true) b = b * b;", null, Js.Context(), Js.All, "snippet.js", cts.Token);
        await Task.Delay(300);
        long start = Stopwatch.GetTimestamp();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(3));
    }
}
