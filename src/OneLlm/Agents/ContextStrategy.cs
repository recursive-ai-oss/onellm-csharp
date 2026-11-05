using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Agents;

/// <summary>How an agent keeps its context from outgrowing the model's window.</summary>
public enum ContextStrategyKind
{
    /// <summary>The conversation is sent whole. The default.</summary>
    None = 0,

    /// <summary>Old tool results are replaced by a short note; the calls stay, so every call keeps its result.</summary>
    ClearToolResults = 1,

    /// <summary>Older turns are summarized by a model call (the compaction definition) and replaced by the summary.</summary>
    Compact = 2,

    /// <summary>The provider compacts the conversation itself, when it implements <see cref="IProviderCompaction"/>.</summary>
    ProviderNative = 3,
}

/// <summary>
/// An opt-in context strategy. Set it in a definition as <c>contextStrategy:</c> (<c>type</c>, <c>triggerTokens</c> or
/// <c>triggerPercent</c>, <c>keepLast</c>) or in <see cref="AgentStartOptions.ContextStrategy"/>. It runs at a turn boundary, when the
/// last model call's context passed the trigger, and each run is recorded on the instance with what it replaced and what it cost.
/// </summary>
public sealed record ContextStrategyOptions
{
    /// <summary>No strategy.</summary>
    public static ContextStrategyOptions None { get; } = new();

    /// <summary>Which strategy.</summary>
    public ContextStrategyKind Kind { get; init; }

    /// <summary>Run when the last call's context reaches this many tokens. Null uses <see cref="TriggerPercent"/> of the model's window.</summary>
    public long? TriggerTokens { get; init; }

    /// <summary>Run when the context reaches this percentage of the model's window (1 to 100). Used when <see cref="TriggerTokens"/> is null. Default 70.</summary>
    public int TriggerPercent { get; init; } = 70;

    /// <summary>
    /// For <see cref="ContextStrategyKind.ClearToolResults"/>: how many of the latest tool-calling turns keep their results (default 3).
    /// For <see cref="ContextStrategyKind.Compact"/>: how many of the latest messages stay as they are (default 6). Zero means the default.
    /// </summary>
    public int KeepLast { get; init; }

    internal int KeepOrDefault => KeepLast > 0 ? KeepLast : Kind == ContextStrategyKind.ClearToolResults ? 3 : 6;

    /// <summary>Reads a definition's <c>contextStrategy</c> key.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The options, or <see cref="None"/> when the key is absent or names an unknown strategy.</returns>
    public static ContextStrategyOptions FromDefinition(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.ExtensionData is not { } extra || !extra.TryGetValue("contextStrategy", out object? value))
        {
            return None;
        }

        if (value is string name)
        {
            return KindOf(name) is { } k ? new ContextStrategyOptions { Kind = k } : None;
        }

        if (value is not IDictionary<string, object?> map || !map.TryGetValue("type", out object? type) || KindOf(type as string) is not { } kind)
        {
            return None;
        }

        return new ContextStrategyOptions
        {
            Kind = kind,
            TriggerTokens = map.TryGetValue("triggerTokens", out object? t) ? AsLong(t) : null,
            TriggerPercent = map.TryGetValue("triggerPercent", out object? p) && AsLong(p) is { } pc and > 0 and <= 100 ? (int)pc : 70,
            KeepLast = map.TryGetValue("keepLast", out object? keep) && AsLong(keep) is { } kl and > 0 and < int.MaxValue ? (int)kl : 0,
        };
    }

    private static ContextStrategyKind? KindOf(string? name) => name switch
    {
        "clearToolResults" => ContextStrategyKind.ClearToolResults,
        "compact" => ContextStrategyKind.Compact,
        "providerNative" => ContextStrategyKind.ProviderNative,
        _ => null,
    };

    private static long? AsLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        double d when d == Math.Floor(d) => (long)d,
        decimal m when m == Math.Floor(m) => (long)m,
        string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) => parsed,
        _ => null,
    };
}

/// <summary>A conversation after a provider compacted it.</summary>
public sealed class ProviderCompactionResult
{
    /// <summary>The messages that replace the whole conversation so far.</summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>What the compaction used, when the provider reported it.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>
/// Implemented by a provider that can compact a conversation itself (the <see cref="ContextStrategyKind.ProviderNative"/> strategy).
/// No built-in provider implements it yet; a provider that does not is skipped with a note in the run's warnings.
/// </summary>
public interface IProviderCompaction : IProvider
{
    /// <summary>Compacts the conversation in a request.</summary>
    /// <param name="request">The request that would be sent next, with the full history.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The replacement messages, or null when the provider decides there is nothing to compact.</returns>
    Task<ProviderCompactionResult?> CompactAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Copies of immutable messages with one part changed.</summary>
internal static class MessageCopy
{
    public static Message WithToolCalls(Message m, IReadOnlyList<ToolCall> calls) => new()
    {
        Id = m.Id,
        User = m.User,
        Assistant = m.Assistant,
        ToolCalls = calls,
        DateTime = m.DateTime,
        Source = m.Source,
        Cache = m.Cache,
        Usage = m.Usage,
        When = m.When,
        ExtensionData = m.ExtensionData,
    };

