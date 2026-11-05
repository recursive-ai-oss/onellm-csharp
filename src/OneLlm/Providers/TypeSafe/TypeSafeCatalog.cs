using OneLlm.Catalog;

namespace OneLlm.Providers.TypeSafe;

/// <summary>The catalog file OneLLM ships for TypeSafe Jev: its models, context limits and price.</summary>
public static class TypeSafeCatalog
{
    private const string ResourceName = "OneLlm.Providers.TypeSafe.llm-provider.yaml";

    /// <summary>Registers the shipped TypeSafe file with <see cref="CatalogLoader"/> so catalogs built there include it. Safe to call more than once.</summary>
    public static void Register() => CatalogLoader.RegisterShipped(typeof(TypeSafeCatalog).Assembly, ResourceName);
}
