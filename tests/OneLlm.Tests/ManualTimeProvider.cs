namespace OneLlm.Tests;

/// <summary>A clock that moves only when a test says so. Timers fire from <see cref="Advance"/>; their due times are recorded.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Due times, in order of creation, of every timer ever created.</summary>
    public List<TimeSpan> CreatedDueTimes { get; } = [];

    /// <summary>The number of timers waiting to fire.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.IsPending);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            CreatedDueTimes.Add(dueTime);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that falls due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.IsPending && t.DueAt <= _now)];
            foreach (ManualTimer t in due)
            {
                t.MarkFired();
            }
        }

        foreach (ManualTimer t in due)
        {
            t.Fire();
        }
    }

    /// <summary>Waits until a timer other than those with the given due time is pending, for tests that wait on a retry delay.</summary>
    public async Task WaitForPendingAsync(TimeSpan? ignoreDueTime = null)
    {
        for (int i = 0; i < 2000; i++)
        {
            lock (_gate)
            {
                if (_timers.Any(t => t.IsPending && (ignoreDueTime is null || t.Due != ignoreDueTime)))
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("No timer became pending.");
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public bool IsPending { get; private set; }

        public DateTimeOffset DueAt { get; private set; }

        public TimeSpan Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                IsPending = false;
                return true;
            }

            Due = dueTime;
            DueAt = owner._now + dueTime;
            IsPending = true;
            return true;
        }

        public void MarkFired() => IsPending = false;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                IsPending = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
