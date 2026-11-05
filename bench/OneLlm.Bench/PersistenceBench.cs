using BenchmarkDotNet.Attributes;
using OneLlm.Instances;
using OneLlm.Messages;

namespace OneLlm.Bench;

/// <summary>
/// The cost of persisting one more turn of a long instance (about 200,000 tokens of conversation, 800 KB of YAML): serialize the
/// instance and replace its file atomically with a flush to disk. "Append" is the steady state of a run; "Cold" is a first save
/// of an instance just loaded, with nothing cached.
/// </summary>
[MemoryDiagnoser]
public class PersistenceBench
{
    private string _dir = "";
    private FileInstanceStore _store = null!;
    private Instance _hot = null!;
    private Instance _cold = null!;
    private string _text = "";
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "onellm-bench-" + Guid.NewGuid().ToString("N"));
        _store = new FileInstanceStore(_dir);
        _hot = Build();
        _text = _hot.ToYaml();
        _store.SaveAsync(_hot).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"instance: {_hot.Messages.Count} messages, {_text.Length / 1024} KB of YAML");
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [IterationSetup(Target = nameof(SaveCold))]
    public void ColdSetup() => _cold = Instance.Parse(_text);

    /// <summary>Add one turn, save, then take the turn off again so every run measures the same size.</summary>
    [Benchmark]
    public void SaveAppendedTurn()
    {
        _hot.Messages.Add(Message.FromAssistant("A new answer of ordinary length for this turn. " + _next++, "next" + _next));
        _store.SaveAsync(_hot).AsTask().GetAwaiter().GetResult();
        _hot.Messages.RemoveAt(_hot.Messages.Count - 1);
    }

    /// <summary>Serialize alone, with every message's text already cached.</summary>
    [Benchmark]
    public string SerializeAppendedTurn()
    {
        _hot.Messages.Add(Message.FromAssistant("A new answer of ordinary length for this turn.", "next"));
        string s = _hot.ToYaml();
        _hot.Messages.RemoveAt(_hot.Messages.Count - 1);
        return s;
    }

    [Benchmark]
    public string SaveCold() => _cold.ToYaml();

    [Benchmark]
    public Instance Load() => Instance.Parse(_text);

    private static Instance Build()
    {
        Instance i = Instance.Create("long");
        i.Name = "Long run";
        i.Model = "model";
        i.SystemPrompt = "You are a careful agent.";
        string para = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog while the committee reviews the quarterly figures. ", 12));
        for (int turn = 0; turn < 110; turn++)
        {
            i.Messages.Add(Message.FromUser($"Turn {turn}: " + para, "u" + turn));
            i.Messages.Add(new Message
            {
                Id = "a" + turn,
                Assistant = [new TextPart(para + para)],
                ToolCalls = [new ToolCall("c" + turn, "read_file") { Inputs = new System.Text.Json.Nodes.JsonObject { ["path"] = "file" + turn + ".txt" }, Result = [new TextPart(para + para + para + para)] }],
                Usage = new Usage { Model = "model", InputTokens = 1000, OutputTokens = 300, Cost = 0.01m },
            });
        }

        return i;
    }
}
