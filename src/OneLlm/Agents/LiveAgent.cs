using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Tools;

namespace OneLlm.Agents;

/// <summary>Everything a run needs that is decided when the agent is created: the model, tools, hooks, grants and strategy.</summary>
internal sealed class AgentSetup
{
    public required ResolvedModel Model { get; init; }

    public required IChatProvider Provider { get; init; }

    public required IReadOnlyDictionary<string, ITool> Tools { get; init; }

    public required IReadOnlyList<ToolDefinition> ToolDefinitions { get; init; }

    public required HookSet Hooks { get; init; }

    public required ICapabilities Capabilities { get; init; }

    public required ContextStrategyOptions Strategy { get; init; }

    public required AskRequest Settings { get; init; }

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// One agent instance in a runtime: its state, message inbox, run segments and subscribers. The loop itself is in the other parts of
/// this class. All public behaviour is reached through <see cref="AgentRun"/> and the agent tools.
/// </summary>
internal sealed partial class LiveAgent
{
    private const int HistoryLimit = 1000;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Message> _inbox = new();
    private readonly List<AgentEvent> _history = [];
    private readonly List<Channel<AgentEvent>> _subscribers = [];
    private TaskCompletionSource<AgentResult>? _segment;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _cancel;
    private bool _running;
    private bool _stopping;
    private int _waiters;

    public LiveAgent(Runtime runtime, AgentHost host, Instance instance, AgentSetup setup)
    {
        Runtime = runtime;
        Host = host;
        Instance = instance;
        Setup = setup;
    }

    public Runtime Runtime { get; }

    public AgentHost Host { get; }

    public Instance Instance { get; }

    public AgentSetup Setup { get; }

    /// <summary>The tool now running (or the first of several), and when it started, for the monitor snapshot.</summary>
    public string? CurrentTool { get; private set; }

    public long CurrentToolStarted { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public AgentRun Handle => new(this);

    public IReadOnlyList<PendingApproval> PendingSnapshot()
    {
        lock (_gate)
        {
            return [.. Instance.PendingApprovals];
        }
    }

    // ---- events -------------------------------------------------------------------------------------------------------------

    public void Emit(AgentEventKind kind, string? state = null, Message? message = null, ToolCall? call = null, string? detail = null)
    {
        var e = new AgentEvent(kind, Instance.InstanceId, Runtime.TimeProvider.GetUtcNow()) { State = state, Message = message, ToolCall = call, Detail = detail };
        lock (_gate)
        {
            if (_history.Count >= HistoryLimit)
            {
                _history.RemoveAt(0);
            }

            _history.Add(e);
            foreach (Channel<AgentEvent> s in _subscribers)
            {
                s.Writer.TryWrite(e);
            }
        }

        Runtime.RaiseAgentEvent(e);
    }

    public async IAsyncEnumerable<AgentEvent> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            foreach (AgentEvent e in _history)
            {
                channel.Writer.TryWrite(e);
            }

            _subscribers.Add(channel);
        }

