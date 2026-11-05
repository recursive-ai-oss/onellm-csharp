using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;

namespace OneLlm.Stores.Tests;

/// <summary>A clock tests move by hand. Timers are the system's, whose periods are far longer than any test.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds the instances and records the conformance runs save.</summary>
internal static class Samples
{
    public static Instance Make(string id, string name = "Helper", string state = InstanceStates.Waiting, string? caller = null, string? spawner = null)
    {
        Instance i = OneLlm.Instances.Instance.FromDefinition(
            AgentDefinition.Parse($"name: {name}\nmodel: sonnet\nsystemPrompt: Be brief.\nvendor: {{x: 1}}\n"), id);
        i.State = state;
        i.CallerInstanceId = caller;
        i.SpawnerInstanceId = spawner;
        i.RenderedSystemPrompt = "Be brief.";
        i.Messages.Add(Message.FromUser("hello \u00e9 \u2603", "m1"));
        i.Messages.Add(Message.FromAssistant("hi", "m2"));
        i.PendingApprovals.Add(new PendingApproval { ToolCallId = "c1", Tool = "write_file", MessageId = "m2" });
        return i;
    }

    public static InferenceRecord Inference(string instance, decimal cost, DateTimeOffset time) => new()
    {
        InstanceId = instance,
        Time = time,
        Provider = "anthropic",
        Usage = new Usage { Model = "m", InputTokens = 10, OutputTokens = 5, Cost = cost },
    };
}

/// <summary>What every <see cref="IInstanceStore"/> must do, run once per store.</summary>
public abstract class InstanceStoreConformance : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    /// <summary>The clock the store runs on.</summary>
    public TestClock Clock { get; } = new();

    /// <summary>The store under test.</summary>
    protected IInstanceStore Store { get; private set; } = null!;

    /// <summary>Makes a store over the backend's storage. Every store made in one test shares that storage; null when the backend cannot be reopened (memory).</summary>
    protected abstract ValueTask<IInstanceStore?> OpenAsync(TestClock clock, Action<IAsyncDisposable> own);

    /// <summary>False when the backend never looks at a cancellation token.</summary>
    protected virtual bool HonoursCancellation => true;

    public async ValueTask InitializeAsync() => Store = (await OpenAsync(Clock, _cleanup.Add))!;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (IAsyncDisposable d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    [Fact]
    public async Task Save_then_load_round_trips_the_instance_file_text()
    {
        Instance i = Samples.Make("a1");
        await Store.SaveAsync(i);
        Instance? back = await Store.LoadAsync("a1");
        Assert.NotNull(back);
        Assert.Equal(i.ToYaml(), back.ToYaml());
        Assert.Equal(2, back.Messages.Count);
        Assert.Equal("c1", back.PendingApprovals[0].ToolCallId);
        Assert.True(back.ExtensionData!.ContainsKey("vendor"));
    }

    [Fact]
    public async Task Load_of_a_missing_instance_is_null()
    {
        Assert.Null(await Store.LoadAsync("nothing"));
    }

    [Fact]
    public async Task Saving_again_replaces_the_stored_instance()
    {
        Instance i = Samples.Make("a2");
        await Store.SaveAsync(i);
        i.State = InstanceStates.Stopped;
        i.Messages.Add(Message.FromUser("more", "m3"));
        await Store.SaveAsync(i);
        Instance? back = await Store.LoadAsync("a2");
        Assert.Equal(InstanceStates.Stopped, back!.State);
        Assert.Equal(3, back.Messages.Count);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task Bad_ids_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.LoadAsync("../x"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.SaveAsync(Samples.Make("has space")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.AcquireAsync(""));
    }

    [Fact]
    public async Task Delete_removes_the_instance_and_reports_whether_it_existed()
    {
        await Store.SaveAsync(Samples.Make("d1"));
        Assert.True(await Store.DeleteAsync("d1"));
        Assert.False(await Store.DeleteAsync("d1"));
        Assert.Null(await Store.LoadAsync("d1"));
    }

    [Fact]
    public async Task List_is_newest_first_and_filters_combine()
    {
        await Store.SaveAsync(Samples.Make("l1", "Alpha Agent", InstanceStates.Stopped));
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Store.SaveAsync(Samples.Make("l2", "Beta Agent", InstanceStates.Waiting, caller: "l1"));
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Store.SaveAsync(Samples.Make("l3", "Gamma Worker", InstanceStates.Waiting, caller: "l1"));

        Assert.Equal(["l3", "l2", "l1"], (await ListAsync()).Select(s => s.InstanceId));
        Assert.Equal(["l3", "l2"], (await ListAsync(new InstanceQuery { State = "waiting" })).Select(s => s.InstanceId));
        Assert.Equal(["l3", "l2"], (await ListAsync(new InstanceQuery { CallerInstanceId = "l1" })).Select(s => s.InstanceId));
        Assert.Equal(["l2", "l1"], (await ListAsync(new InstanceQuery { NameContains = "AGENT" })).Select(s => s.InstanceId));
        Assert.Equal(["l3"], (await ListAsync(new InstanceQuery { Limit = 1 })).Select(s => s.InstanceId));

        InstanceSummary top = (await ListAsync()).First();
        Assert.Equal("Gamma Worker", top.Name);
        Assert.Equal("sonnet", top.Model);
        Assert.Equal("l1", top.CallerInstanceId);
    }

    [Fact]
    public async Task A_held_lease_refuses_a_second_taker_until_disposed()
    {
        IInstanceLease first = await Store.AcquireAsync("r1");
        Assert.Equal("r1", first.InstanceId);
        await Assert.ThrowsAsync<InstanceLockedException>(async () => await Store.AcquireAsync("r1"));
        await using (await Store.AcquireAsync("r2"))
        {
        }

        await first.DisposeAsync();
        await using IInstanceLease again = await Store.AcquireAsync("r1");
    }

    [Fact]
    public async Task A_lease_excludes_a_second_store_over_the_same_storage()
    {
        IInstanceStore? other = await OpenAsync(Clock, _cleanup.Add);
        if (other is null)
        {
            return;
        }

        await using IInstanceLease held = await Store.AcquireAsync("x1");
        await Assert.ThrowsAsync<InstanceLockedException>(async () => await other.AcquireAsync("x1"));
        // And the other handle sees what this one saves.
        await Store.SaveAsync(Samples.Make("x1"));
        Assert.NotNull(await other.LoadAsync("x1"));
    }

    [Fact]
    public async Task Concurrent_acquires_let_exactly_one_runner_in()
    {
        IInstanceLease?[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                return (IInstanceLease?)await Store.AcquireAsync("race");
            }
            catch (InstanceLockedException)
            {
                return null;
            }
        }));
        Assert.Single(results, r => r is not null);
        await results.First(r => r is not null)!.DisposeAsync();
    }

    [Fact]
    public async Task A_save_that_is_cancelled_leaves_the_old_instance()
    {
        if (!HonoursCancellation)
        {
            return;
        }

        Instance i = Samples.Make("c1");
        await Store.SaveAsync(i);
        i.State = InstanceStates.Error;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.SaveAsync(i, cts.Token));
        Assert.Equal(InstanceStates.Waiting, (await Store.LoadAsync("c1"))!.State);
    }

    [Fact]
    public async Task Concurrent_saves_of_different_instances_all_land()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(n => Store.SaveAsync(Samples.Make("p" + n)).AsTask()));
        Assert.Equal(12, (await ListAsync()).Count);
    }

    private async Task<List<InstanceSummary>> ListAsync(InstanceQuery? q = null)
    {
        List<InstanceSummary> all = [];
        await foreach (InstanceSummary s in Store.ListAsync(q))
        {
            all.Add(s);
        }

        return all;
    }
}

