using OneLlm.Providers.Anthropic;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Providers;

/// <summary>
/// The providers that ship in the core package. <see cref="Register"/> adds a factory for each to a registry; a factory reads its
/// API key from the context (which the registry took from the variable the catalog names) and creates the client lazily.
/// </summary>
public static class BuiltInProviders
{
    /// <summary>Registers the shipped catalog text of every built-in provider so the default catalog loads it. Safe to call repeatedly.</summary>
    public static void RegisterCatalogs()
    {
        AnthropicCatalog.Register();
        OpenAICatalog.Register();
        // The Gemini catalog registers here when its provider joins the package.
    }

    /// <summary>Registers the core package's provider factories on a registry.</summary>
    /// <param name="registry">The registry.</param>
    public static void Register(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("anthropic", static ctx => new AnthropicProvider(
            new AnthropicDirectDeployment(ctx.ApiKey ?? throw new AuthException("anthropic", "No API key. Set the environment variable ANTHROPIC_API_KEY.", null)),
            ctx.TimeProvider));
        registry.Register("openai", static ctx => new OpenAIProvider(new OpenAIOptions { ApiKey = ctx.ApiKey }, ctx.TimeProvider));
        // One factory serves every preset of the compatible provider (Ollama, vLLM, OpenRouter, ...): the preset names the server.
        registry.Register(OpenAICompatibleProvider.CatalogProvider, static ctx =>
        {
            string? baseUrl = ctx.Model.Preset?.BaseUrl;
            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? address))
            {
                throw new BadDefinitionException($"The preset '{ctx.Model.PresetName}' has no valid baseUrl in the catalog.");
            }

            return new OpenAICompatibleProvider(ctx.Model.PresetName ?? OpenAICompatibleProvider.CatalogProvider, new OpenAIOptions { ApiKey = ctx.ApiKey, BaseUrl = address }, ctx.TimeProvider);
        });
        // Gemini registers here by provider name when its client joins the package.
    }
}
