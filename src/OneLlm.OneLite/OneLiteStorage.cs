using RecursiveAI.OneLite;

namespace OneLlm.OneLite;

/// <summary>Opens and prepares a OneLite database for OneLLM's stores and sinks.</summary>
public static class OneLiteStorage
{
    // Schema scripts, applied in order by PRAGMA user_version (OneLite's MigrateAsync). Append a script for every later change; never
    // edit one that has shipped. Version 1: the three tables. Times are UTC ticks and costs are decimal text, so nothing depends on a
    // date or float format.
    private static readonly string[] Scripts =
    [
        """
        create table OneLlmInstances (
            InstanceId text not null primary key,
            Name text not null,
            State text,
            Model text,
            CallerInstanceId text,
            SpawnerInstanceId text,
            ModifiedTicks integer not null,
            Yaml text not null
        ) strict;
        create index OneLlmInstancesModified on OneLlmInstances (ModifiedTicks);
        create index OneLlmInstancesCaller on OneLlmInstances (CallerInstanceId);
        create index OneLlmInstancesSpawner on OneLlmInstances (SpawnerInstanceId);
        create table OneLlmRecords (
            Id integer primary key autoincrement,
            InstanceId text not null,
            Kind text not null,
            TimeTicks integer not null,
            MessageId text,
            Cost text,
            Json text not null
        ) strict;
        create index OneLlmRecordsInstance on OneLlmRecords (InstanceId, Id);
        create table OneLlmLeases (
            InstanceId text not null primary key,
            Owner text not null,
            ExpiresTicks integer not null
        ) strict;
        """,
    ];

    /// <summary>The schema version this package writes.</summary>
    public static int SchemaVersion => Scripts.Length;

    /// <summary>Opens (creating) a database file with the schema in place. One database can back a store and a sink at once.</summary>
    /// <param name="path">The database file path, or <c>:memory:</c> for a private in-memory database.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The database; the caller disposes it.</returns>
    /// <exception cref="OneLiteException">The file cannot be opened, or its schema version is newer than this package knows.</exception>
    public static async ValueTask<Database> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Database db = await Database.OpenAsync(path, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateAsync(db, cancellationToken).ConfigureAwait(false);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Brings an open database to the current schema. Safe to call every time; it does nothing when the schema is current.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    public static Task MigrateAsync(Database database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.MigrateAsync(cancellationToken, Scripts);
    }
}
