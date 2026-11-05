using System.Text.Json;
using System.Text.Json.Serialization;
using OneYaml;

namespace OneLlm.Messages;

/// <summary>
/// System.Text.Json source-generated metadata for the message model. Naming is camelCase, as in the file format; null values are
/// left out. Use <see cref="Default"/> with <see cref="JsonSerializer"/>, or list <see cref="Message"/> in your own context: the
/// message and part converters carry their own metadata, so they work under any options.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(NaturalObjectJsonConverter)])]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(List<Message>))]
[JsonSerializable(typeof(Part))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(Cost))]
[JsonSerializable(typeof(CostLine))]
[JsonSerializable(typeof(MessageSource))]
[JsonSerializable(typeof(AssetRef))]
public sealed partial class OneLlmJsonContext : JsonSerializerContext;

/// <summary>OneYaml source-generated metadata for the message model, with the same keys as <see cref="OneLlmJsonContext"/>.</summary>
[YamlSerializable(typeof(Message))]
[YamlSerializable(typeof(List<Message>))]
[YamlSerializable(typeof(Part))]
[YamlSerializable(typeof(Usage))]
[YamlSerializable(typeof(MessageSource))]
[YamlSerializable(typeof(AssetRef))]
public sealed partial class OneLlmYamlContext : YamlSerializerContext
{
    /// <summary>The context with the file format's settings: camelCase keys, null values left out.</summary>
    public static OneLlmYamlContext Default { get; } = new();

    /// <summary>Creates the context with the file format's settings.</summary>
    public OneLlmYamlContext()
        : base(YamlDefaults.Options())
    {
    }

    /// <summary>Creates the context with settings of your own.</summary>
    /// <param name="options">The settings. The key shape of the file format needs camelCase names and null values left out.</param>
    public OneLlmYamlContext(YamlSerializerOptions options)
        : base(options)
    {
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(NaturalObjectJsonConverter)])]
[JsonSerializable(typeof(MessageWire))]
[JsonSerializable(typeof(PartWire))]
internal sealed partial class WireJsonContext : JsonSerializerContext;

[YamlSerializable(typeof(MessageWire))]
[YamlSerializable(typeof(PartWire))]
internal sealed partial class WireYamlContext : YamlSerializerContext
{
    public static WireYamlContext Default { get; } = new();

    public WireYamlContext()
        : base(YamlDefaults.Options())
    {
    }
}

internal static class YamlDefaults
{
    public static YamlSerializerOptions Options() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
    };
}
