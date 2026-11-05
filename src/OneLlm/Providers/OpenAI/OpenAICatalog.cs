using System.Reflection;
using OneLlm.Catalog;

namespace OneLlm.Providers.OpenAI;

/// <summary>The catalog entries shipped for the OpenAI provider and the OpenAI-compatible presets.</summary>
public static class OpenAICatalog
{
    /// <summary>The shipped layer: every OpenAI model with its limits, capabilities and prices, and the compatible-server presets.</summary>
    /// <returns>A new layer named <see cref="CatalogLayer.Shipped"/>.</returns>
    public static CatalogLayer Shipped()
    {
        var providers = new List<ProviderSpec>();
        foreach (string file in new[] { "openai.llm-provider.yaml", "openai-compatible.llm-provider.yaml" })
        {
            providers.AddRange(CatalogLayer.FromYaml(CatalogLayer.Shipped, ReadResource(file), file).Providers);
        }

        return new CatalogLayer(CatalogLayer.Shipped, providers);
    }

    private static string ReadResource(string file)
    {
        Assembly assembly = typeof(OpenAICatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("OneLlm.Providers.OpenAI." + file)
            ?? throw new InvalidOperationException($"The embedded catalog file {file} is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
