using System.Text.Json.Nodes;
using OneLlm.Definitions;

namespace OneLlm.Agents;

/// <summary>
/// Agents that <c>run_agent_async</c> can start by name or path, besides definition files under the caller's file grants. Register
/// definitions here when agents should be startable by an agent without file access.
/// </summary>
public sealed class AgentDefinitionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a definition the registry does not hold, for example in a catalog of agents. Called after the registered names.</summary>
    public Func<string, AgentDefinition?>? Resolver { get; set; }

    /// <summary>Registers a definition under a name or path.</summary>
    /// <param name="name">What agents pass as <c>agentSwarmUri</c>.</param>
    /// <param name="definition">The definition. It is copied into each instance, so later changes do not reach running agents.</param>
    public void Add(string name, AgentDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            _byName[name] = definition;
        }
    }

    /// <summary>Finds a definition by name.</summary>
    /// <param name="name">The name or path.</param>
    /// <returns>The definition, or null.</returns>
    public AgentDefinition? Find(string name)
    {
        lock (_gate)
        {
            if (_byName.TryGetValue(name, out AgentDefinition? found))
            {
                return found;
            }
        }

        return Resolver?.Invoke(name);
    }
}

/// <summary>Resolves a path the way the file system will, so a symlink cannot lead a check at one place to a file at another.</summary>
internal static class SafePath
{
    /// <summary>The path with every symbolic link in it followed, as an absolute path. A part that does not exist is kept as written.</summary>
    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        string[] parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        int hops = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                if (++hops > 40)
                {
                    throw new IOException("Too many levels of symbolic links.");
                }

                string target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
                // The rest of the path continues under wherever the link led.
                string rest = i + 1 < parts.Length ? Path.Combine(parts[(i + 1)..]) : "";
                return Resolve(rest.Length == 0 ? target : Path.Combine(target, rest));
            }

            current = next;
        }

        return current;
    }

    /// <summary>True when the resolved path is inside one of the roots (themselves resolved).</summary>
    public static bool IsUnder(string path, IReadOnlyList<string> roots)
    {
        string resolved = Resolve(path);
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string root in roots)
        {
            string r = Resolve(root).TrimEnd(Path.DirectorySeparatorChar);
            if (resolved.Equals(r, cmp) || resolved.StartsWith(r + Path.DirectorySeparatorChar, cmp))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads tool inputs the way models write them: a number where a string is expected, a string where a number is.</summary>
internal static class ToolArgs
{
    public static string? String(JsonObject inputs, string name)
    {
        if (inputs[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out string? s))
        {
            return s.Trim();
        }

        return v.ToJsonString().Trim('"');
    }

    public static bool? Bool(JsonObject inputs, string name)
    {
        if (inputs[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out bool b))
        {
            return b;
        }

        return v.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed) ? parsed : null;
    }

    public static long? Int(JsonObject inputs, string name)
    {
        if (inputs[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        if (v.TryGetValue(out double d) && d == Math.Floor(d))
        {
            return (long)d;
        }

        return v.TryGetValue(out string? s) && long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }

    /// <summary>A single id or an array of ids, trimmed, without empties or duplicates, in order.</summary>
    public static List<string>? Ids(JsonObject inputs, string name)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        switch (inputs[name])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue(out string? one):
                Add(one);
                break;
            case JsonArray arr:
                foreach (JsonNode? n in arr)
                {
                    if (n is JsonValue item && item.TryGetValue(out string? s))
                    {
                        Add(s);
                    }
                    else
                    {
                        return null;
                    }
                }

                break;
            default:
                return null;
        }

        return result;

        void Add(string raw)
        {
            string t = raw.Trim();
            if (t.Length > 0 && seen.Add(t))
            {
                result.Add(t);
            }
        }
    }
}
