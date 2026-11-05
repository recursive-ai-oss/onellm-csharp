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
    private long _allocUsed;
    private long _allocLast = GC.GetAllocatedBytesForCurrentThread();
    private int _allocThread = Environment.CurrentManagedThreadId;

    public SandboxHost(TimeProvider time, JsSandboxOptions options, HostModule? module, OutputBuffer output)
        : base(options.Deterministic ? options.RandomSeed : null)
    {
        _time = time;
        _start = time.GetTimestamp();
        _deterministic = options.Deterministic;
        _fixedMs = options.FixedTime.ToUnixTimeMilliseconds();
        _module = module;
        _output = output;
        _allocLimit = options.MaxHostAllocatedBytes;
    }

    /// <summary>True once the .NET allocation backstop tripped; the run is then ended through <see cref="OnMemoryExceeded"/>.</summary>
    public bool MemoryExceeded { get; private set; }

    /// <summary>Called on the engine's thread when the backstop trips. The sandbox uses it to cancel the run.</summary>
    public Action? OnMemoryExceeded { get; set; }

    public override double CurrentTimeMs() => _deterministic ? _fixedMs : _time.GetUtcNow().ToUnixTimeMilliseconds();

    // The wall-clock limit reads this, so it follows the injected clock: a test that controls time controls the limit.
    // The engine reads this clock every so many steps for its wall-clock limit, which makes it the one place the host runs on the engine's
    // thread while a script computes. The allocation backstop piggybacks on it.
    public override double MonotonicMs()
    {
        CheckAllocation();
        return _time.GetElapsedTime(_start).TotalMilliseconds;
    }

    // The engine's own budget counts the objects it creates but not the backing storage of arrays, so a script can still make .NET allocate a lot.
    // This counts what .NET allocated on the running thread since the run began. If the run resumes on another thread the count carries on from there.
    private void CheckAllocation()
    {
        if (_allocLimit <= 0 || MemoryExceeded)
        {
            return;
        }

        long now = GC.GetAllocatedBytesForCurrentThread();
        if (Environment.CurrentManagedThreadId == _allocThread)
        {
            _allocUsed += now - _allocLast;
        }

        _allocThread = Environment.CurrentManagedThreadId;
        _allocLast = now;
        if (_allocUsed > _allocLimit)
        {
            MemoryExceeded = true;
            OnMemoryExceeded?.Invoke();
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
