using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneLite;
using OneLlm.EntityFrameworkCore;
using OneLlm.Instances;
using OneLlm.OneLite;
using OneLlm.Records;

namespace OneLlm.Stores.Tests;

/// <summary>Removes a scratch directory when a test ends.</summary>
internal sealed class TempDir : IAsyncDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onellm-stores-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class Disposer(Action dispose) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A user's own context, with the OneLLM tables applied to its model.</summary>
internal sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyOneLlm();

    public static Func<DbContext> Factory(string path, bool create)
    {
        var options = new DbContextOptionsBuilder<TestDb>().UseSqlite($"Data Source={path}").Options;
        if (create)
        {
            using var db = new TestDb(options);
            db.Database.EnsureCreated();
        }

        return () => new TestDb(options);
    }
}

public sealed class FileStoreConformance : InstanceStoreConformance
{
    protected override ValueTask<IInstanceStore?> OpenAsync(TestClock clock, Action<IAsyncDisposable> own)
    {
        // All stores of one test share one directory, kept in a field of the first.
        _dir ??= NewDir(own);
        return ValueTask.FromResult<IInstanceStore?>(new FileInstanceStore(_dir));
    }

    private string? _dir;

    private static string NewDir(Action<IAsyncDisposable> own)
    {
        var t = new TempDir();
        own(t);
        return t.Path;
    }
}

public sealed class MemoryStoreConformance : InstanceStoreConformance
{
    protected override bool HonoursCancellation => false;

    protected override ValueTask<IInstanceStore?> OpenAsync(TestClock clock, Action<IAsyncDisposable> own) =>
        ValueTask.FromResult<IInstanceStore?>(_opened++ == 0 ? new MemoryInstanceStore(clock) : null);

    private int _opened;
}

public sealed class EfStoreConformance : InstanceStoreConformance
{
    private string? _path;

    protected override ValueTask<IInstanceStore?> OpenAsync(TestClock clock, Action<IAsyncDisposable> own)
    {
        bool first = _path is null;
        if (first)
        {
            var t = new TempDir();
            own(t);
            Directory.CreateDirectory(t.Path);
            _path = Path.Combine(t.Path, "app.db");
        }

        return ValueTask.FromResult<IInstanceStore?>(new EfInstanceStore(TestDb.Factory(_path!, first), clock));
    }
}

public sealed class OneLiteStoreConformance : InstanceStoreConformance
{
    private string? _path;

    protected override async ValueTask<IInstanceStore?> OpenAsync(TestClock clock, Action<IAsyncDisposable> own)
    {
        if (_path is null)
        {
            var t = new TempDir();
            own(t);
            Directory.CreateDirectory(t.Path);
            _path = Path.Combine(t.Path, "app.db");
        }

        Database db = await OneLiteStorage.OpenAsync(_path);
        own(new Disposer(db.Dispose));
        return await OneLiteInstanceStore.CreateAsync(db, clock);
    }
}

public sealed class JsonlSinkConformance : RecordSinkConformance
{
    protected override ValueTask<(IRecordSink Sink, Func<string, Task<List<RecordEntry>>> Read)> OpenAsync(Action<IAsyncDisposable> own)
    {
        var t = new TempDir();
        own(t);
        var sink = new JsonlRecordSink(t.Path);
        return ValueTask.FromResult<(IRecordSink, Func<string, Task<List<RecordEntry>>>)>((sink, id => Task.FromResult(RecordReader.ReadFile(sink.PathOf(id)))));
    }
}

public sealed class EventSinkConformance : RecordSinkConformance
{
    protected override ValueTask<(IRecordSink Sink, Func<string, Task<List<RecordEntry>>> Read)> OpenAsync(Action<IAsyncDisposable> own)
    {
        // The event sink keeps nothing; the test collects what it announces, in the JSON form every other sink stores.
        var sink = new EventRecordSink();
        var lines = new List<AuditRecord>();
        sink.Written += r =>
        {
            lock (lines)
            {
                lines.Add(r);
            }
        };
        return ValueTask.FromResult<(IRecordSink, Func<string, Task<List<RecordEntry>>>)>((sink, id =>
        {
            lock (lines)
            {
                return Task.FromResult(lines.Where(r => r.InstanceId == id).Select(r => RecordReader.ParseLine(JsonlRecordSink.ToJsonLine(r))).ToList());
            }
        }));
    }
}

public sealed class EfSinkConformance : RecordSinkConformance
{
    protected override ValueTask<(IRecordSink Sink, Func<string, Task<List<RecordEntry>>> Read)> OpenAsync(Action<IAsyncDisposable> own)
    {
        var t = new TempDir();
        own(t);
        Directory.CreateDirectory(t.Path);
        var sink = new EfRecordSink(TestDb.Factory(Path.Combine(t.Path, "app.db"), create: true));
        return ValueTask.FromResult<(IRecordSink, Func<string, Task<List<RecordEntry>>>)>((sink, id => sink.ReadAsync(id)));
    }
}

public sealed class OneLiteSinkConformance : RecordSinkConformance
{
    protected override async ValueTask<(IRecordSink Sink, Func<string, Task<List<RecordEntry>>> Read)> OpenAsync(Action<IAsyncDisposable> own)
    {
        var t = new TempDir();
        own(t);
        Directory.CreateDirectory(t.Path);
        Database db = await OneLiteStorage.OpenAsync(Path.Combine(t.Path, "app.db"));
        own(new Disposer(db.Dispose));
        OneLiteRecordSink sink = await OneLiteRecordSink.CreateAsync(db);
        return (sink, id => sink.ReadAsync(id));
    }
}
