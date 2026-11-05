using Microsoft.EntityFrameworkCore;
using OneLlm.Records;

namespace OneLlm.EntityFrameworkCore;

/// <summary>
/// An <see cref="IRecordSink"/> over your own DbContext: each record is one appended row. Also reads records back and sums cost.
/// Wrap it in <see cref="RecordSinks"/> so a database outage never fails a run.
/// </summary>
public sealed class EfRecordSink : IRecordSink
{
    private readonly Func<DbContext> _contexts;

    /// <summary>Creates the sink.</summary>
    /// <param name="contextFactory">Makes a new context per call; the sink disposes it.</param>
    public EfRecordSink(Func<DbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contexts = contextFactory;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        using DbContext ctx = _contexts();
        ctx.Set<OneLlmRecord>().Add(new OneLlmRecord
        {
            InstanceId = record.InstanceId,
            Kind = record.Kind,
            Time = record.Time,
            MessageId = record.MessageId,
            Cost = record switch
            {
                InferenceRecord i => i.Usage?.Cost,
                ToolCallRecord t => t.Call.Cost,
                _ => null,
            },
            Json = JsonlRecordSink.ToJsonLine(record),
        });
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an instance's records in the order they were written.</summary>
    /// <param name="instanceId">The instance id; empty for runtime-level records.</param>
    /// <param name="kind">Only this kind, or null for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in the same shape <see cref="RecordReader"/> returns for a JSONL file.</returns>
    public async Task<List<RecordEntry>> ReadAsync(string instanceId, string? kind = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        using DbContext ctx = _contexts();
        IQueryable<OneLlmRecord> q = ctx.Set<OneLlmRecord>().AsNoTracking().Where(r => r.InstanceId == instanceId);
        if (kind is not null)
        {
            q = q.Where(r => r.Kind == kind);
        }

        List<string> lines = await q.OrderBy(r => r.Id).Select(r => r.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return lines.ConvertAll(RecordReader.ParseLine);
    }

    /// <summary>Sums what an instance spent: its inference and tool-call records.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="includeSubtree">True to add every instance below it (those it called or spawned, at any depth).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The total as a <c>decimal</c>; zero when nothing was recorded.</returns>
    public async Task<decimal> CostAsync(string instanceId, bool includeSubtree = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        using DbContext ctx = _contexts();
        var ids = new HashSet<string> { instanceId };
        if (includeSubtree)
        {
            List<string> frontier = [instanceId];
            while (frontier.Count > 0)
            {
                List<string> children = await ctx.Set<OneLlmInstance>().AsNoTracking()
                    .Where(i => (i.CallerInstanceId != null && frontier.Contains(i.CallerInstanceId)) || (i.SpawnerInstanceId != null && frontier.Contains(i.SpawnerInstanceId)))
                    .Select(i => i.InstanceId).ToListAsync(cancellationToken).ConfigureAwait(false);
                frontier = children.Where(ids.Add).ToList();
            }
        }

        List<string> all = [.. ids];
        // Summed here rather than in SQL: some providers cannot aggregate decimals.
        List<decimal?> costs = await ctx.Set<OneLlmRecord>().AsNoTracking()
            .Where(r => all.Contains(r.InstanceId) && r.Cost != null).Select(r => r.Cost).ToListAsync(cancellationToken).ConfigureAwait(false);
        decimal total = 0m;
        foreach (decimal? c in costs)
        {
            total += c.GetValueOrDefault();
        }

        return total;
    }
}
