using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAICompatiblePresetTests
{
    public static TheoryData<string, string, string?> Presets => new()
    {
        { "ollama", "llama3.2", "reasoning" },
        { "vllm", "Qwen/Qwen3-8B", "reasoning_content" },
        { "lmstudio", "qwen/qwen3-8b", "reasoning_content" },
        { "openrouter", "openai/gpt-oss-20b", "reasoning" },
        { "groq", "openai/gpt-oss-20b", "reasoning" },
        { "deepseek", "deepseek-reasoner", "reasoning_content" },
        { "xai", "grok-4", "reasoning_content" },
        { "mistral", "mistral-small-latest", null },
        { "together", "meta-llama/Llama-3.3-70B-Instruct-Turbo", "reasoning_content" },
    };

    private static ChatRequest Hello(string model) => new() { Model = model, System = "Be brief.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64 };

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task A_preset_answers_a_chat_call(string preset, string model, string? reasoningField)
    {
        using var fx = OpenAIFx.Open($"openai-compatible/{preset}/chat.json");
        OpenAICompatibleProvider p = OpenAIFx.Preset(fx, preset);
        Assert.Equal(preset, p.Name);
        ChatResponse r = await p.SendAsync(Hello(model));
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal(FinishReason.Stop, r.Finish);
        Assert.Equal(24, r.Usage!.TotalInputTokens);
        Assert.Equal(9, r.Usage.OutputTokens);
        Assert.Equal(8, r.Usage.CacheReadInputTokens);
        Assert.Equal(16, r.Usage.InputTokens);
        if (reasoningField is null)
        {
            Assert.DoesNotContain(r.Message.Assistant, p => p is ReasoningPart);
        }
        else
        {
            Assert.Equal("Thinking about a greeting.", Assert.IsType<ReasoningPart>(r.Message.Assistant[0]).Display);
            Assert.Equal(3, r.Usage.Counters!["reasoningTokens"]);
        }

        fx.AssertAllUsed();
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task A_preset_streams_the_message_it_answers_whole(string preset, string model, string? reasoningField)
    {
        using var whole = OpenAIFx.Open($"openai-compatible/{preset}/chat.json");
        using var streamed = OpenAIFx.Open($"openai-compatible/{preset}/stream.json");
        ChatResponse a = await OpenAIFx.Preset(whole, preset).SendAsync(Hello(model));
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.Preset(streamed, preset), Hello(model));
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
        Assert.Equal(a.Usage!.CacheReadInputTokens, b.Usage!.CacheReadInputTokens);
        Assert.Equal(reasoningField is null, b.Message.Assistant.All(p => p is not ReasoningPart));
        streamed.AssertAllUsed();
    }

    [Fact]
    public async Task OpenRouter_reports_the_cost_it_charged_in_the_usage()
    {
        using var fx = OpenAIFx.Open("openai-compatible/openrouter/chat.json");
        ChatResponse r = await OpenAIFx.Preset(fx, "openrouter").SendAsync(Hello("openai/gpt-oss-20b"));
        Assert.Equal(0.00012m, r.Usage!.Cost);
    }

    [Fact]
    public void Every_preset_in_the_catalog_has_a_base_url_and_builds()
    {
        ProviderSpec spec = OpenAIFx.Catalog.FindProvider(OpenAICompatibleProvider.CatalogProvider)!;
        Assert.Equal(9, spec.Presets!.Count);
        foreach ((string name, PresetSpec preset) in spec.Presets)
        {
            Assert.False(string.IsNullOrEmpty(preset.BaseUrl), name);
            Assert.Equal("chat-completions", preset.Protocol);
            OpenAICompatibleProvider p = OpenAICompatibleProvider.FromPreset(OpenAIFx.Catalog, name, TimeProvider.System);
            Assert.Equal(name, p.Name);
            Assert.Equal(preset.Properties?.GetValueOrDefault("local") is true, p.IsLoopback);
        }
    }

    [Fact]
    public void An_unknown_preset_is_a_model_not_found_error()
    {
        Assert.Throws<ModelNotFoundException>(() => OpenAICompatibleProvider.FromPreset(OpenAIFx.Catalog, "nosuch", TimeProvider.System));
    }

    [Fact]
    public void A_server_with_no_preset_needs_only_a_base_url()
    {
        var p = new OpenAICompatibleProvider("llamafile", new OpenAIOptions { BaseUrl = new Uri("http://127.0.0.1:8080/v1") }, TimeProvider.System);
        Assert.True(p.IsLoopback);
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider("x", new OpenAIOptions(), TimeProvider.System));
    }
}

