using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;

namespace OneLlm.Cli;

internal sealed partial class CliApp
{
    private async Task<int> ChatVerbAsync(List<string> rest, CancellationToken cancellationToken)
    {
        CliArgs args = CliArgs.Parse(rest, [.. s_common, "system"], [], []);
        if (args.Positional.Count != 1)
        {
            throw new UsageException("chat needs one model name, for example 'onellm chat claude-sonnet'.");
        }

        Runtime rt = OpenRuntime(args, withGrants: false);
        using ChatSession chat = rt.Chat(new AskRequest { Model = args.Positional[0], System = args.Get("system") });
        Note("Type a message and press Enter. /exit or end of input stops.");
        while (true)
        {
            if (Interactive)
            {
                lock (_errGate)
                {
                    _err.Write("you> ");
                    _err.Flush();
                }
            }

            string? line = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Trim() == "/exit")
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            await foreach (string text in chat.StreamAsync(line).TextAsync(cancellationToken).ConfigureAwait(false))
            {
                _out.Write(text);
            }

            _out.WriteLine();
        }

        Note($"instance {chat.InstanceId} · cost {Usd(OwnCost(chat.Instance).Total)}");
        return ExitCodes.Ok;
    }

    private async Task<int> InstancesVerbAsync(List<string> rest, CancellationToken cancellationToken)
    {
        if (rest.Count == 0)
        {
            throw new UsageException("instances needs list, show, resume or fork.");
        }

        string sub = rest[0];
        CliArgs args = CliArgs.Parse(rest.Skip(1).ToList(), [.. s_common, "state-filter", "name", "limit", "prompt", "at"], ["allow-shell", "allow-net"], ["yes", "quiet"]);
        Runtime rt = OpenRuntime(args);
        switch (sub)
        {
            case "list":
            {
                int? limit = args.Get("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null;
                var query = new InstanceQuery { State = args.Get("state-filter"), NameContains = args.Get("name"), Limit = limit };
                int n = 0;
                await foreach (InstanceSummary s in rt.Store.ListAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    _out.WriteLine($"{s.InstanceId}\t{s.State}\t{Time(s.Modified)}\t{s.Name}\t{s.Model}");
                    n++;
                }

                Note($"{n} instance{(n == 1 ? "" : "s")}");
                return ExitCodes.Ok;
            }

            case "show":
            {
                Instance instance = await RequireAsync(rt, One(args, "show"), cancellationToken).ConfigureAwait(false);
                (decimal total, _) = OwnCost(instance);
                _out.WriteLine($"instance: {instance.InstanceId}");
                _out.WriteLine($"name: {instance.Name}");
                _out.WriteLine($"state: {instance.State}");
                _out.WriteLine($"model: {instance.Model}");
                if (instance.SpawnerInstanceId is { } sp)
                {
                    _out.WriteLine($"spawner: {sp}");
                }

                _out.WriteLine($"messages: {instance.Messages.Count}");
                _out.WriteLine($"cost (this instance): {Usd(total)}");
                foreach (PendingApproval p in instance.PendingApprovals)
                {
                    _out.WriteLine($"pending approval: {p.Tool} ({p.ToolCallId})");
                }

                foreach (Message m in instance.Messages)
                {
                    string user = m.GetUserText();
                    if (user.Length > 0)
                    {
                        _out.WriteLine($"[user] {Clip(user)}");
                    }

                    string assistant = m.GetAssistantText();
                    if (assistant.Length > 0)
                    {
                        _out.WriteLine($"[assistant] {Clip(assistant)}");
                    }

                    foreach (ToolCall c in m.ToolCalls)
                    {
                        string said = string.Concat(c.Result.OfType<TextPart>().Select(static t => t.Text));
                        _out.WriteLine(said.Length == 0 ? $"[tool] {c.Tool}" : $"[tool] {c.Tool} -> {Clip(said)}");
                    }
                }

                return ExitCodes.Ok;
            }

            case "resume":
            {
                string id = One(args, "resume");
                AgentRun run = await rt.GetAgentAsync(id, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"There is no instance '{id}'.");
                if (!args.Has("quiet"))
                {
                    rt.AgentEvents += PrintEvent;
                }

                if (args.Get("prompt") is { } prompt)
                {
                    await run.MessageAsync(prompt, cancellationToken).ConfigureAwait(false);
                }

                return await FinishAsync(rt, run, args.Has("yes"), cancellationToken).ConfigureAwait(false);
            }

            case "fork":
            {
                Instance copy = await rt.ForkAsync(One(args, "fork"), args.Get("at"), cancellationToken).ConfigureAwait(false);
                _out.WriteLine(copy.InstanceId);
                return ExitCodes.Ok;
            }

            default:
                throw new UsageException($"Unknown instances command '{sub}'. Use list, show, resume or fork.");
        }
    }

    private static string One(CliArgs args, string verb) =>
        args.Positional.Count == 1 ? args.Positional[0] : throw new UsageException($"instances {verb} needs one instance id.");

    private static async Task<Instance> RequireAsync(Runtime rt, string id, CancellationToken cancellationToken) =>
        await rt.Store.LoadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There is no instance '{id}'.");

    private static string Clip(string text)
    {
        string one = text.ReplaceLineEndings(" ");
        return one.Length <= 200 ? one : one[..200] + "…";
    }

    private async Task<int> CostVerbAsync(List<string> rest, CancellationToken cancellationToken)
    {
        CliArgs args = CliArgs.Parse(rest, [.. s_common, "since"], [], []);
        Runtime rt = OpenRuntime(args, withGrants: false);
        var byModel = new SortedDictionary<string, ModelTotals>(StringComparer.Ordinal);
        decimal total = 0m;
        int count = 0;
        if (args.Positional.Count == 1)
        {
            string id = args.Positional[0];
            Instance instance = await RequireAsync(rt, id, cancellationToken).ConfigureAwait(false);
            (total, byModel) = OwnCost(instance);
            decimal subtree = await rt.GetSubtreeCostAsync(id, cancellationToken).ConfigureAwait(false);
            Print(byModel);
            _out.WriteLine($"this instance: {Usd(total)}");
            _out.WriteLine($"with sub-agents: {Usd(subtree)}");
            return ExitCodes.Ok;
        }

        if (args.Positional.Count > 1)
        {
            throw new UsageException("cost takes at most one instance id.");
        }

        DateTimeOffset? since = args.Get("since") is { } s ? ParseSince(s, rt.TimeProvider.GetUtcNow()) : null;
        await foreach (InstanceSummary summary in rt.Store.ListAsync(new InstanceQuery { ModifiedSince = since }, cancellationToken).ConfigureAwait(false))
        {
            Instance? instance = await rt.Store.LoadAsync(summary.InstanceId, cancellationToken).ConfigureAwait(false);
            if (instance is null)
            {
                continue;
            }

            // Every instance, sub-agents included, is listed on its own, so adding each one's own cost counts nothing twice.
            (decimal own, SortedDictionary<string, ModelTotals> models) = OwnCost(instance);
            total += own;
            count++;
            foreach ((string model, ModelTotals t) in models)
            {
                byModel.TryGetValue(model, out ModelTotals sum);
                byModel[model] = new ModelTotals(sum.Calls + t.Calls, sum.InputTokens + t.InputTokens, sum.OutputTokens + t.OutputTokens, sum.Cost + t.Cost);
            }
        }

        Print(byModel);
        _out.WriteLine($"{count} instance{(count == 1 ? "" : "s")}: {Usd(total)}");
        return ExitCodes.Ok;
    }

    private void Print(SortedDictionary<string, ModelTotals> byModel)
    {
        foreach ((string model, ModelTotals t) in byModel)
        {
            _out.WriteLine($"{model}\t{t.Calls} calls\t{t.InputTokens} in\t{t.OutputTokens} out\t{Usd(t.Cost)}");
        }
    }

    /// <summary>A duration such as <c>90m</c>, <c>24h</c>, <c>7d</c> or <c>2w</c> back from now, or a date or timestamp.</summary>
    internal static DateTimeOffset ParseSince(string text, DateTimeOffset now)
    {
        if (text.Length >= 2 && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long n))
        {
            TimeSpan? span = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => null,
            };
            if (span is { } s)
            {
                return now - s;
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d)
            ? d
            : throw new UsageException("--since needs a duration like 24h or 7d, or a date like 2026-11-01.");
    }

    private int CatalogVerb(List<string> rest)
    {
        CliArgs args = CliArgs.Parse(rest, s_common, [], []);
        Runtime rt = OpenRuntime(args, withGrants: false);
        ModelCatalog catalog = rt.Catalog;
        if (args.Positional.Count == 0)
        {
            foreach (ProviderSpec p in catalog.Providers)
            {
                foreach (ModelSpec m in p.Models ?? [])
                {
                    _out.WriteLine($"{p.Name}:{m.Name}\t{m.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                }
            }

            return ExitCodes.Ok;
        }

        ResolvedModel resolved = catalog.Resolve(args.Positional[0]);
        _out.WriteLine($"model: {resolved.Id}");
        _out.WriteLine($"provider: {resolved.Provider.Name}" + (resolved.PresetName is null ? "" : $" (preset {resolved.PresetName})"));
        if (resolved.Model is not { } model)
        {
            _out.WriteLine("(matched by a name pattern; the catalog has no entry for it, so no values are known)");
            return ExitCodes.Ok;
        }

        JsonNode? node = JsonSerializer.SerializeToNode(model, CatalogJsonContext.Default.ModelSpec);
        var lines = new List<(string Path, string Value)>();
        Flatten(node, "", lines);
        foreach ((string path, string value) in lines)
        {
            string layer = catalog.SourceOf(resolved.Provider.Name, $"models[{model.Name}].{path}") ?? "-";
            _out.WriteLine($"{path}: {value}\t[{layer}]");
        }

        return ExitCodes.Ok;
    }

    private static void Flatten(JsonNode? node, string prefix, List<(string, string)> lines)
    {
        if (node is JsonObject o)
        {
            foreach ((string key, JsonNode? v) in o)
            {
                Flatten(v, prefix.Length == 0 ? key : prefix + "." + key, lines);
            }
        }
        else if (node is not null)
        {
            lines.Add((prefix, node.ToJsonString()));
        }
    }

    private int ExamplesVerb(List<string> rest)
    {
        CliArgs args = CliArgs.Parse(rest, [], [], []);
        if (args.Positional.Count == 0)
        {
            foreach (string name in Examples.Names)
            {
                string description = AgentDefinition.Parse(Examples.Text(name), name).Description ?? "";
                _out.WriteLine($"examples/{name}\t{description}");
            }

            return ExitCodes.Ok;
        }

        if (args.Positional is ["show", { } which])
        {
            _out.Write(Examples.Text(which.StartsWith("examples/", StringComparison.Ordinal) ? which : "examples/" + which));
            return ExitCodes.Ok;
        }

        throw new UsageException("examples takes no arguments (to list) or 'show <name>'.");
    }
}
