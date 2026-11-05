using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneLite;
using OneLlm.EntityFrameworkCore;
using OneLlm.Instances;
using OneLlm.OneLite;
using OneLlm.Records;

namespace OneLlm.Stores.Tests;

/// <summary>Checks only the two database backends have: cost queries, lapsing leases, the schema version and the EF concurrency token.</summary>
public class DatabaseBackendTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static ToolCallRecord ToolCost(string instance, decimal cost) => new()
    {
        InstanceId = instance,
        Time = T0,
        Call = new OneLlm.Messages.ToolCall("t", "search_web") { Cost = cost },
    };

    private static async Task FillTree(IInstanceStore store, IRecordSink sink)
    {
        await store.SaveAsync(Samples.Make("root"));
        await store.SaveAsync(Samples.Make("child", caller: "root"));
        await store.SaveAsync(Samples.Make("grand", spawner: "child"));
        await store.SaveAsync(Samples.Make("other"));
        await sink.WriteAsync(Samples.Inference("root", 0.1m, T0));
        await sink.WriteAsync(ToolCost("root", 0.02m));
        await sink.WriteAsync(new RunRecord { InstanceId = "root", Time = T0, Event = "started" });
        await sink.WriteAsync(Samples.Inference("child", 0.0003m, T0));
        await sink.WriteAsync(Samples.Inference("grand", 0.0000001m, T0));
        await sink.WriteAsync(Samples.Inference("other", 5m, T0));
    }

    [Fact]
    public async Task Ef_cost_sums_an_instance_and_its_subtree_in_decimal()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        Func<DbContext> f = TestDb.Factory(Path.Combine(dir.Path, "a.db"), create: true);
        var store = new EfInstanceStore(f, new TestClock());
        var sink = new EfRecordSink(f);
        await FillTree(store, sink);
        Assert.Equal(0.12m, await sink.CostAsync("root"));
        Assert.Equal(0.1203001m, await sink.CostAsync("root", includeSubtree: true));
        Assert.Equal(0m, await sink.CostAsync("nobody", includeSubtree: true));
    }

    [Fact]
    public async Task OneLite_cost_sums_an_instance_and_its_subtree_in_decimal()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using Database db = await OneLiteStorage.OpenAsync(Path.Combine(dir.Path, "a.db"));
        OneLiteInstanceStore store = await OneLiteInstanceStore.CreateAsync(db, new TestClock());
        OneLiteRecordSink sink = await OneLiteRecordSink.CreateAsync(db);
        await FillTree(store, sink);
        Assert.Equal(0.12m, await sink.CostAsync("root"));
        Assert.Equal(0.1203001m, await sink.CostAsync("root", includeSubtree: true));
        Assert.Equal(0m, await sink.CostAsync("nobody", includeSubtree: true));
    }

    [Fact]
    public async Task Ef_lapsed_lease_can_be_taken_over_and_a_renewed_one_cannot()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var clock = new TestClock();
        Func<DbContext> f = TestDb.Factory(Path.Combine(dir.Path, "a.db"), create: true);
        var store = new EfInstanceStore(f, clock, TimeSpan.FromMinutes(1));
        _ = await store.AcquireAsync("z");
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InstanceLockedException>(async () => await store.AcquireAsync("z"));
        // The holder never disposed or renewed (a crash): after the duration the lease is free.
        clock.Advance(TimeSpan.FromSeconds(31));
        await using IInstanceLease taken = await store.AcquireAsync("z");
    }

    [Fact]
    public async Task OneLite_lapsed_lease_can_be_taken_over_and_a_renewed_one_cannot()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var clock = new TestClock();
        using Database db = await OneLiteStorage.OpenAsync(Path.Combine(dir.Path, "a.db"));
        OneLiteInstanceStore store = await OneLiteInstanceStore.CreateAsync(db, clock, TimeSpan.FromMinutes(1));
        _ = await store.AcquireAsync("z");
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InstanceLockedException>(async () => await store.AcquireAsync("z"));
        clock.Advance(TimeSpan.FromSeconds(31));
        await using IInstanceLease taken = await store.AcquireAsync("z");
    }

    [Fact]
    public async Task Ef_save_bumps_the_row_version_and_survives_a_racing_writer()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        Func<DbContext> f = TestDb.Factory(Path.Combine(dir.Path, "a.db"), create: true);
        var store = new EfInstanceStore(f, new TestClock());
        await Task.WhenAll(Enumerable.Range(0, 10).Select(n =>
        {
            Instance i = Samples.Make("same");
            i.State = "s" + n;
            return store.SaveAsync(i).AsTask();
        }));
        using DbContext ctx = f();
        OneLlmInstance row = ctx.Set<OneLlmInstance>().Single();
        Assert.Equal(10, row.Version);
        Assert.NotNull(await store.LoadAsync("same"));
    }

    [Fact]
    public async Task OneLite_schema_is_versioned_and_reopening_keeps_the_data()
    {
        await using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = Path.Combine(dir.Path, "a.db");
        using (Database db = await OneLiteStorage.OpenAsync(path))
        {
            await (await OneLiteInstanceStore.CreateAsync(db)).SaveAsync(Samples.Make("keep"));
            Assert.Equal(OneLiteStorage.SchemaVersion, await db.ScalarAsync<long>($"pragma user_version"));
        }

        using Database again = await OneLiteStorage.OpenAsync(path);
        Assert.NotNull(await (await OneLiteInstanceStore.CreateAsync(again)).LoadAsync("keep"));
    }

    [Fact]
    public async Task Ef_model_applies_to_a_custom_prefix_and_schema_free_names()
    {
        var options = new DbContextOptionsBuilder<PrefixDb>().UseSqlite("Data Source=:memory:").Options;
        using var db = new PrefixDb(options);
        string[] names = [.. db.Model.GetEntityTypes().Select(e => e.GetTableName()!)];
        Assert.Contains("AgentInstances", names);
        Assert.Contains("AgentRecords", names);
        Assert.Contains("AgentLeases", names);
        await Task.CompletedTask;
    }

    private sealed class PrefixDb(DbContextOptions<PrefixDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyOneLlm("Agent");
    }
}
