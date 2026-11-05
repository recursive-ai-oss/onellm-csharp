using System.Globalization;
using RecursiveAI.OneLite;
using OneLlm.Records;

namespace OneLlm.OneLite;

[OneLiteRow]
internal sealed class RecordJsonRow
{
    public string Json { get; set; } = "";
}

[OneLiteRow]
internal sealed class CostRow
{
    public string Cost { get; set; } = "";
}

/// <summary>
/// An <see cref="IRecordSink"/> on a OneLite database: each record is one appended row. Also reads records back and sums cost.
/// Wrap it in <see cref="RecordSinks"/> so a database problem never fails a run.
/// </summary>
public sealed class OneLiteRecordSink : IRecordSink
{
    private readonly Database _db;

    private OneLiteRecordSink(Database db) => _db = db;

    /// <summary>Creates the sink over a database, bringing its schema up to date.</summary>
    /// <param name="database">The open database; the caller keeps ownership and disposes it. It may be the one an instance store uses.</param>
    /// <param name="cancellationToken">Cancels the schema check.</param>
    /// <returns>The sink.</returns>
    public static async ValueTask<OneLiteRecordSink> CreateAsync(Database database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await OneLiteStorage.MigrateAsync(database, cancellationToken).ConfigureAwait(false);
        return new OneLiteRecordSink(database);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        decimal? cost = record switch
        {
            InferenceRecord i => i.Usage?.Cost,
            ToolCallRecord t => t.Call.Cost,
            _ => null,
        };
        // Decimal text keeps every digit; SQLite's own numbers are binary floats.
        string? costText = cost?.ToString(CultureInfo.InvariantCulture);
        string json = JsonlRecordSink.ToJsonLine(record);
        string id = record.InstanceId, kind = record.Kind;
        string? messageId = record.MessageId;
        long ticks = record.Time.UtcTicks;
        await _db.ExecuteAsync($"insert into OneLlmRecords (InstanceId, Kind, TimeTicks, MessageId, Cost, Json) values ({id}, {kind}, {ticks}, {messageId}, {costText}, {json})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an instance's records in the order they were written.</summary>
    /// <param name="instanceId">The instance id; empty for runtime-level records.</param>
    /// <param name="kind">Only this kind, or null for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in the same shape <see cref="RecordReader"/> returns for a JSONL file.</returns>
    public async Task<List<RecordEntry>> ReadAsync(string instanceId, string? kind = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        List<RecordJsonRow> rows = await _db.QueryAsync<RecordJsonRow>(
            $"select Json from OneLlmRecords where InstanceId = {instanceId} and ({kind} is null or Kind = {kind}) order by Id", cancellationToken).ConfigureAwait(false);
        return rows.ConvertAll(r => RecordReader.ParseLine(r.Json));
    }

    /// <summary>Sums what an instance spent: its inference and tool-call records.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="includeSubtree">True to add every instance below it (those it called or spawned, at any depth).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The total as a <c>decimal</c>; zero when nothing was recorded.</returns>
    public async Task<decimal> CostAsync(string instanceId, bool includeSubtree = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        List<CostRow> costs;
        if (includeSubtree)
        {
            // A recursive query walks callers and spawners down from the instance; UNION also stops a cycle.
            costs = await _db.QueryAsync<CostRow>($"""
                with recursive tree (Id) as (
                    select {instanceId}
                    union
                    select i.InstanceId from OneLlmInstances i join tree t on i.CallerInstanceId = t.Id or i.SpawnerInstanceId = t.Id
                )
                select Cost from OneLlmRecords where Cost is not null and InstanceId in (select Id from tree)
                """, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            costs = await _db.QueryAsync<CostRow>($"select Cost from OneLlmRecords where Cost is not null and InstanceId = {instanceId}", cancellationToken).ConfigureAwait(false);
        }

        decimal total = 0m;
        foreach (CostRow c in costs)
        {
            total += decimal.Parse(c.Cost, CultureInfo.InvariantCulture);
        }

        return total;
    }
}
