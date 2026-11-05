using System.Text.Json.Serialization;
using OneLlm.Definitions;
using OneLlm.Messages;

namespace OneLlm.Instances;

/// <summary>The lifecycle states of an instance. The values are the strings written to the file.</summary>
public static class InstanceStates
{
    /// <summary>A run is in progress.</summary>
    public const string Running = "running";

    /// <summary>The run ended; the instance can be continued with a new message.</summary>
    public const string Stopped = "stopped";

    /// <summary>The agent finished its turn and waits for input.</summary>
    public const string Waiting = "waiting";

    /// <summary>A tool call needs a person's approval before it runs. See <see cref="Instance.PendingApprovals"/>.</summary>
    public const string WaitingForApproval = "waiting_for_approval";

    /// <summary>The run failed.</summary>
    public const string Error = "error";
}

/// <summary>
/// One run of a definition and everything it has said and done so far: the definition's fields as they were when the run
/// started, the conversation, and the run's own state. Every run is an instance, whether it is one call or a long agent run.
/// </summary>
/// <remarks>
/// <see cref="State"/> is a string so that states written by other tools (for example <c>suspended</c>) load and save unchanged.
/// See <c>spec/instances.md</c> for the file format.
/// </remarks>
public sealed class Instance : AgentDefinition
{
    /// <summary>The instance id: 1 to 128 characters of letters, digits, <c>-</c> and <c>_</c>. It names the file in a file store.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>The path or URI of the definition the run started from, or null.</summary>
    public string? DefinitionPath { get; set; }

    /// <summary>The model name as first requested, before alias resolution; <see cref="AgentDefinition.Model"/> holds the resolved one.</summary>
    public string? RequestedModel { get; set; }

    /// <summary>The longest image edge in pixels the instance sends; null for no cap.</summary>
    public int? MaxImageSize { get; set; }

    /// <summary>The lifecycle state: one of <see cref="InstanceStates"/>, or any other string a file carried.</summary>
    public string? State { get; set; }

    /// <summary>The instance that last messaged this one, or null.</summary>
    public string? CallerInstanceId { get; set; }

    /// <summary>The instance that started this one, or null. Set once and never replaced.</summary>
    public string? SpawnerInstanceId { get; set; }

    /// <summary>The tool call of the caller that started this instance, or null.</summary>
    public string? CallerToolCallId { get; set; }

    /// <summary>The prompt a calling agent gave when it started this instance, or null.</summary>
    public string? DelegationPrompt { get; set; }

    /// <summary>The system prompt as rendered for the latest turn: what the model actually saw. Null before the first render.</summary>
    public string? RenderedSystemPrompt { get; set; }

    /// <summary>When the current run started (RFC 3339, kept as written), or null.</summary>
    public string? RunStartedAt { get; set; }

    /// <summary>The conversation so far.</summary>
    public List<Message> Messages { get; set; } = [];

    /// <summary>Tool calls waiting for a decision. They are saved with the instance, so an approval survives a restart.</summary>
    public List<PendingApproval> PendingApprovals { get; set; } = [];

    /// <summary>What context compaction replaced, in order.</summary>
    public List<Compaction> Compactions { get; set; } = [];

    /// <summary>Creates an empty instance with the given id.</summary>
    /// <param name="instanceId">The instance id; see <see cref="InstanceId"/>.</param>
    /// <returns>The instance, in the <see cref="InstanceStates.Stopped"/> state.</returns>
    public static Instance Create(string instanceId) => new() { InstanceId = instanceId, State = InstanceStates.Stopped };

    /// <summary>Starts an instance from a definition: copies its fields and gives it an id.</summary>
    /// <param name="definition">The definition to run.</param>
    /// <param name="instanceId">The new instance's id.</param>
    /// <returns>A stopped instance with no messages.</returns>
    public static Instance FromDefinition(AgentDefinition definition, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Instance i = DefinitionFiles.CopyFields(definition);
        i.InstanceId = instanceId;
        i.State = InstanceStates.Stopped;
        i.RequestedModel = definition.Model;
        i.DefinitionPath = definition.SourceName;
        return i;
    }

