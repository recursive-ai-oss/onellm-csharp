using System.Reflection;
using OneLlm.Catalog;

namespace OneLlm.Providers.Anthropic;

/// <summary>The model catalog file OneLLM ships for Anthropic: models, limits, thinking settings and prices with their dates and sources.</summary>
public static class AnthropicCatalog
{
    private const string ResourceName = "OneLlm.Providers.Anthropic.llm-provider.yaml";
    private static readonly Lazy<CatalogLayer> s_layer = new(LoadLayer);
    private static readonly Lazy<ModelCatalog> s_default = new(() => ModelCatalog.Create([Shipped]));

    /// <summary>The shipped Anthropic catalog layer, to be placed under user files and code overrides in a <see cref="ModelCatalog"/>.</summary>
    public static CatalogLayer Shipped => s_layer.Value;

    /// <summary>A catalog holding only the shipped Anthropic layer. Used by <see cref="AnthropicProvider"/> when no catalog is given.</summary>
    public static ModelCatalog Default => s_default.Value;

    /// <summary>
    /// Registers the shipped Anthropic file with <see cref="CatalogLoader"/> so catalogs built there include it. Safe to call more than once;
    /// creating an <see cref="AnthropicProvider"/> calls it too.
    /// </summary>
    public static void Register() => CatalogLoader.RegisterShipped(typeof(AnthropicCatalog).Assembly, ResourceName);

    private static CatalogLayer LoadLayer()
    {
        Assembly assembly = typeof(AnthropicCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Anthropic catalog file is missing from the OneLLM assembly.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return CatalogLayer.FromYaml(CatalogLayer.Shipped, reader.ReadToEnd(), "anthropic/llm-provider.yaml");
    }
}
