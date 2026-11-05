using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OneLlm.Extensions.AI;

/// <summary>
/// A function declaration (name, description, schema) that is not invokable, built from a OneLLM tool definition. Microsoft.Extensions.AI reads a
/// strict-schema request from the declaration's <c>strictJsonSchema</c> property, which the factory methods cannot set, so the bridge declares it itself.
/// </summary>
internal sealed class ToolDeclaration : AIFunctionDeclaration
{
    private readonly JsonElement _schema;
    private readonly IReadOnlyDictionary<string, object?> _properties;

    public ToolDeclaration(string name, string description, JsonElement schema, bool strict)
    {
        Name = name;
        Description = description;
        _schema = schema;
        _properties = strict ? new Dictionary<string, object?> { ["strictJsonSchema"] = true } : new Dictionary<string, object?>();
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema => _schema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _properties;
}
