using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;

namespace OneLlm.Tests.Pricing;

// Every expected value below is worked out by hand in the comment above it, on synthetic prices (the "acme" provider).
public class CostCalculatorTests
{
    private const string Yaml = """
        name: acme
        modelPatterns: ["acme-*"]
        presets:
          local:
            baseUrl: http://localhost:11434/v1
            modelPatterns: ["llama*"]
          remote:
            baseUrl: https://models.example.com/v1
            modelPatterns: ["mistral*"]
        models:
          - name: acme-pro
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              cacheCreationPerMillion: 3.75
              cacheReadPerMillion: 0.30
              asOf: 2026-03-01
              source: https://example.com/pricing
              multipliers:
                batch: 0.5
                "region:eu": 1.1
            serviceTiers:
              flex:
                pricingMultiplier: 0.5
              priority:
                pricingMultiplier: 2
          - name: acme-long
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              cacheReadPerMillion: 0.30
              longContextInputPerMillion: 6.00
              longContextOutputPerMillion: 22.50
              longContextThreshold: 200000
          - name: acme-wide
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              cacheCreationPerMillion: 3.75
              asOf: 2026-03-01
              source: https://example.com/pricing
              subsetOf: { inputDocumentTokens: inputTokens }
              items:
                - counter: inputDocumentTokens
                  price: 50.00
                - counter: cacheCreation1hInputTokens
                  price: 6.00
                - counter: reasoningTokens
                  price: 60.00
                - counter: inputAudioTokens
                  price: 100.00
                - counter: images
                  price: 0.02
                - counter: images
                  price: 0.04
                  when: { size: 1024x1024, quality: standard }
                - counter: images
                  price: 0.08
                  when: { size: 1024x1024, quality: high }
                - counter: audioSeconds
                  price: 0.006
                  per: 60
                - counter: characters
                  price: 15.00
                  per: 1000000
                - counter: toolCalls.web_search
                  price: 10.00
                  per: 1000
                - counter: containerHours
                  price: 0.03
              tiers:
                - above: 128000
                  label: large prompt
                  multipliers: { inputTokens: 2, outputTokens: 1.5 }
                - above: 500000
                  label: huge prompt
                  multipliers: { "*": 3 }
          - name: acme-plain
            pricing:
              inputPerMillion: 1.00
              outputPerMillion: 2.00
              asOf: 2025-01-01
        """;

    private static readonly ModelCatalog Catalog = ModelCatalog.Create([CatalogLayer.FromYaml("shipped", Yaml)]);

    private static Cost Price(string model, Usage usage, PricingContext? context = null, List<string>? warnings = null) =>
        CostCalculator.Price(Catalog.Resolve(model), usage, context, warnings)!;

    private static decimal Line(Cost cost, string counter) => cost.Lines.Single(l => l.Counter == counter).Amount;

    [Fact]
    public void Input_and_output_tokens_price_per_million()
    {
        // 1,000 input tokens at $3/M = 1000 / 1,000,000 * 3 = 0.003; 500 output at $15/M = 0.0075; total 0.0105.
        Cost cost = Price("acme-pro", new Usage { InputTokens = 1000, OutputTokens = 500 });
        Assert.Equal(0.003m, Line(cost, "inputTokens"));
        Assert.Equal(0.0075m, Line(cost, "outputTokens"));
        Assert.Equal(0.0105m, cost.Total);
        Assert.Equal(2, cost.Lines.Count);
        Assert.Equal("https://example.com/pricing", cost.Lines[0].Source);
        Assert.Equal("2026-03-01", cost.Lines[0].AsOf);
        Assert.Equal(1_000_000m, cost.Lines[0].Per);
    }

    [Fact]
    public void Cache_write_and_cache_read_have_their_own_prices()
    {
        // 2,000 cache-write tokens at $3.75/M = 0.0075; 10,000 cache-read tokens at $0.30/M = 0.003.
        Cost cost = Price("acme-pro", new Usage { CacheCreationInputTokens = 2000, CacheReadInputTokens = 10_000 });
        Assert.Equal(0.0075m, Line(cost, "cacheCreationInputTokens"));
        Assert.Equal(0.003m, Line(cost, "cacheReadInputTokens"));
        Assert.Equal(0.0105m, cost.Total);
    }

    [Fact]
    public void Long_context_prices_the_whole_request_at_the_long_context_rates()
    {
        // Total input 150,000 + 100,000 = 250,000 > 200,000. Input at the long price $6/M: 150,000 * 6 / 1e6 = 0.9.
        // Cache reads scale by 6/3 = 2: $0.30 * 2 = $0.60/M, 100,000 * 0.60 / 1e6 = 0.06. Output at $22.50/M: 1,000 * 22.5 / 1e6 = 0.0225.
        Cost cost = Price("acme-long", new Usage { InputTokens = 150_000, CacheReadInputTokens = 100_000, OutputTokens = 1000 });
        Assert.Equal(0.9m, Line(cost, "inputTokens"));
        Assert.Equal(0.06m, Line(cost, "cacheReadInputTokens"));
        Assert.Equal(0.0225m, Line(cost, "outputTokens"));
        Assert.Equal(0.9825m, cost.Total);
        Assert.Contains("long context", cost.Lines[0].Label);
    }

