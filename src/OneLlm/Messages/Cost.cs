using System.Globalization;
using System.Text.Json.Serialization;

namespace OneLlm.Messages;

/// <summary>
/// One line of a cost breakdown: a quantity of one usage counter times a unit price. Amounts are exact <see cref="decimal"/>
/// values and are never rounded here; rounding happens only when a cost is displayed.
/// </summary>
public sealed class CostLine
{
    /// <summary>The usage counter this line multiplies, for example <c>inputTokens</c>, <c>cacheReadInputTokens</c>, <c>images</c> or <c>audioSeconds</c>.</summary>
    public string Counter { get; init; } = "";

    /// <summary>How many units of the counter were used.</summary>
    public decimal Quantity { get; init; }

    /// <summary>How many units one <see cref="UnitPrice"/> covers: 1,000,000 for per-million-token prices, 1 for per-item prices.</summary>
    public decimal Per { get; init; } = 1m;

    /// <summary>The price in USD for <see cref="Per"/> units, before <see cref="Multiplier"/>.</summary>
    public decimal UnitPrice { get; init; }

    /// <summary>Factors applied to the price (service tier, batch, long context, region), multiplied together. 1 when none apply.</summary>
    public decimal Multiplier { get; init; } = 1m;

    /// <summary>The amount in USD: <c>Quantity / Per * UnitPrice * Multiplier</c>.</summary>
    public decimal Amount { get; init; }

    /// <summary>A short human label such as <c>input (long context)</c>.</summary>
    public string? Label { get; init; }

    /// <summary>Where the price came from: the URL the catalog records for it.</summary>
    public string? Source { get; init; }

    /// <summary>The date the price was last checked, in ISO form.</summary>
    public string? AsOf { get; init; }

    /// <summary>Fields that were present when the line was read and that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }

    /// <summary>Builds a line and computes its amount from the other values.</summary>
    /// <param name="counter">The usage counter name.</param>
    /// <param name="quantity">Units used.</param>
    /// <param name="per">Units one price covers. Must be positive.</param>
    /// <param name="unitPrice">Price in USD per <paramref name="per"/> units.</param>
    /// <param name="multiplier">Combined price factor.</param>
    /// <param name="label">A short label.</param>
    /// <param name="source">The price's source URL.</param>
    /// <param name="asOf">The date the price was checked.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="per"/> is not positive.</exception>
    public static CostLine Create(string counter, decimal quantity, decimal per, decimal unitPrice, decimal multiplier = 1m, string? label = null, string? source = null, string? asOf = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(per, 0m);
        return new CostLine
        {
            Counter = counter,
            Quantity = quantity,
            Per = per,
            UnitPrice = unitPrice,
            Multiplier = multiplier,
            Amount = quantity / per * unitPrice * multiplier,
            Label = label,
            Source = source,
            AsOf = asOf,
        };
    }
}

/// <summary>
/// A cost in USD as line items plus their total. Combine costs of calls, tool calls and sub-agents with <see cref="Add"/> or
/// <see cref="Sum"/>; the lines are kept, so a subtree's cost can still be broken down by counter.
/// </summary>
public sealed class Cost
{
    /// <summary>A cost with no lines and a total of zero.</summary>
    public static Cost Zero { get; } = new([]);

    /// <summary>Creates a cost from its lines.</summary>
    /// <param name="lines">The line items. The total is their sum.</param>
    public Cost(IReadOnlyList<CostLine> lines)
    {
        Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        decimal total = 0m;
        foreach (CostLine line in lines)
        {
            total += line.Amount;
        }

        Total = total;
    }

    /// <summary>The line items.</summary>
    public IReadOnlyList<CostLine> Lines { get; }

    /// <summary>The sum of the line amounts in USD, unrounded.</summary>
    public decimal Total { get; }

    /// <summary>Returns a cost holding the lines of this cost followed by those of <paramref name="other"/>.</summary>
    /// <param name="other">The cost to add.</param>
    /// <returns>The combined cost.</returns>
    public Cost Add(Cost other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Lines.Count == 0)
        {
            return this;
        }

        if (Lines.Count == 0)
        {
            return other;
        }

        var lines = new List<CostLine>(Lines.Count + other.Lines.Count);
        lines.AddRange(Lines);
        lines.AddRange(other.Lines);
        return new Cost(lines);
    }

    /// <summary>Adds two costs; see <see cref="Add"/>.</summary>
    /// <param name="left">The first cost.</param>
    /// <param name="right">The second cost.</param>
    /// <returns>The combined cost.</returns>
    public static Cost operator +(Cost left, Cost right) => (left ?? throw new ArgumentNullException(nameof(left))).Add(right);

    /// <summary>Adds a cost to this one; the named equivalent of the <c>+</c> operator.</summary>
    /// <param name="left">The first cost.</param>
    /// <param name="right">The second cost.</param>
    /// <returns>The combined cost.</returns>
    public static Cost Combine(Cost left, Cost right) => left + right;

    /// <summary>Adds up any number of costs, for example the costs of every call in a subtree.</summary>
    /// <param name="costs">The costs. Null entries are skipped.</param>
    /// <returns>The combined cost.</returns>
    public static Cost Sum(IEnumerable<Cost?> costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        var lines = new List<CostLine>();
        foreach (Cost? cost in costs)
        {
            if (cost is not null)
            {
                lines.AddRange(cost.Lines);
            }
        }

        return lines.Count == 0 ? Zero : new Cost(lines);
    }

    /// <summary>The total per usage counter, in the order counters first appear.</summary>
    /// <returns>Counter names and their summed amounts in USD.</returns>
    public IReadOnlyDictionary<string, decimal> TotalsByCounter()
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (CostLine line in Lines)
        {
            totals[line.Counter] = totals.GetValueOrDefault(line.Counter) + line.Amount;
        }

        return totals;
    }

    /// <summary>
    /// The total as text for people: USD, rounded half away from zero to <paramref name="decimals"/> places, with a dollar sign.
    /// This is the only place a cost is rounded.
    /// </summary>
    /// <param name="decimals">Places after the decimal point, 0 to 12. Default 6.</param>
    /// <returns>For example <c>$0.001235</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="decimals"/> is outside 0 to 12.</exception>
    public string Display(int decimals = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 12);
        decimal rounded = Math.Round(Total, decimals, MidpointRounding.AwayFromZero);
        return "$" + rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override string ToString() => Display();
}
