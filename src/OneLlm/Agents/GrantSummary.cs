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

    /// <summary>
    /// Written beside <see cref="Key"/> by every agent start. It tells an instance whose grants were recorded and then lost or damaged
    /// (the entry is missing, or is not a valid summary) from a file written before grants were recorded, which has neither entry.
    /// The first fails closed; the second keeps the runtime's grants. It is not a signature: someone who can rewrite the file can remove
    /// both entries, so the instance store itself must be out of the model's reach (the runtime protects its file store directory).
    /// </summary>
    internal const string RecordedKey = "capabilitiesRecorded";

    /// <summary>What an instance file says about its grants.</summary>
    internal enum Kind
    {
        /// <summary>A file from before grants were recorded: no entry and no marker. The runtime's grants apply.</summary>
        Legacy,

        /// <summary>A valid summary.</summary>
        Valid,

        /// <summary>Recorded, but the entry is missing or malformed. The instance gets no grants and does not run.</summary>
        Invalid,
    }

    /// <summary>Reads the grants out of an instance's extension data.</summary>
    /// <param name="extension">The instance's extension data, or null.</param>
    /// <param name="caps">The restored grants when the result is <see cref="Kind.Valid"/>.</param>
    /// <returns>Which of the three cases the file is.</returns>
    public static Kind Evaluate(IDictionary<string, object?>? extension, out ICapabilities? caps)
    {
        caps = null;
        bool hasEntry = extension is not null && extension.TryGetValue(Key, out _);
        bool recorded = extension is not null && extension.ContainsKey(RecordedKey);
        if (!hasEntry)
        {
            return recorded ? Kind.Invalid : Kind.Legacy;
        }

        caps = Restore(extension![Key]);
        return caps is null ? Kind.Invalid : Kind.Valid;
    }

    /// <summary>The error for an instance whose recorded grants cannot be read.</summary>
    public static BadDefinitionException Unreadable(string instanceId) =>
        new($"Instance {instanceId} cannot run: the grants it was started with are missing or damaged in its saved file, so it gets none. Start it again, or restore its file.", "instance " + instanceId);

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

    /// <summary>Rebuilds the grants a summary describes, or null when <paramref name="saved"/> is not a well-formed summary.</summary>
    public static ICapabilities? Restore(object? saved)
    {
        if (saved is not IDictionary<string, object?> map || !WellFormed(map))
        {
            return null;
        }

        try
        {
            return Build(map);
        }
        catch (ArgumentException)
        {
            // A root or host entry that cannot be parsed.
            return null;
        }
    }

    // Of() always writes readRoots, writeRoots, shell and network; a summary missing one, or with a value of the wrong type, was not
    // written by this library and is not trusted.
    private static bool WellFormed(IDictionary<string, object?> map)
    {
        if (!StringList(map, "readRoots") || !StringList(map, "writeRoots"))
        {
            return false;
        }

        if (!(map.TryGetValue("shell", out object? s) && s is IDictionary<string, object?> shell
            && shell.TryGetValue("enabled", out object? e) && e is bool
            && (!shell.TryGetValue("approval", out object? a) || a is bool)
            && (!shell.ContainsKey("commands") || StringList(shell, "commands"))))
        {
            return false;
        }

        if (!(map.TryGetValue("network", out object? n) && n is IDictionary<string, object?> net
            && net.TryGetValue("anyPublic", out object? any) && any is bool
            && StringList(net, "hosts")))
        {
            return false;
        }

        return (!map.ContainsKey("tools") || StringList(map, "tools"))
            && (!map.TryGetValue("workingDirectory", out object? wd) || wd is string);
    }

    private static bool StringList(IDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out object? v) && v is IEnumerable<object?> list && list.All(static i => i is string);

    private static Capabilities Build(IDictionary<string, object?> map)
    {
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
