using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.AotSmoke;

internal enum Priority
{
    Low,
    High,
}

internal sealed record Ticket(string Title, Priority Priority, List<string>? Labels = null);

internal sealed record Triaged(string Queue, int Score);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Ticket))]
[JsonSerializable(typeof(Triaged))]
internal sealed partial class ToolJson : JsonSerializerContext;

/// <summary>Tools written with the generator: no reflection runs when they are listed, described or called.</summary>
internal sealed partial class Desk
{
    private readonly string _owner = "desk";

    /// <summary>Puts a ticket in a queue.</summary>
    /// <param name="ticket">The ticket to file.</param>
    /// <param name="urgent">True to jump the line.</param>
    [Tool]
    public Triaged Triage(Ticket ticket, bool urgent = false) =>
        new(ticket.Priority == Priority.High || urgent ? "now" : "later", ticket.Labels?.Count ?? 0);

    /// <summary>Joins words.</summary>
    [Tool]
    public async Task<string> JoinAsync(List<string> words, ToolContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        context.AddCost(CostLine.Create("join", 1m, 1m, 0.001m));
        return _owner + ":" + string.Join(' ', words);
    }

    /// <summary>Ends the run.</summary>
    [Tool(EndsLoop = true)]
    public static Triaged Finish(string queue, int score) => new(queue, score);
}

/// <summary>Lists generated tools, runs them through the invoker and checks results, errors and cost under NativeAOT.</summary>
internal static class ToolExercise
{
    /// <summary>Runs the exercise.</summary>
    /// <returns>0 when every step gave the expected result.</returns>
    public static int Run()
    {
        IReadOnlyList<ITool> tools = Desk.GetTools();
        ITool triage = tools[0];
        ITool join = tools[1];
        ITool finish = tools[2];

        var clock = TimeProvider.System;
        var ctx = new ToolContext("instance", "call", clock);
        ToolResult filed = ToolInvoker.InvokeAsync(
            triage,
            (JsonObject)JsonNode.Parse("""{"ticket":{"title":"t","priority":"High","labels":["a","b"]}}""")!,
            ctx,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        JsonNode? triaged = ((JsonPart)filed.Parts[0]).Value;

        var joinCtx = new ToolContext("instance", "call-2", clock);
        ToolResult joined = ToolInvoker.InvokeAsync(join, (JsonObject)JsonNode.Parse("""{"words":["x","y"]}""")!, joinCtx, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        ToolResult bad = ToolInvoker.InvokeAsync(join, (JsonObject)JsonNode.Parse("""{"words":"x"}""")!, new ToolContext("instance", "call-3", clock), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        ToolResult done = ToolInvoker.InvokeAsync(finish, (JsonObject)JsonNode.Parse("""{"queue":"q","score":3}""")!, new ToolContext("instance", "call-4", clock), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        bool ok = tools.Count == 3
            && triage.Definition.Name == "triage"
            && triage.Definition.InputSchema["required"]!.ToJsonString() == """["ticket"]"""
            && triaged!["queue"]!.GetValue<string>() == "now"
            && ((TextPart)joined.Parts[0]).Text == "desk:x y"
            && joined.CostLines.Count == 1
            && bad.IsError
            && ((TextPart)bad.Parts[0]).Text.Contains("'words'", StringComparison.Ordinal)
            && done.EndsLoop
            && done.TypedResult!["score"]!.GetValue<int>() == 3;
        return ok ? 0 : 1;
    }
}