public class OpenAICatalogTests
{
    [Fact]
    public void The_shipped_files_load_through_the_layered_catalog()
    {
        ModelCatalog catalog = OpenAIFx.Catalog;
        ProviderSpec openai = catalog.FindProvider("openai")!;
        Assert.Equal("OPENAI_API_KEY", openai.ApiKeyEnvVar);
        Assert.True(openai.Models!.Count >= 25);
        Assert.NotNull(catalog.FindProvider(OpenAICompatibleProvider.CatalogProvider));
    }

    [Fact]
    public void Every_model_resolves_by_name_and_every_alias_resolves_to_a_model()
    {
        ProviderSpec openai = OpenAIFx.Catalog.FindProvider("openai")!;
        foreach (ModelSpec m in openai.Models!)
        {
            ResolvedModel r = OpenAIFx.Catalog.Resolve(m.Name);
            Assert.Equal(m.Name, r.Name);
            Assert.Equal(ModelMatch.Exact, r.Match);
            Assert.Equal("openai", r.Provider.Name);
            Assert.Equal("openai:" + m.Name, OpenAIFx.Catalog.Resolve("openai:" + m.Name).Id);
        }

        foreach ((string alias, string target) in openai.Aliases!)
        {
            ResolvedModel r = OpenAIFx.Catalog.Resolve(alias);
            Assert.Equal(target, r.Name);
            Assert.NotNull(r.Model);
        }
    }

    [Fact]
    public void A_dated_snapshot_resolves_by_pattern()
    {
        ResolvedModel r = OpenAIFx.Catalog.Resolve("gpt-5.5-2026-04-23");
        Assert.Equal(ModelMatch.Pattern, r.Match);
        Assert.Equal("openai", r.Provider.Name);
        Assert.Equal("gpt-5.5", OpenAIWire.StripSnapshot("gpt-5.5-2026-04-23"));
        Assert.Equal("gpt-4.1", OpenAIWire.StripSnapshot("gpt-4.1-20250414"));
        Assert.Equal("gpt-4.1", OpenAIWire.StripSnapshot("gpt-4.1"));
    }

    [Fact]
    public void Every_price_has_an_as_of_date_and_a_source_url()
    {
        foreach (ModelSpec m in OpenAIFx.Catalog.FindProvider("openai")!.Models!)
        {
            PricingSpec p = Assert.IsType<PricingSpec>(m.Pricing);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", p.AsOf);
            Assert.StartsWith("https://", p.Source, StringComparison.Ordinal);
            if (m.Kind is null or "chat")
            {
                // Media models (speech, transcription, moderation) are priced by items instead.
                Assert.NotNull(p.InputPerMillion);
            }

            foreach (PriceItem item in p.Items ?? [])
            {
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", item.AsOf);
                Assert.StartsWith("https://", item.Source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Models_state_the_limits_and_rules_the_provider_acts_on()
    {
        ModelCatalog c = OpenAIFx.Catalog;
        ModelSpec astra = c.Resolve("gpt-6-astra").Model!;
        Assert.Equal(1050000, astra.ContextWindow);
        Assert.Equal(128000, astra.MaxOutputTokens);
        Assert.False(astra.SupportsTemperature);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], astra.Thinking!.Efforts);
        Assert.Equal(10m, astra.Pricing!.InputPerMillion);
        Assert.Equal(12.5m, astra.Pricing.CacheCreationPerMillion);
        Assert.Equal(2m, astra.ServiceTiers!["priority"].PricingMultiplier);
        Assert.Equal(0.5m, astra.ServiceTiers["flex"].PricingMultiplier);
        Assert.Equal(1.5m, Assert.Single(astra.Pricing.Tiers!).Multipliers!["outputTokens"]);
        Assert.True(c.Resolve("gpt-4.1").Model!.SupportsTemperature);
        Assert.Equal(1536, c.Resolve("embedding-small").Model!.Dimensions);
    }
}
