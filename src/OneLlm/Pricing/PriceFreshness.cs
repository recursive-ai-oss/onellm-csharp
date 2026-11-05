using System.Globalization;
using OneLlm.Catalog;

namespace OneLlm.Pricing;

/// <summary>A model whose prices are older than <see cref="CostCalculator.StaleAfterDays"/> days.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Model">The model's name.</param>
/// <param name="AsOf">The date the prices were last checked.</param>
/// <param name="AgeDays">Days between <paramref name="AsOf"/> and the date checked against.</param>
public readonly record struct StalePrice(string Provider, string Model, DateOnly AsOf, int AgeDays);

/// <summary>Checks how old the catalog's prices are. The rule is 180 days past a price's <c>asOf</c> date.</summary>
public static class PriceFreshness
{
    /// <summary>Says whether an <c>asOf</c> date is more than 180 days before today.</summary>
    /// <param name="asOf">The date as <c>yyyy-MM-dd</c>. A missing or unreadable date counts as not stale (nothing is known).</param>
    /// <param name="today">Today's date, from the runtime's clock.</param>
    /// <returns>True when the prices are stale.</returns>
    public static bool IsStale(string? asOf, DateOnly today) =>
        TryAge(asOf, today, out int age) && age > CostCalculator.StaleAfterDays;

    /// <summary>Lists every model in the catalog, presets included, whose prices are stale.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="today">Today's date, from the runtime's clock.</param>
    /// <returns>The stale models, in catalog order.</returns>
    public static IReadOnlyList<StalePrice> FindStale(ModelCatalog catalog, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var stale = new List<StalePrice>();
        foreach (ProviderSpec provider in catalog.Providers)
        {
            Collect(provider.Name, provider.Models, today, stale);
            if (provider.Presets is not null)
            {
                foreach ((string name, PresetSpec preset) in provider.Presets)
                {
                    Collect(name, preset.Models, today, stale);
                }
            }
        }

        return stale;
    }

    private static void Collect(string provider, List<ModelSpec>? models, DateOnly today, List<StalePrice> into)
    {
        if (models is null)
        {
            return;
        }

        foreach (ModelSpec model in models)
        {
            string? asOf = model.Pricing?.AsOf;
            if (TryAge(asOf, today, out int age) && age > CostCalculator.StaleAfterDays)
            {
                into.Add(new StalePrice(provider, model.Name, DateOnly.ParseExact(asOf!, "yyyy-MM-dd", CultureInfo.InvariantCulture), age));
            }
        }
    }

    private static bool TryAge(string? asOf, DateOnly today, out int age)
    {
        age = 0;
        if (asOf is null || !DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return false;
        }

        age = today.DayNumber - date.DayNumber;
        return true;
    }
}