        try
        {
            await foreach (AgentEvent e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <summary>Takes a queued message back out of the inbox, if the agent has not read it yet.</summary>
    public void Retract(string messageId)
    {
        lock (_gate)
        {
            if (_inbox.IsEmpty)
            {
                return;
            }

            var keep = new List<Message>();
            while (_inbox.TryDequeue(out Message? m))
            {
                if (m.Id != messageId)
                {
                    keep.Add(m);
                }
            }

            foreach (Message m in keep)
            {
                _inbox.Enqueue(m);
            }
        }
    }

    // ---- messages and waiting -----------------------------------------------------------------------------------------------

    /// <summary>Completes when a message next arrives for this agent. Waits use it to end early, as a message interrupts a wait.</summary>
    public Task NextMessage
    {
        get
        {
            lock (_gate)
            {
                return _signal.Task;
            }
        }
    }

    /// <summary>While a caller is blocked in an await tool, runtime-sent monitor alerts are held back (the tool returns them instead).</summary>
    public void BeginWait()
    {
        lock (_gate)
        {
            _waiters++;
        }
    }

    public void EndWait()
    {
        lock (_gate)
        {
            _waiters--;
        }
    }

    public Task DeliverAsync(string text, MessageSource? source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var message = new Message
        {
            Id = Runtime.NewId("m"),
            User = [new TextPart(text)],
            DateTime = Runtime.TimeProvider.GetUtcNow(),
            Source = source ?? new MessageSource { Kind = "api" },
        };
        return EnqueueAsync(message, cancellationToken);
    }

    public async Task EnqueueAsync(Message message, CancellationToken cancellationToken)
    {
        Task? finishing = null;
        lock (_gate)
        {
            _inbox.Enqueue(message);
            TaskCompletionSource old = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
            if (_running && !_stopping)
            {
                return;
            }

            if (_running)
            {
                finishing = _segment!.Task;
            }
        }

        if (finishing is not null)
        {
            try
            {
                await finishing.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The earlier segment failed; the message still starts a new one.
            }
        }

        ResumeIfInboxWaiting();
    }

    /// <summary>Starts a segment when messages are queued and the agent is resting without pending approvals.</summary>
    private void ResumeIfInboxWaiting()
    {
        lock (_gate)
        {
            if (_running || _inbox.IsEmpty || Instance.PendingApprovals.Count > 0)
            {
                return;
            }
        }

        StartSegment(initial: false);
    }

    public async Task DecideAsync(string? toolCallId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        PendingApproval pending;
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException($"Instance '{Instance.InstanceId}' is running; there is nothing to decide.");
            }

            List<PendingApproval> undecided = [.. Instance.PendingApprovals.Where(static p => !ApprovalDecision.IsDecided(p))];
            pending = toolCallId is null
                ? undecided.Count == 1 ? undecided[0] : throw new InvalidOperationException(undecided.Count == 0 ? "No tool call is waiting for a decision." : "Several tool calls are waiting; name one by id.")
                : undecided.FirstOrDefault(p => p.ToolCallId == toolCallId) ?? throw new InvalidOperationException($"Tool call '{toolCallId}' is not waiting for a decision.");
            ApprovalDecision.Set(pending, approve, reason);
        }

        await using (await Runtime.Store.AcquireAsync(Instance.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            await Runtime.Store.SaveAsync(Instance, cancellationToken).ConfigureAwait(false);
        }

        await Runtime.WriteRecordAsync(
            new ApprovalRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), ToolCallId = pending.ToolCallId, Tool = pending.Tool, Decision = approve ? "approved" : "denied", By = "api" },
            cancellationToken).ConfigureAwait(false);
        Emit(AgentEventKind.ApprovalDecided, detail: (approve ? "approved " : "denied ") + pending.ToolCallId);

        bool allDecided;
        lock (_gate)
        {
            allDecided = Instance.PendingApprovals.All(ApprovalDecision.IsDecided);
        }

        if (allDecided)
        {
            StartSegment(initial: false);
        }
    }

