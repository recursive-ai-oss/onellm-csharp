namespace OneLlm.JavaScript;

/// <summary>
/// The limits one sandboxed JavaScript run works under, and the switches that make it reproducible. Every run gets a fresh engine, so
/// nothing a script leaves behind reaches the next call.
/// </summary>
/// <remarks>
/// Limits are enforced by the engine itself (steps, allocation volume, call depth, string length) and by the host (the time limit,
/// which also covers a script waiting on <c>ctx.fetch</c>). A limit ends the run with a <see cref="JsSandboxException"/>; script cannot catch it.
/// </remarks>
public sealed class JsSandboxOptions
{
    /// <summary>The step budget: one unit per call, per loop iteration and per unit of work in long-running built-ins. Default 200 million.</summary>
    public long Fuel { get; init; } = 200_000_000;

    /// <summary>
    /// The allocation budget in bytes. The engine counts bytes requested over the whole run, not bytes alive at once, so a script that
    /// allocates and drops in a loop is stopped too. Default 256 MiB.
    /// </summary>
    public long MaxAllocatedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// A backstop on what .NET allocates while the script runs, in bytes. The engine's budget does not count the backing storage of arrays, so
    /// <c>new Array(1e8)</c> would pass it; this counts everything allocated on the running thread and ends the run when it passes the limit.
    /// It is checked about as often as the time limit, so one very large single allocation can still happen before it fires. Zero turns it off. Default 1 GiB.
    /// </summary>
    public long MaxHostAllocatedBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>The most cost lines one run may add with <c>ctx.addCost</c>; the next call throws a <c>TypeError</c>. Default 100.</summary>
    public int MaxCostLines { get; init; } = 100;

    /// <summary>How long one run may take, on the runtime's clock. A tool's own <c>timeout</c> shortens it. Default 10 seconds.</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How deep script calls may nest before a catchable <c>RangeError</c>. Default 1,000.</summary>
    public int MaxCallDepth { get; init; } = 1_000;

    /// <summary>The longest string a script may build, in UTF-16 code units. Default 16 million.</summary>
    public int MaxStringLength { get; init; } = 16_000_000;

    /// <summary>The most bytes <c>ctx.readFile</c> returns and <c>ctx.writeFile</c> writes. Default 1 MiB.</summary>
    public int MaxFileBytes { get; init; } = 1024 * 1024;

    /// <summary>The most bytes of a response body <c>ctx.fetch</c> reads; the rest is cut and <c>truncated</c> is true. Default 1 MiB.</summary>
    public int MaxFetchBytes { get; init; } = 1024 * 1024;

    /// <summary>The longest JSON text a run may return, in UTF-16 code units. Default 1 million.</summary>
    public int MaxResultChars { get; init; } = 1_000_000;

    /// <summary>
    /// The most characters of console output kept for the model, as in the shell tool: the first half and the last half are kept and the middle is
    /// replaced by a note saying how much was left out. Default 10,240.
    /// </summary>
    public int MaxOutputChars { get; init; } = 10 * 1024;

    /// <summary>
    /// When true, <c>Date.now()</c> and <c>new Date()</c> read <see cref="FixedTime"/> and <c>Math.random()</c> follows <see cref="RandomSeed"/>, so a run
    /// gives the same result every time. Default false: time comes from the runtime's clock and randomness is unseeded. Meant for tests.
    /// </summary>
    public bool Deterministic { get; init; }

    /// <summary>The instant a deterministic run reads as "now". Default 2000-01-01T00:00:00Z.</summary>
    public DateTimeOffset FixedTime { get; init; } = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The seed of <c>Math.random()</c> in a deterministic run. Default 1.</summary>
    public int RandomSeed { get; init; } = 1;
}
