using System.Text.Json.Nodes;
using OneLlm.Providers;

namespace OneLlm.Tools;

/// <summary>
/// A client-side tool the model can call. This is the one contract every tool source implements: generated C# tools,
/// <see cref="Tool"/> tools, and the tool sources later packages add.
/// </summary>
/// <remarks>
/// <see cref="InvokeAsync"/> may throw. Callers that run tools for a model should go through <see cref="ToolInvoker"/>, which turns
/// exceptions and timeouts into error results, as the loop rules require.
/// </remarks>
public interface ITool
{
    /// <summary>The tool's name, description, input schema and run metadata.</summary>
    ToolSpec Definition { get; }

    /// <summary>Runs the tool.</summary>
    /// <param name="inputs">The arguments the model wrote, as a JSON object. An empty object when the model sent none.</param>
    /// <param name="context">What the tool may use. One context belongs to one call; do not share it between calls.</param>
    /// <param name="cancellationToken">Cancels the call. A tool that observes it should throw <see cref="OperationCanceledException"/>.</param>
    /// <returns>The result to give the model.</returns>
    /// <exception cref="ToolInputException">An argument is missing, unknown or of the wrong type.</exception>
    ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>A tool's definition: what the model sees, plus the metadata the loop uses to run it.</summary>
public sealed record ToolSpec
{
    /// <summary>Creates a definition.</summary>
    /// <param name="name">The tool's name: letters, digits, underscores and hyphens, at most 64 characters (what every provider accepts).</param>
    /// <param name="description">What the tool does and when to use it, written for the model.</param>
    /// <param name="inputSchema">A JSON Schema 2020-12 object for the arguments.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, too long or has characters providers reject.</exception>
    public ToolSpec(string name, string description, JsonObject inputSchema)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid tool name: use letters, digits, '_' and '-', at most 64 characters.", nameof(name));
        }

        Name = name;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
    }

    /// <summary>The tool's name.</summary>
    public string Name { get; }

    /// <summary>What the tool does, written for the model.</summary>
    public string Description { get; }

    /// <summary>The JSON Schema for the arguments.</summary>
    public JsonObject InputSchema { get; }

    /// <summary>A JSON Schema for the value the tool returns as a typed result, when it has one. Not sent to the model.</summary>
    public JsonObject? OutputSchema { get; init; }

    /// <summary>True when a successful call ends the agent loop; the result's <see cref="ToolResult.TypedResult"/> is the loop's output.</summary>
    public bool EndsLoop { get; init; }

    /// <summary>True when a person must approve each call before it runs.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>A rough price of one call in USD, shown to approvers and used for planning. Zero when unknown. Never charged.</summary>
    public decimal CostHint { get; init; }

    /// <summary>False when the tool must not run beside other tool calls from the same turn. Default true.</summary>
    public bool ParallelSafe { get; init; } = true;

    /// <summary>How long one call may run before <see cref="ToolInvoker"/> gives up and returns an error result. Null means no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Whether <paramref name="name"/> satisfies the provider rules on tool names.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns>True when it is 1 to 64 letters, digits, underscores or hyphens.</returns>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 64)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The provider-neutral definition to put in a <see cref="ChatRequest"/>.</summary>
    /// <returns>A definition with this tool's name, description and input schema.</returns>
    public ToolDefinition ToProviderDefinition() => new(Name, Description, InputSchema);
}

/// <summary>
/// Implemented (by the generator) on partial classes that hold <see cref="ToolAttribute"/> methods, so a runtime can register
/// all of a class's tools without reflection.
/// </summary>
/// <typeparam name="TSelf">The class itself.</typeparam>
public interface IToolSource<TSelf>
    where TSelf : class, IToolSource<TSelf>
{
    /// <summary>Makes one <see cref="ITool"/> per <see cref="ToolAttribute"/> method.</summary>
    /// <param name="instance">The object instance methods run on. May be null when the class has a public parameterless constructor (one is made) or when every tool method is static.</param>
    /// <returns>The tools, in declaration order.</returns>
    static abstract IReadOnlyList<ITool> GetTools(TSelf? instance = null);
}
