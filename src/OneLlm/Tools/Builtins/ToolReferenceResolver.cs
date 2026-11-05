using OneLlm.Definitions;

namespace OneLlm.Tools.Builtins;

/// <summary>What <see cref="ToolReferenceResolver"/> found for one reference in a definition.</summary>
public sealed class ReferenceCheck
{
    internal ReferenceCheck(IReadOnlyList<ITool> granted, IReadOnlyList<string> problems)
    {
        GrantedTools = granted;
        Problems = problems;
    }

    /// <summary>True when the source is known, every tool it names is registered, and the grants cover each one.</summary>
    public bool IsGranted => Problems.Count == 0;

    /// <summary>The tools of the reference that can be used under the grants. For a partly granted reference this is the usable part.</summary>
    public IReadOnlyList<ITool> GrantedTools { get; }

    /// <summary>Why the reference is not granted, one line each, for example <c>tool 'execute_terminal_command' needs shell access</c>.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Decides, when a definition is loaded, which of its tool references the runtime can honour. A reference is granted only when it names
/// known tools and the grants cover every one. Pass <see cref="Strict"/> or <see cref="Lenient"/> to the definition loader: an ungranted
/// reference then fails the load or is stripped with a warning.
/// </summary>
/// <remarks>Routine paths of the built-in tools are understood (<c>/agent-tools/os/read-file</c>, the <c>/agent-tools/os</c> group, or a bare tool name).
/// Other routines and swarms are known only after <see cref="MapSource"/>.</remarks>
public sealed class ToolReferenceResolver
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly ICapabilities _capabilities;
    private readonly Dictionary<string, string[]> _sources = new(StringComparer.Ordinal);

    /// <summary>Creates a resolver.</summary>
    /// <param name="tools">The tools the runtime has registered.</param>
    /// <param name="capabilities">The runtime's grants.</param>
    public ToolReferenceResolver(IEnumerable<ITool> tools, ICapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
    }

    /// <summary>Teaches the resolver that a routine or swarm path stands for some registered tools.</summary>
    /// <param name="source">The path as written in a definition.</param>
    /// <param name="toolNames">The tools it provides.</param>
    public void MapSource(string source, params string[] toolNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources[source.Trim().Trim('/')] = toolNames;
    }

    /// <summary>Load options that fail on an ungranted reference.</summary>
    public DefinitionLoadOptions Strict => new() { IsStrict = true, IsGranted = IsGranted };

    /// <summary>Load options that strip an ungranted reference and record a warning.</summary>
    public DefinitionLoadOptions Lenient => new() { IsStrict = false, IsGranted = IsGranted };

    /// <summary>Whether the runtime can honour <paramref name="reference"/>. This is what <see cref="DefinitionLoadOptions.IsGranted"/> calls.</summary>
    /// <param name="reference">A reference from a definition.</param>
    /// <returns>True when it is granted in full.</returns>
    public bool IsGranted(ToolReference reference) => Check(reference).IsGranted;

    /// <summary>Resolves a reference to tools and says what is missing.</summary>
    /// <param name="reference">A reference from a definition.</param>
    /// <returns>The usable tools and the problems.</returns>
    public ReferenceCheck Check(ToolReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var problems = new List<string>();
        string? source = reference.Routine ?? reference.Swarm;
        IReadOnlyList<string>? provided = null;
        if (source is not null)
        {
            provided = _sources.TryGetValue(source.Trim().Trim('/'), out string[]? mapped) ? mapped : BuiltinTools.ToolsForRoutine(source);
            if (provided is null)
            {
                problems.Add($"the tool source '{source}' is not known to this runtime");
            }
        }
        else if (reference.Tools is null)
        {
            problems.Add("the reference names no routine, swarm or tools");
        }

        IReadOnlyList<string> wanted = reference.Tools ?? provided ?? [];
        var granted = new List<ITool>();
        foreach (string name in wanted)
        {
            if (provided is not null && reference.Tools is not null && !provided.Contains(name, StringComparer.Ordinal))
            {
                problems.Add($"tool '{name}' is not part of '{source}'");
                continue;
            }

            if (!_tools.TryGetValue(name, out ITool? tool))
            {
                problems.Add(name == "fetch_webpage_markdown"
                    ? "tool 'fetch_webpage_markdown' needs the RecursiveAI.OneLlm.Web package"
                    : $"tool '{name}' is not registered with this runtime");
                continue;
            }

            string? missing = tool is ICapabilityTool c ? Missing(c.Requires) : null;
            if (missing is not null)
            {
                problems.Add($"tool '{name}' needs {missing}");
                continue;
            }

            granted.Add(tool);
        }

        return new ReferenceCheck(granted, problems);
    }

    /// <summary>The tools for a definition's references that the grants cover, without duplicates, in reference order.</summary>
    /// <param name="references">The definition's references.</param>
    /// <returns>The usable tools. Ungranted tools are left out; use <see cref="Check"/> to find out why.</returns>
    public IReadOnlyList<ITool> ToolsFor(IEnumerable<ToolReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return [.. references.SelectMany(r => Check(r).GrantedTools).DistinctBy(t => t.Definition.Name)];
    }

    private string? Missing(ToolCapability needs)
    {
        if (needs.HasFlag(ToolCapability.FileRead) && _capabilities.ReadRoots.Count == 0)
        {
            return "a file root (read access)";
        }

        if (needs.HasFlag(ToolCapability.FileWrite) && _capabilities.WriteRoots.Count == 0)
        {
            return "a file root with write access";
        }

        if (needs.HasFlag(ToolCapability.Shell) && !_capabilities.ShellEnabled)
        {
            return "shell access";
        }

        if (needs.HasFlag(ToolCapability.Network) && !_capabilities.NetworkEnabled)
        {
            return "network access";
        }

        return null;
    }
}
