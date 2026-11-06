using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.Agents;

internal sealed class MonitorAgentTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "monitor_agent",
        "Watch an agent and get a message when something happens to it, or just look at its status.\n\nEvery condition is optional. You can set several: they are OR-combined, the FIRST one to hit fires the monitor, and the whole monitor is then disarmed. To keep watching, call `monitor_agent` again. The alert is a message to you (it interrupts an `await_agent` / `await_monitor` you are blocked in, and restarts you if you have stopped); or call `await_monitor` with the returned monitor id to block until it fires. Elapsed time never kills anything — it only tells you something has run longer than expected so you can look.\n\nConditions:\n- `contextAt`: context window reaches this size — a token count (400000, 400k, 0.4m) or a percentage of the model's window (40%).\n- `turns`: N inference turns from now.\n- `onFinish`: the agent finishes (stopped, error, or waiting for input).\n- `elapsed`: this much time passes (30m, 2h, 1d). Use it for hang detection.\n\nIf a condition is already met when you arm the monitor, it fires immediately. Arming again on the same agent replaces your earlier monitor and returns a new id.\n\nReturns a monitor id and a status snapshot: context tokens / window / %, total inference turns, runtime since the current run started, state with the current tool and how long it has been running, and spend including all descendant agents. With NO conditions it returns only the snapshot and arms nothing.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "instanceId":{"type":"string","description":"Which agent to watch. Pass \"me\" to watch yourself, or an agent instance ID."},
              "contextAt":{"type":"string","description":"Optional. Fire when the context window reaches this size: a token count (400000, 400,000, 400k, 0.4m) or a percentage of the model's window (40%)."},
              "turns":{"type":"integer","description":"Optional. Fire after this many more inference turns (counted from now)."},
              "onFinish":{"type":"boolean","description":"Optional. Fire when the agent finishes: its state becomes stopped, error or waiting."},
              "elapsed":{"type":"string","description":"Optional. Fire when this much time has passed, e.g. 30m, 2h, 1d."}},
             "required":["instanceId"]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        AgentHost? host = AgentTools.HostOf(context);
        if (host is null)
        {
            return AgentTools.NoRuntime();
        }

        string requested = ToolArgs.String(inputs, "instanceId") ?? "";
        bool self = requested.Length == 0 || requested.Equals("me", StringComparison.OrdinalIgnoreCase);
        string targetId = self ? context.InstanceId : requested;
        if (targetId == context.InstanceId)
        {
            self = true;
        }

        ContextThreshold? threshold = null;
        long turns = 0;
        TimeSpan? elapsed = null;
        try
        {
            if (ToolArgs.String(inputs, "contextAt") is { Length: > 0 } c)
            {
                threshold = ContextThreshold.Parse(c);
            }
        }
        catch (ArgumentException ex)
        {
            return AgentTools.Err("contextAt: " + ex.Message);
        }

        if (inputs["turns"] is not null)
        {
            if (ToolArgs.Int(inputs, "turns") is not { } t || t < 0)
            {
                return AgentTools.Err("turns: must be a whole number of turns");
            }

            turns = t;
        }

        try
        {
            if (ToolArgs.String(inputs, "elapsed") is { Length: > 0 } e)
            {
                elapsed = MonitorDurations.Parse(e);
            }
        }
        catch (ArgumentException ex)
        {
            return AgentTools.Err("elapsed: " + ex.Message);
        }

        bool onFinish = ToolArgs.Bool(inputs, "onFinish") ?? false;
        if (!await AgentTools.MayReachAsync(context, targetId, cancellationToken).ConfigureAwait(false))
        {
            return AgentTools.NotYours(targetId);
        }

        LiveAgent? target = await host.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return AgentTools.Err($"no agent instance '{targetId}'");
        }

        bool hasConditions = threshold is not null || turns > 0 || onFinish || elapsed is not null;
        if (!hasConditions)
        {
            AgentStatus status = target.Snapshot();
            var sb = new StringBuilder("No conditions given, so nothing was armed.\n\n");
            sb.Append(StatusLines(status, self));
            List<AgentMonitor> armed = [.. host.ArmedOn(targetId, context.InstanceId)];
            if (armed.Count > 0)
            {
                sb.Append("\nYour armed monitor(s) on this agent:\n");
                foreach (AgentMonitor m in armed)
                {
                    sb.Append("- `").Append(m.Id).Append("`: fires when ").Append(Describe(m)).Append('\n');
                }
            }

            return ToolResult.FromText(sb.ToString().TrimEnd('\n'));
        }

        AgentMonitor monitor = host.Arm(context.InstanceId, target, threshold, (int)Math.Min(turns, int.MaxValue), onFinish, elapsed);
        AgentStatus snapshot = monitor.Fired?.Snapshot ?? target.Snapshot();
        var text = new StringBuilder();
        if (monitor.Fired is { } fired)
        {
            text.Append(CultureInfo.InvariantCulture, $"Monitor `{monitor.Id}` fired immediately: {fired.Detail}. The monitor is disarmed; call `monitor_agent` again to re-arm.\n\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"Monitor `{monitor.Id}` armed. You will be messaged once when {Describe(monitor)}. The first condition to hit fires the monitor and disarms it; call `monitor_agent` again to keep watching. To block until it fires, call `await_monitor` with this id.\n\n");
        }

        text.Append(StatusLines(snapshot, self));
        return ToolResult.FromText(text.ToString().TrimEnd('\n'));
    }

    internal static string Describe(AgentMonitor m)
    {
        var parts = new List<string>();
        if (m.Context is { } c)
        {
            parts.Add("context reaches " + c.Spec);
        }

        if (m.TurnsAt > 0)
        {
            parts.Add($"it reaches turn {m.TurnsAt.ToString(CultureInfo.InvariantCulture)}");
        }

        if (m.OnFinish)
        {
            parts.Add("it finishes (stopped, error or waiting)");
        }

        if (m.Deadline is { } deadline && m.Elapsed is { } e)
        {
            parts.Add($"{AgentHost.FormatDuration((long)e.TotalSeconds)} elapse (at {deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)})");
        }

        return string.Join("; or ", parts);
    }

    internal static string StatusLines(AgentStatus st, bool self)
    {
        string subject = st.Name.Length > 0 ? $"{st.Name} (`{st.Id}`)" : $"agent `{st.Id}`";
        if (self)
        {
            subject = $"you ({subject})";
        }

        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"Status of {subject}:\n");
        string state = st.State;
        if (st.CurrentTool is not null)
        {
            state += $" — current tool `{st.CurrentTool}`, running {AgentHost.FormatDuration(st.CurrentToolSeconds)}";
        }

        b.Append("- State: ").Append(state).Append('\n');
        if (st.ContextWindow > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"- Context: {AgentTools.Commas(st.ContextTokens)}/{AgentTools.Commas(st.ContextWindow)} tokens ({TrimFloat(st.ContextPercent)}%)\n");
        }
        else
        {
            b.Append(CultureInfo.InvariantCulture, $"- Context: {AgentTools.Commas(st.ContextTokens)} tokens (window size unknown)\n");
        }

        b.Append(CultureInfo.InvariantCulture, $"- Turns: {st.Turns} inference turns\n");
        if (st.RuntimeSeconds is { } secs)
        {
            b.Append("- Runtime: ").Append(AgentHost.FormatDuration(secs)).Append(" since the current run started\n");
        }
        else if (st.RunStartedAt is { } startedAt)
        {
            b.Append("- Current run started: ").Append(startedAt).Append(" (not running now)\n");
        }

        b.Append(CultureInfo.InvariantCulture, $"- Spend: {AgentTools.Usd2(st.Total)} including descendants ({AgentTools.Usd2(st.Own)} own + {AgentTools.Usd2(st.Descendants)} across {st.DescendantCount} descendant agent(s))\n");
        return b.ToString();
    }

    private static string TrimFloat(double f)
    {
        string s = f.ToString("F1", CultureInfo.InvariantCulture);
        return s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s;
    }
}

