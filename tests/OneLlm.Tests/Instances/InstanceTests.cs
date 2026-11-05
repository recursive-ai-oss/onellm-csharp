using System.Text.Json.Nodes;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tests.Messages;

namespace OneLlm.Tests.Instances;

public class InstanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onellm-inst-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        AtomicFile.FaultAfterPartialWrite = null;
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Instance Sample(string id = "inst1")
    {
        Instance i = Instance.FromDefinition(
            AgentDefinition.Parse("name: Helper\nmodel: sonnet\nsystemPrompt: Be brief.\nvendor: {x: 1}\n"), id);
        i.State = InstanceStates.Waiting;
        i.CallerInstanceId = "parent";
        i.RenderedSystemPrompt = "Be brief.";
        i.Inputs = new() { ["repo"] = "." };
        i.Messages.Add(Message.FromUser("hello", "u1"));
        i.Messages.Add(SampleMessages.Everything());
        i.Messages.Add(new Message
        {
            Id = "m2",
            ToolCalls = [new ToolCall("c1", "read_file") { Inputs = new JsonObject { ["path"] = "a.txt" }, Result = [new TextPart("contents")] }],
        });
        i.PendingApprovals.Add(new PendingApproval { ToolCallId = "c2", Tool = "execute_terminal_command", MessageId = "m3", RequestedAt = "2026-01-01T00:00:00Z", ExtensionData = new() { ["policy"] = "shell" } });
        i.Compactions.Add(new Compaction { Strategy = "clearToolResults", ReplacedMessageIds = ["m1"], Cost = 0.002m });
        return i;
    }

    [Fact]
    public void An_instance_round_trips_with_everything_it_holds()
    {
        Instance i = Sample();
        string yaml = i.ToYaml();
        Instance back = Instance.Parse(yaml);
        Assert.Equal("inst1", back.InstanceId);
        Assert.Equal("waiting", back.State);
        Assert.Equal("parent", back.CallerInstanceId);
        Assert.Equal("Be brief.", back.RenderedSystemPrompt);
        Assert.Equal(3, back.Messages.Count);
        Assert.Equal("contents", back.Messages[2].ToolCalls[0].Result[0] is TextPart t ? t.Text : null);
        Assert.Equal("c2", back.PendingApprovals[0].ToolCallId);
        Assert.Equal("shell", back.PendingApprovals[0].ExtensionData!["policy"]);
        Assert.Equal(0.002m, back.Compactions[0].Cost);
        Assert.True(back.ExtensionData!.ContainsKey("vendor"));
        // Saving what was loaded changes nothing.
        Assert.Equal(yaml, back.ToYaml());
        Assert.Equal(yaml, Instance.Parse(back.ToYaml()).ToYaml());
    }

    [Fact]
    public void An_unknown_state_and_unknown_keys_survive()
    {
        const string yaml = "name: \"\"\nmodel: m\nstate: suspended\ninstanceId: abc\nsubId: jess\nsubscriptionWait:\n  reason: x\nmetadata:\n  renderedSystemPrompt: |2\n    hi\n  other: 1\nmessages:\n  - id: a\n    user:\n      - text: hey\n";
        Instance i = Instance.Parse(yaml);
        Assert.Equal("suspended", i.State);
        Assert.Equal("hi\n", i.RenderedSystemPrompt);
        i.State = "stopped";
        Instance again = Instance.Parse(i.ToYaml());
        Assert.Equal("jess", again.ExtensionData!["subId"]);
        Assert.True(again.ExtensionData.ContainsKey("subscriptionWait"));
        Assert.True(((Dictionary<string, object?>)again.ExtensionData["metadata"]!).ContainsKey("other"));
    }

    [Fact]
    public void Fork_and_duplicate_make_independent_copies()
    {
        Instance i = Sample();
        Instance dup = i.Duplicate("copy1");
        Assert.Equal("copy1", dup.InstanceId);
        Assert.Equal(3, dup.Messages.Count);
        Assert.Empty(dup.PendingApprovals);
        Assert.Equal("stopped", dup.State);
        dup.Messages.RemoveAt(2);
        Assert.Equal(3, i.Messages.Count);

        Instance fork = i.Fork("fork1", "u1");
        Assert.Single(fork.Messages);
        var from = (Dictionary<string, object?>)fork.ExtensionData!["forkedFrom"]!;
        Assert.Equal("inst1", from["instanceId"]);
        Assert.Equal("u1", from["messageId"]);
        Assert.Throws<ArgumentException>(() => i.Fork("x", "nope"));
        Assert.True(Instance.Parse(fork.ToYaml()).ExtensionData!.ContainsKey("forkedFrom"));
    }

    [Fact]
    public async Task The_file_store_saves_loads_lists_and_deletes()
    {
        var store = new FileInstanceStore(_dir);
        Assert.Null(await store.LoadAsync("none"));
        await store.SaveAsync(Sample("a"));
        Instance b = Sample("b");
        b.State = InstanceStates.Running;
        b.CallerInstanceId = null;
        await store.SaveAsync(b);

        Instance? a = await store.LoadAsync("a");
        Assert.NotNull(a);
        Assert.Equal(3, a!.Messages.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "a.yaml")));

        var all = await ToListAsync(store.ListAsync());
        Assert.Equal(["a", "b"], all.Select(s => s.InstanceId).Order());
        var running = await ToListAsync(store.ListAsync(new InstanceQuery { State = "running" }));
        Assert.Equal("b", Assert.Single(running).InstanceId);
        var byCaller = await ToListAsync(store.ListAsync(new InstanceQuery { CallerInstanceId = "parent" }));
        Assert.Equal("a", Assert.Single(byCaller).InstanceId);
        Assert.Equal("Helper", all[0].Name);

        Assert.True(await store.DeleteAsync("a"));
        Assert.False(await store.DeleteAsync("a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    public async Task Ids_that_could_name_other_files_are_refused(string id)
    {
        var store = new FileInstanceStore(_dir);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.LoadAsync(id));
    }

    [Fact]
    public async Task A_save_killed_mid_write_leaves_the_previous_instance_intact()
    {
        var store = new FileInstanceStore(_dir);
        Instance i = Sample();
        await store.SaveAsync(i);
        string before = File.ReadAllText(store.PathOf("inst1"));

        i.Messages.Add(Message.FromAssistant("a new turn", "m9"));
        AtomicFile.FaultAfterPartialWrite = _ => throw new IOException("killed");
        await Assert.ThrowsAsync<IOException>(async () => await store.SaveAsync(i));
        AtomicFile.FaultAfterPartialWrite = null;

        Assert.Equal(before, File.ReadAllText(store.PathOf("inst1")));
        Assert.Equal(3, (await store.LoadAsync("inst1"))!.Messages.Count);
        Assert.Empty(Directory.GetFiles(_dir, ".*tmp*"));

        await store.SaveAsync(i);
        Assert.Equal(4, (await store.LoadAsync("inst1"))!.Messages.Count);
    }

    [Fact]
    public async Task A_second_live_runner_is_refused_until_the_first_lets_go()
    {
        var store = new FileInstanceStore(_dir);
        await using (IInstanceLease first = await store.AcquireAsync("inst1"))
        {
            Assert.Equal("inst1", first.InstanceId);
            await Assert.ThrowsAsync<InstanceLockedException>(async () => await store.AcquireAsync("inst1"));
            // A different instance is not blocked, and neither is a second store over the same directory for another id.
            await using IInstanceLease other = await new FileInstanceStore(_dir).AcquireAsync("inst2");
            // The same instance through another store object is still refused: the lock is the file, not the object.
            await Assert.ThrowsAsync<InstanceLockedException>(async () => await new FileInstanceStore(_dir).AcquireAsync("inst1"));
        }

        await using IInstanceLease again = await store.AcquireAsync("inst1");
        Assert.Equal("inst1", again.InstanceId);
    }

    [Fact]
    public async Task The_memory_store_copies_in_and_out_and_leases_one_runner()
    {
        var store = new MemoryInstanceStore(new ManualTimeProvider());
        Instance i = Sample();
        await store.SaveAsync(i);
        Instance loaded = (await store.LoadAsync("inst1"))!;
        Assert.NotSame(i, loaded);
        loaded.Messages.Clear();
        Assert.Equal(3, (await store.LoadAsync("inst1"))!.Messages.Count);
        Assert.Single(await ToListAsync(store.ListAsync(new InstanceQuery { NameContains = "help" })));
        await using IInstanceLease l = await store.AcquireAsync("inst1");
        await Assert.ThrowsAsync<InstanceLockedException>(async () => await store.AcquireAsync("inst1"));
        Assert.True(await store.DeleteAsync("inst1"));
    }

    [Fact]
    public void Appending_a_turn_reuses_the_text_of_the_earlier_messages()
    {
        Instance i = Sample();
        string first = i.ToYaml();
        i.Messages.Add(Message.FromAssistant("next", "m3"));
        string second = i.ToYaml();
        Assert.StartsWith(first[..first.IndexOf("messages:", StringComparison.Ordinal)], second);
        Assert.Contains("next", second);
        Assert.Equal(4, Instance.Parse(second).Messages.Count);
    }

    [Fact]
    public void Text_with_a_byte_order_mark_or_control_characters_is_written_so_that_it_loads_again()
    {
        Instance i = Instance.Create("odd");
        i.Messages.Add(Message.FromUser("a\uFEFFb\u2028c\u0085d  \ttrail \n\nline", "x"));
        Instance back = Instance.Parse(i.ToYaml());
        Assert.Equal("a\uFEFFb\u2028c\u0085d  \ttrail \n\nline", back.Messages[0].GetUserText());
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (T item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
