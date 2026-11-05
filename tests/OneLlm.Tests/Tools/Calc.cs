using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.Tests.Tools;

public partial class Calc
{
    [Tool]
    public int Add(int a, int b) => a + b;

    [Tool]
    public async Task<string> EchoAsync(string text, int times = 1, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return string.Concat(Enumerable.Repeat(text, times));
    }

    [Tool(EndsLoop = true)]
    public Verdict Finish(string decision, int score) => new(decision, score);

    [Tool]
    public void Nothing()
    {
    }

    [Tool]
    public string Boom(string why) => throw new InvalidOperationException(why);

    [Tool(TimeoutSeconds = 5)]
    public async Task Slow(CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);

    [Tool]
    public async Task WaitForCancel(CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);

    [Tool]
    public string Priced(ToolContext ctx)
    {
        ctx.AddCost(CostLine.Create("search_query", 1m, 1m, 0.005m));
        return "ok";
    }

    [Tool]
    public ToolResult Raw() => ToolResult.FromText("raw");

    [Tool]
    public Part[] Several() => [new TextPart("a"), new TextPart("b")];

    [Tool]
    public Address Where(string q) => new(q);

    [Tool(RequiresApproval = true, ParallelSafe = false, Name = "do_it", Description = "Does it.")]
    public string Danger() => "x";

    [Tool]
    public ValueTask<int> Later(int n) => new(n * 2);

    [Tool]
    public JsonNode? Node() => new JsonObject { ["k"] = 1 };

    [Tool]
    public Color Pick(Color c) => c;

    [Tool]
    public Guid? MaybeGuid(bool have) => have ? Guid.Parse("11111111-1111-1111-1111-111111111111") : null;

    [Tool]
    public async Task<string> SlowGuard(ToolContext ctx)
    {
        await Task.Yield();
        ctx.Log("hello");
        ctx.Progress("half");
        return ctx.InstanceId;
    }
}
