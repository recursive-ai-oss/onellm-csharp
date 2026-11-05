using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Deciders;
using OneLlm.Definitions;
using OneLlm.Instances;

namespace OneLlm.Cli;

internal sealed partial class CliApp
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions s_compact = new();

    private enum Kind
    {
        Agent,
        Script,
        Decider,
    }

    private async Task<int> RunVerbAsync(List<string> rest, CancellationToken cancellationToken)
    {
        CliArgs args = CliArgs.Parse(rest, [.. s_common, "input", "input-json", "prompt", "model"], ["allow-shell", "allow-net"], ["yes", "quiet"]);
        if (args.Positional.Count != 1)
        {
            throw new UsageException("run needs one definition: a .yaml agent, a .flow script, a decider directory or examples/<name>. Try 'onellm run --help'.");
        }

        string target = args.Positional[0];
        Kind kind = Detect(target, out string path);
        Dictionary<string, object?> inputs = ReadInputs(args);
        string? prompt = args.Get("prompt");
        string? model = args.Get("model");
        if (kind == Kind.Decider && (prompt is not null || model is not null))
        {
            throw new UsageException("--prompt and --model do not apply to a decider; its model is in decider.yaml.");
        }

        if (kind == Kind.Script && prompt is not null)
        {
            throw new UsageException("--prompt does not apply to a .flow script; its prompts are in the file.");
        }

        Runtime rt = OpenRuntime(args);
        bool quiet = args.Has("quiet");
        switch (kind)
        {
            case Kind.Decider:
            {
                DecisionResult r = await rt.DecideAsync(Decider.Load(path), inputs, cancellationToken).ConfigureAwait(false);
                _out.WriteLine(Pretty(r.Outputs.GetRawText()));
                Note($"decider {r.Instance.InstanceId} · {r.Engine} {r.Model} · cost {Usd(r.Cost.Total)}");
                return ExitCodes.Ok;
            }

            case Kind.Script:
            {
                AgentDefinition def = AgentDefinition.Load(path);
                if (model is not null)
                {
                    def.Model = model;
                }

                ScriptResult r = await rt.RunScriptAsync(def, inputs, cancellationToken).ConfigureAwait(false);
                _out.WriteLine(r.Output is { } output ? Pretty(output.GetRawText()) : r.Text);
                foreach (string w in r.Warnings)
                {
                    Note("warning: " + w);
                }

                Note($"script {r.Instance.InstanceId} · {r.Turns.Count} turns · cost {Usd(r.Cost.Total)}");
                return ExitCodes.Ok;
            }

            default:
            {
                AgentDefinition def = Examples.IsExample(target) ? Examples.Load(target) : AgentDefinition.Load(path);
                if (model is not null)
                {
                    def.Model = model;
                }

                if (!quiet)
                {
                    rt.AgentEvents += PrintEvent;
                }

                AgentRun run = await rt.StartAgentAsync(def, inputs, new AgentStartOptions { Prompt = prompt, WorkingDirectory = Path.GetFullPath(args.Get("root") ?? ".", WorkingDirectory) }, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(rt, run, args.Has("yes"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits for an agent to rest, asking at the terminal for each held tool call, and prints the outcome.</summary>
    private async Task<int> FinishAsync(Runtime rt, AgentRun run, bool yes, CancellationToken cancellationToken)
    {
        try
        {
            return await DriveAsync(rt, run, yes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C ends the wait; the agent itself is told to stop so the instance is saved as stopped with its partial output.
            await run.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> DriveAsync(Runtime rt, AgentRun run, bool yes, CancellationToken cancellationToken)
    {
        AgentResult result = await run.AwaitAsync(cancellationToken).ConfigureAwait(false);
        while (result.StopReason == AgentStopReason.WaitingForApproval && result.PendingApprovals.Count > 0)
        {
            foreach (PendingApproval p in result.PendingApprovals.ToList())
            {
                string shown = p.Inputs is null ? "" : " " + JsonSerializer.Serialize(p.Inputs, s_compact);
                bool ok = yes || await AskAsync($"Approve {p.Tool}{shown}?" + (p.Reason is null ? "" : $" ({p.Reason})"), cancellationToken).ConfigureAwait(false);
                run = ok
                    ? await rt.ApproveAsync(result.Instance.InstanceId, p.ToolCallId, cancellationToken).ConfigureAwait(false)
                    : await rt.DenyAsync(result.Instance.InstanceId, p.ToolCallId, "denied at the terminal", cancellationToken).ConfigureAwait(false);
            }

            result = await run.AwaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result.Output is JsonObject { } done && done["result"] is JsonValue text && text.TryGetValue(out string? resultText))
        {
            // task_complete's typed result: the result is the answer, the summary is how it was reached.
            _out.WriteLine(resultText);
            if (done["summary"] is JsonValue sum && sum.TryGetValue(out string? summary))
            {
                Note("summary: " + summary);
            }
        }
        else if (result.Output is { } typed)
        {
            _out.WriteLine(typed.ToJsonString(s_indented));
        }
        else
        {
            _out.WriteLine(result.Text);
        }

        foreach (string w in result.Warnings)
        {
            Note("warning: " + w);
        }

        string id = result.Instance.InstanceId;
        Note($"instance {id} · {result.State} · {result.Turns} turns · cost {Usd(result.SubtreeCost > 0 ? result.SubtreeCost : result.Cost.Total)}");
        switch (result.StopReason)
        {
            case AgentStopReason.Waiting:
                Note($"waiting for you: onellm instances resume {id} --prompt \"...\"");
                return ExitCodes.Ok;
            case AgentStopReason.WaitingForApproval:
                Note($"waiting for approval: onellm instances resume {id}");
                return ExitCodes.Ok;
            case AgentStopReason.MaxIterations:
                Note("stopped: the agent used all its iterations.");
                return ExitCodes.MaxIterations;
            default:
                return result.State == InstanceStates.Error ? ExitCodes.Failed : ExitCodes.Ok;
        }
    }

    private void PrintEvent(AgentEvent e)
    {
        if (e.Kind == AgentEventKind.ToolCallStarted && e.ToolCall is { } call)
        {
            Note($"  [{Short(e.InstanceId)}] {call.Tool}");
        }
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string Pretty(string json) => JsonNode.Parse(json)?.ToJsonString(s_indented) ?? "null";

    /// <summary>
    /// Mode follows the file: <c>examples/&lt;name&gt;</c> and agent YAML run as agents, <c>.flow</c> files as scripts, and a directory with
    /// <c>decider.yaml</c> (or that file) as a decider.
    /// </summary>
    private Kind Detect(string target, out string path)
    {
        path = Path.GetFullPath(target, WorkingDirectory);
        if (Examples.IsExample(target))
        {
            _ = Examples.Text(target);
            return Kind.Agent;
        }

        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, "decider.yaml")))
            {
                return Kind.Decider;
            }

            string agent = Path.Combine(path, "agent.yaml");
            if (File.Exists(agent))
            {
                path = agent;
                return Kind.Agent;
            }

            throw new FileNotFoundException($"'{target}' is a directory with no decider.yaml or agent.yaml.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no definition at '{target}'.");
        }

        if (Path.GetFileName(path) == "decider.yaml")
        {
            return Kind.Decider;
        }

        return path.EndsWith(".flow", StringComparison.OrdinalIgnoreCase) ? Kind.Script : Kind.Agent;
    }

    /// <summary>Inputs from <c>--input k=v</c> (a value starting with <c>@</c> is read from that file) and <c>--input-json {...}</c>.</summary>
    private Dictionary<string, object?> ReadInputs(CliArgs args)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string json in args.GetAll("input-json"))
        {
            JsonElement el;
            try
            {
                el = JsonDocument.Parse(json.StartsWith('@') ? File.ReadAllText(Path.GetFullPath(json[1..], WorkingDirectory)) : json).RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new UsageException("--input-json is not valid JSON: " + e.Message);
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new UsageException("--input-json must be a JSON object.");
            }

            foreach (JsonProperty p in el.EnumerateObject())
            {
                inputs[p.Name] = Plain(p.Value);
            }
        }

        foreach (string kv in args.GetAll("input"))
        {
            int eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new UsageException($"--input needs key=value, got '{kv}'.");
            }

            string value = kv[(eq + 1)..];
            inputs[kv[..eq]] = value.StartsWith('@') ? File.ReadAllText(Path.GetFullPath(value[1..], WorkingDirectory)) : value;
        }

        return inputs;
    }

    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(static p => p.Name, static p => Plain(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDecimal(),
        _ => null,
    };
}
