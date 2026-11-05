using System.Runtime.CompilerServices;
using System.Text;
using OneYaml;

namespace OneLlm.Instances;

/// <summary>
/// Keeps each instance as <c>&lt;directory&gt;/&lt;instanceId&gt;.yaml</c>, with its assets under <c>&lt;instanceId&gt;.assets/</c>. Saves write a temporary
/// file, flush it and rename it over the instance, so a crash leaves the previous version. A <c>&lt;instanceId&gt;.lock</c> file held open
/// with no sharing keeps a second runner out, in this process or another; the operating system drops it if the holder dies.
/// </summary>
public sealed class FileInstanceStore : IInstanceStore
{
    private readonly string _directory;

    /// <summary>Creates a store over a directory, which is created on the first save.</summary>
    /// <param name="directory">The directory that holds the instance files.</param>
    public FileInstanceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>The directory that holds the instance files.</summary>
    public string Directory => _directory;

    /// <summary>The path of the file for an instance.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <returns>The path of <c>&lt;instanceId&gt;.yaml</c>.</returns>
    public string PathOf(string instanceId)
    {
        InstanceIds.Validate(instanceId);
        return Path.Combine(_directory, instanceId + ".yaml");
    }

    /// <summary>The directory an instance's assets live in; it may not exist yet.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <returns>The path of <c>&lt;instanceId&gt;.assets</c>.</returns>
    public string AssetDirectoryOf(string instanceId)
    {
        InstanceIds.Validate(instanceId);
        return Path.Combine(_directory, instanceId + ".assets");
    }

    /// <inheritdoc />
    public async ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        string path = PathOf(instanceId);
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Instance.Parse(text, path);
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathOf(instance.InstanceId);
        // Serializing is CPU work and the write is one flush; both are short, so this stays synchronous under the async signature.
        AtomicFile.WriteAllText(path, instance.ToYaml());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        string path = PathOf(instanceId);
        bool existed = File.Exists(path);
        File.Delete(path);
        string assets = AssetDirectoryOf(instanceId);
        if (System.IO.Directory.Exists(assets))
        {
            System.IO.Directory.Delete(assets, recursive: true);
        }

        return ValueTask.FromResult(existed);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            yield break;
        }

        var found = new List<InstanceSummary>();
        foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "*.yaml"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (name[0] == '.')
            {
                continue;
            }

            InstanceSummary? s = ReadSummary(file);
            if (s is not null && (query is null || query.Matches(s)))
            {
                found.Add(s);
            }
        }

        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        int count = 0;
        foreach (InstanceSummary s in found)
        {
            if (query?.Limit is int limit && count++ >= limit)
            {
                yield break;
            }

            yield return s;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        System.IO.Directory.CreateDirectory(_directory);
        string lockPath = Path.Combine(_directory, instanceId + ".lock");
        try
        {
            // FileShare.None makes a second open fail while this one lives, in any process; DeleteOnClose tidies up.
            var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return ValueTask.FromResult<IInstanceLease>(new FileLease(instanceId, fs));
        }
        catch (IOException)
        {
            throw new InstanceLockedException(instanceId);
        }
    }

    // Reads only the top-level scalar keys before the large sections (inputs, systemPrompt, references, messages) begin.
    private static InstanceSummary? ReadSummary(string file)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        string? name = null, state = null, model = null, caller = null;
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line[0] is ' ' or '-' or '#')
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                string key = line[..colon];
                if (key is "inputs" or "systemPrompt" or "references" or "messages" or "initialPrompts")
                {
                    break;
                }

                string value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "name": name = Scalar(value); break;
                    case "state": state = Scalar(value); break;
                    case "model": model = Scalar(value); break;
                    case "callerInstanceId": caller = Scalar(value); break;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        return new InstanceSummary(id, name ?? "", state, model, caller, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
    }

    private static string? Scalar(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            return YamlSerializer.Deserialize<string>(value, InstanceYamlStrings.Info);
        }
        catch (YamlException)
        {
            return value;
        }
    }

    private sealed class FileLease(string id, FileStream stream) : IInstanceLease
    {
        public string InstanceId => id;

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keeps instances in memory. Instances are copied in and out through their YAML text, so callers never share objects with the store.</summary>
public sealed class MemoryInstanceStore : IInstanceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Text, DateTimeOffset Modified)> _items = [];
    private readonly HashSet<string> _leases = [];
    private readonly TimeProvider _time;

    /// <summary>Creates an empty store.</summary>
    /// <param name="timeProvider">The clock that stamps saves; null uses the system clock.</param>
    public MemoryInstanceStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        lock (_gate)
        {
            return ValueTask.FromResult(_items.TryGetValue(instanceId, out var item) ? Instance.Parse(item.Text, instanceId) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        InstanceIds.Validate(instance.InstanceId);
        string text = instance.ToYaml();
        lock (_gate)
        {
            _items[instance.InstanceId] = (text, _time.GetUtcNow());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        lock (_gate)
        {
            return ValueTask.FromResult(_items.Remove(instanceId));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<InstanceSummary> all = [];
        lock (_gate)
        {
            foreach ((string id, (string text, DateTimeOffset modified)) in _items)
            {
                Instance i = Instance.Parse(text, id);
                all.Add(new InstanceSummary(id, i.Name, i.State, i.Model, i.CallerInstanceId, modified));
            }
        }

        all.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        int count = 0;
        foreach (InstanceSummary s in all)
        {
            if (query is not null && !query.Matches(s))
            {
                continue;
            }

            if (query?.Limit is int limit && count++ >= limit)
            {
                yield break;
            }

            yield return s;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        lock (_gate)
        {
            if (!_leases.Add(instanceId))
            {
                throw new InstanceLockedException(instanceId);
            }
        }

        return ValueTask.FromResult<IInstanceLease>(new Lease(this, instanceId));
    }

    private sealed class Lease(MemoryInstanceStore store, string id) : IInstanceLease
    {
        public string InstanceId => id;

        public ValueTask DisposeAsync()
        {
            lock (store._gate)
            {
                store._leases.Remove(id);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Type info for reading single scalar strings.</summary>
internal static class InstanceYamlStrings
{
    public static YamlTypeInfo<string> Info => Definitions.DefinitionsYamlContext.Default.String;
}
