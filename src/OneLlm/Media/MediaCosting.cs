using System.Globalization;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.Media;

/// <summary>Prices the usage of a media call from the catalog, collecting notices instead of throwing.</summary>
internal static class MediaCosting
{
    /// <summary>
    /// Finds the catalog entry for a model of one provider. A dated snapshot name (<c>gpt-image-2-2026-04-21</c>) falls back to its family when
    /// the catalog has no entry for the snapshot itself.
    /// </summary>
    public static ResolvedModel? Resolve(ModelCatalog? catalog, string scope, string model)
    {
        if (catalog is null)
        {
            return null;
        }

        string id = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
        if (catalog.TryResolve(scope + ":" + id, out ResolvedModel? resolved) && resolved!.Model is not null)
        {
            return resolved;
        }

        string trimmed = Providers.OpenAI.OpenAIWire.StripSnapshot(id);
        return trimmed != id && catalog.TryResolve(scope + ":" + trimmed, out resolved) && resolved!.Model is not null ? resolved : null;
    }

    /// <summary>
    /// Prices a usage. Returns the usage carrying its cost, and the cost; both stay as given and the cost is null when there is no catalog,
    /// no entry or no price. Notices go into <paramref name="warnings"/>.
    /// </summary>
    public static (Usage Usage, Cost? Cost) Price(
        ModelCatalog? catalog,
        string scope,
        string model,
        Usage usage,
        TimeProvider time,
        List<ResponseWarning> warnings,
        IReadOnlyDictionary<string, string>? qualifiers = null,
        bool batch = false)
    {
        if (catalog is null)
        {
            return (usage, null);
        }

        ResolvedModel? resolved = Resolve(catalog, scope, model);
        if (resolved?.Model?.Pricing is null)
        {
            warnings.Add(new ResponseWarning("cost_unknown", $"The catalog has no price for '{model}', so the cost was not computed."));
            return (usage, null);
        }

        var context = new PricingContext
        {
            Qualifiers = qualifiers,
            Batch = batch,
            Today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
        };
        var notes = new List<string>();
        Cost? cost = CostCalculator.Price(resolved, usage, context, notes);
        foreach (string note in notes)
        {
            warnings.Add(new ResponseWarning("cost_incomplete", note));
        }

        return cost is null ? (usage, null) : (CostCalculator.WithCost(usage, cost), cost);
    }

    /// <summary>Adds the counters of two usages. Used when one request is made as several calls.</summary>
    public static Usage Sum(Usage a, Usage b)
    {
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Usage u in new[] { a, b })
        {
            if (u.Counters is null)
            {
                continue;
            }

            foreach ((string name, long value) in u.Counters)
            {
                counters[name] = counters.GetValueOrDefault(name) + value;
            }
        }

        return new Usage
        {
            Model = b.Model.Length > 0 ? b.Model : a.Model,
            ServiceTier = b.ServiceTier ?? a.ServiceTier,
            InputTokens = a.InputTokens + b.InputTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
            CacheCreationInputTokens = a.CacheCreationInputTokens + b.CacheCreationInputTokens,
            CacheReadInputTokens = a.CacheReadInputTokens + b.CacheReadInputTokens,
            Counters = counters.Count == 0 ? null : counters,
        };
    }

    /// <summary>The qualifiers a price item can name: the size and quality of an image.</summary>
    public static Dictionary<string, string>? ImageQualifiers(string? size, string? quality)
    {
        if (size is null && quality is null)
        {
            return null;
        }

        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (size is not null)
        {
            q["size"] = size;
        }

        if (quality is not null)
        {
            q["quality"] = quality;
        }

        return q;
    }

    /// <summary>A map from a file extension or format word to its media type.</summary>
    public static string AudioContentType(string? format) => (format ?? "mp3").ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "pcm" => "audio/L16;rate=24000",
        var other => "audio/" + other,
    };

    /// <summary>The file extension for an audio or image media type, with the dot; empty when not known.</summary>
    public static string ExtensionFor(string contentType)
    {
        string main = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return main switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" or "application/ogg" => ".ogg",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "audio/webm" or "video/webm" => ".webm",
            "video/mp4" => ".mp4",
            "audio/mpga" => ".mpga",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => string.Empty,
        };
    }

    /// <summary>The media type for an image format word.</summary>
    public static string ImageContentType(string? format) => (format ?? "png").ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpeg" or "jpg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "svg" => "image/svg+xml",
        var other => "image/" + other,
    };

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
