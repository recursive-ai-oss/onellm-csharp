using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.Tests.Pricing;

public class EstimateAndSpendTests
{
    private const string Yaml = """
        name: acme
        modelPatterns: ["acme-*"]
        presets:
          local:
            baseUrl: http://localhost:11434/v1
            modelPatterns: ["llama*"]
        models:
          - name: acme-pro
            maxOutputTokens: 8000
            serviceTiers: { flex: { pricingMultiplier: 0.5 } }
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              cacheCreationPerMillion: 3.75
              cacheReadPerMillion: 0.30
          - name: acme-nolimit
            pricing: { inputPerMillion: 1.00, outputPerMillion: 2.00 }
          - name: acme-free
            maxOutputTokens: 100
        """;

    private static readonly ModelCatalog Catalog = ModelCatalog.Create([CatalogLayer.FromYaml("shipped", Yaml)]);

    private static ChatRequest Request(int? maxOutput = null) => new() { Messages = [Message.FromUser("hi")], MaxOutputTokens = maxOutput };

    [Fact]
    public void The_worst_case_is_all_input_at_the_input_price_plus_the_full_output_allowance()
    {
        // 100,000 input at $3/M = 0.3; the request's 2,000 output allowance at $15/M = 0.03; total 0.33.
        CostEstimate e = CostEstimator.Estimate(Catalog.Resolve("acme-pro"), Request(2000), new TokenCount(100_000, TokenCountTier.Provider));
        Assert.Equal(0.33m, e.WorstCaseCost);
        Assert.Equal(2000, e.MaxOutputTokens);
        Assert.Equal(TokenCountTier.Provider, e.Tier);
    }

    [Fact]
    public void Without_a_request_limit_the_models_maximum_output_is_used()
    {
        // 8,000 * 15 / 1e6 = 0.12 + 1,000 input * 3 / 1e6 = 0.003.
        Assert.Equal(0.123m, CostEstimator.Estimate(Catalog.Resolve("acme-pro"), Request(), new TokenCount(1000, TokenCountTier.Estimate)).WorstCaseCost);
    }

    [Fact]
    public void Long_prompts_flex_and_cache_writes_are_taken_into_account()
    {
        ResolvedModel model = Catalog.Resolve("acme-pro");
        // Flex halves everything: (1000 * 3 + 1000 * 15) / 1e6 * 0.5 = 0.009.
        Assert.Equal(0.009m, CostEstimator.Estimate(model, Request(1000) with { ServiceTier = "flex" }, new TokenCount(1000, TokenCountTier.Estimate)).WorstCaseCost);

        // A request that may write the cache is bounded by the dearer cache-write price: 1,000 * 3.75 / 1e6 = 0.00375, plus 1,000 output = 0.015; total 0.01875.
        ChatRequest caching = Request(1000) with { Cache = new CachePolicy() };
        Assert.Equal(0.01875m, CostEstimator.Estimate(model, caching, new TokenCount(1000, TokenCountTier.Estimate)).WorstCaseCost);
    }

    [Fact]
    public void An_unpriced_model_or_unbounded_output_gives_no_bound_and_says_why()
    {
        CostEstimate noPrice = CostEstimator.Estimate(Catalog.Resolve("acme-free"), Request(), new TokenCount(10, TokenCountTier.Estimate));
        Assert.Null(noPrice.WorstCaseCost);
        Assert.NotEmpty(noPrice.Warnings);

        CostEstimate noOutput = CostEstimator.Estimate(Catalog.Resolve("acme-nolimit"), Request(), new TokenCount(10, TokenCountTier.Estimate));
        Assert.Null(noOutput.WorstCaseCost);
        Assert.Null(noOutput.MaxOutputTokens);
    }

    [Fact]
    public void A_local_loopback_model_has_a_zero_worst_case_so_a_cap_works_with_it()
    {
        CostEstimate e = CostEstimator.Estimate(Catalog.Resolve("local:llama3"), Request(500), new TokenCount(10, TokenCountTier.Estimate));
        Assert.Equal(0m, e.WorstCaseCost);
    }

    private static CostEstimate Estimate(decimal? worst) => new(1, TokenCountTier.Estimate, 1, worst, []);

    [Fact]
    public void The_per_call_limit_refuses_a_call_that_could_cost_more()
    {
        var guard = new SpendGuard(maxCostPerCall: 0.10m);
        Assert.Equal(0.10m, guard.Reserve(Estimate(0.10m)));
        BudgetExceededException e = Assert.Throws<BudgetExceededException>(() => guard.Reserve(Estimate(0.11m)));
        Assert.Equal(BudgetKind.PerCall, e.Kind);
        Assert.Equal(0.10m, e.Limit);
        Assert.Equal(0.11m, e.Required);
    }

    [Fact]
    public void The_spend_cap_counts_what_is_spent_and_what_is_in_flight()
    {
        var guard = new SpendGuard(spendCap: 1.00m, alreadySpent: 0.40m);
        decimal first = guard.Reserve(Estimate(0.30m));       // 0.40 + 0.30 = 0.70 <= 1.00
        decimal second = guard.Reserve(Estimate(0.30m));      // 0.40 + 0.30 + 0.30 = 1.00 <= 1.00
        BudgetExceededException e = Assert.Throws<BudgetExceededException>(() => guard.Reserve(Estimate(0.01m)));   // 1.01 > 1.00
        Assert.Equal(BudgetKind.SpendCap, e.Kind);
        Assert.Equal(0.60m, guard.Reserved);

        guard.Settle(first, 0.10m);                           // the call cost less than its worst case
        Assert.Equal(0.50m, guard.Spent);
        Assert.Equal(0.30m, guard.Reserved);
        guard.Release(second);                                // the other never reached the provider
        Assert.Equal(0m, guard.Reserved);
        guard.Reserve(Estimate(0.50m));                       // 0.50 + 0.50 = 1.00 fits again
    }

    [Fact]
    public void A_limit_with_an_unknown_price_refuses_rather_than_ignores_and_no_limit_never_refuses()
    {
        BudgetExceededException e = Assert.Throws<BudgetExceededException>(() => new SpendGuard(spendCap: 5m).Reserve(Estimate(null)));
        Assert.Equal(BudgetKind.UnknownPrice, e.Kind);
        Assert.Equal(0m, new SpendGuard().Reserve(Estimate(null)));
        Assert.Equal(0m, new SpendGuard(spendCap: 5m).Reserve(Estimate(0m)));
    }

    [Fact]
    public void Concurrent_reservations_cannot_pass_the_cap_together()
    {
        var guard = new SpendGuard(spendCap: 1.00m);
        int granted = 0;
        Parallel.For(0, 50, _ =>
        {
            try
            {
                guard.Reserve(Estimate(0.10m));
                Interlocked.Increment(ref granted);
            }
            catch (BudgetExceededException)
            {
            }
        });
        Assert.Equal(10, granted);
    }
}
