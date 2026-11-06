using OneLlm.Messages;

namespace OneLlm.Tools;

/// <summary>
/// What one tool call may use: who is calling, the host's grants, the clock, a place to report cost and progress. One context is
/// made per call, so line items added to it belong to that call.
/// </summary>
public sealed class ToolContext
{
    private readonly object _gate = new();
    private readonly List<CostLine> _costLines = [];

    /// <summary>Creates a context.</summary>
    /// <param name="instanceId">The id of the instance (run) making the call.</param>
    /// <param name="toolCallId">The id of this tool call.</param>
    /// <param name="timeProvider">The clock for stamps, timeouts and delays. Tools use it instead of the system clock, so tests can control time.</param>
    public ToolContext(string instanceId, string toolCallId, TimeProvider timeProvider)
    {
        InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
        ToolCallId = toolCallId ?? throw new ArgumentNullException(nameof(toolCallId));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>The id of the instance making the call.</summary>
    public string InstanceId { get; }

    /// <summary>The id of the instance that spawned this one, for a sub-agent. Null otherwise.</summary>
    public string? CallerInstanceId { get; init; }

    /// <summary>The id of this tool call.</summary>
    public string ToolCallId { get; }

    /// <summary>The clock.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>What the host grants. Default <see cref="NoCapabilities.Instance"/>: nothing.</summary>
    public ICapabilities Capabilities { get; init; } = NoCapabilities.Instance;

    /// <summary>Where <see cref="Log"/> and <see cref="Progress"/> lines go. Null drops them.</summary>
    public IToolLog? LogSink { get; init; }

    /// <summary>The runtime running the call, or null when the context was built without one. Agent tools use it to start and watch other instances.</summary>
    public Runtime? Runtime { get; init; }

    /// <summary>The live agent making the call, when the loop made this context. Lets a wait end when a message arrives for the caller.</summary>
    internal Agents.LiveAgent? Agent { get; init; }

    /// <summary>True when the agent loop held this call for approval and a person approved it.</summary>
    internal bool ApprovedByLoop { get; init; }

    /// <summary>Host services a tool may look up, such as an HTTP client factory. Null when the host offers none.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>The cost line items added so far, in the order added.</summary>
    public IReadOnlyList<CostLine> CostLines
    {
        get
        {
            lock (_gate)
            {
                return _costLines.ToArray();
            }
        }
    }

    /// <summary>Records a cost for this call, for example a search query priced by the catalog. Safe to call from several threads.</summary>
    /// <param name="line">The line item. Each line names its usage counter, unit price and source.</param>
    public void AddCost(CostLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            _costLines.Add(line);
        }
    }

    /// <summary>Writes an informational line to <see cref="LogSink"/>.</summary>
    /// <param name="message">The line.</param>
    public void Log(string message) => LogSink?.Write(ToolCallId, ToolLogLevel.Information, message);

    /// <summary>Writes a progress note to <see cref="LogSink"/>.</summary>
    /// <param name="message">The note.</param>
    public void Progress(string message) => LogSink?.Write(ToolCallId, ToolLogLevel.Progress, message);
}
