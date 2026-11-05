using OneLlm.Definitions;

namespace OneLlm.Cli;

/// <summary>The example agents embedded in the tool, run as <c>examples/&lt;name&gt;</c>.</summary>
internal static class Examples
{
    private const string Prefix = "examples/";
    private const string Suffix = "/agent.yaml";

    /// <summary>The names of the embedded examples, in name order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [.. typeof(Examples).Assembly.GetManifestResourceNames()
            .Where(static n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
            .Select(static n => n[Prefix.Length..^Suffix.Length])
            .Order(StringComparer.Ordinal)];

    /// <summary>Whether a run target names an embedded example.</summary>
    public static bool IsExample(string target) => target.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The YAML text of an example.</summary>
    /// <exception cref="UsageException">There is no such example.</exception>
    public static string Text(string target)
    {
        string name = target.StartsWith(Prefix, StringComparison.Ordinal) ? target[Prefix.Length..] : target;
        using Stream? s = typeof(Examples).Assembly.GetManifestResourceStream(Prefix + name + Suffix);
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
