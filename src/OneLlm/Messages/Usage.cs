using System.Text.Json.Serialization;

namespace OneLlm.Messages;

/// <summary>What one model call consumed and what it cost.</summary>
/// <remarks>
/// <see cref="InputTokens"/> counts only input that was neither written to nor read from a cache, as the cache counters are separate;
/// total input is the sum of the three. Provider codecs normalize to this rule (for example OpenAI reports cached tokens inside its
/// input count, and the codec subtracts them).
/// </remarks>
public sealed class Usage
{
    /// <summary>The concrete model that answered.</summary>
    public string Model { get; init; } = "";

    /// <summary>The service tier the call ran on (<c>flex</c>, <c>priority</c>, ...), when not the standard one.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Input tokens that were not cache reads or cache writes.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens, including reasoning tokens.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache.</summary>
    public long CacheCreationInputTokens { get; init; }

    /// <summary>Input tokens read from the prompt cache.</summary>
    public long CacheReadInputTokens { get; init; }

    /// <summary>The cost of the call in USD, unrounded; the sum of <see cref="CostLines"/> when those are present.</summary>
    public decimal Cost { get; init; }

    /// <summary>
    /// Counters beyond the four token counts, by name: <c>reasoningTokens</c>, <c>cacheCreation1hInputTokens</c>, <c>inputAudioTokens</c>,
    /// <c>inputImageTokens</c>, <c>images</c>, <c>audioSeconds</c>, <c>characters</c>, <c>toolCalls.web_search</c>, ... Null when there are none.
    /// </summary>
    public Dictionary<string, long>? Counters { get; init; }

    /// <summary>The cost broken into line items. Null when the call was not priced.</summary>
    public IReadOnlyList<CostLine>? CostLines { get; init; }

    /// <summary>Fields that were present when the usage was read and that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }

    /// <summary>Total input tokens: uncached, cache writes and cache reads.</summary>
    [JsonIgnore]
    public long TotalInputTokens => InputTokens + CacheCreationInputTokens + CacheReadInputTokens;

    /// <summary>Reads a counter by name, from the four token counts or <see cref="Counters"/>.</summary>
    /// <param name="counter">The counter name.</param>
    /// <returns>The value, or 0 when the counter is not set.</returns>
    public long GetCounter(string counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return counter switch
        {
            "inputTokens" => InputTokens,
            "outputTokens" => OutputTokens,
            "cacheCreationInputTokens" => CacheCreationInputTokens,
            "cacheReadInputTokens" => CacheReadInputTokens,
            _ => Counters is not null && Counters.TryGetValue(counter, out long value) ? value : 0,
        };
    }

    /// <summary>The cost as a <see cref="Messages.Cost"/>: its lines when present, otherwise a single unnamed line holding <see cref="Cost"/>.</summary>
    /// <returns>The cost.</returns>
    public Cost ToCost()
    {
        if (CostLines is { Count: > 0 })
        {
            return new Cost(CostLines);
        }

        return this.Cost == 0m
            ? Messages.Cost.Zero
            : new Cost([new CostLine { Counter = "total", Quantity = 1m, UnitPrice = this.Cost, Amount = this.Cost }]);
    }

    /// <summary>
    /// Merges a later usage report into this one, for streams that report usage in pieces. A counter that is zero in
    /// <paramref name="later"/> keeps this value; any other counter takes the later value. Cost and model follow the same rule.
    /// </summary>
    /// <param name="later">The later report.</param>
    /// <returns>The merged usage.</returns>
    public Usage MergeLater(Usage later)
    {
        ArgumentNullException.ThrowIfNull(later);
        Dictionary<string, long>? counters = Counters;
        if (later.Counters is { Count: > 0 })
        {
            counters = counters is null ? new Dictionary<string, long>(later.Counters, StringComparer.Ordinal) : new Dictionary<string, long>(counters, StringComparer.Ordinal);
            foreach ((string key, long value) in later.Counters)
            {
                if (value != 0)
                {
                    counters[key] = value;
                }
            }
        }

        return new Usage
        {
            Model = later.Model.Length > 0 ? later.Model : Model,
            ServiceTier = later.ServiceTier ?? ServiceTier,
            InputTokens = later.InputTokens != 0 ? later.InputTokens : InputTokens,
            OutputTokens = later.OutputTokens != 0 ? later.OutputTokens : OutputTokens,
            CacheCreationInputTokens = later.CacheCreationInputTokens != 0 ? later.CacheCreationInputTokens : CacheCreationInputTokens,
            CacheReadInputTokens = later.CacheReadInputTokens != 0 ? later.CacheReadInputTokens : CacheReadInputTokens,
            Cost = later.Cost != 0m ? later.Cost : Cost,
            Counters = counters,
            CostLines = later.CostLines ?? CostLines,
            ExtensionData = later.ExtensionData ?? ExtensionData,
        };
    }
}
