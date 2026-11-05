using System.Text.Json;
using System.Text.Json.Serialization;
using OneLlm.Messages;
using OneYaml;

namespace OneLlm.Catalog;

/// <summary>
/// A catalog file that holds several providers under a <c>providers</c> key. A file with one provider has the provider's fields at the top
/// level instead.
/// </summary>
public sealed class CatalogFileSpec
{
    /// <summary>The providers in the file.</summary>
    public List<ProviderSpec>? Providers { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>System.Text.Json source-generated metadata for the catalog types: camelCase keys, null values left out.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(NaturalObjectJsonConverter)])]
[JsonSerializable(typeof(ProviderSpec))]
[JsonSerializable(typeof(CatalogFileSpec))]
[JsonSerializable(typeof(ModelSpec))]
[JsonSerializable(typeof(PricingSpec))]
public sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>OneYaml source-generated metadata for the catalog types, with the same keys as <see cref="CatalogJsonContext"/>.</summary>
[YamlSerializable(typeof(ProviderSpec))]
[YamlSerializable(typeof(CatalogFileSpec))]
[YamlSerializable(typeof(ModelSpec))]
[YamlSerializable(typeof(PricingSpec))]
public sealed partial class CatalogYamlContext : YamlSerializerContext
{
    /// <summary>The context with the file format's settings: camelCase keys, null values left out.</summary>
    public static CatalogYamlContext Default { get; } = new();

    /// <summary>Creates the context with the file format's settings.</summary>
    public CatalogYamlContext()
        : base(YamlDefaults.Options())
    {
    }

    /// <summary>Creates the context with settings of your own.</summary>
    /// <param name="options">The settings.</param>
    public CatalogYamlContext(YamlSerializerOptions options)
        : base(options)
    {
    }
}
