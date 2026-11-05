using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;

namespace OneLlm.AotSmoke;

/// <summary>Exercises definitions, instances, stores and records so the native-AOT publish checks them.</summary>
internal static class DefinitionsExercise
{
    public static async Task<bool> RunAsync()
    {
        AgentDefinition def = AgentDefinition.Parse("name: Smoke\nmodel: m\nsystemPrompt: hi\nreferences:\n  - routine: a/b\nextra: {k: 1}\n", "smoke.yaml");
        Instance inst = Instance.FromDefinition(def, "smoke1");
        inst.Messages.Add(Message.FromUser("hello", "u1"));
        inst.Messages.Add(Message.FromAssistant("hi there", "a1"));

        var store = new MemoryInstanceStore();
        await store.SaveAsync(inst);
        Instance? back = await store.LoadAsync("smoke1");

        string dir = Path.Combine(Path.GetTempPath(), "onellm-aot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new FileInstanceStore(dir);
            await files.SaveAsync(inst);
            Instance? fromDisk = await files.LoadAsync("smoke1");

            var sink = new JsonlRecordSink(dir);
            await sink.WriteAsync(new MessageRecord { InstanceId = "smoke1", Time = DateTimeOffset.UnixEpoch, Message = inst.Messages[0] });
            int lines = RecordReader.ReadFile(sink.PathOf("smoke1")).Count;

            DeciderDefinition decider = DeciderDefinition.Parse("model: m\nquestions:\n  q:\n    type: noul\n    instructions: Is it?\n", "d.yaml");
            return back is { Messages.Count: 2 } && fromDisk is { Messages.Count: 2 } && lines == 1 && decider.Questions.Count == 1 && def.ExtensionData!.ContainsKey("extra");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
