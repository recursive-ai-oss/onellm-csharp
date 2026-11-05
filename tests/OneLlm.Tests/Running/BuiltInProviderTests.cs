using OneLlm.Catalog;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Running;

public class BuiltInProviderTests
{
    private static Runtime Make(Dictionary<string, string> env) => new()
    {
        GetEnvironmentVariable = n => env.GetValueOrDefault(n),
        Providers = ProviderRegistry.CreateWithDefaults(n => env.GetValueOrDefault(n)),
    };

    [Fact]
    public void The_default_catalog_and_registry_create_the_real_providers_from_environment_keys()
    {
        Runtime rt = Make(new() { ["ANTHROPIC_API_KEY"] = "test-a", ["OPENAI_API_KEY"] = "test-o" });

        ResolvedModel claude = rt.Catalog.Providers.First(p => p.Name == "anthropic").Models!.Select(m => rt.Resolve("anthropic:" + m.Name)).First();
        ResolvedModel gpt = rt.Catalog.Providers.First(p => p.Name == "openai").Models!.Select(m => rt.Resolve("openai:" + m.Name)).First();

        Assert.IsType<AnthropicProvider>(rt.Providers.Get(claude, TimeProvider.System));
        Assert.IsType<OpenAIProvider>(rt.Providers.Get(gpt, TimeProvider.System));
    }

    [Fact]
    public void A_compatible_preset_on_this_machine_needs_no_key_and_others_name_their_variable()
    {
        Runtime rt = Make([]);

        IProvider local = rt.Providers.Get(rt.Resolve("ollama:llama3"), TimeProvider.System);
        Assert.IsType<OpenAICompatibleProvider>(local);
        Assert.Equal("ollama", local.Name);

        AuthException e = Assert.Throws<AuthException>(() => rt.Providers.Get(rt.Resolve("groq:llama-3.3-70b"), TimeProvider.System));
        Assert.Contains("GROQ_API_KEY", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_anthropic_key_names_the_variable()
    {
        Runtime rt = Make([]);
        ResolvedModel claude = rt.Catalog.Providers.First(p => p.Name == "anthropic").Models!.Select(m => rt.Resolve("anthropic:" + m.Name)).First();
        AuthException e = Assert.Throws<AuthException>(() => rt.Providers.Get(claude, TimeProvider.System));
        Assert.Contains("ANTHROPIC_API_KEY", e.Message, StringComparison.Ordinal);
    }
}
