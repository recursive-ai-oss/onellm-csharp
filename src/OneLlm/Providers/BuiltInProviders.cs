namespace OneLlm.Providers;

/// <summary>
/// The providers that ship in the core package. <see cref="Register"/> adds a factory for each to a registry; a factory reads its
/// API key from the context (which the registry took from the variable the catalog names) and creates the client lazily.
/// </summary>
public static class BuiltInProviders
{
    /// <summary>Registers the core package's provider factories on a registry.</summary>
    /// <param name="registry">The registry.</param>
    public static void Register(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        // The Anthropic, OpenAI (Responses and Chat Completions, with the compatible presets) and Gemini clients register
        // here by provider name and preset protocol as they join the package.
    }
}
