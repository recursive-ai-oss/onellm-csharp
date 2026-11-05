namespace OneLlm.Instances;

/// <summary>Persists instances. Saves are atomic per instance: a reader sees the old instance or the new one, never a mix.</summary>
public interface IInstanceStore
{
    /// <summary>Loads an instance.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The instance, or null when there is none with that id.</returns>
    /// <exception cref="BadDefinitionException">The stored file is malformed; carries file, line and column.</exception>
    ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Saves an instance, replacing the stored one atomically.</summary>
    /// <param name="instance">The instance; its <see cref="Instance.InstanceId"/> names it.</param>
    /// <param name="cancellationToken">Cancels the write before it starts.</param>
    /// <exception cref="ArgumentException">The id is empty, too long or has characters other than letters, digits, <c>-</c> and <c>_</c>.</exception>
    ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default);

    /// <summary>Deletes an instance and what belongs to it.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>True when an instance was deleted.</returns>
    ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Lists instances without loading their messages.</summary>
    /// <param name="query">What to match; null lists everything.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Summaries, newest first.</returns>
    IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Takes the right to run an instance. One runner at a time may hold it, across processes for stores that share storage.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="cancellationToken">Cancels the wait (there is none today: a held lease fails at once).</param>
    /// <returns>A lease; dispose it when the run ends. A crash releases it.</returns>
    /// <exception cref="InstanceLockedException">Another runner holds the instance.</exception>
    ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>The right to run one instance. Disposing it lets another runner in.</summary>
public interface IInstanceLease : IAsyncDisposable
{
    /// <summary>The instance this lease is for.</summary>
    string InstanceId { get; }
}

/// <summary>A listing entry: the header of an instance without its conversation.</summary>
/// <param name="InstanceId">The instance id.</param>
/// <param name="Name">The definition's name, or empty.</param>
/// <param name="State">The state string, or null.</param>
/// <param name="Model">The model, or null.</param>
/// <param name="CallerInstanceId">The caller, or null.</param>
/// <param name="Modified">When the instance was last saved.</param>
public sealed record InstanceSummary(string InstanceId, string Name, string? State, string? Model, string? CallerInstanceId, DateTimeOffset Modified);

/// <summary>Filters for <see cref="IInstanceStore.ListAsync"/>. Every set field must match.</summary>
public sealed class InstanceQuery
{
    /// <summary>Matches names that contain this text, ignoring case.</summary>
    public string? NameContains { get; init; }

    /// <summary>Matches this exact state.</summary>
    public string? State { get; init; }

    /// <summary>Matches instances messaged last by this instance.</summary>
    public string? CallerInstanceId { get; init; }

    /// <summary>Matches instances saved at or after this time.</summary>
    public DateTimeOffset? ModifiedSince { get; init; }

    /// <summary>The most entries to return; null for all.</summary>
    public int? Limit { get; init; }

    internal bool Matches(InstanceSummary s) =>
        (NameContains is null || s.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
        && (State is null || s.State == State)
        && (CallerInstanceId is null || s.CallerInstanceId == CallerInstanceId)
        && (ModifiedSince is null || s.Modified >= ModifiedSince);
}

/// <summary>Another runner already holds the instance.</summary>
public sealed class InstanceLockedException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="instanceId">The instance that is locked.</param>
    public InstanceLockedException(string instanceId)
        : base($"Instance '{instanceId}' is already being run by another runner.")
    {
        InstanceId = instanceId;
    }

    /// <summary>The instance that is locked.</summary>
    public string InstanceId { get; }
}

/// <summary>Checks instance ids, which become file names.</summary>
internal static class InstanceIds
{
    public static void Validate(string id)
    {
        if (id is null || id.Length is 0 or > 128)
        {
            throw new ArgumentException("An instance id must be 1 to 128 characters.", nameof(id));
        }

        foreach (char c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                throw new ArgumentException("An instance id may only contain letters, digits, '-' and '_'.", nameof(id));
            }
        }
    }
}
