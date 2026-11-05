using OneLlm.Catalog;
using OneLlm.Providers.Anthropic;

namespace OneLlm.Tests.Providers;

public class AnthropicCatalogTests
{
    private static ProviderSpec Provider => AnthropicCatalog.Default.FindProvider("anthropic")!;

    [Fact]
    public void The_shipped_file_loads_through_the_layered_catalog()
    {
        ModelCatalog catalog = ModelCatalog.Create([AnthropicCatalog.Shipped]);
        Assert.NotNull(catalog.FindProvider("anthropic"));
        Assert.Equal(16, Provider.Models!.Count);
        Assert.Equal("shipped", catalog.SourceOf("anthropic", "models[claude-sonnet-5-5].contextWindow"));
    }

    [Fact]
    public void Every_model_resolves_by_its_name_and_every_alias_resolves_to_a_model()
    {
        foreach (ModelSpec m in Provider.Models!)
        {
            ResolvedModel r = AnthropicCatalog.Default.Resolve(m.Name);
            Assert.Equal(m.Name, r.Name);
            Assert.Same(Provider, r.Provider);
            Assert.NotNull(r.Model);
            Assert.Equal(m.Name, AnthropicCatalog.Default.Resolve("anthropic:" + m.Name).Name);
        }

        foreach ((string alias, string target) in Provider.Aliases!)
        {
            ResolvedModel r = AnthropicCatalog.Default.Resolve(alias);
            Assert.Equal(target, r.Name);
            Assert.NotNull(r.Model);
        }
    }

    [Fact]
    public void A_new_dated_model_matches_the_pattern_without_a_catalog_entry()
    {
        ResolvedModel r = AnthropicCatalog.Default.Resolve("claude-future-9");
        Assert.Equal("anthropic", r.Provider.Name);
        Assert.Null(r.Model);
    }

    [Fact]
    public void Every_price_has_a_date_and_a_source_address()
    {
        foreach (ModelSpec m in Provider.Models!)
        {
            PricingSpec p = m.Pricing ?? throw new InvalidOperationException(m.Name + " has no pricing.");
            Assert.True(DateOnly.TryParseExact(p.AsOf, "yyyy-MM-dd", out _), m.Name + " asOf");
            Assert.StartsWith("https://", p.Source, StringComparison.Ordinal);
            Assert.NotNull(p.InputPerMillion);
            Assert.NotNull(p.OutputPerMillion);
            Assert.NotNull(p.CacheCreationPerMillion);
            Assert.NotNull(p.CacheReadPerMillion);
            foreach (PriceItem item in p.Items ?? [])
            {
                // An item inherits the model's date and source unless it names its own.
                Assert.True(item.AsOf is not null || p.AsOf is not null);
                Assert.True(item.Source is not null || p.Source is not null);
                Assert.True(item.Price > 0);
            }
        }
    }

    [Fact]
    public void Prices_match_the_pricing_page_as_read()
    {
        PricingSpec sonnet = AnthropicCatalog.Default.Resolve("claude-sonnet").Model!.Pricing!;
        Assert.Equal(2m, sonnet.InputPerMillion);
        Assert.Equal(10m, sonnet.OutputPerMillion);
        Assert.Equal(2.50m, sonnet.CacheCreationPerMillion);
        Assert.Equal(0.10m, sonnet.CacheReadPerMillion);
        Assert.Equal(4m, sonnet.Items!.Single(i => i.Counter == "cacheCreation1hInputTokens").Price);

        PricingSpec sonnet45 = AnthropicCatalog.Default.Resolve("claude-sonnet-4-5").Model!.Pricing!;
        Assert.Equal(3m, sonnet45.InputPerMillion);
        Assert.Equal(15m, sonnet45.OutputPerMillion);
        Assert.Equal(3.75m, sonnet45.CacheCreationPerMillion);
        Assert.Equal(0.30m, sonnet45.CacheReadPerMillion);
        Assert.Equal(6m, sonnet45.Items!.Single(i => i.Counter == "cacheCreation1hInputTokens").Price);

        PricingSpec haiku45 = AnthropicCatalog.Default.Resolve("claude-haiku-4-5").Model!.Pricing!;
        Assert.Equal(1m, haiku45.InputPerMillion);
        Assert.Equal(5m, haiku45.OutputPerMillion);
        Assert.Equal(1.25m, haiku45.CacheCreationPerMillion);

        PricingSpec fable = AnthropicCatalog.Default.Resolve("claude-fable").Model!.Pricing!;
        Assert.Equal(10m, fable.InputPerMillion);
        Assert.Equal(0.25m, fable.CacheReadPerMillion);
    }

    [Fact]
    public void Web_search_is_priced_per_thousand_calls()
    {
        PriceItem search = AnthropicCatalog.Default.Resolve("claude-sonnet").Model!.Pricing!.Items!.Single(i => i.Counter == "toolCalls.web_search");
        Assert.Equal(10m, search.Price);
        Assert.Equal(1000m, search.Per);
    }

    [Fact]
    public void Limits_and_support_flags_are_filled_in_for_every_model()
    {
        foreach (ModelSpec m in Provider.Models!)
        {
            Assert.True(m.ContextWindow >= 200_000, m.Name);
            Assert.True(m.MaxOutputTokens >= 32_000, m.Name);
            Assert.NotNull(m.SupportsTemperature);
            Assert.True(m.SupportsTools);
            Assert.True(m.SupportsVision);
            Assert.NotNull(m.Thinking);
            Assert.True(m.Thinking!.AcceptsBudget == true || m.Thinking.Efforts is { Count: > 0 } || m.Properties is not null);
        }
    }

    [Fact]
    public void A_user_layer_can_override_a_price_and_repoint_an_alias_over_the_shipped_file()
    {
        var user = CatalogLayer.FromYaml(
            CatalogLayer.User,
            """
            name: anthropic
            aliases:
              claude-sonnet: claude-sonnet-4-5-20250929
            models:
              - name: claude-sonnet-4-5-20250929
                pricing:
                  inputPerMillion: 2.5
            """);
        ModelCatalog catalog = ModelCatalog.Create([AnthropicCatalog.Shipped, user]);
        ResolvedModel r = catalog.Resolve("claude-sonnet");
        Assert.Equal("claude-sonnet-4-5-20250929", r.Name);
        Assert.Equal(2.5m, r.Model!.Pricing!.InputPerMillion);
        Assert.Equal(15m, r.Model.Pricing.OutputPerMillion);
        Assert.Equal("user", catalog.SourceOf("anthropic", "models[claude-sonnet-4-5-20250929].pricing.inputPerMillion"));
    }
}
