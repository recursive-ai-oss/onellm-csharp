using OneLlm.Agents;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Http;
using OneLlm.Instances;
using OneLlm.Records;
using OneLlm.Tools;

namespace OneLlm;

public sealed partial class Runtime
{
    private AgentHost? _agentHost;

    /// <summary>The tools agents can ask for. Starts with the agent tools (<c>task_complete</c>, <c>run_agent_async</c>, <c>await_agent</c>, <c>monitor_agent</c>, ...). A definition's <c>references</c> must resolve here, or loading the agent fails.</summary>
    public ToolRegistry Tools { get; set; } = new();

    /// <summary>The host's hooks. They wrap hooks a definition names, and apply to every agent this runtime starts afterwards.</summary>
    public HookRegistry Hooks { get; } = new();

    /// <summary>Loads the hooks a definition's <c>hooks:</c> list names. Null by default: a definition with hooks then fails to load (or has them stripped, in lenient mode), naming the package that provides this.</summary>
    public IDefinitionHookSource? DefinitionHooks { get; set; }

    /// <summary>
    /// What agents may do: file roots, shell, network. Definitions never grant anything; this is the ceiling. A sub-agent gets at most its
    /// parent's grants. Defaults to files under the working directory at the time the runtime was made, with no shell and no network.
    /// </summary>
    /// <remarks>
    /// When these are a <see cref="global::OneLlm.Capabilities"/>, the value read back also protects the directories of a
    /// <see cref="FileInstanceStore"/> and <see cref="JsonlRecordSink"/> this runtime uses, so file tools cannot rewrite instances or audit records
    /// that sit under a write root. The value read back may therefore be a different object from the one assigned.
    /// </remarks>
    public ICapabilities Capabilities
    {
        get
        {
            ICapabilities set = _capabilities;
            if (set is not global::OneLlm.Capabilities caps)
            {
                return set;
            }

            // Read on every tool call, so the common case allocates nothing: the answer is reused while the grants, the store and the
            // sink list are the same objects as last time.
            IInstanceStore? store = _store;
            IReadOnlyList<IRecordSink> sinks = Sinks.Items;
            ProtectedCapabilities? cached = _protectedCaps;
            if (cached is not null && ReferenceEquals(cached.Source, caps) && ReferenceEquals(cached.Store, store) && ReferenceEquals(cached.Sinks, sinks))
            {
                return cached.Result;
            }

            string[] dirs = StateDirectories(store, sinks);
            global::OneLlm.Capabilities result = dirs.Length == 0 ? caps : caps.WithProtectedPaths(dirs);
            _protectedCaps = new ProtectedCapabilities(caps, store, sinks, result);
            return result;
        }

        set => _capabilities = value ?? throw new ArgumentNullException(nameof(value));
    }

    private ICapabilities _capabilities = global::OneLlm.Capabilities.Default();
    private volatile ProtectedCapabilities? _protectedCaps;

    private sealed record ProtectedCapabilities(global::OneLlm.Capabilities Source, IInstanceStore? Store, IReadOnlyList<IRecordSink> Sinks, global::OneLlm.Capabilities Result);

    private static string[] StateDirectories(IInstanceStore? store, IReadOnlyList<IRecordSink> sinks)
    {
        var dirs = new List<string>();
        if (store is FileInstanceStore fs)
        {
            dirs.Add(fs.Directory);
        }

        foreach (IRecordSink sink in sinks)
        {
            if (sink is JsonlRecordSink j)
            {
                dirs.Add(j.DirectoryPath);
            }
        }

        return [.. dirs];
    }

    /// <summary>
    /// False by default: an agent's tools (<c>query_agent</c>, <c>message_agent</c>, <c>await_agent</c>, <c>monitor_agent</c>, <c>list_agent_instances</c>)
    /// reach only the agent itself and the agents it started. Another instance may hold wider grants, so letting a narrower agent read or message it
    /// would void the narrowing. Set true to let agents reach every instance in the store. Host calls on the runtime are never restricted.
    /// </summary>
    public bool AgentsMayReachAnyInstance { get; set; }

