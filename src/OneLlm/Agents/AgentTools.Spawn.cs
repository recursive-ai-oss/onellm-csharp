using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Runners;
using OneLlm.Tools;

namespace OneLlm.Agents;

internal sealed class RunAgentAsyncTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "run_agent_async",
        "Delegate a task to another agent WITHOUT waiting for it to finish. Starts the agent (or sends a follow-up message to an existing instance) and returns its instance id immediately.\n\nThis is the fire-and-forget way to delegate. Use it to kick off one or more long-running agents in parallel, then call `await_agent` with the returned instance id(s) to collect their results. The agent keeps running in the background after this call returns.\n\nTo message an existing agent instead of starting a new one, pass its instance id via `agentId` (with `prompt` as the new message).",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "agentSwarmUri":{"type":"string","description":"Name or path of the agent to run (e.g., /agents/routine-updater)"},
              "prompt":{"type":"string","description":"The task/prompt to give to the agent"},
              "agentId":{"type":"string","description":"Optional: existing agent instance ID to send a message to instead of starting a new agent"},
              "workingDir":{"type":"string","description":"Optional: working directory for the agent."},
              "swarm":{"type":"string","description":"Optional: swarm URI to provide swarm context to the agent via the .swarm template namespace."},
              "inputs":{"type":"object","description":"Optional: JSON object of template variables. On start, passed as the new agent's inputs. When messaging an existing agent via agentId, only used if the agent is stopped and needs to be restarted."}},
             "required":["agentSwarmUri","prompt"]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        Runtime? rt = context.Runtime;
        if (rt is null)
        {
            return AgentTools.NoRuntime();
        }

        string reference = ToolArgs.String(inputs, "agentSwarmUri") ?? ToolArgs.String(inputs, "agentPath") ?? "";
        string agentId = ToolArgs.String(inputs, "agentId") ?? "";
        string prompt = ToolArgs.String(inputs, "prompt") ?? "";
        if (reference.Length == 0 && agentId.Length == 0)
        {
            return AgentTools.Err("agentSwarmUri is required (or provide agentId to message an existing agent)");
        }

        if (prompt.Length == 0)
        {
            return AgentTools.Err("prompt is required");
        }

        string instanceId;
        string action;
        if (agentId.Length > 0)
        {
            LiveAgent? existing = await rt.AgentHost.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return AgentTools.Err($"Failed to send message to agent: there is no instance '{agentId}'");
            }

            try
            {
                await existing.DeliverAsync(
                    prompt,
                    new MessageSource { Kind = "agent", InstanceId = context.InstanceId, ToolCallId = context.ToolCallId },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OneLlmException ex)
            {
                return AgentTools.Err($"Failed to send message to agent: {ex.Message}");
            }

            instanceId = agentId;
            action = "messaged";
        }
        else
        {
            AgentDefinition? definition;
            try
            {
                definition = FindDefinition(rt, reference, context);
            }
            catch (OneLlmException ex)
            {
                return AgentTools.Err($"Failed to start agent: {ex.Message}");
            }

            if (definition is null)
            {
                return AgentTools.Err($"Failed to start agent: no agent '{reference}' is registered, and no definition file of that name is under the folders this agent may read.");
            }

            Dictionary<string, object?>? agentInputs = null;
            if (inputs["inputs"] is JsonObject given)
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(given.ToJsonString());
                agentInputs = PlainValues.FromElement(doc.RootElement) as Dictionary<string, object?>;
            }

            try
            {
                AgentRun run = await rt.StartAgentAsync(
                    definition,
                    agentInputs,
                    new AgentStartOptions
                    {
                        Prompt = prompt,
                        // The child can hold what its parent holds and nothing more.
                        Capabilities = context.Capabilities,
                        SpawnerInstanceId = context.InstanceId,
                        CallerToolCallId = context.ToolCallId,
                        DelegationPrompt = prompt,
                        WorkingDirectory = ToolArgs.String(inputs, "workingDir") is { Length: > 0 } wd ? wd : null,
                        SwarmUri = ToolArgs.String(inputs, "swarm") is { Length: > 0 } sw ? sw : null,
                    },
                    cancellationToken).ConfigureAwait(false);
                instanceId = run.InstanceId;
            }
            catch (OneLlmException ex)
            {
                return AgentTools.Err($"Failed to start agent: {ex.Message}");
            }

            action = "started";
        }

        string target = reference.Length > 0 ? reference : instanceId;
        return ToolResult.FromText(
            $"Agent {action}: {target}\nInstance Id: {instanceId}\nCall the `await_agent` tool with this instance id to get the response. To supervise it meanwhile, arm `monitor_agent` on it (context size, turns, finish, elapsed time).");
    }

    // A registered name wins. A path is read only when it lies under a folder the calling agent may read, after symlinks are followed.
    private static AgentDefinition? FindDefinition(Runtime rt, string reference, ToolContext context)
    {
        if (rt.AgentDefinitions.Find(reference) is { } registered)
        {
            return registered;
        }

        foreach (string candidate in new[] { reference, reference + ".yaml", reference + ".yml", Path.Combine(reference, "agent.yaml") })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (!SafePath.IsUnder(candidate, context.Capabilities.FileRoots))
            {
                throw new CapabilityMissingException($"Reading the agent file '{reference}'", "a file root that covers it");
            }

            return AgentDefinition.Load(candidate);
        }

        return null;
    }
}

