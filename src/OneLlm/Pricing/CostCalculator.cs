using System.Globalization;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Pricing;

/// <summary>
/// Turns a <see cref="Usage"/> into a <see cref="Cost"/>: one line per usage counter, each line a quantity times the unit price the catalog gives
/// that counter, times the factors that apply. All arithmetic is exact <see cref="decimal"/> and nothing is rounded; rounding happens only when
/// a cost is displayed (<see cref="Cost.Display"/>).
/// </summary>
/// <remarks>
/// <para><b>Counters and prices.</b> The four token counters have flat fields on <see cref="PricingSpec"/>
/// (<c>inputPerMillion</c> ...). Every other counter (<c>inputAudioTokens</c>, <c>images</c>, <c>audioSeconds</c>, <c>characters</c>,
/// <c>toolCalls.web_search</c>, <c>containerHours</c> ...) is priced by a <see cref="PriceItem"/> naming it. An item that names one of the four
/// counters takes precedence over the flat field when its <c>when</c> qualifiers match the call.</para>
/// <para><b>Subset counters.</b> Some counters count tokens that are also inside a main counter (the catalog can add relations with <c>subsetOf</c>): <c>reasoningTokens</c> and the output
/// audio/image counters are inside <c>outputTokens</c>; <c>inputAudioTokens</c>, <c>inputImageTokens</c> and <c>inputVideoTokens</c> are inside
/// <c>inputTokens</c>; <c>cacheCreation1hInputTokens</c> is inside <c>cacheCreationInputTokens</c>. When the model prices a subset counter, its
/// tokens are taken out of the main counter's quantity and charged at their own price. When it does not, they stay in the main counter at its price.</para>
/// <para><b>Long context.</b> When total input tokens (uncached, cache writes and cache reads) exceed <c>longContextThreshold</c> (200,000 when
/// unset) and <c>longContextInputPerMillion</c> is set, the whole request is priced at the long-context rates: input and output take the long-context
/// prices, and cache prices are scaled by the ratio of long-context to base input price. A <see cref="PriceTier"/> multiplies prices of
/// the named counters (or all, under <c>*</c>) when its basis exceeds <c>above</c>; of several tiers that apply, the one with the highest <c>above</c> wins.</para>
/// <para><b>Factors.</b> The service tier's <c>pricingMultiplier</c>, the named <c>batch</c> and <c>region:&lt;name&gt;</c> multipliers and the tier
/// multiplier multiply together into each line's <see cref="CostLine.Multiplier"/>; they apply to every line, token or not.</para>
/// </remarks>
public static class CostCalculator
{
    /// <summary>Days after a price's <c>asOf</c> when it counts as stale: 180.</summary>
    public const int StaleAfterDays = 180;

    /// <summary>The long-context threshold used when a model sets long-context prices but no threshold: 200,000 input tokens.</summary>
    public const long DefaultLongContextThreshold = 200_000;

    private const decimal PerMillion = 1_000_000m;
    private const string TotalInputBasis = "totalInputTokens";

    // Subset counter -> the main counter it is counted inside.
    private static readonly (string Child, string Parent)[] DefaultSubsets =
    [
        ("cacheCreation1hInputTokens", "cacheCreationInputTokens"),
        ("reasoningTokens", "outputTokens"),
        ("outputAudioTokens", "outputTokens"),
        ("outputImageTokens", "outputTokens"),
        ("inputAudioTokens", "inputTokens"),
        ("inputImageTokens", "inputTokens"),
        ("inputVideoTokens", "inputTokens"),
    ];

    private static readonly string[] BaseCounters = ["inputTokens", "outputTokens", "cacheCreationInputTokens", "cacheReadInputTokens"];

    private readonly record struct Rate(decimal Price, decimal Per, string? Label, string? Source, string? AsOf, int Specificity, bool Flat);