internal sealed class AwaitMonitorTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "await_monitor",
        "Wait for one or more monitors (armed with `monitor_agent`) to fire, and return the condition that hit and the agent's status snapshot for each.\n\n`ids` is an array of monitor ids (a single id string is also accepted). `mode` is `one` (the default) or `all`:\n- `one` — returns as soon as at least one monitor has fired, with every monitor that has fired at that moment; the rest are listed as still armed.\n- `all` — waits until every listed monitor has fired.\n\nIf a monitor fired because the agent finished (`onFinish`), the agent's result is appended, exactly as `await_agent` would return it — one call covers the common case. A monitor that already fired returns immediately. A monitor that is blocked-on here does not also send you the usual alert message.\n\nAny message that arrives for you while you are waiting interrupts the wait; the monitors stay armed and you can call `await_monitor` again. Elapsed time never kills the agent.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "ids":{"type":"array","description":"Monitor ids returned by monitor_agent (or a single id string).","items":{"type":"string"}},
              "mode":{"type":"string","enum":["one","all"],"description":"`one` (default): return as soon as at least one monitor has fired, with every monitor fired at that moment. `all`: wait for every monitor."}},
             "required":["ids"]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        AgentHost? host = AgentTools.HostOf(context);
        if (host is null)
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
            return AgentTools.Err("ids is required: a monitor id (from monitor_agent), or an array of them");
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

        // A monitor is its watcher's: another agent's monitor ids read as unknown.
        var monitors = ids.Select(id => (Id: id, Monitor: host.FindMonitor(id) is { } found && (context.Agent is null || found.WatcherId == context.InstanceId || context.Runtime!.AgentsMayReachAnyInstance) ? found : null)).ToList();
        List<AgentMonitor> known = [.. monitors.Where(static m => m.Monitor is not null).Select(static m => m.Monitor!)];
        foreach (AgentMonitor m in known)
        {
            m.BeginWait();
        }

        try
        {
            List<Task> tasks = [.. known.Select(static m => (Task)m.FiredTask.Task)];
            Task interrupt = context.Agent?.NextMessage ?? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (tasks.Count > 0 && !tasks.All(static t => t.IsCompleted))
            {
                Task waitFor = mode == "all" ? Task.WhenAll(tasks) : Task.WhenAny(tasks);
                await Task.WhenAny(waitFor, interrupt).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (AgentMonitor m in known)
            {
                m.EndWait();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var b = new StringBuilder("## Monitor result");
        if (ids.Count > 1)
        {
            b.Append('s');
        }

        b.Append("\n\n");
        List<(string Id, AgentMonitor? Monitor)> fired = [.. monitors.Where(static m => m.Monitor?.Fired is not null || m.Monitor is null)];
        if (ids.Count > 1)
        {
            b.Append(CultureInfo.InvariantCulture, $"**Fired:** {fired.Count} of {ids.Count}\n\n");
        }

        foreach ((string id, AgentMonitor? monitor) in fired)
        {
            if (monitor is null)
            {
                b.Append(CultureInfo.InvariantCulture, $"### `{id}`\n\n**Error:** there is no monitor with that id; it may have been replaced by arming again.\n\n");
                continue;
            }

            MonitorFire f = monitor.Fired!;
            AgentStatus st = f.Snapshot;
            // The watcher is reading the answer here, so the alert queued for it would only repeat this.
            await host.RetractPushAsync(monitor, cancellationToken).ConfigureAwait(false);
            string name = st.Name.Length > 0 ? $"{st.Name} (`{st.Id}`)" : st.Id;
            b.Append(CultureInfo.InvariantCulture, $"### `{monitor.Id}` — {name}\n\n**Condition:** {f.Condition} — {f.Detail}\n\n");
            b.Append("Status when it fired:\n- State: ").Append(st.State);
            if (st.CurrentTool is not null)
            {
                b.Append(" — current tool `").Append(st.CurrentTool).Append("`, running ").Append(AgentHost.FormatDuration(st.CurrentToolSeconds));
            }

            b.Append('\n');
            if (st.ContextWindow > 0)
            {
                b.Append(CultureInfo.InvariantCulture, $"- Context: {st.ContextTokens}/{st.ContextWindow} tokens ({st.ContextPercent.ToString("F1", CultureInfo.InvariantCulture)}%)\n");
            }
            else
            {
                b.Append(CultureInfo.InvariantCulture, $"- Context: {st.ContextTokens} tokens\n");
            }

            b.Append(CultureInfo.InvariantCulture, $"- Turns: {st.Turns}\n");
            if (st.RuntimeSeconds is { } secs)
            {
                b.Append("- Runtime: ").Append(AgentHost.FormatDuration(secs)).Append('\n');
            }

            b.Append("- Spend: ").Append(AgentTools.Usd2(st.Total)).Append(" including descendants\n");
            b.Append("\nThe monitor is disarmed; call `monitor_agent` to re-arm.\n\n");
            if (f.Condition == "onFinish" && await host.GetAsync(st.Id, cancellationToken).ConfigureAwait(false) is { } agent)
            {
                // One call covers the common case: the finished agent's result, as await_agent would give it.
                string result = await AwaitCore.RunAsync(
                    new ToolContext(context.InstanceId, context.ToolCallId, context.TimeProvider) { Runtime = context.Runtime, Capabilities = context.Capabilities },
                    [agent.Instance.InstanceId], all: true, cancellationToken).ConfigureAwait(false);
                b.Append(result.Trim()).Append("\n\n");
            }
        }

        List<(string Id, AgentMonitor? Monitor)> still = [.. monitors.Where(static m => m.Monitor is { Fired: null })];
        if (still.Count > 0)
        {
            b.Append("### Still armed\n\n");
            foreach ((string id, _) in still)
            {
                b.Append("- `").Append(id).Append("`\n");
            }

            b.Append("\nCall `await_monitor` again with the ids you still need.\n");
        }

        return ToolResult.FromText(b.ToString().TrimEnd('\n'));
    }
}

internal sealed class QueryAgentTool : ITool
{
    public ToolSpec Definition { get; } = new(
        "query_agent",
        "Search and read another agent instance's record (running or finished) without loading the whole history into your context. Reads the persisted instance.\n\n- `fields`: top-level instance properties to return (e.g. `state`, `model`, `requestedModel`, `swarmUri`, `inputs`, `invocationMode`, `callerInstanceId`, `metadata`). With `fields` and no message filter, only those properties come back. Unknown names are listed in `unknownFields`.\n- Message filters (AND-ed): `role`, `sourceKind` (user messages by origin), `toolName`, `contains` (case-insensitive), `regex`, `fromMessageId` / `toMessageId` (inclusive), `lastN` (keep the last N matches).\n- Tool calls match as their own results with `toolCall{name, inputs, result}`; `contains`/`regex` search the tool name, inputs and result.\n- Limits: `maxResults` (default 20, first ones in conversation order; `totalMatches` is the full count) and `maxResultChars` per match (default 2000; `truncated` flags a cut).\n\nReturns `{properties?, unknownFields?, matches:[{messageId, index, role, dateTime, source?, text | toolCall, truncated}], totalMatches, truncated}`.",
        AgentTools.Schema("""
            {"type":"object","properties":{
              "instanceId":{"type":"string","description":"The agent instance ID to query."},
              "fields":{"type":"array","items":{"type":"string"},"description":"Top-level instance properties to return, e.g. [\"state\",\"model\",\"requestedModel\"]."},
              "role":{"type":"string","enum":["user","assistant","tool"],"description":"Only messages of this role (tool = tool calls)."},
              "sourceKind":{"type":"string","enum":["user","agent","runtime","api"],"description":"Only user messages whose source.kind is this."},
              "toolName":{"type":"string","description":"Only tool calls to this tool, e.g. run_agent_async."},
              "contains":{"type":"string","description":"Case-insensitive substring to look for in message text, or in a tool call's name, inputs and result."},
              "regex":{"type":"string","description":"Regular expression to match against the same text as contains."},
              "fromMessageId":{"type":"string","description":"Start at this message id (inclusive)."},
              "toMessageId":{"type":"string","description":"End at this message id (inclusive)."},
              "lastN":{"type":"integer","description":"Keep only the last N matches."},
              "maxResults":{"type":"integer","description":"Maximum matches to return (default 20)."},
              "maxResultChars":{"type":"integer","description":"Maximum characters of text/result per match (default 2000)."}},
             "required":["instanceId"]}
            """));

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        Runtime? rt = context.Runtime;
        if (rt is null)
        {
            return AgentTools.NoRuntime();
        }

        string id = ToolArgs.String(inputs, "instanceId") ?? "";
        if (id.Length == 0)
        {
            return AgentTools.Err("instanceId is required");
        }

        if (!await AgentTools.MayReachAsync(context, id, cancellationToken).ConfigureAwait(false))
        {
            return AgentTools.NotYours(id);
        }

        Instance? live = (await rt.AgentHost.GetAsync(id, cancellationToken).ConfigureAwait(false))?.Instance;
        Instance? instance = live ?? await rt.Store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return AgentTools.Err("no instance found for " + id);
        }

        Regex? regex = null;
        if (ToolArgs.String(inputs, "regex") is { Length: > 0 } pattern)
        {
            try
            {
                // A model-written pattern must not be able to stall the run.
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return AgentTools.Err("invalid regex: " + ex.Message);
            }
        }

        string? role = ToolArgs.String(inputs, "role");
        string? sourceKind = ToolArgs.String(inputs, "sourceKind");
        string? toolName = ToolArgs.String(inputs, "toolName");
        string? contains = ToolArgs.String(inputs, "contains");
        string? from = ToolArgs.String(inputs, "fromMessageId");
        string? to = ToolArgs.String(inputs, "toMessageId");
        long lastN = ToolArgs.Int(inputs, "lastN") ?? 0;
        long maxResults = ToolArgs.Int(inputs, "maxResults") is { } mr and > 0 ? mr : 20;
        int maxChars = ToolArgs.Int(inputs, "maxResultChars") is { } mc and > 0 ? (int)Math.Min(mc, 1_000_000) : 2000;
        var fields = new List<string>();
        if (inputs["fields"] is JsonArray fa)
        {
            fields.AddRange(fa.OfType<JsonValue>().Select(static v => v.TryGetValue(out string? s) ? s : "").Where(static s => s.Length > 0));
        }

        var output = new JsonObject();
        bool anyFilter = role is not null || sourceKind is not null || toolName is not null || contains is not null || regex is not null || from is not null || to is not null || lastN > 0;
        if (fields.Count > 0)
        {
            var props = new JsonObject();
            var unknown = new JsonArray();
            foreach (string f in fields)
            {
                if (Property(instance, f) is { } value)
                {
                    props[f] = value;
                }
                else
                {
                    unknown.Add((JsonNode?)JsonValue.Create(f));
                }
            }

            output["properties"] = props;
            if (unknown.Count > 0)
            {
                output["unknownFields"] = unknown;
            }

            if (!anyFilter)
            {
                output["matches"] = new JsonArray();
                output["totalMatches"] = 0;
                output["truncated"] = false;
                return ToolResult.FromJson(output);
            }
        }

        var matches = new List<JsonObject>();
        bool inRange = from is null;
        for (int i = 0; i < instance.Messages.Count; i++)
        {
            Message m = instance.Messages[i];
            if (!inRange && m.Id == from)
            {
                inRange = true;
            }

            if (!inRange)
            {
                continue;
            }

            if (m.User.Count > 0 && (role is null or "user") && (sourceKind is null || m.Source?.Kind == sourceKind) && toolName is null)
            {
                Add(matches, m, i, "user", m.GetUserText(), null, contains, regex);
            }

            if (sourceKind is null && toolName is null)
            {
                string text = m.GetAssistantText();
                if (text.Length > 0 && (role is null or "assistant"))
                {
                    Add(matches, m, i, "assistant", text, null, contains, regex);
                }
            }

            if (sourceKind is null && (role is null or "tool"))
            {
                foreach (ToolCall c in m.ToolCalls)
                {
                    if (toolName is not null && c.Tool != toolName)
                    {
                        continue;
                    }

                    string result = string.Concat(c.Result.OfType<TextPart>().Select(static p => p.Text));
                    Add(matches, m, i, "tool", null, new JsonObject { ["id"] = c.Id, ["name"] = c.Tool, ["inputs"] = c.Inputs?.DeepClone(), ["result"] = result }, contains, regex);
                }
            }

            if (to is not null && m.Id == to)
            {
                break;
            }
        }

        int total = matches.Count;
        if (lastN > 0 && matches.Count > lastN)
        {
            matches = matches[^(int)lastN..];
        }

        bool truncated = matches.Count > maxResults;
        if (truncated)
        {
            matches = matches[..(int)maxResults];
        }

        foreach (JsonObject match in matches)
        {
            bool cut = false;
            if (match["text"] is JsonValue tv && tv.TryGetValue(out string? t) && t.Length > maxChars)
            {
                match["text"] = t[..maxChars];
                cut = true;
            }

            if (match["toolCall"] is JsonObject tc && tc["result"] is JsonValue rv && rv.TryGetValue(out string? r) && r.Length > maxChars)
            {
                tc["result"] = r[..maxChars];
                cut = true;
            }

            match["truncated"] = cut;
            truncated |= cut;
        }

        output["matches"] = new JsonArray(matches.Cast<JsonNode?>().ToArray());
        output["totalMatches"] = total;
        output["truncated"] = truncated;
        return ToolResult.FromJson(output);
    }

    private static void Add(List<JsonObject> into, Message m, int index, string role, string? text, JsonObject? call, string? contains, Regex? regex)
    {
        string haystack = text ?? string.Join(' ', call!["name"]!.ToString(), call["inputs"]?.ToJsonString() ?? "", call["result"]!.ToString());
        if (contains is not null && haystack.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        try
        {
            if (regex is not null && !regex.IsMatch(haystack))
            {
                return;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        var item = new JsonObject { ["messageId"] = m.Id, ["index"] = index, ["role"] = role };
        if (m.DateTime is { } dt)
        {
            item["dateTime"] = dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
        }

        if (m.Source is { } src)
        {
            item["source"] = new JsonObject { ["kind"] = src.Kind };
        }

        if (text is not null)
        {
            item["text"] = text;
        }
        else
        {
            item["toolCall"] = call;
        }

        into.Add(item);
    }

    // The properties a model may ask for. Anything else is reported as unknown, so a typo is visible.
    private static JsonNode? Property(Instance i, string name) => name switch
    {
        "state" => i.State,
        "model" => i.Model,
        "requestedModel" => i.RequestedModel,
        "name" => i.Name,
        "invocationMode" => i.InvocationMode,
        "callerInstanceId" => i.CallerInstanceId,
        "spawnerInstanceId" => i.SpawnerInstanceId,
        "callerToolCallId" => i.CallerToolCallId,
        "delegationPrompt" => i.DelegationPrompt,
        "runStartedAt" => i.RunStartedAt,
        "instanceId" => i.InstanceId,
        "swarmUri" => i.ExtensionData is { } e && e.TryGetValue("swarmUri", out object? v) ? v as string : null,
        "inputs" => i.Inputs is null ? null : ToNode(i.Inputs),
        "metadata" => new JsonObject { ["renderedSystemPrompt"] = i.RenderedSystemPrompt },
        _ => null,
    };

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int n => JsonValue.Create(n),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create(m),
        IDictionary<string, object?> map => new JsonObject(map.Select(p => KeyValuePair.Create(p.Key, ToNode(p.Value)))),
        IEnumerable<object?> list => new JsonArray(list.Select(ToNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
