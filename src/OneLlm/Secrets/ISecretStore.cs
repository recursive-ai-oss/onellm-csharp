namespace OneLlm.Secrets;

/// <summary>Host storage keyed by opaque owner ID and name. Implementations must not log values.</summary>
public interface ISecretStore
{
    /// <summary>Reads a value, or null when absent. Authorization belongs to <see cref="SecretScope"/>.</summary>
    /// <param name="ownerId">Opaque owner ID.</param>
    /// <param name="name">Case-sensitive name.</param>
    /// <param name="cancellationToken">Cancels storage I/O.</param>
    /// <returns>The wrapped value, or null.</returns>
    ValueTask<Secret?> GetAsync(string ownerId, string name, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe, nonpersistent store suitable for small hosts and test doubles. Values are never formatted.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Owner, string Name), Secret> _values = new();

    /// <summary>Sets or replaces one value. Owner IDs and names are case-sensitive.</summary>
    public void Set(string ownerId, string name, Secret value)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[(ownerId, name)] = value;
    }

    /// <summary>Removes a value; returns false when absent.</summary>
    public bool Remove(string ownerId, string name) => _values.TryRemove((ownerId, name), out _);

    /// <inheritdoc />
    public ValueTask<Secret?> GetAsync(string ownerId, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryGetValue((ownerId, name), out Secret? value);
        return ValueTask.FromResult(value);
    }
}

/// <summary>A host-selected, immutable read scope. Names resolve in owner order; scripts cannot supply an owner.</summary>
public sealed class SecretScope
{
    private readonly ISecretStore _store;
    private readonly string[] _owners;

    /// <summary>Snapshots readable owner IDs. The first owner with a matching name wins; an empty list grants nothing.</summary>
    public SecretScope(ISecretStore store, IEnumerable<string> readableOwners)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(readableOwners);
        _owners = readableOwners.ToArray();
    }

    /// <summary>Reads only within this scope. Returns null for absent and inaccessible names alike.</summary>
    public async ValueTask<Secret?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string owner in _owners)
        {
            Secret? value = await _store.GetAsync(owner, name, cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