    /// <summary>Parses an instance file.</summary>
    /// <param name="yaml">The file contents.</param>
    /// <param name="sourceName">The name used in error positions, usually the file path.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="BadDefinitionException">The YAML is malformed or a value has the wrong type; carries file, line and column.</exception>
    public static Instance Parse(string yaml, string? sourceName = null) => DefinitionFiles.ParseInstance(yaml, sourceName);

    /// <summary>Copies the instance under a new id: a branch of the conversation that can go its own way.</summary>
    /// <param name="newInstanceId">The id of the copy.</param>
    /// <param name="upToMessageId">Keep messages up to and including this one; null keeps all of them.</param>
    /// <returns>A stopped instance that remembers where it forked from in its extension data (<c>forkedFrom</c>).</returns>
    /// <exception cref="ArgumentException"><paramref name="upToMessageId"/> is not a message of this instance.</exception>
    public Instance Fork(string newInstanceId, string? upToMessageId = null)
    {
        Instance copy = DefinitionFiles.CopyInstance(this);
        copy.InstanceId = newInstanceId;
        copy.State = InstanceStates.Stopped;
        copy.PendingApprovals = [];
        copy.RunStartedAt = null;
        if (upToMessageId is not null)
        {
            int at = Messages.FindIndex(m => m.Id == upToMessageId);
            if (at < 0)
            {
                throw new ArgumentException($"Message '{upToMessageId}' is not in instance '{InstanceId}'.", nameof(upToMessageId));
            }

            copy.Messages = Messages.GetRange(0, at + 1);
        }

        (copy.ExtensionData ??= [])["forkedFrom"] = new Dictionary<string, object?> { ["instanceId"] = InstanceId, ["messageId"] = upToMessageId ?? Messages.LastOrDefault()?.Id };
        return copy;
    }

    /// <summary>Copies the instance whole under a new id.</summary>
    /// <param name="newInstanceId">The id of the copy.</param>
    /// <returns>An exact copy with a new id, stopped and with no pending approvals.</returns>
    public Instance Duplicate(string newInstanceId) => Fork(newInstanceId, null);

    /// <summary>Writes the instance as YAML in the instance file layout: header fields first, the messages last.</summary>
    /// <returns>The YAML text.</returns>
    public override string ToYaml()
    {
        string text = InstanceWriter.Write(this);
        return OriginalText is not null && LoadedSerialization == text ? OriginalText : text;
    }
}

/// <summary>A tool call that is waiting for a person to approve or deny it.</summary>
public sealed class PendingApproval
{
    /// <summary>The id of the tool call.</summary>
    public string ToolCallId { get; set; } = "";

    /// <summary>The tool's name.</summary>
    public string Tool { get; set; } = "";

    /// <summary>The message that holds the call, or null if the call has not been written into a message yet.</summary>
    public string? MessageId { get; set; }

    /// <summary>The call's inputs.</summary>
    public Dictionary<string, object?>? Inputs { get; set; }

    /// <summary>Why approval is needed (the definition's <c>requiresApproval</c>, a hook, a shell policy), or null.</summary>
    public string? Reason { get; set; }

    /// <summary>When approval was requested (RFC 3339, kept as written).</summary>
    public string? RequestedAt { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A record of one context compaction: what it replaced and what it cost.</summary>
public sealed class Compaction
{
    /// <summary>The strategy: <c>clearToolResults</c>, <c>compact</c> or <c>providerNative</c>.</summary>
    public string Strategy { get; set; } = "";

    /// <summary>When it ran (RFC 3339, kept as written).</summary>
    public string? At { get; set; }

    /// <summary>The ids of the messages it replaced or shortened.</summary>
    public List<string>? ReplacedMessageIds { get; set; }

    /// <summary>The id of the message that holds the summary, if the strategy wrote one.</summary>
    public string? SummaryMessageId { get; set; }

    /// <summary>What the compaction itself cost, in USD, or null when it made no model call.</summary>
    public decimal? Cost { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
