using OneLlm.Caching;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.AotSmoke;

/// <summary>Exercises catalog loading, pricing, request adjustment, token estimates, spend limits and the result cache under NativeAOT.</summary>
internal static class PricingExercise
{
    private const string Yaml = """
        name: acme
        modelPatterns: ["acme-*"]
        models:
          - name: acme-pro
            maxOutputTokens: 1000
            supportsTemperature: false
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              cacheReadPerMillion: 0.30
              asOf: 2026-03-01
        """;

    /// <summary>Runs the exercise.</summary>
    /// <returns>0 when every step gave the expected result.</returns>
    public static int Run()
    {
        ModelCatalog catalog = ModelCatalog.Create([CatalogLayer.FromYaml("shipped", Yaml)]);
        ResolvedModel model = catalog.Resolve("acme-pro");
        var usage = new Usage { InputTokens = 1_000_000, CacheReadInputTokens = 100_000 };
        Cost cost = CostCalculator.Price(model, usage)!;

        var request = new ChatRequest { Model = "acme-pro", Messages = [Message.FromUser("hello")], Temperature = 0.5 };
        AdjustedRequest adjusted = RequestAdjuster.Adjust(model, request);
        TokenCount count = TokenEstimator.Estimate(adjusted.Request, model);
        CostEstimate estimate = CostEstimator.Estimate(model, adjusted.Request, count);
        decimal reserved = new SpendGuard(spendCap: 1m).Reserve(estimate);
        string key = ResultCache.KeyFor("acme", adjusted.Request);

        bool ok = cost.Total == 3.03m && adjusted.Request.Temperature is null && count.Tier == TokenCountTier.Estimate
            && reserved > 0m && key.Length == 64;
        return ok ? 0 : 1;
    }
}
