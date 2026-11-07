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
        if (!_runtime.IndependentSubAgentPolicy && parent is not null)
        {
            ceiling = CapabilityIntersection.Of(ceiling, parent.Setup.Capabilities);
        }
        else if (!_runtime.IndependentSubAgentPolicy && options.SpawnerInstanceId is { } savedParent && await _runtime.Store.LoadAsync(savedParent, cancellationToken).ConfigureAwait(false) is { } parentInstance)
        {
            // The parent is resting, not live: the grants it was saved with still bound the child. Damaged grants bound it to nothing.
            switch (GrantSummary.Evaluate(parentInstance.ExtensionData, out ICapabilities? parentGrants))
            {
                case GrantSummary.Kind.Valid:
                    ceiling = CapabilityIntersection.Of(ceiling, parentGrants!);
                    break;
                case GrantSummary.Kind.Invalid:
                    throw GrantSummary.Unreadable(savedParent);
            }
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

        IReadOnlySet<string>? toolCeiling = _runtime.IndependentSubAgentPolicy ? null : options.ToolCeiling;
        AgentSetup setup = BuildSetup(instance, options, caps, lenient, toolCeiling);
        Dictionary<string, object?> saved = GrantSummary.Of(caps);
        if (toolCeiling is not null)
        {
            saved["tools"] = toolCeiling.Order(StringComparer.Ordinal).Select(static t => (object?)t).ToList();
        }

        (instance.ExtensionData ??= [])[GrantSummary.Key] = saved;
        instance.ExtensionData[GrantSummary.RecordedKey] = true;
        instance.ExtensionData["agentRecovery"] = true;
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

    /// <summary>
    /// Whether the agent <paramref name="callerId"/> may look at or act on <paramref name="targetId"/> with its agent tools: itself and its own
    /// descendants (found by following spawner links, live or saved). Other instances may hold wider grants than the caller, so reaching
    /// them would void the caller's narrowing. A host that wants agents to reach any instance sets <see cref="Runtime.AgentsMayReachAnyInstance"/>.
    /// </summary>
    public async ValueTask<bool> MayReachAsync(string callerId, string targetId, CancellationToken cancellationToken)
    {
        if (_runtime.AgentsMayReachAnyInstance || callerId == targetId)
        {
            return true;
        }

        string current = targetId;
        // The depth bound also stops a corrupted store that links instances in a circle.
        for (int depth = 0; depth < 256; depth++)
        {
            string? parent = _parentOf.TryGetValue(current, out string? known)
                ? known
                : (await _runtime.Store.LoadAsync(current, cancellationToken).ConfigureAwait(false))?.SpawnerInstanceId;
            if (parent is null)
            {
                return false;
            }

            if (parent == callerId)
            {
                return true;
            }

            current = parent;
        }

        return false;
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

    private async Task<LiveAgent?> LoadAsync(string instanceId, bool reload = false)
    {
        if (!reload && _live.TryGetValue(instanceId, out LiveAgent? live))
        {
            return live;
        }

        Instance? instance = await _runtime.Store.LoadAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        LiveAgent? parent = instance.SpawnerInstanceId is { } sid && _live.TryGetValue(sid, out LiveAgent? p) ? p : null;
        ICapabilities caps = _runtime.IndependentSubAgentPolicy || parent is null ? _runtime.Capabilities : CapabilityIntersection.Of(_runtime.Capabilities, parent.Setup.Capabilities);
        // What the instance was started with bounds it again: a read-only start stays read-only in any runtime that resumes it.
        // A recorded entry that is missing or damaged is not a licence to run with the runtime's grants: the instance does not run.
        object? savedGrants = instance.ExtensionData?.GetValueOrDefault(GrantSummary.Key);
        switch (GrantSummary.Evaluate(instance.ExtensionData, out ICapabilities? restored))
        {
            case GrantSummary.Kind.Valid:
                caps = CapabilityIntersection.Of(caps, restored!);
                break;
            case GrantSummary.Kind.Invalid:
                throw GrantSummary.Unreadable(instanceId);
        }

        AgentSetup setup = BuildSetup(instance, new AgentStartOptions(), caps, lenient: true, GrantSummary.SavedToolCeiling(savedGrants));
        var agent = new LiveAgent(_runtime, this, instance, setup);
        _live[instanceId] = agent;
        if (instance.SpawnerInstanceId is { } spawner)
        {
            LinkChild(spawner, instanceId, null);
        }

        await ReindexChildrenAsync(instance.InstanceId, CancellationToken.None).ConfigureAwait(false);
        return agent;
    }

    public async Task<IReadOnlyList<AgentRun>> ResumeInterruptedAsync(CancellationToken cancellationToken)
    {
        var runs = new List<AgentRun>();
        var interrupted = new Dictionary<string, InstanceSummary>(StringComparer.Ordinal);
        await foreach (InstanceSummary summary in _runtime.Store.ListAsync(new InstanceQuery { State = InstanceStates.Running }, cancellationToken).ConfigureAwait(false))
        {
            interrupted[summary.InstanceId] = summary;
        }

        var spawners = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string id in interrupted.Keys)
        {
            spawners[id] = (await _runtime.Store.LoadAsync(id, cancellationToken).ConfigureAwait(false))?.SpawnerInstanceId;
        }

        // Start children before their awaiting parents, so await_agent sees their active segment rather than a resting handle.
        int Depth(InstanceSummary summary)
        {
            int depth = 0;
            while (depth < 256 && spawners.GetValueOrDefault(summary.InstanceId) is { } parent && interrupted.TryGetValue(parent, out summary!))
            {
                depth++;
            }

            return depth;
        }

        foreach (InstanceSummary summary in interrupted.Values.OrderByDescending(Depth))
        {
            if (_live.TryGetValue(summary.InstanceId, out LiveAgent? existing) && existing.IsRunning)
            {
                continue;
            }

            IInstanceLease lease;
            try
            {
                lease = await _runtime.Store.AcquireAsync(summary.InstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (InstanceLockedException)
            {
                continue;
            }

            bool transferred = false;
            try
            {
                // Recheck under the lease: the listing may predate another process finishing this instance.
                Instance? saved = await _runtime.Store.LoadAsync(summary.InstanceId, cancellationToken).ConfigureAwait(false);
                if (saved?.State != InstanceStates.Running || saved.ExtensionData?.GetValueOrDefault("agentRecovery") is not true)
                {
                    continue;
                }

                LiveAgent? agent = await LoadAsync(summary.InstanceId, reload: true).ConfigureAwait(false);
                if (agent is not null)
                {
                    transferred = agent.StartSegment(initial: false, lease);
                    if (transferred)
                    {
                        runs.Add(agent.Handle);
                    }
                }
            }
            finally
            {
                if (!transferred)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return runs;
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

    private static readonly HashSet<string> s_observerPoints = new(StringComparer.OrdinalIgnoreCase) { "onMessage", "onStateChange", "onCost" };

    /// <summary>
    /// Loads the hook files a definition lists. Without a hook source an interceptor fails the load (it must never be silently left
    /// out); a definition whose hooks are all observers runs without them and says so in <paramref name="warnings"/> (lenient only).
    /// </summary>
    internal static IReadOnlyList<IHook> LoadDefinitionHooks(Runtime runtime, Instance instance, ICapabilities caps, bool lenient, List<string> warnings)
    {
        IReadOnlyList<IHook> definitionHooks = [];
        if (instance.Hooks.Count > 0)
        {
            if (runtime.DefinitionHooks is { } source)
            {
                definitionHooks = source.Load(instance, caps);
            }
            else if (!lenient || instance.Hooks.Any(static h => h.Points is null || h.Points.Any(static p => !s_observerPoints.Contains(p))))
            {
                // Interceptors fail closed: a hook that can block or change a call must never be silently left out, on resume either.
                // Only a definition whose hooks all name observer points may run without a hook source.
                throw new CapabilityMissingException("Definition hooks", "RecursiveAI.OneLlm.JavaScript, or set Runtime.DefinitionHooks", isPackage: true);
            }
            else
            {
                warnings.Add("The definition lists observer hooks, but this runtime has no hook source, so they were left out.");
            }
        }

        return definitionHooks;
    }

    internal void RefreshTools(LiveAgent agent)
    {
        ToolRegistry registry = _runtime.Tools;
        long version = registry.Version;
        AgentSetup setup = agent.Setup;
        var warnings = new List<string>();
        Dictionary<string, ITool> tools = ResolveTools(registry, agent.Instance, agent.Options, setup.Capabilities, lenient: true, setup.ToolCeiling, warnings);
        setup.Tools = tools;
        setup.ToolDefinitions = [.. tools.Values.Select(static t => t.Definition.ToProviderDefinition())];
        setup.Registry = registry;
        setup.RegistryVersion = version;
        foreach (string warning in warnings)
        {
            if (!setup.Warnings.Contains(warning))
            {
                setup.Warnings.Add(warning);
            }
        }
    }

    private static Dictionary<string, ITool> ResolveTools(ToolRegistry registry, Instance instance, AgentStartOptions options, ICapabilities caps, bool lenient, IReadOnlySet<string>? toolCeiling, List<string> warnings)
    {
        // The same resolver the definition loader offers decides what is granted, so a reference the runtime's grants do not cover
        // fails the load (strict) or is dropped with a warning (lenient).
        ToolReferenceResolver resolver = registry.CreateResolver(caps, instance.References);
        var fromRefs = new List<ITool>();
        foreach (ToolReference reference in instance.References)
        {
            ReferenceCheck check = resolver.Check(reference);
            foreach (ITool granted in check.GrantedTools)
            {
                // A sub-agent started from a file the model could have written holds at most the tools its parent holds (task_complete
                // grants nothing, so every agent may end its run).
                if (toolCeiling is not null && granted.Definition.Name != "task_complete" && !toolCeiling.Contains(granted.Definition.Name))
                {
                    string over = $"The tool '{granted.Definition.Name}' is not one the agent that started this one holds.";
                    if (!lenient)
                    {
                        throw new BadDefinitionException(over, instance.DefinitionPath ?? instance.SourceName, reference.Line, reference.Column);
                    }

                    warnings.Add(over + " It was left out.");
                    continue;
                }

                fromRefs.Add(granted);
            }

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

        return tools;
    }

    private AgentSetup BuildSetup(Instance instance, AgentStartOptions options, ICapabilities caps, bool lenient, IReadOnlySet<string>? toolCeiling)
    {
        ToolRegistry registry = _runtime.Tools;
        long registryVersion = registry.Version;
        ResolvedModelPair pair = ResolvedModelPair.Of(_runtime, instance.Model!);
        var warnings = new List<string>();
        Dictionary<string, ITool> tools = ResolveTools(registry, instance, options, caps, lenient, toolCeiling, warnings);

        IReadOnlyList<IHook> definitionHooks = LoadDefinitionHooks(_runtime, instance, caps, lenient, warnings);

        IHook[] host = _runtime.Hooks.Snapshot;
        var setup = new AgentSetup
        {
            Registry = registry,
            RegistryVersion = registryVersion,
            ToolCeiling = toolCeiling,
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

    public void DisarmOwnedMonitors(string watcherId)
    {
        foreach (AgentMonitor monitor in _monitors.Values.Where(m => m.WatcherId == watcherId))
        {
            monitor.Dispose();
            _monitors.TryRemove(monitor.Id, out _);
        }
    }

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
