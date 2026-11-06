using OneLlm.Tools;

namespace OneLlm.Agents;

/// <summary>
/// What an instance was allowed to do when it started, in a form that is saved with it (the <c>capabilities</c> entry of the instance's
/// extension data). A resumed instance gets these grants intersected with the resuming runtime's, so narrowing a run (a read-only start, a
/// sub-agent's inherited grants) survives a restart, a message to a resting instance and a lazy load. The summary can only narrow:
/// whatever it says, the runtime's own grants stay the ceiling.
/// </summary>
internal static class GrantSummary
{
    internal const string Key = "capabilities";

    /// <summary>Describes <paramref name="caps"/> as plain values for the instance file.</summary>
    public static Dictionary<string, object?> Of(ICapabilities caps)
    {
        ShellPolicy shell = caps.ShellAccess;
        var shellMap = new Dictionary<string, object?>
        {
            ["enabled"] = shell.Enabled,
            ["approval"] = shell.RequiresApproval,
        };
        if (shell.Commands is { } commands)
        {
            shellMap["commands"] = commands.Select(static c => (object?)c).ToList();
        }

        // Only the built-in grants can say which hosts they allow; any other implementation is saved with network off, so a resumed
        // instance never gets more network than it was shown to have.
        var network = new Dictionary<string, object?>
        {
            ["anyPublic"] = caps is Capabilities { AnyPublicNetwork: true },
            ["hosts"] = (caps is Capabilities c ? c.NetworkHosts.Select(static h => (object?)h.Text).ToList() : []),
        };

        var map = new Dictionary<string, object?>
        {
            ["readRoots"] = caps.ReadRoots.Select(static r => (object?)r).ToList(),
            ["writeRoots"] = caps.WriteRoots.Select(static r => (object?)r).ToList(),
            ["shell"] = shellMap,
            ["network"] = network,
        };
        if (caps.WorkingDirectory is { } wd)
        {
            map["workingDirectory"] = wd;
        }

        return map;
    }

    /// <summary>The tool names a saved summary limits the instance to, or null for no limit.</summary>
    public static IReadOnlySet<string>? SavedToolCeiling(object? saved) =>
        saved is IDictionary<string, object?> map && map.ContainsKey("tools") ? new HashSet<string>(Strings(map, "tools"), StringComparer.Ordinal) : null;

    /// <summary>Rebuilds the grants a summary describes, or null when <paramref name="saved"/> is not a summary (an older instance file).</summary>
    public static ICapabilities? Restore(object? saved)
    {
        if (saved is not IDictionary<string, object?> map)
        {
            return null;
        }

        Capabilities caps = Capabilities.None;
        foreach (string root in Strings(map, "readRoots"))
        {
            caps += Capabilities.ReadOnlyFiles(root);
        }

        foreach (string root in Strings(map, "writeRoots"))
        {
            caps += Capabilities.Files(root);
        }

        if (map.TryGetValue("workingDirectory", out object? wd) && wd is string wdText && caps.ReadRoots.Count > 0)
        {
            try
            {
                caps = caps.WithWorkingDirectory(wdText);
            }
            catch (CapabilityMissingException)
            {
                // A working directory outside the roots is dropped; the first root stays the default.
            }
        }

        if (map.TryGetValue("shell", out object? s) && s is IDictionary<string, object?> shell && shell.TryGetValue("enabled", out object? e) && e is true)
        {
            bool approval = shell.TryGetValue("approval", out object? a) && a is true;
            string[]? commands = shell.ContainsKey("commands") ? [.. Strings(shell, "commands")] : null;
            ShellPolicy policy = approval
                ? ShellPolicy.LoopApprovalFor(commands)
                : commands is null ? ShellPolicy.Open : ShellPolicy.AllowList(commands);
            caps = caps.WithShell(policy);
        }

        if (map.TryGetValue("network", out object? n) && n is IDictionary<string, object?> net)
        {
            if (net.TryGetValue("anyPublic", out object? any) && any is true)
            {
                caps += Capabilities.AnyPublicHosts;
            }

            string[] hosts = [.. Strings(net, "hosts")];
            if (hosts.Length > 0)
            {
                caps += Capabilities.Network(hosts);
            }
        }

        return caps;
    }

    private static IEnumerable<string> Strings(IDictionary<string, object?> map, string key)
    {
        if (map.TryGetValue(key, out object? v) && v is IEnumerable<object?> list)
        {
            foreach (object? item in list)
            {
                if (item is string text)
                {
                    yield return text;
                }
            }
        }
    }
}
