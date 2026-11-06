using System.Globalization;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Pricing;

/// <summary>The most a request could cost, worked out before it is sent.</summary>
/// <param name="InputTokens">The input tokens the estimate assumed.</param>
/// <param name="Tier">How the input tokens were counted.</param>
/// <param name="MaxOutputTokens">The output tokens the estimate assumed: the request's limit, otherwise the model's maximum; null when neither is known.</param>
/// <param name="WorstCaseCost">The cost in USD, unrounded; null when the model has no price or the output is unbounded, so no bound can be given.</param>
/// <param name="Warnings">Anything that makes the estimate doubtful (an unpriced counter, a stale price).</param>
public sealed record CostEstimate(long InputTokens, TokenCountTier Tier, long? MaxOutputTokens, decimal? WorstCaseCost, IReadOnlyList<string> Warnings);

/// <summary>Works out the worst-case cost of a chat request before it is sent.</summary>
public static class CostEstimator
{
    /// <summary>
    /// The worst case: every input token at the dearer of the plain input price and the cache write price (when the request may write the cache),
    /// and the full output allowance at the output price, with long-context rates when the input is that large. Reasoning tokens are part of the output
    /// allowance. Hosted tool fees and per-unit items are not known beforehand and are not included.
    /// </summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="request">The request.</param>
    /// <param name="inputTokens">The input count, from <see cref="TokenCounter"/>.</param>
    /// <param name="context">Price factors (batch, region, today); the request's service tier is used when the context names none.</param>
    /// <returns>The estimate.</returns>
    public static CostEstimate Estimate(ResolvedModel model, ChatRequest request, TokenCount inputTokens, PricingContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        long? maxOutput = request.MaxOutputTokens ?? model.Model?.MaxOutputTokens;
        PricingContext ctx = (context ?? PricingContext.Default) with { ServiceTier = context?.ServiceTier ?? request.ServiceTier };

        decimal? worst = null;
        if (maxOutput is null)
        {
            warnings.Add($"Neither the request nor the catalog gives a maximum output for '{model.Name}', so the cost cannot be bounded.");
        }
        else
        {
            var plain = new Usage { InputTokens = inputTokens.InputTokens, OutputTokens = maxOutput.Value };
            Cost? asInput = CostCalculator.Price(model, plain, ctx, warnings);
            if (asInput is null)
            {
                warnings.Add($"The catalog has no price for '{model.Name}', so the cost cannot be bounded.");
            }
            else
            {
                worst = asInput.Total;
                if (MayWriteCache(request))
                {
                    var written = new Usage { CacheCreationInputTokens = inputTokens.InputTokens, OutputTokens = maxOutput.Value };
                    Cost? asCacheWrite = CostCalculator.Price(model, written, ctx, null);
                    if (asCacheWrite is not null && asCacheWrite.Total > worst)
                    {
                        worst = asCacheWrite.Total;
                    }
                }
            }
        }

        return new CostEstimate(inputTokens.InputTokens, inputTokens.Tier, maxOutput, worst, warnings);
    }

    private static bool MayWriteCache(ChatRequest request) =>
        request.Cache is { Enabled: true } || request.Messages.Any(m => m.Cache);
}

/// <summary>
/// Enforces a per-call limit and a spend cap on estimated costs. It keeps what has been spent and what is reserved for calls in flight, so
/// concurrent calls cannot together pass the cap. Thread-safe.
/// </summary>
/// <remarks>
/// A limit that cannot be checked is refused rather than ignored: with any limit set, an estimate with no worst-case cost throws
/// <see cref="BudgetKind.UnknownPrice"/>. Typical use: <c>Reserve</c> before sending, then <c>Settle</c> with the real cost, or <c>Release</c> when the call failed.
/// </remarks>
public sealed class SpendGuard
{
    private readonly object _gate = new();
    private decimal _spent;
    private decimal _reserved;
    private decimal? _spendCap;

