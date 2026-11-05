# Instance stores and record sinks

An instance store keeps instances between runs; a record sink keeps the audit trail of what a run did. OneLLM ships four stores and
four sinks. Instance state is the truth; records are the trail of how it got there.

| Store | Package | Keeps | Use it when |
|---|---|---|---|
| `FileInstanceStore` | `RecursiveAI.OneLlm` | one `<instanceId>.yaml` per instance in a directory, written by temp file and rename | The default for tools and agents on one machine. The files are readable, diffable and portable. |
| `MemoryInstanceStore` | `RecursiveAI.OneLlm` | the instance text in the process | Tests, one-shot scripts, and runs that must leave nothing behind. |
| `OneLiteInstanceStore` | `RecursiveAI.OneLlm.OneLite` | one row per instance in a OneLite (SQLite-format) file | You want many instances in one file, queries, or several processes sharing a store; trimming and NativeAOT matter. |
| `EfInstanceStore` | `RecursiveAI.OneLlm.EntityFrameworkCore` | one row per instance in your own `DbContext` | Your application already uses Entity Framework Core and a server database. |

| Sink | Package | Keeps |
|---|---|---|
| `JsonlRecordSink` | `RecursiveAI.OneLlm` | one `<instanceId>.jsonl` per instance, appended |
| `EventRecordSink` | `RecursiveAI.OneLlm` | nothing; raises an event per record |
| `OneLiteRecordSink` | `RecursiveAI.OneLlm.OneLite` | one row per record |
| `EfRecordSink` | `RecursiveAI.OneLlm.EntityFrameworkCore` | one row per record in your `DbContext` |

Several sinks can run at once through `RecordSinks`. A sink that throws is skipped for that record and reported to
`RecordSinks.OnError`; it never fails a run.

## What every store does

Every store passes the same conformance suite (`tests/OneLlm.Stores.Tests`):

- `SaveAsync` replaces the stored instance atomically; a reader sees the old instance or the new one. A save that is cancelled before it
  starts leaves the old one.
- `LoadAsync` returns an instance whose file text equals what was saved, unknown fields included; `null` for an unknown id.
- `ListAsync` returns summaries newest first and applies `InstanceQuery` (state, caller, name contains, modified since, limit).
- `AcquireAsync` hands out one lease per instance. A second taker, in the same process or (for file, OneLite and EF) another process or store handle on
  the same storage, gets `InstanceLockedException`. Disposing the lease frees it.
- Ids are 1 to 128 letters, digits, `-` and `_` (`InstanceIds.Validate`).
- `DeleteAsync` removes the instance; the audit trail stays.

## Leases that survive crashes

The file store holds a lock file open. The database stores cannot tie a lease to a process, so a lease is a row with an expiry. The holder renews
it every third of the lease duration (default 60 seconds, a constructor argument); a process that dies stops renewing, and the next taker may take
the row once it has lapsed. The expiry uses the store's `TimeProvider`.

## OneLite

```csharp
using RecursiveAI.OneLite;
using OneLlm.OneLite;

using Database db = await OneLiteStorage.OpenAsync("onellm.db");      // creates the file and the schema
var store = await OneLiteInstanceStore.CreateAsync(db);
var sink  = await OneLiteRecordSink.CreateAsync(db);                  // the same database can hold both
decimal spent = await sink.CostAsync("inst1", includeSubtree: true);
var entries = await sink.ReadAsync("inst1", kind: "toolCall");
```

The schema is versioned with `PRAGMA user_version` and created on first open; later versions append migration scripts, and a file from a newer
package is refused. Times are stored as UTC ticks and costs as decimal text, so nothing depends on float or date formats. Each save is one write
transaction. The adapter is trim and NativeAOT compatible (row mapping is generated at build time).

## Entity Framework Core

```csharp
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b) => b.ApplyOneLlm();   // tablePrefix: "OneLlm", schema: null
}

var factory = /* IDbContextFactory<AppDb> */;
var store = new EfInstanceStore(() => factory.CreateDbContext());
var sink  = new EfRecordSink(() => factory.CreateDbContext());
```

`ApplyOneLlm` maps three tables (`OneLlmInstances`, `OneLlmRecords`, `OneLlmLeases`) into your model; the table names take a prefix and an
optional schema. Create them with your own migrations (`dotnet ef migrations add`) or `EnsureCreated`; the package ships no migrations and no
database provider. Each call uses a fresh short-lived context from your factory.

- The instance row's `Version` is a concurrency token, bumped on every save. Writers that race on one row retry against the new row, so the last
  save wins, the same rule as the file store.
- Times are stored as UTC ticks so they sort the same on every provider. Costs are `decimal(28,12)` and summed in the application, because some
  providers cannot aggregate decimals in SQL.
- Entity Framework Core builds its model by reflection, so this package is not trim or NativeAOT compatible. Use the OneLite package where that matters.

## Costs and records

Both database sinks store each record as its JSON line (the same text `JsonlRecordSink` writes, read back by `RecordReader.ParseLine`) plus
columns for instance, kind, time, message and cost. `CostAsync(instanceId, includeSubtree)` sums inference and tool-call costs as `decimal`.
A subtree is every instance reached by following `callerInstanceId` and `spawnerInstanceId` downward. Only those two kinds are summed; a compaction
record's own cost field is not added, so a summarising call is counted once, as the inference record it produced.
