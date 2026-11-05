namespace OneLlm.Tools;

/// <summary>
/// Marks a method as a tool the model can call. The class must be <c>partial</c>: the OneLLM source generator adds an
/// <see cref="ITool"/> for each marked method, with the input schema taken from the parameter list and XML doc comments
/// (<c>&lt;summary&gt;</c> for the tool, <c>&lt;param&gt;</c> for each argument; <see cref="System.ComponentModel.DescriptionAttribute"/> wins when both exist).
/// </summary>
/// <remarks>
/// Parameters of type <see cref="ToolContext"/> and <see cref="CancellationToken"/> are filled in by the runtime and are not part of the schema.
/// The signature table that says what schema each C# type gets is in <c>docs/tool-schemas.md</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>The tool's name. Default: the method name in snake_case without an <c>Async</c> suffix (<c>GetDocketAsync</c> becomes <c>get_docket</c>).</summary>
    public string? Name { get; set; }

    /// <summary>The description for the model. Default: the method's XML <c>&lt;summary&gt;</c>, or its <see cref="System.ComponentModel.DescriptionAttribute"/>.</summary>
    public string? Description { get; set; }

    /// <summary>True when a successful call ends the agent loop and the return value is the loop's typed result. Default false.</summary>
    public bool EndsLoop { get; set; }

    /// <summary>True when each call needs a person's approval before it runs. Default false.</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>False when the tool must not run beside other calls from the same turn. Default true.</summary>
    public bool ParallelSafe { get; set; } = true;

    /// <summary>The longest one call may run, in seconds. 0 (the default) means no limit.</summary>
    public int TimeoutSeconds { get; set; }
}