    /// <summary>Prices a usage against a resolved model.</summary>
    /// <param name="resolved">The model, from <see cref="ModelCatalog.Resolve"/>.</param>
    /// <param name="usage">What the call consumed.</param>
    /// <param name="context">Facts that change the price; null for the standard tier, no batch.</param>
    /// <param name="warnings">Receives a message for anything that makes the cost incomplete or doubtful (a counter with no price, a missing multiplier, a stale price).</param>
    /// <returns>
    /// The cost. <see cref="Cost.Zero"/> for a model on a loopback server that has no price (a local server costs nothing unless a catalog says otherwise).
    /// Null when the model has no price and is not on a loopback server: the cost is unknown.
    /// </returns>
    public static Cost? Price(ResolvedModel resolved, Usage usage, PricingContext? context = null, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(usage);
        if (resolved.Model is { } model && HasPrice(model.Pricing))
        {
            return Price(model, usage, context, warnings);
        }

        Uri? address = context?.BaseUrl;
        if (address is null && resolved.Preset?.BaseUrl is { } text && Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
        {
            address = parsed;
        }

        return address is { IsLoopback: true } ? Cost.Zero : null;
    }

    /// <summary>Prices a usage against a model entry.</summary>
    /// <param name="model">The model entry. It must have <see cref="ModelSpec.Pricing"/>.</param>
    /// <param name="usage">What the call consumed.</param>
    /// <param name="context">Facts that change the price; null for the standard tier, no batch.</param>
    /// <param name="warnings">Receives messages about an incomplete or doubtful cost.</param>
    /// <returns>The cost, with one line per priced counter that has a non-zero quantity.</returns>
    /// <exception cref="ArgumentException">The model has no pricing.</exception>
    public static Cost Price(ModelSpec model, Usage usage, PricingContext? context = null, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        PricingSpec pricing = model.Pricing ?? throw new ArgumentException($"Model '{model.Name}' has no pricing.", nameof(model));
        PricingContext ctx = context ?? PricingContext.Default;

        if (ctx.Today is { } today && PriceFreshness.IsStale(pricing.AsOf, today))
        {
            warnings?.Add(string.Create(CultureInfo.InvariantCulture,
                $"The prices for '{model.Name}' are from {pricing.AsOf}, more than {StaleAfterDays} days ago; costs may be wrong."));
        }

        decimal factor = ServiceTierFactor(model, usage, ctx, warnings);
        if (ctx.Batch)
        {
            factor *= NamedFactor(pricing, "batch", warnings);
        }

        if (!string.IsNullOrEmpty(ctx.Region))
        {
            factor *= NamedFactor(pricing, "region:" + ctx.Region, warnings);
        }

        if (ctx.Multipliers is not null)
        {
            foreach (string name in ctx.Multipliers)
            {
                factor *= NamedFactor(pricing, name, warnings);
            }
        }

        long totalInput = usage.TotalInputTokens;
        bool longContext = pricing.LongContextInputPerMillion is > 0m
            && totalInput > (pricing.LongContextThreshold is > 0 ? pricing.LongContextThreshold.Value : DefaultLongContextThreshold);

        PriceTier? tier = null;
        if (pricing.Tiers is not null)
        {
            foreach (PriceTier candidate in pricing.Tiers)
            {
                long basis = candidate.Basis is null or TotalInputBasis ? totalInput : usage.GetCounter(candidate.Basis);
                if (basis > candidate.Above && (tier is null || candidate.Above > tier.Above))
                {
                    tier = candidate;
                }
            }
        }

        // The usual call reports only the four main counters, and no subset rule can touch them: price them straight from the usage, with no
        // dictionaries (this runs for every model call of every run).
        if (usage.Counters is not { Count: > 0 } && pricing.SubsetOf is not { Count: > 0 })
        {
            List<CostLine>? basic = null;
            foreach (string counter in BaseCounters)
            {
                decimal amount = usage.GetCounter(counter);
                if (amount == 0m)
                {
                    continue;
                }

                if (FindRate(pricing, counter, ctx.Qualifiers, longContext) is not { } found)
                {
                    warnings?.Add(string.Create(CultureInfo.InvariantCulture, $"'{model.Name}' has no price for '{counter}' ({amount}); the cost is incomplete."));
                    continue;
                }

                (basic ??= new List<CostLine>(BaseCounters.Length)).Add(MakeLine(pricing, counter, amount, found, factor, longContext, tier));
            }

            return basic is null ? Cost.Zero : new Cost(basic);
        }

        // Quantities: the four main counters, then every other counter the usage carries.
        // Sized up front: growing a dictionary past its first few slots allocates twice per priced call.
        var quantities = new Dictionary<string, decimal>(BaseCounters.Length + (usage.Counters?.Count ?? 0), StringComparer.Ordinal);
        foreach (string counter in BaseCounters)
        {
            quantities[counter] = usage.GetCounter(counter);
        }

        if (usage.Counters is not null)
        {
            foreach ((string counter, long value) in usage.Counters)
            {
                if (!quantities.ContainsKey(counter))
                {
                    quantities[counter] = value;
                }
            }
        }

        // A usage has a handful of counters, so the rates found are kept in two small arrays and looked up by scanning, which costs far less
        // than a dictionary of these wide structs.
        var rateCounters = new string[quantities.Count];
        var rateValues = new Rate[quantities.Count];
        int rateCount = 0;
        foreach ((string counter, decimal amount) in quantities)
        {
            if (amount != 0m && FindRate(pricing, counter, ctx.Qualifiers, longContext) is { } found)
            {
                rateCounters[rateCount] = counter;
                rateValues[rateCount++] = found;
            }
        }

        int RateIndex(string counter)
        {
            for (int i = 0; i < rateCount; i++)
            {
                if (rateCounters[i] == counter)
                {
                    return i;
                }
            }

            return -1;
        }

        (string Child, string Parent)[] subsets = DefaultSubsets;
        if (pricing.SubsetOf is { Count: > 0 })
        {
            subsets = [.. pricing.SubsetOf.Select(kv => (kv.Key, kv.Value)), .. DefaultSubsets];
        }

        foreach ((string child, string parent) in subsets)
        {
            if (RateIndex(child) >= 0 && quantities.TryGetValue(child, out decimal childQuantity) && quantities.TryGetValue(parent, out decimal parentQuantity))
            {
                quantities[parent] = Math.Max(0m, parentQuantity - childQuantity);
            }
        }

        var lines = new List<CostLine>();
        // Most usages carry only the four main counters; skip the sort of the others then.
        string[] order = quantities.Count == BaseCounters.Length ? BaseCounters : [.. BaseCounters, .. quantities.Keys.Except(BaseCounters).Order(StringComparer.Ordinal)];
        foreach (string counter in order)
        {
            decimal quantity = quantities[counter];
            if (quantity == 0m)
            {
                continue;
            }

            int rateAt = RateIndex(counter);
            if (rateAt < 0)
            {
                if (!Array.Exists(subsets, s => s.Child == counter))
                {
                    warnings?.Add(string.Create(CultureInfo.InvariantCulture, $"'{model.Name}' has no price for '{counter}' ({quantity}); the cost is incomplete."));
                }

                continue;
            }

            lines.Add(MakeLine(pricing, counter, quantity, rateValues[rateAt], factor, longContext, tier));
        }

        return lines.Count == 0 ? Cost.Zero : new Cost(lines);
    }

    private static CostLine MakeLine(PricingSpec pricing, string counter, decimal quantity, Rate rate, decimal factor, bool longContext, PriceTier? tier)
    {
        decimal multiplier = factor;
        string label = rate.Label ?? DefaultLabel(counter);
        if (longContext && rate.Flat)
        {
            label += " (long context)";
            if (counter is "cacheCreationInputTokens" or "cacheReadInputTokens" && pricing.InputPerMillion is > 0m)
            {
                multiplier *= pricing.LongContextInputPerMillion!.Value / pricing.InputPerMillion!.Value;
            }
        }

        if (tier?.Multipliers is { } tierFactors
            && (tierFactors.TryGetValue(counter, out decimal tierFactor) || tierFactors.TryGetValue("*", out tierFactor)))
        {
            multiplier *= tierFactor;
            label += " (" + (tier.Label ?? string.Create(CultureInfo.InvariantCulture, $"above {tier.Above}")) + ")";
        }

        return CostLine.Create(counter, quantity, rate.Per, rate.Price, multiplier, label, rate.Source ?? pricing.Source, rate.AsOf ?? pricing.AsOf);
    }

    /// <summary>Returns the usage with its <see cref="Usage.Cost"/> and <see cref="Usage.CostLines"/> set from a cost.</summary>
    /// <param name="usage">The usage.</param>
    /// <param name="cost">The cost, from <see cref="Price(ResolvedModel, Usage, PricingContext?, ICollection{string}?)"/>.</param>
    /// <returns>A copy of the usage that carries the cost.</returns>
    public static Usage WithCost(Usage usage, Cost cost)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(cost);
        return new Usage
        {
            Model = usage.Model,
            ServiceTier = usage.ServiceTier,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheCreationInputTokens = usage.CacheCreationInputTokens,
            CacheReadInputTokens = usage.CacheReadInputTokens,
            Counters = usage.Counters,
            ExtensionData = usage.ExtensionData,
            Cost = cost.Total,
            CostLines = cost.Lines,
        };
    }

