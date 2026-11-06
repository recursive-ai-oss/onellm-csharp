using System.Text;
using RecursiveAI.OneEcma;

namespace OneLlm.JavaScript;

/// <summary>
/// The engine's window on the world for one run. Time comes from the runtime's <see cref="TimeProvider"/> (or a fixed instant), randomness is
/// seeded on request, and the only module it can serve is the one the run was given. No other module can be imported.
/// </summary>
internal sealed class SandboxHost : DefaultHost
{
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly bool _deterministic;
    private readonly double _fixedMs;
    private readonly HostModule? _module;
    private readonly OutputBuffer _output;
    private readonly long _allocLimit;
    private readonly long _checkEvery;
    private readonly RunScope _scope;
    private readonly long _baseline;
    private readonly long _baselineHeap;
    private long _lastGcIndex = -1;
    private long _churn;
    private long _allocLast = GC.GetAllocatedBytesForCurrentThread();
    private int _allocThread = Environment.CurrentManagedThreadId;

    public SandboxHost(TimeProvider time, JsSandboxOptions options, HostModule? module, OutputBuffer output, RunScope scope)
        : base(options.Deterministic ? options.RandomSeed : null)
    {
        _time = time;
        _start = time.GetTimestamp();
        _deterministic = options.Deterministic;
        _fixedMs = options.FixedTime.ToUnixTimeMilliseconds();
        _module = module;
        _output = output;
        _scope = scope;
        _allocLimit = options.MaxHostAllocatedBytes;
        _checkEvery = Math.Max(16L * 1024 * 1024, _allocLimit / 16);
        _baseline = _allocLimit > 0 ? GC.GetTotalMemory(false) : 0;
        _baselineHeap = _allocLimit > 0 ? GC.GetGCMemoryInfo().HeapSizeBytes : 0;
    }

    public override double CurrentTimeMs() => _deterministic ? _fixedMs : _time.GetUtcNow().ToUnixTimeMilliseconds();

    // The engine reads this clock every so many steps for its wall-clock limit, which makes it the one place the host runs on the engine's
    // thread while a script computes. The memory backstop piggybacks on it, and so does ending a run the host has closed: once the run is closed
    // the clock reads as far past any limit, so a script the host gave up on stops at its next check instead of running on.
    // The wall-clock limit follows the injected clock: a test that controls time controls the limit.
    public override double MonotonicMs()
    {
        if (_scope.IsClosed)
        {
            return double.MaxValue / 4;
        }

        CheckAllocation();
        return _time.GetElapsedTime(_start).TotalMilliseconds;
    }

    // The engine's own budget counts the objects it creates but not the backing storage of arrays, so a script can still make .NET allocate a lot.
    // The backstop counts what .NET allocated on the running thread (churn). Churn alone is not memory: a loop that makes short-lived strings
    // allocates gigabytes and holds nothing. So each time the churn passes a sixteenth of the limit the heap size at the last collection is compared
    // with the heap size when the run started (a read, not a collection), and only that growth is held against the limit and reported to the
    // limiter. Garbage the collector has not reclaimed yet could make the estimate too high, so a growth over the limit is confirmed after a full
    // collection before the run is ended. If the run resumes on another thread the churn count carries on from there.
    private void CheckAllocation()
    {
        if (_allocLimit <= 0 || _scope.Tripped != TripReason.None)
        {
            return;
        }

        long now = GC.GetAllocatedBytesForCurrentThread();
        if (Environment.CurrentManagedThreadId == _allocThread)
        {
            _churn += now - _allocLast;
        }

        _allocThread = Environment.CurrentManagedThreadId;
        _allocLast = now;
        if (_churn < _checkEvery)
        {
            return;
        }

        _churn = 0;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.Index == _lastGcIndex)
        {
            // No collection since the last look: the estimate has not moved.
            return;
        }

        _lastGcIndex = info.Index;
        long growth = Math.Max(0, info.HeapSizeBytes - _baselineHeap);
        if (growth > _allocLimit)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            growth = Math.Max(0, GC.GetTotalMemory(false) - _baseline);
        }

        if (!_scope.ReportGrowth(growth))
        {
            _scope.Trip(TripReason.Budget);
        }
        else if (growth > _allocLimit)
        {
            _scope.Trip(TripReason.Memory);
        }
    }

    public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes) =>
        _module is not null && referrerId is null && string.Equals(specifier, _module.Id, StringComparison.Ordinal) ? _module : null;

    public override void Print(string text) => _output.AddLine(text);
}

/// <summary>Collects console output, keeping the head and the tail when there is more than the cap.</summary>
internal sealed class OutputBuffer
{
    private readonly int _half;
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private long _omitted;
    private bool _headFull;

    public OutputBuffer(int maxChars) => _half = Math.Max(1, maxChars / 2);

    public void AddLine(string line)
    {
        string text = line + "\n";
        if (!_headFull)
        {
            int room = _half - _head.Length;
            if (text.Length <= room)
            {
                _head.Append(text);
                return;
            }

            _head.Append(text, 0, room);
            text = text[room..];
            _headFull = true;
        }

        _tail.Append(text);
        if (_tail.Length > _half)
        {
            int cut = _tail.Length - _half;
            _tail.Remove(0, cut);
            _omitted += cut;
        }
    }

    public override string ToString()
    {
        string head = _head.ToString();
        if (_tail.Length == 0 && _omitted == 0)
        {
            return head.TrimEnd('\n');
        }

        string note = _omitted > 0 ? $"\n[... {_omitted} characters of output omitted ...]\n" : "";
        return (head + note + _tail).TrimEnd('\n');
    }
}
