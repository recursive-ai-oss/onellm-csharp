using OneLlm.Catalog;

namespace OneLlm.Providers.Subscriptions;

/// <summary>Zero per-token-price catalog entries for the opt-in subscription providers. Availability remains subject to the signed-in account's plan.</summary>
public static class SubscriptionCatalog
{
    /// <summary>Registers the embedded catalog, but never a provider factory. Safe to call repeatedly; provider construction also calls it.</summary>
    public static void Register() => CatalogLoader.RegisterShipped(typeof(SubscriptionCatalog).Assembly, "OneLlm.Providers.Subscriptions.llm-provider.yaml");
}
