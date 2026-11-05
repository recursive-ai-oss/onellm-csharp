using System.Runtime.CompilerServices;
using RecursiveAI.OneLite;
using OneLlm.Instances;

namespace OneLlm.OneLite;

[OneLiteRow]
internal sealed class SummaryRow
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? State { get; set; }
    public string? Model { get; set; }
    public string? CallerInstanceId { get; set; }
    public long ModifiedTicks { get; set; }
}

/// <summary>
/// An <see cref="IInstanceStore"/> on a OneLite database. Each save is one write transaction, so a reader sees the old instance or the
/// new one. Leases are rows with an expiry the holder keeps extending, so a crashed process stops holding its instance after the lease
/// duration, and they work between processes sharing the file.
/// </summary>
public sealed class OneLiteInstanceStore : IInstanceStore
{
    private readonly Database _db;
    private readonly TimeProvider _time;
    private readonly TimeSpan _leaseDuration;

    private OneLiteInstanceStore(Database db, TimeProvider time, TimeSpan leaseDuration)
    {
        _db = db;
        _time = time;
        _leaseDuration = leaseDuration;
    }

    /// <summary>Creates the store over a database, bringing its schema up to date.</summary>
    /// <param name="database">The open database; the caller keeps ownership and disposes it.</param>
    /// <param name="timeProvider">The clock for modified times and lease expiry; the system clock when null.</param>
    /// <param name="leaseDuration">How long a lease lasts without renewal. Default 60 seconds; renewed every third of that while held.</param>
    /// <param name="cancellationToken">Cancels the schema check.</param>
    /// <returns>The store.</returns>
    public static async ValueTask<OneLiteInstanceStore> CreateAsync(Database database, TimeProvider? timeProvider = null, TimeSpan? leaseDuration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        TimeSpan lease = leaseDuration ?? TimeSpan.FromSeconds(60);
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "A lease must last longer than zero.");
        }

        await OneLiteStorage.MigrateAsync(database, cancellationToken).ConfigureAwait(false);
        return new OneLiteInstanceStore(database, timeProvider ?? TimeProvider.System, lease);
    }

    /// <inheritdoc />
    public async ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        string? yaml = await _db.ScalarAsync<string>($"select Yaml from OneLlmInstances where InstanceId = {instanceId}", cancellationToken).ConfigureAwait(false);
        return yaml is null ? null : Instance.Parse(yaml, instanceId);
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        InstanceIds.Validate(instance.InstanceId);
        string yaml = instance.ToYaml();
        long ticks = _time.GetUtcNow().UtcTicks;
        string id = instance.InstanceId, name = instance.Name;
        string? state = instance.State, model = instance.Model, caller = instance.CallerInstanceId, spawner = instance.SpawnerInstanceId;
        await _db.WriteAsync(async tx =>
        {
            await tx.ExecuteAsync($"""
                insert into OneLlmInstances (InstanceId, Name, State, Model, CallerInstanceId, SpawnerInstanceId, ModifiedTicks, Yaml)
                values ({id}, {name}, {state}, {model}, {caller}, {spawner}, {ticks}, {yaml})
                on conflict (InstanceId) do update set Name = excluded.Name, State = excluded.State, Model = excluded.Model,
                    CallerInstanceId = excluded.CallerInstanceId, SpawnerInstanceId = excluded.SpawnerInstanceId,
                    ModifiedTicks = excluded.ModifiedTicks, Yaml = excluded.Yaml
                """, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        // Records are the audit trail and stay, as JSONL files do.
        long n = await _db.ExecuteAsync($"delete from OneLlmInstances where InstanceId = {instanceId}", cancellationToken).ConfigureAwait(false);
        return n > 0;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? state = query?.State, caller = query?.CallerInstanceId;
        long since = query?.ModifiedSince?.UtcTicks ?? long.MinValue;
        // A null filter value matches everything. The name match runs here, ignoring case, so a limit applies after it.
        long sqlLimit = query?.NameContains is null && query?.Limit is int l ? l : -1;
        List<SummaryRow> rows = await _db.QueryAsync<SummaryRow>($"""
            select InstanceId, Name, State, Model, CallerInstanceId, ModifiedTicks from OneLlmInstances
            where ({state} is null or State = {state}) and ({caller} is null or CallerInstanceId = {caller}) and ModifiedTicks >= {since}
            order by ModifiedTicks desc, InstanceId limit {sqlLimit}
            """, cancellationToken).ConfigureAwait(false);
        int count = 0;
        foreach (SummaryRow r in rows)
        {
            if (query?.NameContains is string text && !r.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query?.Limit is int max && count++ >= max)
            {
                yield break;
            }

            yield return new InstanceSummary(r.InstanceId, r.Name, r.State, r.Model, r.CallerInstanceId, new DateTimeOffset(r.ModifiedTicks, TimeSpan.Zero));
        }
    }

    /// <inheritdoc />
    public async ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        string owner = Guid.NewGuid().ToString("N");
        long now = _time.GetUtcNow().UtcTicks;
        long expires = now + _leaseDuration.Ticks;
        // One write transaction: take the row when there is none or it has lapsed. A concurrent taker is serialized by the write lock.
        bool got = await _db.WriteAsync(async tx =>
        {
            long n = await tx.ExecuteAsync($"""
                insert into OneLlmLeases (InstanceId, Owner, ExpiresTicks) values ({instanceId}, {owner}, {expires})
                on conflict (InstanceId) do update set Owner = excluded.Owner, ExpiresTicks = excluded.ExpiresTicks
                where OneLlmLeases.ExpiresTicks < {now}
                """, cancellationToken).ConfigureAwait(false);
            return n > 0;
        }, cancellationToken).ConfigureAwait(false);
        if (!got)
        {
            throw new InstanceLockedException(instanceId);
        }

        return new Lease(this, instanceId, owner);
    }

    private sealed class Lease : IInstanceLease
    {
        private readonly OneLiteInstanceStore _store;
        private readonly string _owner;
        private readonly ITimer _renewal;
        private int _renewing;

        public Lease(OneLiteInstanceStore store, string instanceId, string owner)
        {
            _store = store;
            InstanceId = instanceId;
            _owner = owner;
            TimeSpan every = store._leaseDuration / 3;
            _renewal = store._time.CreateTimer(_ => _ = RenewAsync(), null, every, every);
        }

        public string InstanceId { get; }

        private async Task RenewAsync()
        {
            if (Interlocked.Exchange(ref _renewing, 1) == 1)
            {
                return;
            }

            try
            {
                long expires = (_store._time.GetUtcNow() + _store._leaseDuration).UtcTicks;
                string id = InstanceId, owner = _owner;
                await _store._db.ExecuteAsync($"update OneLlmLeases set ExpiresTicks = {expires} where InstanceId = {id} and Owner = {owner}").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Retried on the next tick; if the database stays away the lease lapses, which is the safe side.
            }
            finally
            {
                Interlocked.Exchange(ref _renewing, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _renewal.DisposeAsync().ConfigureAwait(false);
            string id = InstanceId, owner = _owner;
            await _store._db.ExecuteAsync($"delete from OneLlmLeases where InstanceId = {id} and Owner = {owner}").ConfigureAwait(false);
        }
    }
}
