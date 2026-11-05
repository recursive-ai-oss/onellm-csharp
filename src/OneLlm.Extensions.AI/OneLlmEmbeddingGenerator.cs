using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Extensions.AI;

/// <summary>
/// A OneLLM embedding model as a Microsoft.Extensions.AI <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>. Usage is in the result's
/// <c>Usage</c>; the cost is in its <c>AdditionalProperties</c> under <see cref="OneLlmProperties.Cost"/> and <see cref="OneLlmProperties.CostTotal"/>.
/// </summary>
/// <remarks>The generator is thread-safe and does not own the provider. <c>EmbeddingGenerationOptions.Dimensions</c> and <c>ModelId</c> are honoured per request.</remarks>
public sealed class OneLlmEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingProvider _provider;
    private readonly ResolvedModel? _resolved;
    private readonly string _model;
    private readonly TimeProvider _time;

    /// <summary>A generator over <paramref name="provider"/> that sends <paramref name="model"/> as the model name and prices nothing.</summary>
    /// <param name="provider">The embeddings provider. Not owned.</param>
    /// <param name="model">The model name the provider expects.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmEmbeddingGenerator(IEmbeddingProvider provider, string model, TimeProvider time)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A generator for a catalog entry; every call is priced.</summary>
    /// <param name="provider">The embeddings provider that serves <paramref name="model"/>. Not owned.</param>
    /// <param name="model">The resolved catalog entry.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmEmbeddingGenerator(IEmbeddingProvider provider, ResolvedModel model, TimeProvider time)
        : this(provider, model?.Name ?? throw new ArgumentNullException(nameof(model)), time) => _resolved = model;

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        string model = options?.ModelId is { Length: > 0 } requested && _resolved is null ? requested : _model;
        EmbeddingResponse response = await _provider.EmbedAsync(
            new EmbeddingRequest { Model = model, Inputs = [.. values], Dimensions = options?.Dimensions },
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        Billing.AddWarnings(warnings, response.Warnings);
        (Usage? usage, Cost? cost) = Billing.Price(_resolved, response.Usage, null, _time, warnings);
        var result = new GeneratedEmbeddings<Embedding<float>>(response.Embeddings.Count);
        foreach (ReadOnlyMemory<float> vector in response.Embeddings)
        {
            result.Add(new Embedding<float>(vector) { ModelId = response.Model ?? model });
        }

        if (usage is not null)
        {
            result.Usage = new UsageDetails { InputTokenCount = usage.TotalInputTokens, TotalTokenCount = usage.TotalInputTokens };
        }

        Billing.Annotate(result.AdditionalProperties ??= [], cost, warnings);
        return result;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata(_provider.Name, null, _model);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
