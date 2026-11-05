using System.Runtime.CompilerServices;
using OneLlm.Definitions;
using OneLlm.Messages;

namespace OneLlm.Instances;

/// <summary>Writes instances in the instance file layout, re-serializing only the messages that changed since the last save.</summary>
internal static class InstanceWriter
{
    // A message is immutable once written, so its YAML is cached by reference. Appending a turn to a long instance then costs the
    // new message plus the small header, not the whole conversation; the file is still replaced atomically in one piece.
    private static readonly ConditionalWeakTable<Message, string> MessageText = new();

    public static string Write(Instance instance) => Capture(instance).Text();

    /// <summary>
    /// Takes what a save needs to keep: the serialized header and the messages by reference. Turning it into text is deferred, so a store that is
    /// only written to during a run (the in-memory one) does not pay for the whole conversation on every turn.
    /// </summary>
    public static InstanceSnapshot Capture(Instance instance)
    {
        AgentFileWire wire = DefinitionFiles.ToWire(instance, includeInitialPrompts: true);
        // The header is written to text only when the snapshot is read, so what a run changes later (approvals, extension data) must not
        // reach into it: the collections that change are copied here.
        wire.PendingApprovals = wire.PendingApprovals is null ? null : [.. wire.PendingApprovals];
        wire.Compactions = wire.Compactions is null ? null : [.. wire.Compactions];
        wire.ExtensionData = wire.ExtensionData is null ? null : new Dictionary<string, object?>(wire.ExtensionData);
        wire.Inputs = wire.Inputs is null ? null : new Dictionary<string, object?>(wire.Inputs);
        wire.Headers = wire.Headers is null ? null : new Dictionary<string, string>(wire.Headers);
        bool emptyKey = instance.EmptyKeys is { } k && k.Contains("messages");
        return new InstanceSnapshot(wire, [.. instance.Messages], emptyKey);
    }

    internal static string MessageYaml(Message m)
    {
        if (!MessageText.TryGetValue(m, out string? text))
        {
            text = DefinitionFiles.SerializeMessages([m]);
            if (text.Length > 0 && text[^1] != '\n')
            {
                text += "\n";
            }

            MessageText.AddOrUpdate(m, text);
        }

        return text;
    }
}

/// <summary>An instance as of one save: its header text and its messages. <see cref="Text"/> gives the same YAML <see cref="Instance.ToYaml"/> would have.</summary>
internal sealed class InstanceSnapshot(AgentFileWire wire, Message[] messages, bool emptyMessagesKey)
{
    public string Text()
    {
        string header = DefinitionFiles.Serialize(wire);
        if (messages.Length == 0)
        {
            return emptyMessagesKey ? header + (header.EndsWith('\n') ? "" : "\n") + "messages: []\n" : header;
        }

        var sb = new System.Text.StringBuilder(header.Length + 256);
        sb.Append(header);
        if (header.Length > 0 && header[^1] != '\n')
        {
            sb.Append('\n');
        }

        // A sequence directly under its key, without extra indentation, is valid YAML and lets each message's text be used as is.
        sb.Append("messages:\n");
        foreach (Message m in messages)
        {
            sb.Append(InstanceWriter.MessageYaml(m));
        }

        return sb.ToString();
    }
}
