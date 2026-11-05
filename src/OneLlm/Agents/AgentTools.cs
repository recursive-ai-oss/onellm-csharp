using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.Agents;

/// <summary>The tools agents use to finish, to start and supervise other agents, and to look at instances.</summary>
internal static class AgentTools
{
    public static IReadOnlyList<ITool> All { get; } =
    [
        new TaskCompleteTool(),
        new RunAgentAsyncTool(),
        new AwaitAgentTool(),
        new MessageAgentTool(),
        new MonitorAgentTool(),
        new AwaitMonitorTool(),
        new ListAgentInstancesTool(),
        new QueryAgentTool(),
    ];

    public static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public static ValueTask<ToolResult> Done(ToolResult r) => ValueTask.FromResult(r);

    public static ToolResult Err(string message) => ToolResult.Error("Error: " + message);

    public static string Usd6(decimal d) => "$" + Math.Round(d, 6, MidpointRounding.AwayFromZero).ToString("F6", CultureInfo.InvariantCulture);

    public static string Usd2(decimal d) => "$" + Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);

    public static string Commas(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static AgentHost? HostOf(ToolContext context) => context.Runtime?.AgentHost;

    public static ToolResult NoRuntime() => Err("this tool needs an agent running in a runtime.");

    public static ToolResult Text(StringBuilder sb) => ToolResult.FromText(sb.ToString());
}

internal sealed class TaskCompleteTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "task_complete",
        "Signal that the assigned task has been completed. Call this tool when you have finished the work requested by the user. Provide the result of the task and a summary of the actions taken. Optionally provide a short title. Ends the run and returns a plain-text confirmation.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "title":{"type":"string","description":"Optional short task title."},
              "result":{"type":"string","description":"The result of the task. For example, if asked to write a haiku, this would contain the haiku. If asked to update code, this might be 'Code successfully updated.'"},
              "summary":{"type":"string","description":"A summary of the actions taken to complete the task - the process, not the result."}},
             "required":["result","summary"]}
            """))
    {
        EndsLoop = true,
    };

    public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string? result = ToolArgs.String(inputs, "result");
        string? summary = ToolArgs.String(inputs, "summary");
        if (string.IsNullOrEmpty(result))
        {
            return AgentTools.Done(AgentTools.Err("result is required"));
        }

        if (string.IsNullOrEmpty(summary))
        {
            return AgentTools.Done(AgentTools.Err("summary is required"));
        }

        var typed = new JsonObject();
        if (ToolArgs.String(inputs, "title") is { Length: > 0 } title)
        {
            typed["title"] = title;
        }

        typed["result"] = result;
        typed["summary"] = summary;
        return AgentTools.Done(new ToolResult([new TextPart("Task completion event sent successfully")]) { EndsLoop = true, TypedResult = typed });
    }
}

/// <summary>Kept for definitions that still list it: it always answers that messaging goes through <c>run_agent_async</c>.</summary>
internal sealed class MessageAgentTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "message_agent",
        "Error: deprecated. Use run_agent_async to message agents instead.\n\nTo send a follow-up message to a previously started agent, call run_agent_async with the existing instance ID passed as `agentId` and the new message as `prompt`. The agent resumes from its existing conversation history with full context intact.",
        AgentTools.Schema("""{"type":"object","properties":{}}"""));

    public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken) =>
        AgentTools.Done(ToolResult.Error("Error: deprecated. Use run_agent_async to message agents instead"));
}

internal sealed class ListAgentInstancesTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "list_agent_instances",
        "List agent runs saved in this runtime's instance store. Returns a list of agent instances with their ID, name, state, last activity time, and model. Sorted by last activity (most recent first). Use filters to narrow down results by name or state.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "name":{"type":"string","description":"Filter instances by agent name (case-insensitive, partial match)"},
              "state":{"type":"string","description":"Filter by instance state (e.g., 'running', 'stopped')"},
              "swarm":{"type":"string","description":"Filter by the swarm URI the run executed under (exact match)."},
              "includeDescendants":{"type":"boolean","description":"When true, widen the swarm filter to the swarm itself plus all descendant swarms (matches '<swarm>' and '<swarm>/...', but not sibling prefixes like '<swarm>bot'). Default false (exact match). Ignored when swarm is empty."},
              "limit":{"type":"integer","description":"Maximum number of instances to return (default: 20)"}},
             "required":[]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        Runtime? rt = context.Runtime;
        if (rt is null)
        {
            return AgentTools.NoRuntime();
        }

        string? name = ToolArgs.String(inputs, "name");
        string? state = ToolArgs.String(inputs, "state");
        string? swarm = ToolArgs.String(inputs, "swarm");
        bool descendants = ToolArgs.Bool(inputs, "includeDescendants") ?? false;
        long limit = ToolArgs.Int(inputs, "limit") is { } l and > 0 ? l : 20;

        var list = new JsonArray();
        await foreach (InstanceSummary s in rt.Store.ListAsync(new InstanceQuery { NameContains = string.IsNullOrEmpty(name) ? null : name, State = string.IsNullOrEmpty(state) ? null : state }, cancellationToken).ConfigureAwait(false))
        {
            Instance? loaded = null;
            if (!string.IsNullOrEmpty(swarm))
            {
                loaded = await rt.Store.LoadAsync(s.InstanceId, cancellationToken).ConfigureAwait(false);
                string? uri = loaded?.ExtensionData is { } d && d.TryGetValue("swarmUri", out object? v) ? v as string : null;
                bool match = uri == swarm || (descendants && uri is not null && uri.StartsWith(swarm.TrimEnd('/') + "/", StringComparison.Ordinal));
                if (!match)
                {
                    continue;
                }
            }

            var item = new JsonObject { ["id"] = s.InstanceId, ["name"] = s.Name, ["state"] = s.State ?? "" };
            if (loaded?.ExtensionData is { } ext && ext.TryGetValue("swarmUri", out object? swarmUri) && swarmUri is string su)
            {
                item["swarmUri"] = su;
            }

            item["updatedAt"] = s.Modified.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
            if (s.Model is not null)
            {
                item["model"] = s.Model;
            }

            list.Add((JsonNode?)item);
            if (list.Count >= limit)
            {
                break;
            }
        }

        return ToolResult.FromJson(new JsonObject { ["instances"] = list, ["count"] = list.Count });
    }
}
