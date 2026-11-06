using OneLlm.Definitions;

namespace OneLlm.Cli;

/// <summary>The example agents embedded in the tool, run as <c>examples/&lt;name&gt;</c>.</summary>
internal static class Examples
{
    private const string Prefix = "examples/";
    private const string Suffix = "/agent.yaml";

    // The build names each resource from the file's directory with the platform separator (examples/orchestratorgent.yaml on Windows),
    // so names are matched with both separators folded to '/' and the real resource name is kept for opening the stream.
    private static readonly Dictionary<string, string> s_resources = typeof(Examples).Assembly.GetManifestResourceNames()
        .Select(static n => (Folded: n.Replace('\\', '/'), Real: n))
        .Where(static n => n.Folded.StartsWith(Prefix, StringComparison.Ordinal) && n.Folded.EndsWith(Suffix, StringComparison.Ordinal))
        .ToDictionary(static n => n.Folded[Prefix.Length..^Suffix.Length], static n => n.Real, StringComparer.Ordinal);

    /// <summary>The names of the embedded examples, in name order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. s_resources.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Whether a run target names an embedded example.</summary>
    public static bool IsExample(string target) => target.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The YAML text of an example.</summary>
    /// <exception cref="UsageException">There is no such example.</exception>
    public static string Text(string target)
    {
        string name = target.StartsWith(Prefix, StringComparison.Ordinal) ? target[Prefix.Length..] : target;
        using Stream? s = s_resources.TryGetValue(name, out string? real) ? typeof(Examples).Assembly.GetManifestResourceStream(real) : null;
        if (s is null)
        {
            throw new UsageException($"There is no example '{name}'. Examples: {string.Join(", ", Names)}.");
        }

        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    /// <summary>Loads an example strictly, so a reference the runtime does not offer fails with its position.</summary>
    public static AgentDefinition Load(string target)
    {
        string name = target[Prefix.Length..];
        return AgentDefinition.Parse(Text(target), Prefix + name + Suffix);
    }
}