    public async Task CancelAsync()
    {
        Task? running;
        lock (_gate)
        {
            running = _running ? _segment!.Task : null;
            if (_running)
            {
                // Only while running: the token source is disposed once a segment has ended.
                _cancel?.Invoke();
            }
        }

        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The cancellation surfaces to whoever awaits the run; cancelling is complete once it settled.
            }
        }
    }

    public async Task<AgentResult> AwaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<AgentResult>? segment;
            lock (_gate)
            {
                segment = _running ? _segment!.Task : null;
            }

            if (segment is null)
            {
                lock (_gate)
                {
                    // The latest segment already ended (a quick run can finish before anyone waits): report what it reported.
                    if (_segment is { Task.IsCompleted: true } last)
                    {
                        if (last.Task.IsFaulted && Instance.State == InstanceStates.Error)
                        {
                            return last.Task.GetAwaiter().GetResult();
                        }

                        if (last.Task.IsCompletedSuccessfully)
                        {
                            return last.Task.Result;
                        }
                    }
                }

                return RestingResult();
            }

            AgentResult result = await segment.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_running || ReferenceEquals(_segment!.Task, segment))
                {
                    return result;
                }
            }
        }
    }

    // ---- segments -----------------------------------------------------------------------------------------------------------

    public void StartSegment(bool initial)
    {
        TaskCompletionSource<AgentResult> done;
        CancellationToken token;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _stopping = false;
            cts = new CancellationTokenSource();
            token = cts.Token;
            _cancel = cts.Cancel;
            _segment = done = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SegmentMainAsync(initial, done, token).ConfigureAwait(false);
                }
                finally
                {
                    cts.Dispose();
                }
            },
            CancellationToken.None);
    }

    private async Task SegmentMainAsync(bool initial, TaskCompletionSource<AgentResult> done, CancellationToken token)
    {
        AgentResult? result = null;
        Exception? failure = null;
        try
        {
            result = await RunSegmentAsync(initial, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The run stops being "running" in the same step that its waiters are released (their continuations are queued, never inline),
        // so a caller that gets the result can approve, message or start again at once without finding the agent still busy.
        lock (_gate)
        {
            _running = false;
            _stopping = false;
            if (failure is not null)
            {
                done.TrySetException(failure);
            }
            else
            {
                done.TrySetResult(result!);
            }
        }

        // A message that arrived while the run was settling gets its own segment.
        if (Instance.State != InstanceStates.Error)
        {
            ResumeIfInboxWaiting();
        }
    }

    // ---- status -------------------------------------------------------------------------------------------------------------

    public int TurnCount => Instance.Messages.Count(static m => m.User.Count == 0 && (m.Assistant.Count > 0 || m.ToolCalls.Count > 0) && !(m.ToolCalls.Count > 0 && m.ToolCalls.All(static c => c.Injected) && m.Assistant.Count == 0));

    public decimal OwnCost => OwnCostOf(Instance);

    public static decimal OwnCostOf(Instance instance)
    {
        // A parent prices a live child while the child's own thread is still adding to its history, and the lists are not locked.
        // A pass that overlaps a change is simply run again; the next one sees the settled list.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return SumCost(instance);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException && attempt < 16)
            {
                Thread.Yield();
            }
        }
    }

    private static decimal SumCost(Instance instance)
    {
        decimal total = 0m;
        List<Compaction> compactions = instance.Compactions;
        for (int i = 0; i < compactions.Count; i++)
        {
            if ((Compaction?)compactions[i] is { ExtensionData: { } d } && d.TryGetValue(ReplacedCostKey, out object? v) && v is string text
                && decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal replaced))
            {
                total += replaced;
            }
        }

        List<Message> messages = instance.Messages;
        for (int i = 0; i < messages.Count; i++)
        {
            // A slot that reads as empty is a history being rebuilt by its own thread (compaction); the retry above covers the rest.
            if ((Message?)messages[i] is not { } m)
            {
                continue;
            }

            total += m.Usage?.Cost ?? 0m;
            IReadOnlyList<ToolCall> calls = m.ToolCalls;
            for (int k = 0; k < calls.Count; k++)
            {
                total += calls[k].Cost;
            }
        }

        return total;
    }

    /// <summary>Tokens the next turn will carry: the last model call's input (cached tokens included) plus its output. Zero before any call.</summary>
    public long ContextTokens
    {
        get
        {
            for (int i = Instance.Messages.Count - 1; i >= 0; i--)
            {
                if (Instance.Messages[i].Usage is { } u)
                {
                    return u.TotalInputTokens + u.OutputTokens;
                }
            }

            return 0;
        }
    }

    public long ContextWindow => Setup.Model.Model?.ContextWindow ?? 0;

    public AgentStatus Snapshot()
    {
        long tokens = ContextTokens;
        long window = ContextWindow;
        double percent = window > 0 ? (double)tokens * 100d / window : 0d;
        bool running = IsRunning;
        long? runtimeSeconds = null;
        if (running && Instance.RunStartedAt is { } started && DateTimeOffset.TryParse(started, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset at))
        {
            runtimeSeconds = Math.Max(0, (long)(Runtime.TimeProvider.GetUtcNow() - at).TotalSeconds);
        }

        (decimal descendants, int count) = Host.DescendantCost(Instance.InstanceId);
        long toolSeconds = CurrentTool is null ? 0 : (long)Runtime.TimeProvider.GetElapsedTime(CurrentToolStarted).TotalSeconds;
        return new AgentStatus(
            Instance.InstanceId, Instance.Name, Instance.State ?? "", tokens, window, percent, TurnCount, Instance.RunStartedAt, runtimeSeconds, CurrentTool, toolSeconds, OwnCost, descendants, count);
    }

    private AgentResult RestingResult()
    {
        (string text, JsonNode? output, string? by) = Outcome.Derive(Instance);
        AgentStopReason reason = Instance.State switch
        {
            InstanceStates.Waiting => AgentStopReason.Waiting,
            InstanceStates.WaitingForApproval => AgentStopReason.WaitingForApproval,
            InstanceStates.Stopped when by is not null => AgentStopReason.Completed,
            _ => AgentStopReason.Stopped,
        };
        return new AgentResult(Instance, reason, text, output, by, TurnCount, Cost.Zero, null, [])
        {
            SubtreeCost = OwnCost + Host.DescendantCost(Instance.InstanceId).Total,
        };
    }
}

