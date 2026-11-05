using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using OneLlm.Instances;

namespace OneLlm.EntityFrameworkCore;

/// <summary>
/// An <see cref="IInstanceStore"/> over your own DbContext. Each call uses a short-lived context from the factory you give, so the
/// store is safe to share between threads and runners. The context's model must include <see cref="OneLlmModelBuilderExtensions.ApplyOneLlm"/>.
/// </summary>
/// <remarks>
/// Leases live in a table with an expiry the holder keeps extending, so a crashed process stops holding its instance after the lease
/// duration. Saves are one row each; the row's version is a concurrency token and a save that loses a race is retried against the new row.
/// Entity Framework Core is not trim or AOT compatible.
/// </remarks>
public sealed class EfInstanceStore : IInstanceStore
{
    private const int SaveAttempts = 5;
    private readonly Func<DbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly TimeSpan _leaseDuration;

    /// <summary>Creates the store.</summary>
    /// <param name="contextFactory">Makes a new context per call; the store disposes it. For a pooled factory pass <c>() =&gt; factory.CreateDbContext()</c>.</param>
    /// <param name="timeProvider">The clock for modified times and lease expiry; the system clock when null.</param>
    /// <param name="leaseDuration">How long a lease lasts without renewal. Default 60 seconds; it is renewed every third of that while held.</param>
    public EfInstanceStore(Func<DbContext> contextFactory, TimeProvider? timeProvider = null, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contexts = contextFactory;
        _time = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(60);
        if (_leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "A lease must last longer than zero.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<Instance?> LoadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        using DbContext ctx = _contexts();
        string? yaml = await ctx.Set<OneLlmInstance>().AsNoTracking()
            .Where(i => i.InstanceId == instanceId).Select(i => i.Yaml).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return yaml is null ? null : Instance.Parse(yaml, instanceId);
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(Instance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        InstanceIds.Validate(instance.InstanceId);
        string yaml = instance.ToYaml();
        DateTimeOffset now = _time.GetUtcNow();
        for (int attempt = 1; ; attempt++)
        {
            using DbContext ctx = _contexts();
            try
            {
                OneLlmInstance? row = await ctx.Set<OneLlmInstance>().FirstOrDefaultAsync(i => i.InstanceId == instance.InstanceId, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    row = new OneLlmInstance { InstanceId = instance.InstanceId };
                    ctx.Set<OneLlmInstance>().Add(row);
                }

                row.Name = instance.Name;
                row.State = instance.State;
                row.Model = instance.Model;
                row.CallerInstanceId = instance.CallerInstanceId;
                row.SpawnerInstanceId = instance.SpawnerInstanceId;
                row.Modified = now;
                row.Yaml = yaml;
                row.Version++;
                await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < SaveAttempts)
            {
                // Another writer inserted or updated the row between our read and write (DbUpdateConcurrencyException is a
                // DbUpdateException). Read the new row and apply this save on top: the last save wins, as in the file store.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        using DbContext ctx = _contexts();
        // Records are the audit trail and stay, as JSONL files do.
        int n = await ctx.Set<OneLlmInstance>().Where(i => i.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return n > 0;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstanceSummary> ListAsync(InstanceQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<InstanceSummary> found;
        using (DbContext ctx = _contexts())
        {
            IQueryable<OneLlmInstance> q = ctx.Set<OneLlmInstance>().AsNoTracking();
            if (query?.State is string state)
            {
                q = q.Where(i => i.State == state);
            }

            if (query?.CallerInstanceId is string caller)
            {
                q = q.Where(i => i.CallerInstanceId == caller);
            }

            if (query?.ModifiedSince is DateTimeOffset since)
            {
                q = q.Where(i => i.Modified >= since);
            }

            q = q.OrderByDescending(i => i.Modified).ThenBy(i => i.InstanceId);
            // The name match ignores case the same way on every provider, so it runs here; a limit then applies after it.
            if (query?.NameContains is null && query?.Limit is int limit)
            {
                q = q.Take(limit);
            }

            found = await q.Select(i => new InstanceSummary(i.InstanceId, i.Name, i.State, i.Model, i.CallerInstanceId, i.Modified))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        int count = 0;
        foreach (InstanceSummary s in found)
        {
            if (query?.NameContains is string text && !s.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query?.Limit is int max && count++ >= max)
            {
                yield break;
            }

            yield return s;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IInstanceLease> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        string owner = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset expires = now + _leaseDuration;
        using (DbContext ctx = _contexts())
        {
            // Take over a lapsed lease in one statement, so two takers cannot both win.
            int taken = await ctx.Set<OneLlmLease>().Where(l => l.InstanceId == instanceId && l.ExpiresAt < now)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, owner).SetProperty(l => l.ExpiresAt, expires), cancellationToken).ConfigureAwait(false);
            if (taken == 0)
            {
                try
                {
                    ctx.Set<OneLlmLease>().Add(new OneLlmLease { InstanceId = instanceId, Owner = owner, ExpiresAt = expires });
                    await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // The primary key is taken: someone holds a live lease.
                    throw new InstanceLockedException(instanceId);
                }
            }
        }

        return new Lease(this, instanceId, owner);
    }

    private sealed class Lease : IInstanceLease
    {
        private readonly EfInstanceStore _store;
        private readonly string _owner;
        private readonly ITimer _renewal;
        private int _renewing;

        public Lease(EfInstanceStore store, string instanceId, string owner)
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
            // Skip a tick when the previous renewal is still running.
            if (Interlocked.Exchange(ref _renewing, 1) == 1)
            {
                return;
            }

            try
            {
                using DbContext ctx = _store._contexts();
                DateTimeOffset expires = _store._time.GetUtcNow() + _store._leaseDuration;
                await ctx.Set<OneLlmLease>().Where(l => l.InstanceId == InstanceId && l.Owner == _owner)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, expires)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed renewal is retried on the next tick; if the database stays away the lease lapses, which is the safe side.
            }
            finally
            {
                Interlocked.Exchange(ref _renewing, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _renewal.DisposeAsync().ConfigureAwait(false);
            using DbContext ctx = _store._contexts();
            await ctx.Set<OneLlmLease>().Where(l => l.InstanceId == InstanceId && l.Owner == _owner).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }
}
