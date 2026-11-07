using OneLlm.Instances;

namespace OneLlm.Agents;

/// <summary>
/// A handle on a running (or resting) agent instance. Start one with <c>Runtime.StartAgentAsync</c>, or get one for a saved instance
/// with <c>Runtime.GetAgentAsync</c>. Watch its events, steer it with messages, approve or deny held tool calls, and wait for it to stop.
/// </summary>
/// <remarks>
/// An agent runs in segments: from a start or a resume until it rests in <c>stopped</c> (a loop-ending tool ran), <c>waiting</c> (the
/// model answered without a tool) or <c>waiting_for_approval</c>. A message or an approval decision starts the next segment. Handles
/// are thread-safe.
/// </remarks>
public sealed class AgentRun
{
    private readonly LiveAgent _agent;

    internal AgentRun(LiveAgent agent) => _agent = agent;

    /// <summary>The instance id.</summary>
    public string InstanceId => _agent.Instance.InstanceId;

    /// <summary>The instance. It changes as the agent runs; read it after <see cref="AwaitAsync"/> for a stable view.</summary>
    public Instance Instance => _agent.Instance;

    /// <summary>The state now: <c>running</c>, <c>stopped</c>, <c>waiting</c>, <c>waiting_for_approval</c> or <c>error</c>.</summary>
    public string State => _agent.Instance.State ?? "";

    /// <summary>The tool calls waiting for a decision.</summary>
    public IReadOnlyList<PendingApproval> PendingApprovals => _agent.PendingSnapshot();

    /// <summary>
    /// The events of this agent from the start (the last 1000 are kept for late subscribers), then live ones, until you stop
    /// enumerating. Several subscribers each get every event.
    /// </summary>
    /// <param name="cancellationToken">Ends the enumeration; the agent is unaffected.</param>
    /// <returns>The events in order.</returns>
    public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _agent.Subscribe(cancellationToken);

    /// <summary>Waits until the agent rests, then returns what it produced.</summary>
    /// <param name="cancellationToken">Stops waiting; the agent keeps running. Use <see cref="CancelAsync"/> to stop the agent.</param>
    /// <returns>The result of the latest segment. Returns at once if the agent is already resting.</returns>
    /// <exception cref="ProviderException">A model call failed after retries; the instance is in <c>error</c>.</exception>
    /// <exception cref="RunCancelledException">The run was cancelled.</exception>
    /// <exception cref="BudgetExceededException">A spend cap stopped the run.</exception>
    public Task<AgentResult> AwaitAsync(CancellationToken cancellationToken = default) => _agent.AwaitAsync(cancellationToken);

    /// <summary>Approves a held tool call. When it was the last undecided one, the agent runs the approved calls and goes on.</summary>
    /// <param name="toolCallId">The call to approve; null approves the only pending call.</param>
    /// <param name="cancellationToken">Cancels the request, not the run it starts.</param>
    /// <returns>A task that completes when the decision is saved.</returns>
    /// <exception cref="InvalidOperationException">There is no such pending call, or several are pending and no id was given.</exception>
    public Task ApproveAsync(string? toolCallId = null, CancellationToken cancellationToken = default) => _agent.DecideAsync(toolCallId, true, null, cancellationToken);

    /// <summary>Denies a held tool call. The model sees an error result with the reason, and the agent goes on once every pending call is decided.</summary>
    /// <param name="toolCallId">The call to deny; null denies the only pending call.</param>
    /// <param name="reason">Shown to the model. Defaults to a plain denial.</param>
    /// <param name="cancellationToken">Cancels the request, not the run it starts.</param>
    /// <returns>A task that completes when the decision is saved.</returns>
    /// <exception cref="InvalidOperationException">There is no such pending call, or several are pending and no id was given.</exception>
    public Task DenyAsync(string? toolCallId = null, string? reason = null, CancellationToken cancellationToken = default) => _agent.DecideAsync(toolCallId, false, reason, cancellationToken);

    /// <summary>
    /// Sends the agent a message. A running agent reads it at the next turn boundary; a resting agent starts a new segment with it.
    /// While approvals are pending the message waits until they are decided.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the message is queued (and, for a resting agent, the new segment has started).</returns>
    /// <exception cref="InstanceLockedException">Another runner holds the instance.</exception>
    public Task MessageAsync(string text, CancellationToken cancellationToken = default) => _agent.DeliverAsync(text, null, cancellationToken);

    /// <summary>Cancels the running segment. The instance is saved as <c>stopped</c> with <c>cancelled: true</c> and keeps the partial output. Does nothing when the agent is resting.</summary>
    /// <returns>A task that completes when the run has settled.</returns>
    public Task CancelAsync() => _agent.CancelAsync();

    /// <summary>
    /// Stops active or resting agents durably and disarms their monitors. Automatic monitor alerts cannot resume the instance.
    /// A later explicit message or approval may resume it; held approvals remain held. Unlike <see cref="CancelAsync"/>, this also stops idle agents.
    /// </summary>
    /// <returns>A task that completes when the stopped state is saved.</returns>
    public Task StopAsync() => _agent.StopAsync();
}
