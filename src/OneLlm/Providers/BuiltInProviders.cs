using System.Runtime.CompilerServices;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
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
        TypeSafe.TypeSafeCatalog.Register();
        // The Gemini provider registers its catalog in its static constructor (the first time the type is used); running it here
        // makes the default catalog the same whether or not a Gemini client has been made yet.
        RuntimeHelpers.RunClassConstructor(typeof(GeminiProvider).TypeHandle);
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
        registry.Register("typesafe", static ctx => new TypeSafe.JevProvider(
            new TypeSafe.JevOptions { ApiKey = ctx.ApiKey ?? throw new AuthException("typesafe", "No API key. Set the environment variable TYPESAFE_API_KEY.", null) },
            ctx.TimeProvider));
        registry.Register("gemini", static ctx => new GeminiProvider(new GeminiOptions
        {
            Time = ctx.TimeProvider,
            Auth = new GeminiApiKeyAuth(ctx.ApiKey ?? throw new AuthException("gemini", "No API key. Set the environment variable GEMINI_API_KEY.", null)),
        }));
    }
}