    private static bool HasPrice(PricingSpec? p) =>
        p is not null && (p.InputPerMillion is not null || p.OutputPerMillion is not null || p.CacheCreationPerMillion is not null
            || p.CacheReadPerMillion is not null || p.Items is { Count: > 0 });

    private static decimal ServiceTierFactor(ModelSpec model, Usage usage, PricingContext ctx, ICollection<string>? warnings)
    {
        string tier = (ctx.ServiceTier ?? usage.ServiceTier ?? string.Empty).Trim().ToLowerInvariant();
        if (tier is "" or "standard" or "default" or "auto")
        {
            return 1m;
        }

        ServiceTierSpec? spec = null;
        if (model.ServiceTiers is not null)
        {
            foreach ((string name, ServiceTierSpec value) in model.ServiceTiers)
            {
                if (string.Equals(name, tier, StringComparison.OrdinalIgnoreCase))
                {
                    spec = value;
                }
            }
        }

        if (spec is null)
        {
            warnings?.Add($"'{model.Name}' has no service tier '{tier}' in the catalog; priced at the standard rate.");
            return 1m;
        }

        // A tier entry without a multiplier leaves the price unchanged (a zero or missing factor never prices a call at zero).
        return spec.PricingMultiplier is > 0m ? spec.PricingMultiplier.Value : 1m;
    }

