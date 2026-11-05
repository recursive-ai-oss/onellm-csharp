using System.Globalization;
using OneLlm.Definitions;

namespace OneLlm.Deciders;

/// <summary>How a chat model answers a decider's questions: the <c>llm</c> block of <c>decider.yaml</c>. It applies only when a chat model, not a decisions engine, answers.</summary>
public sealed class DeciderLlmSettings
{
    /// <summary>How many times each question is asked; the answers' distributions are averaged. At least 1; default 1.</summary>
    public int Samples { get; init; } = 1;

    /// <summary>The sampling temperature, 0 to 2. Null means 0, or 1 when <see cref="Samples"/> is more than 1 (samples of one fixed answer would add nothing).</summary>
    public double? Temperature { get; init; }

    /// <summary>The reasoning effort (<c>low</c>, <c>medium</c>, ...), or null for the model's default.</summary>
    public string? ThinkingEffort { get; init; }

    /// <summary>The reasoning token budget, or null.</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>The service tier (<c>standard</c>, <c>flex</c>, ...), or null for standard.</summary>
    public string? ServiceTier { get; init; }

    internal static DeciderLlmSettings From(object? value, string? source)
    {
        if (value is null)
        {
            return new DeciderLlmSettings();
        }

        if (value is not IDictionary<string, object?> map)
        {
            throw new BadDefinitionException("'llm' must be a mapping.", source);
        }

        int samples = 1;
        double? temperature = null;
        string? effort = null;
        int? budget = null;
        string? tier = null;
        if (map.TryGetValue("samples", out object? s) && s is not null)
        {
            if (s is not (long or int) || Convert.ToInt64(s, CultureInfo.InvariantCulture) < 1 || Convert.ToInt64(s, CultureInfo.InvariantCulture) > 100)
            {
                throw new BadDefinitionException("llm.samples must be a whole number from 1 to 100.", source);
            }

            samples = Convert.ToInt32(s, CultureInfo.InvariantCulture);
        }

        if (map.TryGetValue("temperature", out object? t) && t is not null)
        {
            double v = t switch { long l => l, int i => i, double d => d, decimal m => (double)m, _ => double.NaN };
            if (double.IsNaN(v) || v < 0 || v > 2)
            {
                throw new BadDefinitionException("llm.temperature must be a number from 0 to 2.", source);
            }

            temperature = v;
        }

        if (map.TryGetValue("thinking", out object? th) && th is not null)
        {
            switch (th)
            {
                case string e when e.Trim().Length > 0:
                    effort = e.Trim();
                    break;
                case bool b:
                    effort = b ? null : "disabled";
                    break;
                case IDictionary<string, object?> tm:
                    effort = tm.TryGetValue("effort", out object? ef) && ef is string es && es.Length > 0 ? es : null;
                    if (tm.TryGetValue("budget", out object? bg) && bg is long bl && bl > 0)
                    {
                        budget = (int)Math.Min(bl, int.MaxValue);
                    }

                    break;
                default:
                    throw new BadDefinitionException("llm.thinking must be an effort name or a mapping with effort and budget.", source);
            }
        }

        if (map.TryGetValue("serviceTier", out object? st) && st is not null)
        {
            tier = st is string ts ? ts.Trim().ToLowerInvariant() : throw new BadDefinitionException("llm.serviceTier must be a string.", source);
        }

        return new DeciderLlmSettings { Samples = samples, Temperature = temperature, ThinkingEffort = effort, ThinkingBudget = budget, ServiceTier = tier };
    }
}

/// <summary>Prompt caching hints for a decider: the <c>cache</c> block of <c>decider.yaml</c>. Engines that cannot cache ignore it.</summary>
public sealed class DeciderCacheSettings
{
    /// <summary>
    /// Partitions cache reuse (OpenAI Decisions' <c>prompt_cache_key</c>, at most 64 characters). Null uses the decider's name, so
    /// runs of one decider share a cache and different deciders do not.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// When true, the state is marked as the end of the cached prefix with an explicit breakpoint and implicit breakpoints are
    /// turned off, so exactly the state (and what precedes it) is what is cached. Default false.
    /// </summary>
    public bool Explicit { get; init; }

    internal static DeciderCacheSettings From(object? value, string? source)
    {
        if (value is null)
        {
            return new DeciderCacheSettings();
        }

        if (value is not IDictionary<string, object?> map)
        {
            throw new BadDefinitionException("'cache' must be a mapping.", source);
        }

        string? key = null;
        bool explicitMode = false;
        if (map.TryGetValue("key", out object? k) && k is not null)
        {
            key = k as string ?? throw new BadDefinitionException("cache.key must be a string.", source);
            if (key.Length is 0 or > 64)
            {
                throw new BadDefinitionException("cache.key must be 1 to 64 characters.", source);
            }
        }

        if (map.TryGetValue("explicit", out object? e) && e is not null)
        {
            explicitMode = e as bool? ?? throw new BadDefinitionException("cache.explicit must be true or false.", source);
        }

        return new DeciderCacheSettings { Key = key, Explicit = explicitMode };
    }
}