    [Fact]
    public void Long_context_starts_strictly_above_the_threshold()
    {
        // Exactly 200,000 input tokens is not long context: 200,000 * 3 / 1e6 = 0.6. One more token is: 200,001 * 6 / 1e6 = 1.200006.
        Assert.Equal(0.6m, Price("acme-long", new Usage { InputTokens = 200_000 }).Total);
        Assert.Equal(1.200006m, Price("acme-long", new Usage { InputTokens = 200_001 }).Total);
    }

    [Fact]
    public void Service_tier_multiplies_every_price()
    {
        // 1M input at $3 + 1M output at $15 = $18. Flex x0.5 = $9. Priority x2 = $36. The standard names carry no factor.
        var usage = new Usage { InputTokens = 1_000_000, OutputTokens = 1_000_000 };
        Assert.Equal(9m, Price("acme-pro", usage, new PricingContext { ServiceTier = "flex" }).Total);
        Assert.Equal(36m, Price("acme-pro", usage, new PricingContext { ServiceTier = "Priority" }).Total);
        Assert.Equal(18m, Price("acme-pro", usage, new PricingContext { ServiceTier = "default" }).Total);
        Assert.Equal(9m, Price("acme-pro", new Usage { InputTokens = 1_000_000, OutputTokens = 1_000_000, ServiceTier = "flex" }).Total);
        Assert.Equal(0.5m, Price("acme-pro", usage, new PricingContext { ServiceTier = "flex" }).Lines[0].Multiplier);
    }

    [Fact]
    public void An_unknown_service_tier_prices_at_standard_and_warns()
    {
        var warnings = new List<string>();
        Cost cost = Price("acme-pro", new Usage { InputTokens = 1_000_000 }, new PricingContext { ServiceTier = "turbo" }, warnings);
        Assert.Equal(3m, cost.Total);
        Assert.Contains("turbo", Assert.Single(warnings));
    }

    [Fact]
    public void Batch_and_region_multipliers_combine_with_the_service_tier()
    {
        // 1M input at $3: batch x0.5 = 1.5. Region eu x1.1 = 3.3. Batch and region and flex: 3 * 0.5 * 1.1 * 0.5 = 0.825.
        var usage = new Usage { InputTokens = 1_000_000 };
        Assert.Equal(1.5m, Price("acme-pro", usage, new PricingContext { Batch = true }).Total);
        Assert.Equal(3.3m, Price("acme-pro", usage, new PricingContext { Region = "eu" }).Total);
        Cost all = Price("acme-pro", usage, new PricingContext { Batch = true, Region = "eu", ServiceTier = "flex" });
        Assert.Equal(0.825m, all.Total);
        Assert.Equal(0.275m, all.Lines[0].Multiplier);
    }

    [Fact]
    public void A_missing_named_multiplier_warns_and_leaves_the_price()
    {
        var warnings = new List<string>();
        Cost cost = Price("acme-pro", new Usage { InputTokens = 1_000_000 }, new PricingContext { Region = "jp" }, warnings);
        Assert.Equal(3m, cost.Total);
        Assert.Contains("region:jp", Assert.Single(warnings));
    }

    [Fact]
    public void One_hour_cache_writes_are_charged_at_their_price_and_taken_out_of_the_cache_write_quantity()
    {
        // 3,000 cache-write tokens, of which 1,000 were written for one hour. 2,000 at $3.75/M = 0.0075; 1,000 at $6/M = 0.006.
        Cost cost = Price("acme-wide", new Usage
        {
            CacheCreationInputTokens = 3000,
            Counters = new Dictionary<string, long> { ["cacheCreation1hInputTokens"] = 1000 },
        });
        Assert.Equal(0.0075m, Line(cost, "cacheCreationInputTokens"));
        Assert.Equal(0.006m, Line(cost, "cacheCreation1hInputTokens"));
        Assert.Equal(0.0135m, cost.Total);
    }

    [Fact]
    public void Reasoning_tokens_with_their_own_price_are_taken_out_of_the_output_quantity()
    {
        // 1,000 output tokens, 400 of them reasoning. 600 at $15/M = 0.009; 400 at $60/M = 0.024; total 0.033.
        Cost cost = Price("acme-wide", new Usage { OutputTokens = 1000, Counters = new Dictionary<string, long> { ["reasoningTokens"] = 400 } });
        Assert.Equal(0.009m, Line(cost, "outputTokens"));
        Assert.Equal(0.024m, Line(cost, "reasoningTokens"));
        Assert.Equal(0.033m, cost.Total);
    }