    /// <summary>
    /// Retries of a model call inside the agent loop, for failures the provider marks transient, after the provider client's own retries.
    /// Delays honour <c>Retry-After</c>. Defaults to none, because provider clients already retry.
    /// </summary>
    public RetryPolicy AgentRetry { get; set; } = RetryPolicy.None;

    /// <summary>The definition used to summarize old turns for the <c>compact</c> context strategy. Null uses the one shipped in the library; set your own to change the prompt or model.</summary>
    public AgentDefinition? CompactionDefinition { get; set; }

    /// <summary>Where tool log and progress lines go. Null drops them.</summary>
    public IToolLog? ToolLog { get; set; }

    /// <summary>Host services tools may look up through <see cref="ToolContext.Services"/>. Null for none.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Agents that <c>run_agent_async</c> can start by name, besides files the caller's file grants cover.</summary>
    public AgentDefinitionRegistry AgentDefinitions { get; } = new();

    /// <summary>Raised for every event of every agent in this runtime, on the thread that caused it. A handler that throws is ignored.</summary>
    public event Action<AgentEvent>? AgentEvents;

    internal AgentHost AgentHost
    {
        get
        {
            lock (_gate)
            {
                return _agentHost ??= new AgentHost(this);
            }
        }
    }

    internal void RaiseAgentEvent(AgentEvent e)
    {
        try
        {
            AgentEvents?.Invoke(e);
        }
        catch (Exception)
        {
            // Observers must not break the agent they watch.
        }
    }