    /// <summary>Creates a guard.</summary>
    /// <param name="maxCostPerCall">The most one call may cost in USD, or null for no per-call limit.</param>
    /// <param name="spendCap">The most the guard's calls may cost in total in USD, or null for no cap.</param>
    /// <param name="alreadySpent">Spend already recorded, for example when resuming an instance.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit or the spend is negative.</exception>
    public SpendGuard(decimal? maxCostPerCall = null, decimal? spendCap = null, decimal alreadySpent = 0m)
    {
        if (maxCostPerCall < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCostPerCall));
        }

        if (spendCap < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(spendCap));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(alreadySpent);
        MaxCostPerCall = maxCostPerCall;
        _spendCap = spendCap;
        _spent = alreadySpent;
    }

    /// <summary>The per-call limit in USD, or null.</summary>
    public decimal? MaxCostPerCall { get; }

    /// <summary>The spend cap in USD, or null. A new value applies to the next <see cref="Reserve"/>; what has been spent is kept.</summary>
    public decimal? SpendCap
    {
        get
        {
            lock (_gate)
            {
                return _spendCap;
            }
        }
        internal set
        {
            lock (_gate)
            {
                _spendCap = value;
            }
        }
    }

    /// <summary>What has been spent so far in USD.</summary>
    public decimal Spent
    {
        get
        {
            lock (_gate)
            {
                return _spent;
            }
        }
    }

    /// <summary>What is set aside for calls in flight in USD.</summary>
    public decimal Reserved
    {
        get
        {
            lock (_gate)
            {
                return _reserved;
            }
        }
    }

    /// <summary>Checks an estimate against the limits and, if it passes, sets its worst case aside.</summary>
    /// <param name="estimate">The estimate for the call about to be sent.</param>
    /// <returns>The reserved amount in USD (0 when no limit is set). Pass it to <see cref="Settle"/> or <see cref="Release"/>.</returns>
    /// <exception cref="BudgetExceededException">The estimate is above the per-call limit, would take the spend past the cap, or has no worst case while a limit is set.</exception>
    public decimal Reserve(CostEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (MaxCostPerCall is null && SpendCap is null)
        {
            return 0m;
        }

        lock (_gate)
        {
            if (estimate.WorstCaseCost is not { } worst)
            {
                throw new BudgetExceededException(
                    BudgetKind.UnknownPrice,
                    "A cost limit is set but the cost of this call cannot be bounded: the catalog has no price or no output limit for the model. Add a price (a local server costs 0) or set a maximum output.",
                    null,
                    null,
                    _spent);
            }

            if (MaxCostPerCall is { } perCall && worst > perCall)
            {
                throw new BudgetExceededException(
                    BudgetKind.PerCall,
                    string.Create(CultureInfo.InvariantCulture, $"The call could cost up to ${worst:0.######}, above the per-call limit of ${perCall:0.######}."),
                    perCall,
                    worst,
                    _spent);
            }

            if (SpendCap is { } cap && _spent + _reserved + worst > cap)
            {
                throw new BudgetExceededException(
                    BudgetKind.SpendCap,
                    string.Create(CultureInfo.InvariantCulture, $"Spent ${_spent:0.######} (+${_reserved:0.######} in flight); this call could cost up to ${worst:0.######}, past the cap of ${cap:0.######}."),
                    cap,
                    worst,
                    _spent);
            }

            _reserved += worst;
            return worst;
        }
    }

    /// <summary>Ends a reservation by recording what the call really cost.</summary>
    /// <param name="reserved">The amount <see cref="Reserve"/> returned.</param>
    /// <param name="actual">The real cost in USD.</param>
    public void Settle(decimal reserved, decimal actual)
    {
        lock (_gate)
        {
            _reserved = Math.Max(0m, _reserved - reserved);
            _spent += actual;
        }
    }

    /// <summary>Ends a reservation for a call that cost nothing, such as one that failed before reaching the provider.</summary>
    /// <param name="reserved">The amount <see cref="Reserve"/> returned.</param>
    public void Release(decimal reserved) => Settle(reserved, 0m);

    /// <summary>Records spend that was not reserved (a tool's cost, for example).</summary>
    /// <param name="amount">The cost in USD.</param>
    public void Record(decimal amount) => Settle(0m, amount);
}