internal sealed class AwaitAgentTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "await_agent",
        "Wait for one or more already-running agents to finish and return their results. Use it to collect the output of agents started with `run_agent_async`.\n\n`ids` is an array of agent instance ids (a single id string is also accepted). `mode` is `one` (the default) or `all`:\n- `one` — returns as soon as at least one agent has finished, with EVERY result that has finished at that moment; agents still running are listed by id with their current state. Call `await_agent` again with the ids you still need.\n- `all` — waits until every listed agent has finished.\n\nEach result is the agent's `task_complete` result and summary, or a fallback to its last message if it stopped cleanly without `task_complete`. Each result reports the agent's context window usage; failures (e.g. `context_window_exceeded`) carry a code and a suggested next step and are never presented as results. An agent that has ALREADY finished returns its result immediately, recovered from its persisted history.\n\nAny message that arrives for you while you are waiting interrupts the wait: you get the partial picture (who finished, who is still running), read the message, and can call `await_agent` again. The agents are not stopped.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "ids":{"type":"array","description":"Agent instance ids to wait for (or a single id string).","items":{"type":"string"}},
              "mode":{"type":"string","enum":["one","all"],"description":"`one` (default): return as soon as at least one agent has finished, with every result finished at that moment. `all`: wait for every agent."}},
             "required":["ids"]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Runtime is null)
        {
            return AgentTools.NoRuntime();
        }

        List<string>? ids = ToolArgs.Ids(inputs, "ids");
        if (ids is null)
        {
            return AgentTools.Err("ids must be a string or an array of strings");
        }

        if (ids.Count == 0)
        {
            return AgentTools.Err("ids is required: an agent instance id, or an array of them");
        }

        string mode = (ToolArgs.String(inputs, "mode") ?? "").ToLowerInvariant();
        if (mode.Length == 0)
        {
            mode = "one";
        }

        if (mode is not ("one" or "all"))
        {
            return AgentTools.Err($"mode must be \"one\" or \"all\", got \"{ToolArgs.String(inputs, "mode")}\"");
        }

        return ToolResult.FromText(await AwaitCore.RunAsync(context, ids, mode == "all", cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>What <c>await_agent</c> reports for one agent.</summary>
internal sealed class AgentOutcome
{
    public required string InstanceId { get; init; }

    public string Result { get; set; } = "";

    public string Summary { get; set; } = "";

    public decimal Cost { get; set; }

    public string? Error { get; set; }

    public string Code { get; set; } = "";

    public string Detail { get; set; } = "";

    public string LastMessage { get; set; } = "";

    public string Model { get; set; } = "";

    public long ContextTokens { get; set; }

    public long ContextWindow { get; set; }
}

internal static class AwaitCore
{
    private static bool IsFinished(string? state) => state is InstanceStates.Stopped or InstanceStates.Waiting or InstanceStates.Error;

    /// <summary>
    /// Waits for the agents to rest (one of them, or all), unless a message arrives for the caller first, then reports each finished
    /// one and lists the rest with their state. An agent held for approval rests but is not finished: it is listed as pending.
    /// </summary>
    public static async Task<string> RunAsync(ToolContext context, List<string> ids, bool all, CancellationToken cancellationToken)
    {
        AgentHost host = context.Runtime!.AgentHost;
        var agents = new List<(string Id, LiveAgent? Agent)>();
        foreach (string id in ids)
        {
            agents.Add((id, await host.GetAsync(id, cancellationToken).ConfigureAwait(false)));
        }

        LiveAgent? caller = context.Agent;
        var rests = agents.Where(a => a.Agent is not null).Select(a => a.Agent!.AwaitAsync(cancellationToken).ContinueWith(static _ => 0, TaskScheduler.Default)).ToList();
        Task interrupt = caller?.NextMessage ?? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (rests.Count > 0)
        {
            if (all)
            {
                await Task.WhenAny(Task.WhenAll(rests), interrupt).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(Task.WhenAny(rests).ContinueWith(static _ => 0, TaskScheduler.Default), interrupt).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<AgentOutcome>();
        var pending = new List<(string Id, string State)>();
        foreach ((string id, LiveAgent? agent) in agents)
        {
            if (agent is null)
            {
                results.Add(new AgentOutcome { InstanceId = id, Error = $"There is no agent instance '{id}'.", Code = "agent_error" });
            }
            else if (!agent.IsRunning && IsFinished(agent.Instance.State))
            {
                results.Add(Describe(agent, host));
            }
            else
            {
                pending.Add((id, agent.Instance.State ?? "unknown"));
            }
        }

        return Format(results, pending, ids.Count);
    }

    public static AgentOutcome Describe(LiveAgent agent, AgentHost host)
    {
        Instance i = agent.Instance;
        (string text, JsonNode? output, string? by) = Outcome.Derive(i);
        var o = new AgentOutcome
        {
            InstanceId = i.InstanceId,
            Cost = agent.OwnCost + host.DescendantCost(i.InstanceId).Total,
            Model = agent.Setup.Model.Id,
            ContextTokens = agent.ContextTokens,
            ContextWindow = agent.ContextWindow,
            LastMessage = text,
        };
        if (i.State == InstanceStates.Error)
        {
            string message = "agent encountered an error";
            string type = "";
            if (i.ExtensionData is { } d && d.TryGetValue("error", out object? e) && e is IDictionary<string, object?> map)
            {
                message = map.TryGetValue("message", out object? m) && m is string ms ? ms : message;
                type = map.TryGetValue("type", out object? t) ? t as string ?? "" : "";
            }

            bool overflow = type == nameof(ContextOverflowException);
            o.Code = overflow ? "context_window_exceeded" : "agent_error";
            o.Error = overflow ? "agent ran out of context window: the provider rejected its next request as too long" : message;
            o.Detail = overflow ? message : "";
            return o;
        }

        if (by is not null && output is JsonObject obj)
        {
            o.Result = obj["result"]?.GetValue<string>() ?? text;
            o.Summary = obj["summary"]?.GetValue<string>() ?? "";
            return o;
        }

        if (text.Length > 0)
        {
            // A clean stop without task_complete: the last message is the best answer there is.
            o.Result = text;
            return o;
        }

        o.Code = "stopped_without_completion";
        o.Error = "agent stopped without completing the task and produced no output";
        return o;
    }

    public static string Format(List<AgentOutcome> results, List<(string Id, string State)> pending, int total)
    {
        var buf = new StringBuilder();
        int ok = results.Count(static r => r.Error is null);
        int failed = results.Count - ok;
        decimal cost = results.Sum(static r => r.Cost);
        if (total == 1 && results.Count == 1)
        {
            buf.Append("## Agent Result\n\n");
            Body(buf, results[0], "###");
        }
        else
        {
            if (pending.Count > 0)
            {
                buf.Append("## Agent Results (first to finish)\n\n");
                buf.Append(string.Create(CultureInfo.InvariantCulture, $"**Finished:** {results.Count} of {total} | **Succeeded:** {ok} | **Failed:** {failed} | **Cost:** {AgentTools.Usd6(cost)}\n\n"));
            }
            else
            {
                buf.Append("## Agent Results\n\n");
                buf.Append(string.Create(CultureInfo.InvariantCulture, $"**Agents:** {results.Count} | **Succeeded:** {ok} | **Failed:** {failed} | **Total Cost:** {AgentTools.Usd6(cost)}\n\n"));
            }

            for (int i = 0; i < results.Count; i++)
            {
                buf.Append(string.Create(CultureInfo.InvariantCulture, $"---\n### Agent {i + 1}\n"));
                Body(buf, results[i], "####");
                buf.Append('\n');
            }

            if (pending.Count > 0)
            {
                buf.Append("---\n### Still running\n\n");
                foreach ((string id, string state) in pending)
                {
                    buf.Append("- `").Append(id).Append("` — ").Append(state).Append('\n');
                }

                buf.Append("\nCall `await_agent` again with the ids you still need (finished agents return their result immediately).\n");
            }
        }

        return buf.ToString();
    }

    private static void Body(StringBuilder buf, AgentOutcome r, string heading)
    {
        buf.Append("**Instance ID:** `").Append(r.InstanceId).Append("` | **Cost:** ").Append(AgentTools.Usd6(r.Cost)).Append("\n\n");
        string usage = ContextLine(r);
        if (r.Error is not null)
        {
            buf.Append("**Error:** ").Append(r.Error).Append('\n');
            buf.Append("**Code:** `").Append(r.Code).Append("`\n");
            if (usage.Length > 0)
            {
                buf.Append("**Context Window Used:** ").Append(usage).Append('\n');
            }

            if (r.Detail.Length > 0)
            {
                buf.Append("**Provider error:** ").Append(Cut(r.Detail, 800)).Append('\n');
            }

            if (NextStep(r) is { Length: > 0 } step)
            {
                buf.Append("**Next step:** ").Append(step).Append('\n');
            }

            if (r.LastMessage.Length > 0)
            {
                buf.Append("\n**Last message from agent (not a result):**\n\n").Append(Cut(r.LastMessage, 4000)).Append('\n');
            }

            return;
        }

        if (usage.Length > 0)
        {
            buf.Append("**Context Window Used:** ").Append(usage).Append("\n\n");
        }

        buf.Append(heading).Append(" Result\n\n").Append(r.Result).Append("\n\n");
        if (r.Summary.Length > 0)
        {
            buf.Append(heading).Append(" Summary\n\n").Append(r.Summary).Append('\n');
        }
    }

    private static string ContextLine(AgentOutcome r)
    {
        if (r.ContextTokens <= 0)
        {
            return "";
        }

        return r.ContextWindow > 0
            ? $"{AgentHost.Kilo(r.ContextTokens)} / {AgentHost.Kilo(r.ContextWindow)} tokens ({(r.ContextTokens * 100 / r.ContextWindow).ToString(CultureInfo.InvariantCulture)}%)"
            : $"{AgentHost.Kilo(r.ContextTokens)} tokens (window size unknown for model `{r.Model}`)";
    }

    private static string NextStep(AgentOutcome r) => r.Code switch
    {
        "context_window_exceeded" => $"Do not resume `{r.InstanceId}` — its context window is full and its next turn will fail the same way. Start a fresh agent (omit `agentId`) and hand it the remaining work, using the agent's last message as the progress report.",
        "stopped_without_completion" => $"Send a follow-up with `agentId: {r.InstanceId}` asking it to finish and call `task_complete`.",
        "agent_error" => $"If the error looks transient, resume it with `agentId: {r.InstanceId}`; otherwise start a fresh agent.",
        _ => "",
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "… [truncated]";
}
