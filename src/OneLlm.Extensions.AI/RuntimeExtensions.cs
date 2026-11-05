using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Providers;

namespace OneLlm.Extensions.AI;

/// <summary>Entry points of the Microsoft.Extensions.AI bridge: OneLLM models as Microsoft.Extensions.AI clients, and Microsoft.Extensions.AI clients as OneLLM providers.</summary>
public static class RuntimeExtensions
{
    /// <summary>A chat client for <paramref name="model"/>, resolved through the runtime's catalog: calls are adjusted to the model and priced.</summary>
    /// <param name="runtime">The runtime whose catalog, providers and clock the client uses.</param>
    /// <param name="model">A catalog name, alias or <c>provider:model</c>.</param>
    /// <returns>An <see cref="IChatClient"/>. <c>ChatOptions.ModelId</c> may name another catalog model per request.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CapabilityMissingException">The model's provider has no chat capability.</exception>
    public static OneLlmChatClient AsChatClient(this Runtime runtime, string model) => OneLlmChatClient.Create(runtime, model);

    /// <summary>An embedding generator for <paramref name="model"/>, resolved through the runtime's catalog and priced.</summary>
    /// <param name="runtime">The runtime whose catalog, providers and clock the generator uses.</param>
    /// <param name="model">A catalog name, alias or <c>provider:model</c>.</param>
    /// <returns>An <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CapabilityMissingException">The model's provider has no embeddings capability.</exception>
    public static OneLlmEmbeddingGenerator AsEmbeddingGenerator(this Runtime runtime, string model)
    {
        (ResolvedModel resolved, IEmbeddingProvider provider) = Open<IEmbeddingProvider>(runtime, model, "Embeddings");
        return new OneLlmEmbeddingGenerator(provider, resolved, runtime.TimeProvider);
    }

    /// <summary>A speech-to-text client for <paramref name="model"/>, resolved through the runtime's catalog and priced.</summary>
    /// <param name="runtime">The runtime whose catalog, providers and clock the client uses.</param>
    /// <param name="model">A catalog name, alias or <c>provider:model</c>.</param>
    /// <returns>An <see cref="ISpeechToTextClient"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CapabilityMissingException">The model's provider has no transcription capability.</exception>
    public static OneLlmSpeechToTextClient AsSpeechToTextClient(this Runtime runtime, string model)
    {
        (ResolvedModel resolved, ISpeechToTextProvider provider) = Open<ISpeechToTextProvider>(runtime, model, "Speech to text");
        return new OneLlmSpeechToTextClient(provider, resolved, runtime.TimeProvider);
    }

    /// <summary>A text-to-speech client for <paramref name="model"/>, resolved through the runtime's catalog and priced.</summary>
    /// <param name="runtime">The runtime whose catalog, providers and clock the client uses.</param>
    /// <param name="model">A catalog name, alias or <c>provider:model</c>.</param>
    /// <returns>An <see cref="ITextToSpeechClient"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CapabilityMissingException">The model's provider has no speech synthesis capability.</exception>
    public static OneLlmTextToSpeechClient AsTextToSpeechClient(this Runtime runtime, string model)
    {
        (ResolvedModel resolved, ITextToSpeechProvider provider) = Open<ITextToSpeechProvider>(runtime, model, "Text to speech");
        return new OneLlmTextToSpeechClient(provider, resolved, runtime.TimeProvider);
    }

    /// <summary>An image generator for <paramref name="model"/>, resolved through the runtime's catalog and priced.</summary>
    /// <param name="runtime">The runtime whose catalog, providers and clock the generator uses.</param>
    /// <param name="model">A catalog name, alias or <c>provider:model</c>.</param>
    /// <returns>An <see cref="IImageGenerator"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CapabilityMissingException">The model's provider has no image capability.</exception>
    public static OneLlmImageGenerator AsImageGenerator(this Runtime runtime, string model)
    {
        (ResolvedModel resolved, IImageProvider provider) = Open<IImageProvider>(runtime, model, "Image generation");
        return new OneLlmImageGenerator(provider, resolved, runtime.TimeProvider);
    }

    /// <summary>
    /// Registers <paramref name="client"/> as the OneLLM provider called <paramref name="providerName"/>. A catalog entry (a user file or
    /// <c>CatalogLayer</c> in code) whose provider has that name gives its models their prices and capabilities.
    /// </summary>
    /// <param name="registry">The registry, usually <c>runtime.Providers</c>.</param>
    /// <param name="providerName">The provider name used in the catalog entry.</param>
    /// <param name="client">The chat client. Not owned: dispose it yourself.</param>
    /// <param name="embeddings">An embedding generator for the same provider, or <see langword="null"/>.</param>
    /// <returns>The provider that was registered.</returns>
    /// <exception cref="ArgumentException"><paramref name="providerName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="embeddings"/> is <see langword="null"/>.</exception>
    public static ChatClientProvider AddChatClient(this ProviderRegistry registry, string providerName, IChatClient client, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var provider = new ChatClientProvider(providerName, client, embeddings);
        registry.Set(providerName, provider);
        return provider;
    }

    private static (ResolvedModel Model, T Provider) Open<T>(Runtime runtime, string model, string capability)
        where T : class, IProvider
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ResolvedModel resolved = runtime.Resolve(model);
        IProvider provider = runtime.Providers.Get(resolved, runtime.TimeProvider);
        return provider is T typed
            ? (resolved, typed)
            : throw new CapabilityMissingException($"{capability} on provider '{provider.Name}'", $"a provider that offers {capability.ToLowerInvariant()} for '{resolved.Id}'");
    }
}
