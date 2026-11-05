namespace OneLlm;

/// <summary>Names the OneLLM assembly for code that needs a type from it, such as test projects.</summary>
public static class AssemblyMarker
{
    /// <summary>The package version, as built.</summary>
    public static string Version { get; } =
        typeof(AssemblyMarker).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
}