    /// <summary>Reads a definition file and starts it as an agent.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c>, over the definition's own defaults.</param>
    /// <param name="options">The first prompt, grants to ask for and other start options; null for none.</param>
    /// <param name="cancellationToken">Cancels the start. To stop the running agent use <see cref="AgentRun.CancelAsync"/>.</param>
    /// <returns>The running agent.</returns>
    /// <exception cref="BadDefinitionException">The file is invalid, or it references a tool this runtime does not offer (with file, line and column).</exception>
    /// <exception cref="CapabilityMissingException">The definition lists hooks and no hook source is set.</exception>
    public Task<AgentRun> StartAgentAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, AgentStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return StartAgentAsync(AgentDefinition.Load(path), inputs, options, cancellationToken);
    }

    /// <summary>
    /// Starts a definition as an agent: renders the prompts, runs injected initial tool calls, then loops: a model turn, the tool calls it
    /// makes, until a loop-ending tool runs, the model answers without a tool, a call needs approval, or <c>maxIterations</c> (default 200) is
    /// reached. Every step is saved and recorded. The call returns once the agent is started; use the returned handle to follow it.
    /// </summary>
    /// <param name="definition">The definition. Its tool references are checked against <see cref="Tools"/>; nothing is granted by it.</param>
    /// <param name="inputs">Values for <c>.inputs</c>.</param>
    /// <param name="options">Start options; null for none.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running agent.</returns>
    /// <exception cref="BadDefinitionException">The definition names no model, or references a tool this runtime does not offer.</exception>
    /// <exception cref="ModelNotFoundException">The catalog does not know the model.</exception>
    public async Task<AgentRun> StartAgentAsync(AgentDefinition definition, IReadOnlyDictionary<string, object?>? inputs = null, AgentStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        LiveAgent agent = await AgentHost.StartAsync(definition, inputs, options ?? new AgentStartOptions(), lenient: false, cancellationToken).ConfigureAwait(false);
        return agent.Handle;
    }

    /// <summary>Reads a definition file, runs it as an agent and waits until it rests.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c>.</param>
    /// <param name="options">Start options; null for none.</param>
    /// <param name="cancellationToken">Cancels the run: the instance is saved as <c>stopped</c> with <c>cancelled: true</c>, keeping the partial output.</param>
    /// <returns>What the agent produced. If it stopped to wait for approval, <see cref="AgentResult.StopReason"/> says so; decide with <see cref="ApproveAsync"/> or <see cref="DenyAsync"/> and await again.</returns>
    /// <exception cref="RunCancelledException">The token was cancelled.</exception>
    public Task<AgentResult> RunAgentAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, AgentStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunAgentAsync(AgentDefinition.Load(path), inputs, options, cancellationToken);
    }

    /// <summary>Runs a definition as an agent and waits until it rests. See <see cref="StartAgentAsync(AgentDefinition, IReadOnlyDictionary{string, object?}?, AgentStartOptions?, CancellationToken)"/>.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="inputs">Values for <c>.inputs</c>.</param>
    /// <param name="options">Start options; null for none.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the agent produced.</returns>
    /// <exception cref="RunCancelledException">The token was cancelled.</exception>
    /// <exception cref="ProviderException">A model call failed after retries; the instance is in <c>error</c>.</exception>
    /// <exception cref="BudgetExceededException">A spend cap stopped the run.</exception>
    public async Task<AgentResult> RunAgentAsync(AgentDefinition definition, IReadOnlyDictionary<string, object?>? inputs = null, AgentStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        AgentRun run = await StartAgentAsync(definition, inputs, options, cancellationToken).ConfigureAwait(false);
        return await WaitForRunAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentResult> WaitForRunAsync(AgentRun run, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(static state => _ = ((AgentRun)state!).CancelAsync(), run);
        return await run.AwaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a handle on a saved instance, such as one that was waiting for approval before a restart. A running agent in this runtime
    /// is returned as it is; otherwise the instance is loaded and rests until you approve, deny or message it.
    /// </summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The handle, or null when there is no such instance.</returns>
    public async Task<AgentRun?> GetAgentAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        LiveAgent? agent = await AgentHost.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return agent?.Handle;
    }

    /// <summary>Approves a held tool call of a saved or running instance. Same as <see cref="AgentRun.ApproveAsync"/>.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="toolCallId">The call; null for the only pending call.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The handle, to await the agent that goes on.</returns>
    /// <exception cref="KeyNotFoundException">There is no such instance.</exception>
    public async Task<AgentRun> ApproveAsync(string instanceId, string? toolCallId = null, CancellationToken cancellationToken = default)
    {
        AgentRun run = await GetAgentAsync(instanceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There is no instance '{instanceId}'.");
        await run.ApproveAsync(toolCallId, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>Denies a held tool call of a saved or running instance. Same as <see cref="AgentRun.DenyAsync"/>.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="toolCallId">The call; null for the only pending call.</param>
    /// <param name="reason">Shown to the model.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The handle, to await the agent that goes on.</returns>
    /// <exception cref="KeyNotFoundException">There is no such instance.</exception>
    public async Task<AgentRun> DenyAsync(string instanceId, string? toolCallId = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        AgentRun run = await GetAgentAsync(instanceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There is no instance '{instanceId}'.");
        await run.DenyAsync(toolCallId, reason, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>Sends a message to a saved or running instance: read at the next turn boundary if it is running, otherwise it starts the agent again.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="text">The message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The handle.</returns>
    /// <exception cref="KeyNotFoundException">There is no such instance.</exception>
    public async Task<AgentRun> MessageAgentAsync(string instanceId, string text, CancellationToken cancellationToken = default)
    {
        AgentRun run = await GetAgentAsync(instanceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There is no instance '{instanceId}'.");
        await run.MessageAsync(text, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>What an instance and every sub-agent it started have cost, in USD, from their saved histories. Sub-agents are found while this runtime has seen them.</summary>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The total.</returns>
    /// <exception cref="KeyNotFoundException">There is no such instance.</exception>
    public async Task<decimal> GetSubtreeCostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        LiveAgent agent = await AgentHost.GetAsync(instanceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There is no instance '{instanceId}'.");
        return agent.OwnCost + AgentHost.DescendantCost(instanceId).Total;
    }
}
