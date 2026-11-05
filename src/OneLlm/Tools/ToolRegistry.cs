using OneLlm.Definitions;

namespace OneLlm.Tools;

/// <summary>
/// The grants two <see cref="ICapabilities"/> have in common: a path is allowed only under a root both allow, shell only if both enable
/// it, and a host only if both allow it. Sub-agents get their parent's grants intersected with whatever they ask for, so a child can
/// never hold more than its parent.
/// </summary>
public sealed class CapabilityIntersection : ICapabilities
{
    private readonly ICapabilities _a;
    private readonly ICapabilities _b;
    private readonly IReadOnlyList<string> _roots;

    /// <summary>Creates the intersection.</summary>
    /// <param name="a">One set of grants.</param>
    /// <param name="b">The other.</param>
    public CapabilityIntersection(ICapabilities a, ICapabilities b)
    {
        _a = a ?? throw new ArgumentNullException(nameof(a));
        _b = b ?? throw new ArgumentNullException(nameof(b));
        _roots = IntersectRoots(a.FileRoots, b.FileRoots);
    }

    /// <summary>Intersects two sets of grants, returning one of them unchanged when the other is the same object.</summary>
    /// <param name="a">One set of grants.</param>
    /// <param name="b">The other.</param>
    /// <returns>The common grants.</returns>
    public static ICapabilities Of(ICapabilities a, ICapabilities b) => ReferenceEquals(a, b) ? a : new CapabilityIntersection(a, b);

    /// <inheritdoc />
    public IReadOnlyList<string> FileRoots => _roots;

    /// <inheritdoc />
    public bool ShellEnabled => _a.ShellEnabled && _b.ShellEnabled;

    /// <inheritdoc />
    public bool IsNetworkAllowed(Uri uri) => _a.IsNetworkAllowed(uri) && _b.IsNetworkAllowed(uri);

    private static List<string> IntersectRoots(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var result = new List<string>();
        foreach (string x in a)
        {
            foreach (string y in b)
            {
                string fx = Normalize(x);
                string fy = Normalize(y);
                // The deeper of two nested roots is the part both allow; unrelated roots share nothing.
                string? common = IsUnder(fx, fy) ? fx : IsUnder(fy, fx) ? fy : null;
                if (common is not null && !result.Contains(common, PathComparer))
                {
                    result.Add(common);
                }
            }
        }

        return result;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool IsUnder(string candidate, string root)
    {
        if (PathComparer.Equals(candidate, root))
        {
            return true;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>A tool reference the registry could not turn into tools.</summary>
/// <param name="Reference">The reference from the definition.</param>
/// <param name="Name">The tool name or routine that did not resolve.</param>
public sealed record UnresolvedReference(ToolReference Reference, string Name);

/// <summary>
/// The tools a runtime can offer agents, by name, plus the routine paths and swarm names definitions use to ask for them. A definition's
/// <c>references</c> are resolved against it; nothing here is granted by a definition, so a reference to a tool the host did not
/// register fails to load.
/// </summary>
public sealed class ToolRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _routines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a registry holding the agent tools (<c>task_complete</c>, <c>run_agent_async</c>, <c>await_agent</c>, ...).</summary>
    public ToolRegistry()
    {
        foreach (ITool tool in Agents.AgentTools.All)
        {
            _tools[tool.Definition.Name] = tool;
        }
    }

    /// <summary>The names of the registered tools, in name order.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _tools.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Registers a tool, replacing one of the same name.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>This registry.</returns>
    public ToolRegistry Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_gate)
        {
            _tools[tool.Definition.Name] = tool;
        }

        return this;
    }

    /// <summary>Registers every tool of a generated tool class.</summary>
    /// <typeparam name="T">A partial class with <see cref="ToolAttribute"/> methods.</typeparam>
    /// <param name="instance">The object instance methods run on; null makes one when the class has a parameterless constructor.</param>
    /// <returns>This registry.</returns>
    public ToolRegistry Add<T>(T? instance = null)
        where T : class, IToolSource<T>
    {
        foreach (ITool tool in T.GetTools(instance))
        {
            Add(tool);
        }

        return this;
    }

    /// <summary>Registers tools under a routine path or swarm name, so a definition that references that path gets all of them.</summary>
    /// <param name="uri">The path or name a definition's <c>routine:</c> or <c>swarm:</c> uses.</param>
    /// <param name="tools">The tools. They are also registered by name.</param>
    /// <returns>This registry.</returns>
    public ToolRegistry AddRoutine(string uri, params ITool[] tools)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        lock (_gate)
        {
            if (!_routines.TryGetValue(uri, out List<string>? names))
            {
                _routines[uri] = names = [];
            }

            foreach (ITool tool in tools)
            {
                _tools[tool.Definition.Name] = tool;
                names.Add(tool.Definition.Name);
            }
        }

        return this;
    }

    /// <summary>Finds a tool by name.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="tool">The tool when found.</param>
    /// <returns>True when registered.</returns>
    public bool TryGet(string name, out ITool tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool!);
        }
    }

    /// <summary>
    /// Turns a definition's references into tools. A reference with a <c>tools:</c> list asks for those names; otherwise a
    /// <c>routine:</c> path matches a registered routine, or its last segment (hyphens as underscores) a tool name; a <c>swarm:</c>
    /// matches a registered routine group.
    /// </summary>
    /// <param name="references">The references, in file order.</param>
    /// <param name="unresolved">The references that matched nothing.</param>
    /// <returns>The tools, each once, in first-mention order.</returns>
    public IReadOnlyList<ITool> Resolve(IEnumerable<ToolReference> references, out IReadOnlyList<UnresolvedReference> unresolved)
    {
        ArgumentNullException.ThrowIfNull(references);
        var tools = new List<ITool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<UnresolvedReference>();

        void Take(ITool tool)
        {
            if (seen.Add(tool.Definition.Name))
            {
                tools.Add(tool);
            }
        }

        lock (_gate)
        {
            foreach (ToolReference r in references)
            {
                if (r.Tools is { } names)
                {
                    foreach (string name in names)
                    {
                        if (_tools.TryGetValue(name, out ITool? t))
                        {
                            Take(t);
                        }
                        else
                        {
                            missing.Add(new UnresolvedReference(r, name));
                        }
                    }

                    continue;
                }

                string? uri = r.Routine ?? r.Swarm;
                if (uri is null)
                {
                    missing.Add(new UnresolvedReference(r, ""));
                    continue;
                }

                if (_routines.TryGetValue(uri, out List<string>? group))
                {
                    foreach (string name in group)
                    {
                        Take(_tools[name]);
                    }

                    continue;
                }

                string last = uri.TrimEnd('/');
                last = last[(last.LastIndexOf('/') + 1)..].Replace('-', '_');
                if (r.Routine is not null && _tools.TryGetValue(last, out ITool? byName))
                {
                    Take(byName);
                }
                else
                {
                    missing.Add(new UnresolvedReference(r, uri));
                }
            }
        }

        unresolved = missing;
        return tools;
    }
}
