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
    private readonly List<TaskCompletionSource> _waiters = [];
    private readonly object _gate = new();
    private readonly List<Lease> _leases = [];
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);

    /// <summary>Creates a limiter.</summary>
    /// <param name="maxConcurrentRuns">The most runs that may execute at once. Default 8. Must be at least 1.</param>
    /// <param name="maxTotalBytes">The most bytes all active runs together may hold. Default 2 GiB. Must be positive.</param>
    /// <param name="maxWait">How long a run waits for a free slot before it fails as busy. Default 10 seconds; zero fails at once.</param>
    /// <param name="reservedForInterceptors">Slots that only hooks and decider logic may use, so a flood of tool and <c>run_javascript</c> calls cannot make interceptors fail closed. Default 2, or one less than the run limit when that is smaller; must be less than the run limit.</param>
    /// <param name="maxRunsPerCaller">The most slots one caller (an instance id) may hold at once with tool and <c>run_javascript</c> runs. Abandoned runs count until their thread ends, so a caller whose scripts keep hanging is refused until they have. Default 3.</param>
    /// <param name="maxInterceptorsPerOwner">The most slots one owner may hold with hook calls, or with calls of one decider's logic. Default 2. Decider logic as a whole may use at most half the slots.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public JsRunLimiter(int maxConcurrentRuns = 0, long maxTotalBytes = 2L * 1024 * 1024 * 1024, TimeSpan? maxWait = null, int reservedForInterceptors = -1, int maxRunsPerCaller = 3, int maxInterceptorsPerOwner = 2)
    {
        int slots = maxConcurrentRuns == 0 ? 8 : maxConcurrentRuns;
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTotalBytes, 0);
        MaxConcurrentRuns = slots;
        MaxTotalBytes = maxTotalBytes;
        MaxWait = maxWait ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWait, TimeSpan.Zero);
        if (reservedForInterceptors < 0)
        {
            reservedForInterceptors = Math.Min(2, slots - 1);
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(reservedForInterceptors, slots);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRunsPerCaller, 1);
        ReservedForInterceptors = reservedForInterceptors;
        MaxRunsPerCaller = maxRunsPerCaller;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInterceptorsPerOwner, 1);
        MaxInterceptorsPerOwner = maxInterceptorsPerOwner;
    }

    /// <summary>Kept for compatibility; a limiter holds no handle that needs releasing.</summary>
    public void Dispose()
    {
        // Nothing to release: slots are counted, not held in a handle.
    }

    /// <summary>The limiter sandboxes use when their options name none: one for the whole process, with the defaults.</summary>
    public static JsRunLimiter Shared { get; } = new();

    /// <summary>The most runs that may execute at once.</summary>
    public int MaxConcurrentRuns { get; }

    /// <summary>Slots only hooks and decider logic may use.</summary>
    public int ReservedForInterceptors { get; }

    /// <summary>The most slots one owner may hold with hook calls or one decider's logic.</summary>
    public int MaxInterceptorsPerOwner { get; }

    /// <summary>The most slots one caller may hold with tool and snippet runs.</summary>
    public int MaxRunsPerCaller { get; }

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

        // Host results live in the heap, so the heap growth the runs measured already contains them; counting both would charge the same strings twice.
        // Growth is sampled, so the sum of the host results is the floor between samples.
        return Math.Max(host, growth);
    }

    // Caller holds _gate. Follows the spawner chain the limiter has seen to the top instance, so sub-agents count against the tree that started them.
    private string Root(string instanceId, string? callerId)
    {
        if (callerId is not null && callerId != instanceId)
        {
            _parents[instanceId] = callerId;
        }

        string at = instanceId;
        for (int hops = 0; hops < 64 && _parents.TryGetValue(at, out string? up); hops++)
        {
            at = up;
        }

        if (_parents.Count > 10_000)
        {
            _parents.Clear();
        }

        return at;
    }

    // Caller holds _gate.
    //  - General runs (tools, run_javascript, file loads): at most MaxRunsPerCaller per root, never into the interceptor reserve, and never into the last slot.
    //  - Hook calls: at most MaxInterceptorsPerOwner per root; they may use any free slot.
    //  - Decider logic: at most MaxInterceptorsPerOwner per decider and half the slots in all.
    //  - Loads (running a tool, hook or decider file's top level): at most MaxInterceptorsPerOwner per owner, outside the general cap so a flood of tool
    //    runs does not stop a definition from loading.
    //  General runs, loads and decider logic together always leave one slot for hooks of other owners.
    private bool TryAdd(string owner, RunClass cls, out Lease? lease)
    {
        lease = null;
        if (_leases.Count >= MaxConcurrentRuns)
        {
            return false;
        }

        int general = 0;
        int nonHook = 0;
        int deciders = 0;
        int mine = 0;
        foreach (Lease l in _leases)
        {
            if (l.Class == RunClass.General)
            {
                general++;
            }

            if (l.Class == RunClass.Decider)
            {
                deciders++;
            }

            if (l.Class != RunClass.Hook)
            {
                nonHook++;
            }

            if (l.Class == cls && l.Owner == owner)
            {
                mine++;
            }
        }

        switch (cls)
        {
            case RunClass.General:
                if (general >= MaxConcurrentRuns - ReservedForInterceptors || mine >= MaxRunsPerCaller || nonHook >= MaxConcurrentRuns - Math.Min(1, ReservedForInterceptors))
                {
                    return false;
                }

                break;
            case RunClass.Hook:
                if (mine >= MaxInterceptorsPerOwner)
                {
                    return false;
                }

                break;
            case RunClass.Load:
                if (mine >= MaxInterceptorsPerOwner || nonHook >= MaxConcurrentRuns - Math.Min(1, ReservedForInterceptors))
                {
                    return false;
                }

                break;
            default:
                if (mine >= MaxInterceptorsPerOwner || deciders >= Math.Max(1, MaxConcurrentRuns / 2) || nonHook >= MaxConcurrentRuns - Math.Min(1, ReservedForInterceptors))
                {
                    return false;
                }

                break;
        }

        lease = new Lease(this, owner, cls);
        _leases.Add(lease);
        return true;
    }

    internal async Task<Lease> EnterAsync(TimeProvider time, string instanceId, string? callerId, RunClass cls, CancellationToken cancellationToken)
    {
        using var wait = new CancellationTokenSource(MaxWait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token);
        while (true)
        {
            TaskCompletionSource? signal = null;
            lock (_gate)
            {
                if (TryAdd(cls == RunClass.Decider ? instanceId : Root(instanceId, callerId), cls, out Lease? got))
                {
                    return got!;
                }

                if (MaxWait > TimeSpan.Zero)
                {
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(signal);
                }
            }

            try
            {
                if (signal is null)
                {
                    throw new OperationCanceledException();
                }

                await signal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _waiters.Remove(signal!);
                }

                throw new JsSandboxException(JsSandboxFailure.Busy, $"Too many scripts are running ({MaxConcurrentRuns} at once, at most {MaxRunsPerCaller} per caller{(cls == RunClass.Hook ? "" : ", " + ReservedForInterceptors + " kept for hooks")}); this one waited {MaxWait.TotalSeconds:0.###} s for a free slot. Try again later.");
            }
            catch
            {
                lock (_gate)
                {
                    _waiters.Remove(signal!);
                }

                throw;
            }
        }
    }

    /// <summary>One run's hold on the limiter. Disposed when the run's thread has really ended.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly JsRunLimiter _owner;
        private bool _released;

        internal Lease(JsRunLimiter owner, string callerId, RunClass cls)
        {
            _owner = owner;
            Owner = callerId;
            Class = cls;
        }

        internal string Owner { get; }

        internal RunClass Class { get; }

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

            List<TaskCompletionSource> wake;
            lock (_owner._gate)
            {
                wake = [.. _owner._waiters];
                _owner._waiters.Clear();
            }

            foreach (TaskCompletionSource w in wake)
            {
                w.TrySetResult();
            }
        }
    }
}

/// <summary>Which of the limiter's rules a run falls under.</summary>
internal enum RunClass
{
    /// <summary>Tools, <c>run_javascript</c> and file loads.</summary>
    General = 0,

    /// <summary>A definition's JavaScript hook.</summary>
    Hook = 1,

    /// <summary>Decider logic (<c>prepare</c> / <c>decide</c>).</summary>
    Decider = 2,

    /// <summary>Running a tool, hook or decider file's top level when it loads.</summary>
    Load = 3,
}
