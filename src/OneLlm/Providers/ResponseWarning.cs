namespace OneLlm.Providers;

/// <summary>A notice about something OneLLM changed or could not do for a request, such as a parameter it removed because the model does not take it.</summary>
/// <param name="Code">A stable, machine-readable code: <c>parameter_stripped</c> or <c>parameter_clamped</c>.</param>
/// <param name="Message">What happened and why, in a sentence that names the catalog field behind it.</param>
/// <param name="Parameter">The request parameter concerned, when there is one.</param>
public sealed record ResponseWarning(string Code, string Message, string? Parameter = null);
