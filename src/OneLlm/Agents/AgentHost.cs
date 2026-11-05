using System.Collections.Concurrent;
using System.Globalization;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Runners;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Agents;

/// <summary>
/// The agents of one <see cref="Runtime"/>: starts them, finds them again by id (live, or loaded from the store), links children to
/// parents for cost roll-up, and keeps the monitors. One host per runtime.
/// </summary>
internal sealed class AgentHost
{
    private readonly Runtime _runtime;
    private readonly ConcurrentDictionary<string, LiveAgent> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _parentOf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, decimal> _lastCost = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentMonitor> _monitors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<LiveAgent?>>> _loading = new(StringComparer.Ordinal);

    public AgentHost(Runtime runtime) => _runtime = runtime;

    // ---- creating and finding agents ----------------------------------------------------------------------------------------

    public async Task<LiveAgent> StartAsync(AgentDefinition definition, IReadOnlyDictionary<string, object?>? inputs, AgentStartOptions options, bool lenient, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Model))
        {
            throw new BadDefinitionException("The definition names no model.", definition.SourceName);
        }

        LiveAgent? parent = options.SpawnerInstanceId is { } sid && _live.TryGetValue(sid, out LiveAgent? p) ? p : null;
        ICapabilities ceiling = _runtime.Capabilities;
        if (parent is not null)
        {
            ceiling = CapabilityIntersection.Of(ceiling, parent.Setup.Capabilities);
        }

        ICapabilities caps = options.Capabilities is null ? ceiling : CapabilityIntersection.Of(options.Capabilities, ceiling);

        Dictionary<string, object?> merged = definition.Inputs is null ? [] : PlainValues.NormalizeInputs(definition.Inputs);
        foreach ((string key, object? value) in PlainValues.NormalizeInputs(inputs))
        {
            merged[key] = value;
        }

        Instance instance = Instance.FromDefinition(definition, _runtime.NewId("i"));
        instance.Inputs = merged;
        if (options.WorkingDirectory is { } wd)
        {
            instance.WorkingDirectory = wd;
        }

        if (options.SpawnerInstanceId is { } spawner)
        {
            instance.SpawnerInstanceId = spawner;
            instance.CallerInstanceId = spawner;
            instance.CallerToolCallId = options.CallerToolCallId;
            instance.DelegationPrompt = options.DelegationPrompt ?? options.Prompt;
        }

        if (options.SwarmUri is { } swarmUri)
        {
            (instance.ExtensionData ??= [])["swarmUri"] = swarmUri;
        }

        AgentSetup setup = BuildSetup(instance, options, caps, lenient);
        setup.Warnings.InsertRange(0, definition.Warnings);
        var agent = new LiveAgent(_runtime, this, instance, setup) { Options = options };
        _live[instance.InstanceId] = agent;
        if (options.SpawnerInstanceId is { } s)
        {
            LinkChild(s, instance.InstanceId, null);
        }

        agent.StartSegment(initial: true);
        return agent;
    }

    /// <summary>Finds a live agent, or loads a saved instance into one that is resting. Pending approvals keep their state.</summary>
    public async Task<LiveAgent?> GetAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_live.TryGetValue(instanceId, out LiveAgent? live))
        {
            return live;
        }

        // Concurrent callers share one load, so an instance never gets two handles.
        Lazy<Task<LiveAgent?>> load = _loading.GetOrAdd(instanceId, id => new Lazy<Task<LiveAgent?>>(() => LoadAsync(id)));
        try
        {
            return await load.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (load.Value.IsCompleted)
            {
                _loading.TryRemove(instanceId, out _);
            }
        }
    }

    private async Task<LiveAgent?> LoadAsync(string instanceId)
    {
        if (_live.TryGetValue(instanceId, out LiveAgent? live))
        {
            return live;
        }

        Instance? instance = await _runtime.Store.LoadAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        LiveAgent? parent = instance.SpawnerInstanceId is { } sid && _live.TryGetValue(sid, out LiveAgent? p) ? p : null;
        ICapabilities caps = parent is null ? _runtime.Capabilities : CapabilityIntersection.Of(_runtime.Capabilities, parent.Setup.Capabilities);
        AgentSetup setup = BuildSetup(instance, new AgentStartOptions(), caps, lenient: true);
        var agent = new LiveAgent(_runtime, this, instance, setup);
        _live[instanceId] = agent;
        if (instance.SpawnerInstanceId is { } spawner)
        {
            LinkChild(spawner, instanceId, null);
        }

        await ReindexChildrenAsync(instance.InstanceId, CancellationToken.None).ConfigureAwait(false);
        return agent;
    }

    private async Task ReindexChildrenAsync(string parentId, CancellationToken cancellationToken)
    {
        await foreach (InstanceSummary s in _runtime.Store.ListAsync(new InstanceQuery { CallerInstanceId = parentId }, cancellationToken).ConfigureAwait(false))
        {
            if (_parentOf.ContainsKey(s.InstanceId))
            {
                continue;
            }

            Instance? child = await _runtime.Store.LoadAsync(s.InstanceId, cancellationToken).ConfigureAwait(false);
            if (child is { SpawnerInstanceId: { } sp } && sp == parentId)
            {
                LinkChild(parentId, child.InstanceId, LiveAgent.OwnCostOf(child));
                await ReindexChildrenAsync(child.InstanceId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private AgentSetup BuildSetup(Instance instance, AgentStartOptions options, ICapabilities caps, bool lenient)
    {
        ResolvedModelPair pair = ResolvedModelPair.Of(_runtime, instance.Model!);
        var warnings = new List<string>();
        // The same resolver the definition loader offers decides what is granted, so a reference the runtime's grants do not cover
        // fails the load (strict) or is dropped with a warning (lenient).
        ToolReferenceResolver resolver = _runtime.Tools.CreateResolver(caps, instance.References);
        var fromRefs = new List<ITool>();
        foreach (ToolReference reference in instance.References)
        {
            ReferenceCheck check = resolver.Check(reference);
            fromRefs.AddRange(check.GrantedTools);
            if (check.IsGranted)
            {
                continue;
            }

            string text = "This runtime does not grant the tool source " + (reference.Routine ?? reference.Swarm ?? "(no source)") + ": " + string.Join("; ", check.Problems) + ".";
            if (!lenient)
            {
                throw new BadDefinitionException(text, instance.DefinitionPath ?? instance.SourceName, reference.Line, reference.Column);
            }

            warnings.Add(text + " It was left out.");
        }

        var tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (ITool tool in fromRefs.Concat(options.Tools))
        {
            tools[tool.Definition.Name] = tool;
        }

        IReadOnlyList<IHook> definitionHooks = [];
        if (instance.Hooks.Count > 0)
        {
            if (_runtime.DefinitionHooks is { } source)
            {
                definitionHooks = source.Load(instance, caps);
            }
            else if (!lenient)
            {
                throw new CapabilityMissingException("Definition hooks", "RecursiveAI.OneLlm.JavaScript, or set Runtime.DefinitionHooks", isPackage: true);
            }
            else
            {
                warnings.Add("The definition lists hooks, but this runtime has no hook source, so they were left out.");
            }
        }

        IHook[] host = _runtime.Hooks.Snapshot;
        var setup = new AgentSetup
        {
            Model = pair.Model,
            Provider = pair.Provider,
            Tools = tools,
            ToolDefinitions = [.. tools.Values.Select(static t => t.Definition.ToProviderDefinition())],
            Hooks = host.Length == 0 && definitionHooks.Count == 0 ? HookSet.Empty : new HookSet(_runtime, host, definitionHooks),
            Capabilities = caps,
            Strategy = options.ContextStrategy ?? ContextStrategyOptions.FromDefinition(instance),
            Settings = new AskRequest
            {
                Model = instance.Model!,
                Temperature = instance.Temperature,
                MaxOutputTokens = instance.MaxTokens,
                ServiceTier = instance.ServiceTier,
                Reasoning = instance.Thinking is { } t ? new ReasoningOptions { Effort = t.Effort, BudgetTokens = t.Budget } : null,
            },
        };
        setup.Warnings.AddRange(warnings);
        return setup;
    }

    private readonly record struct ResolvedModelPair(Catalog.ResolvedModel Model, IChatProvider Provider)
    {
        public static ResolvedModelPair Of(Runtime runtime, string model)
        {
            Catalog.ResolvedModel resolved = runtime.Resolve(model);
            return new ResolvedModelPair(resolved, runtime.GetChatProvider(resolved));
        }
    }

    // ---- parents, children and cost -----------------------------------------------------------------------------------------

    public void LinkChild(string parentId, string childId, decimal? ownCost)
    {
        _parentOf[childId] = parentId;
        if (ownCost is { } c)
        {
            _lastCost[childId] = c;
        }
    }

    /// <summary>The cost of every instance below an instance, and how many there are. Live ones are priced from their current history.</summary>
    public (decimal Total, int Count) DescendantCost(string id)
    {
        decimal total = 0m;
        int count = 0;
        foreach (string child in _parentOf.Where(p => p.Value == id).Select(static p => p.Key))
        {
            count++;
            total += _live.TryGetValue(child, out LiveAgent? live) ? live.OwnCost : _lastCost.GetValueOrDefault(child);
            (decimal t, int c) = DescendantCost(child);
            total += t;
            count += c;
        }

        return (total, count);
    }

    // ---- lifecycle callbacks from agents ------------------------------------------------------------------------------------

    /// <summary>A model turn was added to the history: check the context and turn conditions of monitors on this agent.</summary>
    public void OnTurn(LiveAgent agent)
    {
        if (_monitors.IsEmpty)
        {
            return;
        }

        foreach (AgentMonitor m in _monitors.Values.Where(m => m.TargetId == agent.Instance.InstanceId && m.IsArmed))
        {
            CheckConditions(m, agent, finished: false);
        }
    }

    /// <summary>A segment settled in a resting state (or failed): record its cost and check finish and other conditions.</summary>
    public void OnSettled(LiveAgent agent)
    {
        _lastCost[agent.Instance.InstanceId] = agent.OwnCost;
        if (_monitors.IsEmpty)
        {
            return;
        }

        bool finished = agent.Instance.State is InstanceStates.Stopped or InstanceStates.Error or InstanceStates.Waiting;
        foreach (AgentMonitor m in _monitors.Values.Where(m => m.TargetId == agent.Instance.InstanceId && m.IsArmed))
        {
            CheckConditions(m, agent, finished);
        }
    }

    // ---- monitors -----------------------------------------------------------------------------------------------------------

    public AgentMonitor? FindMonitor(string id) => _monitors.GetValueOrDefault(id);

    public IReadOnlyList<AgentMonitor> ArmedOn(string targetId, string? watcherId) =>
        [.. _monitors.Values.Where(m => m.TargetId == targetId && m.IsArmed && (watcherId is null || m.WatcherId == watcherId))];

    /// <summary>Arms a monitor. It replaces the watcher's earlier monitor on the same target and fires at once if a condition already holds.</summary>
    public AgentMonitor Arm(string? watcherId, LiveAgent target, ContextThreshold? context, int turns, bool onFinish, TimeSpan? elapsed)
    {
        foreach (AgentMonitor old in _monitors.Values.Where(m => m.TargetId == target.Instance.InstanceId && m.WatcherId == watcherId).ToList())
        {
            old.Dispose();
            _monitors.TryRemove(old.Id, out _);
        }

        DateTimeOffset now = _runtime.TimeProvider.GetUtcNow();
        var monitor = new AgentMonitor(
            "mon_" + target.Instance.InstanceId + "_" + _runtime.IdSource(), watcherId, target.Instance.InstanceId, context, turns > 0 ? target.TurnCount + turns : 0, onFinish, elapsed, now);
        _monitors[monitor.Id] = monitor;
        if (elapsed is { } span)
        {
            monitor.Timer = _runtime.TimeProvider.CreateTimer(_ => FireFromTimer(monitor, target), null, span, Timeout.InfiniteTimeSpan);
        }

        bool finished = target.Instance.State is InstanceStates.Stopped or InstanceStates.Error or InstanceStates.Waiting;
        CheckConditions(monitor, target, finished);
        if (monitor.IsArmed && elapsed is not null && context is null && turns <= 0 && !onFinish && !target.IsRunning)
        {
            // Nothing is running, so an elapsed-time alone could only report that nothing is happening.
            Fire(monitor, target, "elapsed", "nothing is running, so there is no time to wait for");
        }

        return monitor;
    }

    private void FireFromTimer(AgentMonitor m, LiveAgent target)
    {
        if (m.IsArmed)
        {
            Fire(m, target, "elapsed", $"{FormatDuration((long)(m.Elapsed ?? TimeSpan.Zero).TotalSeconds)} elapsed");
        }
    }

    private void CheckConditions(AgentMonitor m, LiveAgent agent, bool finished)
    {
        AgentStatus status = agent.Snapshot();
        if (m.Context is { } threshold && threshold.IsReached(status))
        {
            Fire(m, agent, "contextAt", $"context reached {status.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens (threshold {threshold.Spec})");
        }
        else if (m.TurnsAt > 0 && status.Turns >= m.TurnsAt)
        {
            Fire(m, agent, "turns", $"it reached turn {status.Turns.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (m.OnFinish && finished)
        {
            Fire(m, agent, "onFinish", $"the agent finished ({status.State})");
        }
    }

    private void Fire(AgentMonitor m, LiveAgent target, string condition, string detail)
    {
        AgentStatus snapshot = target.Snapshot();
        if (!m.TryFire(new MonitorFire(condition, detail, snapshot, _runtime.TimeProvider.GetUtcNow())))
        {
            return;
        }

        target.Emit(AgentEventKind.MonitorFired, detail: m.Id + ": " + condition);
        if (m.WatcherId is { } watcher)
        {
            _ = PushAsync(m, watcher, snapshot, detail);
        }
    }

    private async Task PushAsync(AgentMonitor m, string watcherId, AgentStatus s, string detail)
    {
        try
        {
            // A watcher blocked in await_monitor on this monitor gets the answer from that call, not a second message.
            if (m.Waiters > 0)
            {
                return;
            }

            LiveAgent? watcher = await GetAsync(watcherId, CancellationToken.None).ConfigureAwait(false);
            if (watcher is null)
            {
                return;
            }

            string name = s.Name.Length > 0 ? $"{s.Name} (`{s.Id}`)" : $"`{s.Id}`";
            string text = $"Monitor {m.Id}: {name} — {detail}. {StatusLine(s)} Monitor disarmed; call monitor_agent to re-arm.";
            var message = new Message
            {
                Id = _runtime.NewId("m"),
                User = [new TextPart(text)],
                DateTime = _runtime.TimeProvider.GetUtcNow(),
                Source = new MessageSource { Kind = "runtime", Reason = "monitor", InstanceId = s.Id },
            };
            m.PushMessageId = message.Id;
            await watcher.EnqueueAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An alert that cannot be delivered (the watcher is gone or locked) must not break the run that fired it; the monitor keeps its fired state for await_monitor.
        }
    }

    /// <summary>Removes a monitor's alert from the watcher's inbox if it has not been read yet, because the watcher collected the result through await_monitor.</summary>
    public async Task RetractPushAsync(AgentMonitor m, CancellationToken cancellationToken)
    {
        if (m.PushMessageId is { } id && m.WatcherId is { } watcher && await GetAsync(watcher, cancellationToken).ConfigureAwait(false) is { } live)
        {
            live.Retract(id);
        }
    }

    public static string StatusLine(AgentStatus s)
    {
        string context = s.ContextWindow > 0
            ? $"{Kilo(s.ContextTokens)}/{Kilo(s.ContextWindow)} context"
            : $"{Kilo(s.ContextTokens)} context";
        string running = s.RuntimeSeconds is { } secs ? $", running {FormatDuration(secs)}" : "";
        string tool = s.CurrentTool is null ? "" : $", current tool `{s.CurrentTool}` ({FormatDuration(s.CurrentToolSeconds)})";
        return $"{context}, {s.Turns.ToString(CultureInfo.InvariantCulture)} turns{running}{tool}, {Money(s.Total)} incl. children.";
    }

    public static string Kilo(long n) => n < 1000 ? n.ToString(CultureInfo.InvariantCulture) : ((n + 500) / 1000).ToString(CultureInfo.InvariantCulture) + "k";

    public static string Money(decimal d) => "$" + Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);

    public static string FormatDuration(long secs)
    {
        TimeSpan d = TimeSpan.FromSeconds(secs);
        if (d < TimeSpan.FromMinutes(1))
        {
            return secs.ToString(CultureInfo.InvariantCulture) + "s";
        }

        if (d < TimeSpan.FromHours(1))
        {
            return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        }

        int minutes = d.Minutes;
        return minutes == 0
            ? ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
            : ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h" + minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }
}