    public static Message WithAnswer(Message answer, Message stamped) => new()
    {
        Id = stamped.Id,
        User = answer.User,
        Assistant = answer.Assistant,
        ToolCalls = answer.ToolCalls,
        DateTime = stamped.DateTime,
        Source = answer.Source,
        Cache = answer.Cache,
        Usage = stamped.Usage,
        When = answer.When,
        ExtensionData = answer.ExtensionData,
    };

    public static ToolCall WithResult(ToolCall c, IReadOnlyList<Part> result, bool isError, decimal cost, IReadOnlyList<CostLine>? lines) => new(c.Id, c.Tool)
    {
        Routine = c.Routine,
        Inputs = c.Inputs,
        InvalidInputs = c.InvalidInputs,
        Result = result,
        IsError = isError,
        Cost = cost,
        CostLines = lines is { Count: > 0 } ? lines : null,
        When = c.When,
        Injected = c.Injected,
        InjectedBy = c.InjectedBy,
        After = c.After,
        ExtensionData = c.ExtensionData,
    };

    public static ToolCall WithInputs(ToolCall c, System.Text.Json.Nodes.JsonObject inputs) => new(c.Id, c.Tool)
    {
        Routine = c.Routine,
        Inputs = inputs,
        Result = c.Result,
        IsError = c.IsError,
        Cost = c.Cost,
        CostLines = c.CostLines,
        When = c.When,
        Injected = c.Injected,
        InjectedBy = c.InjectedBy,
        After = c.After,
        ExtensionData = c.ExtensionData,
    };
}

/// <summary>The two strategies that rewrite history without a model call, and the text the model-based one summarizes.</summary>
internal static class ContextStrategies
{
    public const string ClearedNote = "[tool result cleared to save context]";

    /// <summary>
    /// Replaces the results of tool calls in all but the latest <paramref name="keepLast"/> tool-calling turns with a short note. Calls
    /// keep a result, so no call is orphaned; messages before the first change are untouched, so the cached prefix of a request is
    /// stable up to the first cleared result, and clearing again changes nothing.
    /// </summary>
    public static List<string> ClearToolResults(Instance instance, int keepLast, ISet<string> skipCallIds, bool dryRun = false)
    {
        var turns = new List<int>();
        for (int i = 0; i < instance.Messages.Count; i++)
        {
            if (instance.Messages[i].ToolCalls.Count > 0)
            {
                turns.Add(i);
            }
        }

        var replaced = new List<string>();
        for (int t = 0; t < turns.Count - keepLast; t++)
        {
            Message m = instance.Messages[turns[t]];
            bool changed = false;
            var calls = new List<ToolCall>(m.ToolCalls.Count);
            foreach (ToolCall c in m.ToolCalls)
            {
                if (skipCallIds.Contains(c.Id) || c.Result.Count == 0 || IsCleared(c))
                {
                    calls.Add(c);
                    continue;
                }

                changed = true;
                calls.Add(MessageCopy.WithResult(c, [new TextPart(ClearedNote)], c.IsError, c.Cost, c.CostLines));
            }

            if (changed)
            {
                if (!dryRun)
                {
                    instance.Messages[turns[t]] = MessageCopy.WithToolCalls(m, calls);
                }

                replaced.Add(m.Id);
            }
        }

        return replaced;
    }

    private static bool IsCleared(ToolCall c) => c.Result.Count == 1 && c.Result[0] is TextPart { Text: ClearedNote };

    /// <summary>
    /// The index of the first message to keep when summarizing everything before it: a user message at least <paramref name="keepLast"/>
    /// messages from the end, so no turn is split (a tool call and its result live in one message, so any cut between messages keeps
    /// pairs whole). Returns 0 when there is nothing to summarize.
    /// </summary>
    public static int CompactCut(Instance instance, int keepLast, ISet<string> skipMessageIds)
    {
        for (int c = instance.Messages.Count - keepLast; c > 0; c--)
        {
            Message m = instance.Messages[c];
            if (m.User.Count > 0 && m.ToolCalls.Count == 0 && !instance.Messages.Take(c).Any(x => skipMessageIds.Contains(x.Id)))
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>The transcript the compaction definition summarizes: one block per message, long tool results cut.</summary>
    public static string Transcript(IReadOnlyList<Message> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Message m in messages)
        {
            if (m.User.Count > 0)
            {
                sb.Append("[user] ").Append(m.GetUserText()).Append('\n');
            }

            string text = m.GetAssistantText();
            if (text.Length > 0)
            {
                sb.Append("[assistant] ").Append(text).Append('\n');
            }

            foreach (ToolCall c in m.ToolCalls)
            {
                sb.Append("[tool call ").Append(c.Tool).Append("] ").Append(c.Inputs?.ToJsonString() ?? "{}").Append('\n');
                string result = string.Concat(c.Result.OfType<TextPart>().Select(static p => p.Text));
                if (result.Length > 2000)
                {
                    result = result[..2000] + "…";
                }

                sb.Append("[tool result").Append(c.IsError ? " (error)" : "").Append("] ").Append(result).Append('\n');
            }
        }

        return sb.ToString();
    }
}
