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

    public static string Write(Instance instance)
    {
        AgentFileWire wire = DefinitionFiles.ToWire(instance, includeInitialPrompts: true);
        string header = DefinitionFiles.Serialize(wire);
        if (instance.Messages.Count == 0)
        {
            return instance.EmptyKeys is { } k && k.Contains("messages") ? header + (header.EndsWith('\n') ? "" : "\n") + "messages: []\n" : header;
        }

        var sb = new System.Text.StringBuilder(header.Length + 256);
        sb.Append(header);
        if (header.Length > 0 && header[^1] != '\n')
        {
            sb.Append('\n');
        }

        // A sequence directly under its key, without extra indentation, is valid YAML and lets each message's text be used as is.
        sb.Append("messages:\n");
        foreach (Message m in instance.Messages)
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

            sb.Append(text);
        }

        return sb.ToString();
    }
}
