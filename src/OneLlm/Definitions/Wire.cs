using System.Text.Json.Serialization;
using OneLlm.Instances;
using OneLlm.Messages;
using OneYaml;

namespace OneLlm.Definitions;

// The file shapes of definitions, flows, deciders and instances. These classes are what the YAML serializer reads and writes;
// the public AgentDefinition / Instance types are mapped from and to them. Every shape keeps unknown keys in ExtensionData.

/// <summary>The union of the keys of a definition file and an instance file, in the order instance files write them.</summary>
internal sealed class AgentFileWire
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Model { get; set; }

    public string? RequestedModel { get; set; }

    public string? ServiceTier { get; set; }

    public ThinkingConfig? Thinking { get; set; }

    public double? Temperature { get; set; }

    public int? MaxIterations { get; set; }

    public int? MaxTokens { get; set; }

    public int? MaxImageSize { get; set; }

    public bool? LlmTurnLogs { get; set; }

    public string? InvocationMode { get; set; }

    public string? MissingKey { get; set; }

    public string? State { get; set; }

    public string? InstanceId { get; set; }

    [JsonPropertyName("agentDefinition")]
    public string? DefinitionPath { get; set; }

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? CallerInstanceId { get; set; }

    public string? SpawnerInstanceId { get; set; }

    public string? CallerToolCallId { get; set; }

    public string? DelegationPrompt { get; set; }

    public string? RunStartedAt { get; set; }

    public InstanceMetadataWire? Metadata { get; set; }

    public Dictionary<string, object?>? Inputs { get; set; }

    public string? SystemPrompt { get; set; }

    public List<ToolReference>? References { get; set; }

    public List<HookReference>? Hooks { get; set; }

    public List<Message>? InitialPrompts { get; set; }

    public List<PendingApproval>? PendingApprovals { get; set; }

    public List<Compaction>? Compactions { get; set; }

    public List<Message>? Messages { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

internal sealed class InstanceMetadataWire
{
    public string? RenderedSystemPrompt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>The shape of a <c>decider.yaml</c>.</summary>
internal sealed class DeciderFileWire
{
    public string? Model { get; set; }

    public object? State { get; set; }

    public Dictionary<string, DeciderQuestion>? Questions { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>Metadata for a flow: the keys of <c>flow.yaml</c>, kept whole.</summary>
internal sealed class FlowMetadataWire
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public int? Version { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

[YamlSerializable(typeof(AgentFileWire))]
[YamlSerializable(typeof(DeciderFileWire))]
[YamlSerializable(typeof(FlowMetadataWire))]
[YamlSerializable(typeof(List<Message>))]
[YamlSerializable(typeof(string))]
internal sealed partial class DefinitionsYamlContext : YamlSerializerContext
{
    public static DefinitionsYamlContext Default { get; } = new();

    public DefinitionsYamlContext()
        : base(YamlDefaults.Options())
    {
    }
}