    [Fact]
    public void Reasoning_tokens_with_no_price_stay_in_the_output_price_without_a_warning()
    {
        // 1,000 output tokens at $15/M = 0.015; the 400 reasoning tokens are inside them.
        var warnings = new List<string>();
        Cost cost = Price("acme-pro", new Usage { OutputTokens = 1000, Counters = new Dictionary<string, long> { ["reasoningTokens"] = 400 } }, null, warnings);
        Assert.Equal(0.015m, cost.Total);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Audio_input_tokens_are_taken_out_of_the_input_quantity()
    {
        // 1,000 input tokens, 300 of them audio. 700 at $3/M = 0.0021; 300 at $100/M = 0.03.
        Cost cost = Price("acme-wide", new Usage { InputTokens = 1000, Counters = new Dictionary<string, long> { ["inputAudioTokens"] = 300 } });
        Assert.Equal(0.0021m, Line(cost, "inputTokens"));
        Assert.Equal(0.03m, Line(cost, "inputAudioTokens"));
    }

    [Fact]
    public void A_catalog_can_declare_its_own_subset_relation()
    {
        // inputDocumentTokens is declared as inside inputTokens. 1,000 input tokens, 200 of them document: 800 at $3/M = 0.0024; 200 at $50/M = 0.01.
        Cost cost = Price("acme-wide", new Usage { InputTokens = 1000, Counters = new Dictionary<string, long> { ["inputDocumentTokens"] = 200 } });
        Assert.Equal(0.0024m, Line(cost, "inputTokens"));
        Assert.Equal(0.01m, Line(cost, "inputDocumentTokens"));
    }

    [Fact]
    public void Images_are_priced_by_size_and_quality_with_the_most_specific_item_winning()
    {
        // 3 images. Standard 1024x1024: 3 * $0.04 = 0.12. High: 3 * $0.08 = 0.24. No qualifiers: the plain item, 3 * $0.02 = 0.06.
        var usage = new Usage { Counters = new Dictionary<string, long> { ["images"] = 3 } };
        Assert.Equal(0.12m, Price("acme-wide", usage, Qualifiers("1024x1024", "standard")).Total);
        Assert.Equal(0.24m, Price("acme-wide", usage, Qualifiers("1024x1024", "high")).Total);
        Assert.Equal(0.06m, Price("acme-wide", usage).Total);
        Assert.Equal(0.06m, Price("acme-wide", usage, Qualifiers("512x512", "standard")).Total);
    }

    [Fact]
    public void Audio_seconds_characters_tool_calls_and_container_hours_are_per_unit_items()
    {
        // 120 s at $0.006 per 60 s = 120 / 60 * 0.006 = 0.012.
        Assert.Equal(0.012m, Price("acme-wide", Counters(("audioSeconds", 120))).Total);

        // 500,000 characters at $15 per 1,000,000 = 0.5 * 15 = 7.5.
        Assert.Equal(7.5m, Price("acme-wide", Counters(("characters", 500_000))).Total);

        // 3 web searches at $10 per 1,000 = 3 / 1000 * 10 = 0.03.
        Assert.Equal(0.03m, Price("acme-wide", Counters(("toolCalls.web_search", 3))).Total);

        // 5 container hours at $0.03 each = 0.15 (the price covers 1 unit when no per is given).
        Assert.Equal(0.15m, Price("acme-wide", Counters(("containerHours", 5))).Total);
    }

    [Fact]
    public void A_tier_multiplies_the_named_counters_when_the_basis_is_passed()
    {
        // Total input 200,000 > 128,000: input x2 = 200,000 * 3 * 2 / 1e6 = 1.2; output x1.5 = 1,000 * 15 * 1.5 / 1e6 = 0.0225.
        Cost cost = Price("acme-wide", new Usage { InputTokens = 200_000, OutputTokens = 1000 });
        Assert.Equal(1.2m, Line(cost, "inputTokens"));
        Assert.Equal(0.0225m, Line(cost, "outputTokens"));
        Assert.Contains("large prompt", cost.Lines[0].Label);

        // At exactly 128,000 no tier applies: 128,000 * 3 / 1e6 = 0.384.
        Assert.Equal(0.384m, Price("acme-wide", new Usage { InputTokens = 128_000 }).Total);
    }

    [Fact]
    public void Of_several_tiers_the_one_with_the_highest_threshold_applies_and_a_star_covers_every_counter()
    {
        // Total input 600,000 > 500,000: the "*" tier x3 applies to every counter, not the 128,000 tier. 600,000 * 3 * 3 / 1e6 = 5.4; 1,000 output * 15 * 3 / 1e6 = 0.045.
        Cost cost = Price("acme-wide", new Usage { InputTokens = 600_000, OutputTokens = 1000 });
        Assert.Equal(5.4m, Line(cost, "inputTokens"));
        Assert.Equal(0.045m, Line(cost, "outputTokens"));
    }

    [Fact]
    public void A_counter_without_a_price_warns_and_is_left_out()
    {
        var warnings = new List<string>();
        Cost cost = Price("acme-plain", new Usage { InputTokens = 1_000_000, CacheReadInputTokens = 500 }, null, warnings);
        Assert.Equal(1m, cost.Total);
        Assert.Contains("cacheReadInputTokens", Assert.Single(warnings));
    }

    [Fact]
    public void Amounts_are_exact_decimals_and_only_display_rounds()
    {
        // 1,234,567 input tokens at $3/M = 3.703701 exactly; 7 output tokens at $15/M = 0.000105.
        Cost cost = Price("acme-pro", new Usage { InputTokens = 1_234_567, OutputTokens = 7 });
        Assert.Equal(3.703701m + 0.000105m, cost.Total);
        Assert.Equal("$3.703806", cost.Display());
        Assert.Equal("$3.70", cost.Display(2));
    }

    [Fact]
    public void A_stale_price_warns_when_the_clock_is_given_and_not_before_180_days()
    {
        // asOf 2025-01-01: 2026-06-30 is 545 days later, stale. acme-pro (2026-03-01) on 2026-08-27 is 179 days, fresh; 2026-08-28 is 180, still fresh; 2026-08-29 is 181, stale.
        var stale = new List<string>();
        Price("acme-plain", new Usage { InputTokens = 1 }, new PricingContext { Today = new DateOnly(2026, 6, 30) }, stale);
        Assert.Contains("acme-plain", Assert.Single(stale));

        foreach ((DateOnly today, int expected) in new[] { (new DateOnly(2026, 8, 28), 0), (new DateOnly(2026, 8, 29), 1) })
        {
            var warnings = new List<string>();
            Price("acme-pro", new Usage { InputTokens = 1 }, new PricingContext { Today = today }, warnings);
            Assert.Equal(expected, warnings.Count);
        }

        IReadOnlyList<StalePrice> found = PriceFreshness.FindStale(Catalog, new DateOnly(2026, 8, 29));
        Assert.Equal(["acme-pro", "acme-wide", "acme-plain"], found.Select(f => f.Model).ToArray());
        Assert.Equal(181, found[0].AgeDays);
    }

    [Fact]
    public void A_model_on_a_loopback_server_with_no_price_costs_nothing_and_a_remote_one_is_unknown()
    {
        var usage = new Usage { InputTokens = 5000, OutputTokens = 100 };
        Assert.Equal(0m, CostCalculator.Price(Catalog.Resolve("local:llama3"), usage)!.Total);
        Assert.Null(CostCalculator.Price(Catalog.Resolve("remote:mistral-large"), usage));

        // The address can also come from the call itself.
        var context = new PricingContext { BaseUrl = new Uri("http://127.0.0.1:8000/v1") };
        Assert.Equal(0m, CostCalculator.Price(Catalog.Resolve("remote:mistral-large"), usage, context)!.Total);
        Assert.Equal(0m, CostCalculator.Price(Catalog.Resolve("remote:mistral-large"), usage, new PricingContext { BaseUrl = new Uri("http://[::1]:8000") })!.Total);
    }

    [Fact]
    public void A_user_price_on_a_loopback_model_overrides_the_zero()
    {
        ModelCatalog withPrice = Catalog.WithLayer(CatalogLayer.FromYaml("user", """
            name: acme
            presets:
              local:
                models:
                  - name: llama3
                    pricing: { inputPerMillion: 0.20, outputPerMillion: 0.40 }
            """));
        Cost? cost = CostCalculator.Price(withPrice.Resolve("local:llama3"), new Usage { InputTokens = 1_000_000 });
        Assert.Equal(0.2m, cost!.Total);
    }

    [Fact]
    public void WithCost_copies_the_usage_and_sets_the_cost_and_lines()
    {
        var usage = new Usage { Model = "acme-pro", InputTokens = 1_000_000 };
        Usage priced = CostCalculator.WithCost(usage, Price("acme-pro", usage));
        Assert.Equal(3m, priced.Cost);
        Assert.Single(priced.CostLines!);
        Assert.Equal("acme-pro", priced.Model);
    }

    private static PricingContext Qualifiers(string size, string quality) =>
        new() { Qualifiers = new Dictionary<string, string> { ["size"] = size, ["quality"] = quality } };

    private static Usage Counters(params (string Name, long Value)[] counters) =>
        new() { Counters = counters.ToDictionary(c => c.Name, c => c.Value) };
}
