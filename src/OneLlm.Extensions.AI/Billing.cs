using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.Extensions.AI;

/// <summary>Prices usage against a catalog entry and writes the result into Microsoft.Extensions.AI property dictionaries.</summary>
internal static class Billing
{
    /// <summary>The cost of <paramref name="usage"/> under <paramref name="model"/>, with the usage carrying it; both unchanged when there is no catalog entry or no usage.</summary>
    public static (Usage? Usage, Cost? Cost) Price(ResolvedModel? model, Usage? usage, string? serviceTier, TimeProvider time, List<string> warnings)
    {
        if (model is null || usage is null)
        {
            return (usage, null);
        }

        var notes = new List<string>();
        PricingContext context = new() { ServiceTier = serviceTier, Today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime) };
        Cost? cost = CostCalculator.Price(model, usage, context, notes);
        warnings.AddRange(notes);
        if (cost is null)
        {
            warnings.Add($"The catalog has no price for '{model.Id}', so this call's cost is not known.");
            return (usage, null);
        }

        return (CostCalculator.WithCost(usage, cost), cost);
    }

    public static void Annotate(AdditionalPropertiesDictionary properties, Cost? cost, IReadOnlyCollection<string> warnings)
    {
        if (cost is not null)
        {
            properties[OneLlmProperties.Cost] = cost;
            properties[OneLlmProperties.CostTotal] = cost.Total;
        }

        if (warnings.Count > 0)
        {
            properties[OneLlmProperties.Warnings] = warnings.ToArray();
        }
    }

    public static void AddWarnings(List<string> into, IEnumerable<ResponseWarning> warnings)
    {
        foreach (ResponseWarning w in warnings)
        {
            into.Add(w.Message);
        }
    }
}
