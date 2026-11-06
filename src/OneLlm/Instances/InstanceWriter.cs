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
    public static InstanceSnapshot Capture(Instance instance, InstanceSnapshot? previous = null)
    {
        AgentFileWire wire = DefinitionFiles.ToWire(instance, includeInitialPrompts: true);
        // The header is written to text only when the snapshot is read, so what a run changes later (approvals, extension data) must not
        // reach into it: the collections that change are copied here. A copy the previous save already made is reused when nothing in the
        // collection differs, which is the usual case from one turn to the next.
        wire.PendingApprovals = wire.PendingApprovals is null ? null : [.. wire.PendingApprovals];
        wire.Compactions = wire.Compactions is null ? null : [.. wire.Compactions];
        wire.ExtensionData = CopyOrReuse(wire.ExtensionData, previous?.Wire.ExtensionData);
        wire.Inputs = CopyOrReuse(wire.Inputs, previous?.Wire.Inputs);
        wire.Headers = wire.Headers is null ? null : new Dictionary<string, string>(wire.Headers);
        bool emptyKey = instance.EmptyKeys is { } k && k.Contains("messages");
        List<Message> messages = instance.Messages;
        (Message[] buffer, int count) = previous is null ? ([.. messages], messages.Count) : previous.Extend(messages);
        return new InstanceSnapshot(wire, buffer, count, emptyKey);
    }

    private static Dictionary<string, object?>? CopyOrReuse(Dictionary<string, object?>? live, Dictionary<string, object?>? kept)
    {
        if (live is null)
        {
            return null;
        }

        if (kept is not null && kept.Count == live.Count)
        {
            bool same = true;
            foreach ((string key, object? value) in live)
            {
                if (!kept.TryGetValue(key, out object? old) || !(ReferenceEquals(old, value) || (value is string or bool or int or long && Equals(old, value))))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return kept;
            }
        }

        return new Dictionary<string, object?>(live);
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

/// <summary>
/// An instance as of one save: its header and its messages. <see cref="Text"/> gives the same YAML <see cref="Instance.ToYaml"/> would have.
/// The messages are the first <c>count</c> slots of a buffer that the next save of the same instance may extend in place, so appending a
/// turn does not copy the whole history; slots below <c>count</c> never change.
/// </summary>
internal sealed class InstanceSnapshot(AgentFileWire wire, Message[] messages, int count, bool emptyMessagesKey)
{
    public AgentFileWire Wire => wire;

    /// <summary>
    /// The buffer and length for <paramref name="live"/>, reusing this snapshot's buffer when the live list starts with the same messages.
    /// Call only for the snapshot the store currently holds, under the store's lock: older snapshots are then unreachable, so slots above
    /// <c>count</c> are free to write.
    /// </summary>
    public (Message[] Buffer, int Count) Extend(List<Message> live)
    {
        int n = live.Count;
        int shared = Math.Min(count, n);
        for (int i = 0; i < shared; i++)
        {
            if (!ReferenceEquals(messages[i], live[i]))
            {
                return ([.. live], n);
            }
        }

        Message[] buffer = messages;
        if (n > buffer.Length)
        {
            buffer = new Message[Math.Max(n, buffer.Length * 2)];
            Array.Copy(messages, buffer, shared);
        }
        else if (n < count)
        {
            Array.Clear(buffer, n, count - n);
        }

        for (int i = shared; i < n; i++)
        {
            buffer[i] = live[i];
        }

        return (buffer, n);
    }

    public string Text()
    {
        string header = DefinitionFiles.Serialize(wire);
        if (count == 0)
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
        for (int i = 0; i < count; i++)
        {
            sb.Append(InstanceWriter.MessageYaml(messages[i]));
        }

        return sb.ToString();
    }
}
