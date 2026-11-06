using System.Globalization;

namespace OneLlm.JavaScript;

/// <summary>Why the host ended a run on its own account (as opposed to a limit the engine enforces).</summary>
internal enum TripReason
{
    None = 0,

    /// <summary>The run's live memory growth passed <see cref="JsSandboxOptions.MaxHostAllocatedBytes"/>.</summary>
    Memory = 1,

    /// <summary>The limiter's total byte budget was passed.</summary>
    Budget = 2,
}

/// <summary>
/// Everything the host tracks about one run: whether it is still open, the quotas it has used, and its hold on the shared limiter. Once the run is
/// closed (it finished, was cancelled, hit a limit or was abandoned) every host function refuses to act, so a script that is still executing on a
/// thread the host gave up on cannot write files, add cost, log or send requests for a run that has already been reported.
/// </summary>
internal sealed class RunScope : IDisposable
{
    // How long Close waits for host operations that are already under way (milliseconds).
    private const long CloseWaitMs = 1000;

    private readonly JsSandboxOptions _options;
    private readonly JsRunLimiter.Lease? _lease;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly object _lock = new();
    private bool _closed;
    private int _active;
    private int _trip;
    private long _hostBytes;
    private long _writeBytes;
    private int _writes;
    private int _fetches;
    private long _logChars;
    private bool _logNoticed;

    public RunScope(JsSandboxOptions options, JsRunLimiter.Lease? lease)
    {
        _options = options;
        _lease = lease;
        _token = _cts.Token;
    }

    /// <summary>Cancelled when the run closes. Host work that outlives a call (a fetch) listens to it.</summary>
    public CancellationToken Token => _token;

    /// <summary>The run's overall token: the caller's, the time limit and <see cref="Token"/>. Set by the runner before the engine starts.</summary>
    public CancellationToken RunToken { get; set; }

    /// <summary>Cancels the run's overall token; set by the runner.</summary>
    public Action? CancelRun { get; set; }

    public bool IsClosed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    public TripReason Tripped => (TripReason)Volatile.Read(ref _trip);

    /// <summary>Ends the run for a host-side reason. The first reason wins.</summary>
    public void Trip(TripReason reason)
    {
        if (Interlocked.CompareExchange(ref _trip, (int)reason, 0) != 0)
        {
            return;
        }

        try
        {
            CancelRun?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // The run already ended and released its token source.
        }
    }

    /// <summary>Starts a host operation. Returns false when the run is closed. The operation must call <see cref="Exit"/>.</summary>
    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _active++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_lock)
        {
            _active--;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Closes the run: no new host operation starts, pending host work is cancelled, and operations already under way are given a moment to finish,
    /// so by the time the caller has its result nothing is still acting for the run. Safe to call more than once.
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Nothing is left to cancel.
        }

        long deadline = Environment.TickCount64 + CloseWaitMs;
        lock (_lock)
        {
            while (_active > 0)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    break;
                }

                Monitor.Wait(_lock, (int)left);
            }
        }
    }

    /// <summary>Releases the cancellation source. Call after <see cref="Close"/>, once the engine's thread has ended; host functions never touch it once the run is closed.</summary>
    public void Dispose() => _cts.Dispose();

    /// <summary>Gives the run's slot in the limiter back. Called when the engine's thread has really ended.</summary>
    public void ReleaseLease() => _lease?.Dispose();

    /// <summary>Charges host-result bytes (file text, response bodies) to the run and the limiter. Returns an error text when a budget is passed.</summary>
    public string? ChargeHost(long bytes)
    {
        long total = Interlocked.Add(ref _hostBytes, bytes);
        if (total > _options.MaxHostResultBytes)
        {
            return string.Create(CultureInfo.InvariantCulture, $"a run may hold at most {_options.MaxHostResultBytes} bytes of file and response text");
        }

        if (_lease is not null && !_lease.TryChargeHost(bytes))
        {
            Trip(TripReason.Budget);
            return "the host's memory budget for scripts is used up";
        }

        return null;
    }

    /// <summary>Reports live growth; returns false when the limiter's total is passed.</summary>
    public bool ReportGrowth(long bytes) => _lease?.TryReportGrowth(bytes) ?? true;

    public string? CountWrite(long bytes)
    {
        if (Interlocked.Increment(ref _writes) > _options.MaxWritesPerRun)
        {
            return string.Create(CultureInfo.InvariantCulture, $"a run may write at most {_options.MaxWritesPerRun} files");
        }

        if (Interlocked.Add(ref _writeBytes, bytes) > _options.MaxWriteBytesPerRun)
        {
            return string.Create(CultureInfo.InvariantCulture, $"a run may write at most {_options.MaxWriteBytesPerRun} bytes");
        }

        return null;
    }

    public string? CountFetch() =>
        Interlocked.Increment(ref _fetches) > _options.MaxFetchesPerRun
            ? string.Create(CultureInfo.InvariantCulture, $"a run may make at most {_options.MaxFetchesPerRun} requests")
            : null;

    /// <summary>Counts log characters. Returns 0 to write the line, 1 for the first line over the quota (write a notice instead), 2 to drop it.</summary>
    public int CountLog(int chars)
    {
        long total = Interlocked.Add(ref _logChars, chars);
        if (total <= _options.MaxLogChars)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_logNoticed)
            {
                return 2;
            }

            _logNoticed = true;
            return 1;
        }
    }
}
