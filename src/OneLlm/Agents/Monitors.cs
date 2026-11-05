using System.Globalization;

namespace OneLlm.Agents;

/// <summary>What a fired monitor saw.</summary>
internal sealed record MonitorFire(string Condition, string Detail, AgentStatus Snapshot, DateTimeOffset At);

/// <summary>A context-size condition: a token count or a percentage of the model's window.</summary>
internal sealed record ContextThreshold(long? Tokens, double? Percent, string Spec)
{
    public bool IsReached(AgentStatus s)
    {
        if (Tokens is { } t)
        {
            return s.ContextTokens >= t;
        }

        return Percent is { } p && s.ContextWindow > 0 && s.ContextPercent >= p;
    }

    /// <summary>Reads <c>400000</c>, <c>400,000</c>, <c>400k</c>, <c>0.4m</c> or <c>40%</c>.</summary>
    /// <exception cref="ArgumentException">The text is none of those.</exception>
    public static ContextThreshold Parse(string text)
    {
        string spec = text.Trim().Replace(",", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        const string hint = "must be a token count like 400000, 400k or 0.4m, or a percentage like 40%";
        if (spec.EndsWith('%'))
        {
            if (!decimal.TryParse(spec[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal pct) || pct <= 0m || pct > 100m)
            {
                throw new ArgumentException(hint);
            }

            return new ContextThreshold(null, (double)pct, text.Trim());
        }

        decimal scale = 1m;
        if (spec.EndsWith('k') || spec.EndsWith('K'))
        {
            scale = 1_000m;
            spec = spec[..^1];
        }
        else if (spec.EndsWith('m') || spec.EndsWith('M'))
        {
            scale = 1_000_000m;
            spec = spec[..^1];
        }

        if (!decimal.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n) || n <= 0m || n * scale > long.MaxValue / 2)
        {
            throw new ArgumentException(hint);
        }

        return new ContextThreshold((long)(n * scale), null, text.Trim());
    }
}

/// <summary>Reads durations such as <c>90s</c>, <c>30m</c>, <c>2h</c>, <c>1d</c> or <c>1h30m</c>.</summary>
internal static class MonitorDurations
{
    /// <exception cref="ArgumentException">The text is not a duration.</exception>
    public static TimeSpan Parse(string text)
    {
        string s = text.Trim();
        const string hint = "must be a duration like 90s, 30m, 2h or 1d";
        if (s.Length == 0)
        {
            throw new ArgumentException(hint);
        }

        TimeSpan total = TimeSpan.Zero;
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            if (i == start || i >= s.Length || !decimal.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
            {
                throw new ArgumentException(hint);
            }

            double seconds = s[i] switch
            {
                's' => 1d,
                'm' => 60d,
                'h' => 3600d,
                'd' => 86400d,
                _ => throw new ArgumentException(hint),
            };
            i++;
            total += TimeSpan.FromSeconds((double)n * seconds);
        }

        return total > TimeSpan.Zero ? total : throw new ArgumentException(hint);
    }
}

/// <summary>
/// A watch on one agent. The first of its conditions to hold fires it, once; after that it is disarmed and keeps its result until it is
/// collected with <c>await_monitor</c> or replaced by arming again. Elapsed time only reports; it never stops the agent.
/// </summary>
internal sealed class AgentMonitor : IDisposable
{
    private readonly object _gate = new();
    private int _waiters;

    public AgentMonitor(string id, string? watcherId, string targetId, ContextThreshold? context, int turnsAt, bool onFinish, TimeSpan? elapsed, DateTimeOffset armedAt)
    {
        Id = id;
        WatcherId = watcherId;
        TargetId = targetId;
        Context = context;
        TurnsAt = turnsAt;
        OnFinish = onFinish;
        Elapsed = elapsed;
        ArmedAt = armedAt;
        IsArmed = true;
    }

    public string Id { get; }

    public string? WatcherId { get; }

    public string TargetId { get; }

    public ContextThreshold? Context { get; }

    /// <summary>The absolute turn number that fires the monitor, or 0 for none.</summary>
    public int TurnsAt { get; }

    public bool OnFinish { get; }

    public TimeSpan? Elapsed { get; }

    public DateTimeOffset ArmedAt { get; }

    public DateTimeOffset? Deadline => Elapsed is { } e ? ArmedAt + e : null;

    public ITimer? Timer { get; set; }

    private volatile bool _armed;

    /// <summary>True until the monitor fires or is replaced.</summary>
    public bool IsArmed
    {
        get => _armed;
        private set => _armed = value;
    }

    public MonitorFire? Fired { get; private set; }

    public TaskCompletionSource<MonitorFire> FiredTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The id of the alert message sent to the watcher, so a later await can take it back.</summary>
    public string? PushMessageId { get; set; }

    public int Waiters => Volatile.Read(ref _waiters);

    public void BeginWait() => Interlocked.Increment(ref _waiters);

    public void EndWait() => Interlocked.Decrement(ref _waiters);

    public bool TryFire(MonitorFire fire)
    {
        lock (_gate)
        {
            if (!IsArmed)
            {
                return false;
            }

            IsArmed = false;
            Fired = fire;
        }

        Timer?.Dispose();
        FiredTask.TrySetResult(fire);
        return true;
    }

    public void Dispose() => Timer?.Dispose();
}
