using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;

namespace OneLlm.Agents;

internal sealed partial class LiveAgent
{
    /// <summary>
    /// At a turn boundary, runs the agent's context strategy when the last model call's context passed the trigger. A strategy runs once
    /// per model call: after it, nothing triggers again until a new call has reported its usage.
    /// </summary>
    private async Task MaybeCompactAsync(CancellationToken token)
    {
        ContextStrategyOptions strategy = Setup.Strategy;
        if (strategy.Kind == ContextStrategyKind.None)
        {
            return;
        }

        Message? last = Instance.Messages.LastOrDefault(static m => m.Usage is not null);
        if (last is null)
        {
            return;
        }

        string? done = Instance.Compactions.Count > 0 && Instance.Compactions[^1].ExtensionData is { } d && d.TryGetValue("atMessageId", out object? id) ? id as string : null;
        if (done == last.Id)
        {
            return;
        }

        long threshold = strategy.TriggerTokens ?? (ContextWindow > 0 ? ContextWindow * strategy.TriggerPercent / 100 : long.MaxValue);
        if (ContextTokens < threshold)
        {
            return;
        }

        var pendingCalls = new HashSet<string>(Instance.PendingApprovals.Select(static p => p.ToolCallId), StringComparer.Ordinal);
        var pendingMessages = new HashSet<string>(Instance.PendingApprovals.Select(static p => p.MessageId ?? ""), StringComparer.Ordinal);
        DateTimeOffset now = Runtime.TimeProvider.GetUtcNow();
        var record = new Compaction { Strategy = StrategyName(strategy.Kind), At = Rfc3339.Format(now), ExtensionData = new Dictionary<string, object?> { ["atMessageId"] = last.Id } };

        switch (strategy.Kind)
        {
            case ContextStrategyKind.ClearToolResults:
            {
                List<string> would = ContextStrategies.ClearToolResults(Instance, strategy.KeepOrDefault, pendingCalls, dryRun: true);
                if (would.Count == 0 || await VetoedAsync(record.Strategy, would, token).ConfigureAwait(false))
                {
                    return;
                }

                record.ReplacedMessageIds = ContextStrategies.ClearToolResults(Instance, strategy.KeepOrDefault, pendingCalls);
                break;
            }

            case ContextStrategyKind.Compact:
            {
                int cut = ContextStrategies.CompactCut(Instance, strategy.KeepOrDefault, pendingMessages);
                if (cut <= 0)
                {
                    return;
                }

                List<Message> older = Instance.Messages.GetRange(0, cut);
                List<string> ids = [.. older.Select(static m => m.Id)];
                if (await VetoedAsync(record.Strategy, ids, token).ConfigureAwait(false))
                {
                    return;
                }

                (string summary, decimal cost) = await SummarizeAsync(older, token).ConfigureAwait(false);
                var summaryMessage = new Message
                {
                    Id = Runtime.NewId("m"),
                    User = [new TextPart("[Summary of the earlier conversation]\n" + summary)],
                    DateTime = now,
                    Source = new MessageSource { Kind = "runtime", Reason = "compaction" },
                };
                Instance.Messages.RemoveRange(0, cut);
                Instance.Messages.Insert(0, summaryMessage);
                record.ReplacedMessageIds = ids;
                record.SummaryMessageId = summaryMessage.Id;
                record.Cost = cost;
                break;
            }

            default:
            {
                if (Setup.Provider is not IProviderCompaction native)
                {
                    string note = $"The provider '{Setup.Provider.Name}' cannot compact context itself, so the providerNative strategy was skipped.";
                    if (!Setup.Warnings.Contains(note))
                    {
                        Setup.Warnings.Add(note);
                    }

                    return;
                }

                List<string> ids = [.. Instance.Messages.Select(static m => m.Id)];
                if (pendingMessages.Count > 0 || await VetoedAsync(record.Strategy, ids, token).ConfigureAwait(false))
                {
                    return;
                }

                ChatRequest request = Runtime.BuildChat(Instance, Setup.Model, Setup.Settings, null);
                ProviderCompactionResult? compacted = await native.CompactAsync(request, token).ConfigureAwait(false);
                if (compacted is null)
                {
                    return;
                }

                Instance.Messages.Clear();
                Instance.Messages.AddRange(compacted.Messages);
                record.ReplacedMessageIds = ids;
                record.Cost = compacted.Usage?.Cost;
                break;
            }
        }

        Instance.Compactions.Add(record);
        await Runtime.Store.SaveAsync(Instance, token).ConfigureAwait(false);
        await Runtime.WriteRecordAsync(new CompactionRecord { InstanceId = Instance.InstanceId, Time = now, Compaction = record }, token).ConfigureAwait(false);
        Emit(AgentEventKind.Compacted, detail: record.Strategy);
        if (record.Cost is > 0m && Setup.Hooks.Has(HookPoints.OnCost))
        {
            var cost = new Cost([new CostLine { Counter = "compaction", Quantity = 1m, UnitPrice = record.Cost.Value, Amount = record.Cost.Value, Label = record.Strategy }]);
            _segmentCosts.Add(cost);
            await Setup.Hooks.EmitCostAsync(new CostHookContext(Instance, _turns, "compaction", cost), token).ConfigureAwait(false);
        }
    }

    private async Task<bool> VetoedAsync(string strategy, IReadOnlyList<string> ids, CancellationToken token)
    {
        if (!Setup.Hooks.Has(HookPoints.BeforeCompaction))
        {
            return false;
        }

        HookOutcome outcome = await Setup.Hooks.BeforeCompactionAsync(new BeforeCompactionHookContext(Instance, _turns, strategy, ids), token).ConfigureAwait(false);
        return outcome.Action == HookAction.Block;
    }

    /// <summary>Runs the compaction definition over the older turns. The summary run is an instance of its own, a child of this agent, so its cost joins the subtree.</summary>
    private async Task<(string Summary, decimal Cost)> SummarizeAsync(List<Message> older, CancellationToken token)
    {
        AgentDefinition definition = Runtime.CompactionDefinition ?? CompactionFlow.Load();
        if (string.IsNullOrWhiteSpace(definition.Model))
        {
            definition.Model = Instance.Model;
        }

        ScriptResult result = await Runtime.RunScriptAsync(
            definition,
            new Dictionary<string, object?> { ["transcript"] = ContextStrategies.Transcript(older), ["messageCount"] = (long)older.Count },
            token).ConfigureAwait(false);
        Instance child = result.Instance;
        child.SpawnerInstanceId = Instance.InstanceId;
        child.CallerInstanceId = Instance.InstanceId;
        await Runtime.Store.SaveAsync(child, token).ConfigureAwait(false);
        Host.LinkChild(Instance.InstanceId, child.InstanceId, result.Cost.Total);
        return (result.Text.Trim(), result.Cost.Total);
    }

    private static string StrategyName(ContextStrategyKind kind) => kind switch
    {
        ContextStrategyKind.ClearToolResults => "clearToolResults",
        ContextStrategyKind.Compact => "compact",
        _ => "providerNative",
    };
}

/// <summary>The shipped definition that summarizes older turns. Replace it with <see cref="Runtime.CompactionDefinition"/>.</summary>
internal static class CompactionFlow
{
    public static AgentDefinition Load()
    {
        using Stream stream = typeof(CompactionFlow).Assembly.GetManifestResourceStream("OneLlm.Agents.compact.flow")
            ?? throw new InvalidOperationException("The compaction definition is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return AgentDefinition.Parse(reader.ReadToEnd(), "compact.flow", DefinitionLoadOptions.Lenient);
    }
}
