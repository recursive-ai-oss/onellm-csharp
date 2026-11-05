using OneLlm.Definitions;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tools;

/// <summary>
/// The grants two <see cref="ICapabilities"/> have in common: a path is allowed only under a root both allow, shell only as far as both
/// allow it, and a host only if both allow it. Sub-agents get their parent's grants intersected with whatever they ask for, so a child
/// can never hold more than its parent. Two <see cref="Capabilities"/> intersect with <see cref="Capabilities.Intersect"/>; this type
/// covers grants a host implemented itself.
/// </summary>
public sealed class CapabilityIntersection : ICapabilities
{
    private readonly ICapabilities _a;
    private readonly ICapabilities _b;
    private readonly string[] _read;
    private readonly string[] _write;

    /// <summary>Creates the intersection.</summary>
    /// <param name="a">One set of grants.</param>
    /// <param name="b">The other.</param>
    public CapabilityIntersection(ICapabilities a, ICapabilities b)
    {
        _a = a ?? throw new ArgumentNullException(nameof(a));
        _b = b ?? throw new ArgumentNullException(nameof(b));
        _read = Capabilities.IntersectRoots(a.ReadRoots, b.ReadRoots);
        _write = Capabilities.IntersectRoots(a.WriteRoots, b.WriteRoots);
    }

    /// <summary>Intersects two sets of grants, returning one of them unchanged when the other is the same object.</summary>
    /// <param name="a">One set of grants.</param>
    /// <param name="b">The other.</param>
    /// <returns>The common grants.</returns>
    public static ICapabilities Of(ICapabilities a, ICapabilities b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a, b))
        {
            return a;
        }

        return a is Capabilities ca && b is Capabilities cb ? ca.Intersect(cb) : new CapabilityIntersection(a, b);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FileRoots => _write;

    /// <inheritdoc />
    public IReadOnlyList<string> ReadRoots => _read;

    /// <inheritdoc />
    public IReadOnlyList<string> WriteRoots => _write;

    /// <inheritdoc />
    public string? WorkingDirectory => new[] { _a.WorkingDirectory, _b.WorkingDirectory }.FirstOrDefault(d => d is not null && _read.Any(r => Access.PathGuard.IsUnder(d, r))) ?? _read.FirstOrDefault();

    /// <inheritdoc />
    public ShellPolicy ShellAccess => _a.ShellAccess.Intersect(_b.ShellAccess);

    /// <inheritdoc />
    public bool ShellEnabled => ShellAccess.Enabled;

    /// <inheritdoc />
    public bool NetworkEnabled => _a.NetworkEnabled && _b.NetworkEnabled;

    /// <inheritdoc />
    public bool IsNetworkAllowed(Uri uri) => _a.IsNetworkAllowed(uri) && _b.IsNetworkAllowed(uri);

    /// <inheritdoc />
    public bool IsAddressAllowed(Uri uri, System.Net.IPAddress address) => _a.IsAddressAllowed(uri, address) && _b.IsAddressAllowed(uri, address);
}

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

    /// <summary>
    /// Creates a registry holding the agent tools (<c>task_complete</c>, <c>run_agent_async</c>, <c>await_agent</c>, ...) and the
    /// built-in tools with default options. Having a tool registered does not grant it: what it may touch is decided by the runtime's
    /// capabilities. Packages that add tools (<c>run_decider</c>, web fetching) register theirs with <see cref="Add(ITool)"/>.
    /// </summary>
    public ToolRegistry()
    {
        foreach (ITool tool in Agents.AgentTools.All)
        {
            _tools[tool.Definition.Name] = tool;
        }

        foreach (ITool tool in BuiltinTools.Create())
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

    /// <summary>Every registered tool, in name order.</summary>
    public IReadOnlyList<ITool> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _tools.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => p.Value)];
            }
        }
    }

    /// <summary>Replaces the built-in tools with ones made from these options (a search provider, another state directory, output limits).</summary>
    /// <param name="options">The options.</param>
    /// <returns>This registry.</returns>
    public ToolRegistry UseBuiltins(BuiltinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            foreach (string name in BuiltinTools.Names)
            {
                _tools.Remove(name);
            }
        }

        foreach (ITool tool in BuiltinTools.Create(options))
        {
            Add(tool);
        }

        return this;
    }

    /// <summary>
    /// Makes the resolver that decides which of a definition's references this runtime grants, for the given grants. Routine paths the
    /// registry knows (<see cref="AddRoutine"/>, or a path whose last segment is a registered tool's name, hyphens as underscores, so
    /// <c>/agents/runtime/await-agent</c> finds <c>await_agent</c>) are mapped on top of the built-in tool groups.
    /// </summary>
    /// <param name="capabilities">What the agent may do.</param>
    /// <param name="references">The references that will be checked.</param>
    /// <returns>The resolver.</returns>
    public ToolReferenceResolver CreateResolver(ICapabilities capabilities, IEnumerable<ToolReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        ToolReferenceResolver resolver;
        lock (_gate)
        {
            resolver = new ToolReferenceResolver(_tools.Values, capabilities);
            foreach ((string uri, List<string> names) in _routines)
            {
                resolver.MapSource(uri, [.. names]);
            }

            foreach (ToolReference r in references)
            {
                string? source = r.Routine ?? r.Swarm;
                if (source is null || _routines.ContainsKey(source) || BuiltinTools.ToolsForRoutine(source) is not null)
                {
                    continue;
                }

                string last = source.TrimEnd('/');
                last = last[(last.LastIndexOf('/') + 1)..].Replace('-', '_');
                if (r.Routine is not null && _tools.ContainsKey(last))
                {
                    resolver.MapSource(source, last);
                }
            }
        }

        return resolver;
    }
}
