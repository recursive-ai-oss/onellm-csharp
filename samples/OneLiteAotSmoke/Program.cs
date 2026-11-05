using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.OneLite;
using OneLlm.Records;
using RecursiveAI.OneLite;

// Saves, lists, leases and loads an instance and writes a record through the OneLite adapter, so a NativeAOT publish proves the
// adapter's generated row mapping and the instance file format work without reflection.
string dir = Path.Combine(Path.GetTempPath(), "onellm-onelite-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    using Database db = await OneLiteStorage.OpenAsync(Path.Combine(dir, "app.db"));
    OneLiteInstanceStore store = await OneLiteInstanceStore.CreateAsync(db);
    OneLiteRecordSink sink = await OneLiteRecordSink.CreateAsync(db);

    Instance i = Instance.Create("smoke1");
    i.Name = "Smoke";
    i.Messages.Add(Message.FromUser("hello", "m1"));
    await store.SaveAsync(i);
    await using (await store.AcquireAsync("smoke1"))
    {
    }

    await sink.WriteAsync(new InferenceRecord { InstanceId = "smoke1", Time = DateTimeOffset.UnixEpoch, Usage = new Usage { Model = "m", Cost = 0.25m } });

    Instance? back = await store.LoadAsync("smoke1");
    int listed = 0;
    await foreach (InstanceSummary _ in store.ListAsync())
    {
        listed++;
    }

    decimal cost = await sink.CostAsync("smoke1");
    bool ok = back is { Name: "Smoke" } && back.Messages.Count == 1 && listed == 1 && cost == 0.25m && (await sink.ReadAsync("smoke1")).Count == 1;
    Console.WriteLine(ok ? "ok" : "FAILED");
    return ok ? 0 : 1;
}
finally
{
    Directory.Delete(dir, recursive: true);
}