    private static decimal NamedFactor(PricingSpec pricing, string name, ICollection<string>? warnings)
    {
        if (pricing.Multipliers is not null)
        {
            foreach ((string key, decimal value) in pricing.Multipliers)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        warnings?.Add($"The catalog has no '{name}' multiplier for this model; priced without it.");
        return 1m;
    }

    private static Rate? FindRate(PricingSpec pricing, string counter, IReadOnlyDictionary<string, string>? qualifiers, bool longContext)
    {
        Rate? best = null;
        decimal? flat = counter switch
        {
            "inputTokens" => longContext ? pricing.LongContextInputPerMillion : pricing.InputPerMillion,
            "outputTokens" => longContext ? pricing.LongContextOutputPerMillion ?? pricing.OutputPerMillion : pricing.OutputPerMillion,
            "cacheCreationInputTokens" => pricing.CacheCreationPerMillion,
            "cacheReadInputTokens" => pricing.CacheReadPerMillion,
            _ => null,
        };
        if (flat is { } price)
        {
            best = new Rate(price, PerMillion, null, null, null, -1, true);
        }

        if (pricing.Items is not null)
        {
            foreach (PriceItem item in pricing.Items)
            {
                if (!string.Equals(item.Counter, counter, StringComparison.Ordinal) || !Matches(item.When, qualifiers, out int specificity))
                {
                    continue;
                }

                if (best is null || specificity > best.Value.Specificity)
                {
                    decimal per = item.Per is > 0m ? item.Per.Value : counter.EndsWith("Tokens", StringComparison.Ordinal) ? PerMillion : 1m;
                    best = new Rate(item.Price, per, item.Label, item.Source, item.AsOf, specificity, false);
                }
            }
        }

        return best;
    }

    private static bool Matches(Dictionary<string, string>? when, IReadOnlyDictionary<string, string>? qualifiers, out int specificity)
    {
        specificity = 0;
        if (when is null || when.Count == 0)
        {
            return true;
        }

        if (qualifiers is null)
        {
            return false;
        }

        foreach ((string key, string wanted) in when)
        {
            bool found = false;
            foreach ((string have, string value) in qualifiers)
            {
                if (string.Equals(have, key, StringComparison.OrdinalIgnoreCase))
                {
                    found = string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        specificity = when.Count;
        return true;
    }

    private static string DefaultLabel(string counter) => counter switch
    {
        "inputTokens" => "input",
        "outputTokens" => "output",
        "cacheCreationInputTokens" => "cache write",
        "cacheReadInputTokens" => "cache read",
        "reasoningTokens" => "reasoning",
        "cacheCreation1hInputTokens" => "cache write (1h)",
        _ => counter,
    };
}
