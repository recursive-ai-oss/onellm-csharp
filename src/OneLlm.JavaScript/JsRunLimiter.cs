namespace OneLlm.JavaScript;

/// <summary>
/// Bounds sandboxed runs together. Each run has its own limits (<see cref="JsSandboxOptions"/>), but a model can start many runs in one turn, so
/// a host also needs a bound on all of them at once: how many may run at the same moment, and how much memory they may hold between them.
/// </summary>
/// <remarks>
/// <para>Share one limiter between the sandboxes of a runtime (set <see cref="JsSandboxOptions.Limiter"/>); sandboxes that set none use
/// <see cref="Shared"/>, one limiter for the process.</para>
/// <para><b>Concurrency.</b> A run waits up to <see cref="MaxWait"/> for a free slot and then fails with <see cref="JsSandboxFailure.Busy"/>; it never
/// waits for ever. A run the host abandoned (it did not stop at its time limit) keeps its slot until its thread really ends, so stuck scripts cannot
/// pile up threads past the limit.</para>
/// <para><b>Bytes.</b> Every run reports two things: the bytes of host results it holds (<c>ctx.readFile</c> and <c>ctx.fetch</c> results) and its
/// live memory growth, measured from a collected heap (see <see cref="JsSandboxOptions.MaxHostAllocatedBytes"/>). Growth is measured for the whole
/// process, so runs that overlap see each other's growth; the limiter therefore counts the <i>largest</i> growth of any active run plus the sum of
/// the host results, and fails the run that pushes the total past <see cref="MaxTotalBytes"/> with <see cref="JsSandboxFailure.Busy"/>.</para>
/// </remarks>
public sealed class JsRunLimiter : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<Lease> _leases = [];

    /// <summary>Creates a limiter.</summary>
    /// <param name="maxConcurrentRuns">The most runs that may execute at once. Default 8. Must be at least 1.</param>
    /// <param name="maxTotalBytes">The most bytes all active runs together may hold. Default 2 GiB. Must be positive.</param>
    /// <param name="maxWait">How long a run waits for a free slot before it fails as busy. Default 10 seconds; zero fails at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public JsRunLimiter(int maxConcurrentRuns = 0, long maxTotalBytes = 2L * 1024 * 1024 * 1024, TimeSpan? maxWait = null)
    {
        int slots = maxConcurrentRuns == 0 ? 8 : maxConcurrentRuns;
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTotalBytes, 0);
        MaxConcurrentRuns = slots;
        MaxTotalBytes = maxTotalBytes;
        MaxWait = maxWait ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWait, TimeSpan.Zero);
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>Releases the limiter's wait handle. Do not dispose a limiter while runs still use it; <see cref="Shared"/> is never disposed.</summary>
    public void Dispose() => _slots.Dispose();

    /// <summary>The limiter sandboxes use when their options name none: one for the whole process, with the defaults.</summary>
    public static JsRunLimiter Shared { get; } = new();

    /// <summary>The most runs that may execute at once.</summary>
    public int MaxConcurrentRuns { get; }

    /// <summary>The most bytes all active runs together may hold.</summary>
    public long MaxTotalBytes { get; }

    /// <summary>How long a run waits for a free slot.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>How many runs hold a slot now, including abandoned ones whose thread has not ended.</summary>
    public int ActiveRuns
    {
        get
        {
            lock (_gate)
            {
                return _leases.Count;
            }
        }
    }

    /// <summary>The bytes the active runs hold now, as the limiter counts them.</summary>
    public long ChargedBytes
    {
        get
        {
            lock (_gate)
            {
                return Usage();
            }
        }
    }

    // Caller holds _gate.
    private long Usage()
    {
        long host = 0;
        long growth = 0;
        foreach (Lease l in _leases)
        {
            host += l.HostBytes;
            growth = Math.Max(growth, l.Growth);
        }

        return host + growth;
    }

    internal async Task<Lease> EnterAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        if (!_slots.Wait(0, CancellationToken.None))
        {
            using var wait = new CancellationTokenSource(MaxWait, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token);
            try
            {
                await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new JsSandboxException(JsSandboxFailure.Busy, $"Too many scripts are running ({MaxConcurrentRuns} at once); this one waited {MaxWait.TotalSeconds:0.###} s for a free slot. Try again later.");
            }
        }

        var lease = new Lease(this);
        lock (_gate)
        {
            _leases.Add(lease);
        }

        return lease;
    }

    /// <summary>One run's hold on the limiter. Disposed when the run's thread has really ended.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly JsRunLimiter _owner;
        private bool _released;

        internal Lease(JsRunLimiter owner) => _owner = owner;

        internal long HostBytes { get; private set; }

        internal long Growth { get; private set; }

        /// <summary>Adds host-result bytes. Returns false when the limiter's total is passed.</summary>
        public bool TryChargeHost(long bytes)
        {
            lock (_owner._gate)
            {
                HostBytes += bytes;
                return _owner.Usage() <= _owner.MaxTotalBytes;
            }
        }

        /// <summary>Records the run's live growth. Returns false when the limiter's total is passed.</summary>
        public bool TryReportGrowth(long bytes)
        {
            lock (_owner._gate)
            {
                Growth = bytes;
                return _owner.Usage() <= _owner.MaxTotalBytes;
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                _owner._leases.Remove(this);
            }

            _owner._slots.Release();
        }
    }
}