/// <summary>What every <see cref="IRecordSink"/> must do, run once per sink.</summary>
public abstract class RecordSinkConformance : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];
    private Func<string, Task<List<RecordEntry>>> _read = null!;

    /// <summary>The sink under test.</summary>
    protected IRecordSink Sink { get; private set; } = null!;

    /// <summary>Makes the sink and the function that reads an instance's records back.</summary>
    protected abstract ValueTask<(IRecordSink Sink, Func<string, Task<List<RecordEntry>>> Read)> OpenAsync(Action<IAsyncDisposable> own);

    public async ValueTask InitializeAsync() => (Sink, _read) = await OpenAsync(_cleanup.Add);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (IAsyncDisposable d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    [Fact]
    public async Task Records_read_back_in_write_order_with_their_fields()
    {
        DateTimeOffset t = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        await Sink.WriteAsync(new RunRecord { InstanceId = "s1", Time = t, Event = "started", State = "running", Model = "sonnet" });
        await Sink.WriteAsync(new MessageRecord { InstanceId = "s1", Time = t.AddSeconds(1), MessageId = "m1", Message = Message.FromUser("hi", "m1") });
        await Sink.WriteAsync(Samples.Inference("s1", 0.0123m, t.AddSeconds(2)));
        await Sink.WriteAsync(new ErrorRecord { InstanceId = "s1", Time = t.AddSeconds(3), ErrorType = "RateLimit", Message = "slow down" });

        List<RecordEntry> got = await _read("s1");
        Assert.Equal(["run", "message", "inference", "error"], got.Select(e => e.Kind));
        Assert.All(got, e => Assert.Equal("s1", e.InstanceId));
        Assert.Equal(t.AddSeconds(1), got[1].Time);
        Assert.Equal("m1", got[1].MessageId);
        Assert.Equal("started", got[0].Json.GetProperty("event").GetString());
        Assert.Equal(0.0123m, got[2].Json.GetProperty("usage").GetProperty("cost").GetDecimal());
        Assert.Equal("slow down", got[3].Json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Records_of_different_instances_stay_apart()
    {
        DateTimeOffset t = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        await Sink.WriteAsync(new RunRecord { InstanceId = "one", Time = t, Event = "started" });
        await Sink.WriteAsync(new RunRecord { InstanceId = "two", Time = t, Event = "started" });
        await Sink.WriteAsync(new RunRecord { InstanceId = "one", Time = t, Event = "stopped" });
        Assert.Equal(2, (await _read("one")).Count);
        Assert.Single(await _read("two"));
        Assert.Empty(await _read("three"));
    }

    [Fact]
    public async Task Concurrent_writes_all_land()
    {
        DateTimeOffset t = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(n => Sink.WriteAsync(Samples.Inference("cc", 0.001m, t.AddSeconds(n))).AsTask()));
        Assert.Equal(30, (await _read("cc")).Count);
    }

    [Fact]
    public async Task A_throwing_sink_never_fails_the_run_and_the_others_still_get_the_record()
    {
        var errors = new List<Exception>();
        var sinks = new RecordSinks { OnError = (_, e) => errors.Add(e) };
        sinks.Add(new ThrowingSink());
        sinks.Add(Sink);
        await sinks.WriteAsync(new RunRecord { InstanceId = "t1", Time = DateTimeOffset.UnixEpoch, Event = "started" });
        Assert.Single(errors);
        Assert.Single(await _read("t1"));
    }

    private sealed class ThrowingSink : IRecordSink
    {
        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) => throw new IOException("disk full");
    }
}