/// <summary>A point-in-time view of an agent for monitors and the status tool.</summary>
internal sealed record AgentStatus(
    string Id, string Name, string State, long ContextTokens, long ContextWindow, double ContextPercent, int Turns, string? RunStartedAt,
    long? RuntimeSeconds, string? CurrentTool, long CurrentToolSeconds, decimal Own, decimal Descendants, int DescendantCount)
{
    public decimal Total => Own + Descendants;
}

/// <summary>Approval decisions live in the pending approval's extension data, so they are saved with the instance and survive a restart.</summary>
internal static class ApprovalDecision
{
    public static bool IsDecided(PendingApproval p) => p.ExtensionData is { } d && d.ContainsKey("decision");

    public static bool IsApproved(PendingApproval p) => p.ExtensionData is { } d && d.TryGetValue("decision", out object? v) && v is "approved";

    public static string? Reason(PendingApproval p) => p.ExtensionData is { } d && d.TryGetValue("reason", out object? v) ? v as string : null;

    public static void Set(PendingApproval p, bool approve, string? reason)
    {
        p.ExtensionData ??= [];
        p.ExtensionData["decision"] = approve ? "approved" : "denied";
        if (reason is not null)
        {
            p.ExtensionData["reason"] = reason;
        }
    }
}

/// <summary>Reads what an agent produced from its saved conversation.</summary>
internal static class Outcome
{
    /// <summary>
    /// The answer of the latest run: the result of the last <c>task_complete</c> call after the last user message, else the text of the
    /// last assistant message after it.
    /// </summary>
    public static (string Text, JsonNode? Output, string? CompletedBy) Derive(Instance instance)
    {
        int start = 0;
        for (int i = instance.Messages.Count - 1; i >= 0; i--)
        {
            if (instance.Messages[i].User.Count > 0)
            {
                start = i;
                break;
            }
        }

        string text = "";
        JsonNode? output = null;
        string? by = null;
        for (int i = start; i < instance.Messages.Count; i++)
        {
            Message m = instance.Messages[i];
            string t = m.GetAssistantText();
            if (t.Length > 0)
            {
                text = t;
            }

            foreach (ToolCall c in m.ToolCalls)
            {
                if (c.Tool == "task_complete" && !c.IsError)
                {
                    by = c.Tool;
                    output = c.Inputs?.DeepClone();
                    text = c.Inputs?["result"]?.GetValue<string>() ?? text;
                }
            }
        }

        return (text, output, by);
    }
}
