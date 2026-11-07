using System.Security.Cryptography;
using OneLlm.Tools;

namespace OneLlm.JavaScript;

/// <summary>
/// Reconciles JavaScript tools directly inside host-selected directories. Polling avoids dropped filesystem notifications and partial editor renames.
/// Later directories win duplicate names. Imports and YAML changes reload tools too. Invalid edits retain the last good registration.
/// </summary>
public sealed class JsToolWatcher : IAsyncDisposable
{
    private readonly ToolRegistry _registry;
    private readonly string[] _directories;
    private readonly JsSandboxOptions? _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Dictionary<string, ITool> _owned = new(StringComparer.Ordinal);
    private int _disposed;
    private string _fingerprint = "";

    /// <summary>Starts watching, including an initial asynchronous scan. Dispose asynchronously to stop and remove registrations still owned by this watcher.</summary>
    /// <param name="registry">The registry to update only through its public API.</param>
    /// <param name="directories">Folders in low-to-high precedence order. They must exist; subdirectories are not scanned.</param>
    /// <param name="options">Tool options, or null for sandbox defaults.</param>
    /// <param name="timeProvider">Polling and loading clock; null uses the system clock.</param>
    /// <param name="pollInterval">Delay between reconciliations; null means 250 milliseconds. Must be positive.</param>
    public JsToolWatcher(ToolRegistry registry, IEnumerable<string> directories, JsSandboxOptions? options = null, TimeProvider? timeProvider = null, TimeSpan? pollInterval = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(directories);
        _directories = directories.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        _worker = WatchAsync(interval);
    }

    /// <summary>Reports a bad file or folder without stopping other registrations. Subscriber failures are ignored. Paths identify host-selected files.</summary>
    public event Action<string, Exception>? Error;

    private async Task WatchAsync(TimeSpan interval)
    {
        // Yield so hosts can subscribe before the initial scan.
        await Task.Yield();
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            do
            {
                Reconcile();
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Report(string path, Exception error)
    {
        try { Error?.Invoke(path, error); }
        catch (Exception) { }
    }

    private void Reconcile()
    {
        var files = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string directory in _directories)
        {
            try
            {
                foreach (string path in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
                {
                    if (!(path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".mjs", StringComparison.Ordinal) || path.EndsWith(".tool.yaml", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
                    var info = new FileInfo(path);
                    if (info.Length > (_options?.MaxFileBytes ?? 1024 * 1024))
                    {
                        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        hash.AppendData(File.ReadAllBytes(path));
                    }
                    if (path.EndsWith(".js", StringComparison.Ordinal))
                    {
                        files.Add(path);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report(directory, e);
                return; // An incomplete enumeration is not proof of deletion.
            }
        }

        string fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        if (fingerprint == _fingerprint)
        {
            return;
        }

        var next = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (string path in files)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (new FileInfo(path).Length > (_options?.MaxFileBytes ?? 1024 * 1024))
                {
                    throw new BadDefinitionException("tool exceeds MaxFileBytes", Path.GetFileName(path));
                }

                ITool tool = JsTools.Load(path, _options, _time);
                next[path] = tool;
            }
            catch (Exception e) when (e is BadDefinitionException or IOException or UnauthorizedAccessException)
            {
                Report(path, e);
                if (_owned.TryGetValue(path, out ITool? previous))
                {
                    next[path] = previous;
                }
            }
        }

        foreach ((string path, ITool old) in _owned)
        {
            if (!next.TryGetValue(path, out ITool? replacement) || replacement.Definition.Name != old.Definition.Name)
            {
                RemoveOwned(old);
            }
        }

        _owned.Clear();
        foreach ((string path, ITool tool) in next)
        {
            _owned[path] = tool;
            _registry.Add(tool);
        }

        _fingerprint = fingerprint;
    }

    private void RemoveOwned(ITool tool)
    {
        if (_registry.TryGet(tool.Definition.Name, out ITool current) && ReferenceEquals(current, tool))
        {
            _registry.Remove(tool.Definition.Name);
        }
    }

    /// <summary>Stops polling, waits for a load already running, and unregisters only tools still owned by this watcher. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        foreach (ITool tool in _owned.Values)
        {
            RemoveOwned(tool);
        }

        _stop.Dispose();
    }
}
