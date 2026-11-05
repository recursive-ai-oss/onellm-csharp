using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Tools;

/// <summary>An argument the model sent does not fit the tool's schema. The message names the argument, so the model can fix the call.</summary>
public sealed class ToolInputException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="parameter">The argument's name or path, for example <c>caseNo</c> or <c>items[2].qty</c>.</param>
    /// <param name="problem">What is wrong with it, for example <c>is required</c>.</param>
    public ToolInputException(string parameter, string problem)
        : base($"Argument '{parameter}' {problem}")
    {
        Parameter = parameter;
    }

    /// <summary>The argument's name or path.</summary>
    public string Parameter { get; }
}

/// <summary>
/// Runs a tool the way the agent loop needs: a tool that throws, times out or gets bad arguments gives the model an error result
/// instead of crashing the run, and cost reported through the context is kept either way.
/// </summary>
public static class ToolInvoker
{
    /// <summary>Runs <paramref name="tool"/> once.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="inputs">The arguments. Null means none.</param>
    /// <param name="context">The context made for this call.</param>
    /// <param name="cancellationToken">Cancels the call. Cancellation is not an error result: it propagates, so the run records itself as cancelled.</param>
    /// <returns>
    /// The tool's result with the context's cost lines added (context lines first). On a thrown exception, an error result:
    /// <see cref="ToolInputException"/> gives its own message, a timeout says how long the limit was, anything else gives the exception type and message
    /// (never a stack trace).
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<ToolResult> InvokeAsync(ITool tool, JsonObject? inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan? timeout = tool.Definition.Timeout;
        using CancellationTokenSource? limit = timeout is { } t ? new CancellationTokenSource(t, context.TimeProvider) : null;
        using CancellationTokenSource? linked = limit is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        CancellationToken token = linked?.Token ?? cancellationToken;

        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(inputs ?? new JsonObject(), context, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (limit is { IsCancellationRequested: true })
        {
            result = ToolResult.Error($"Tool '{tool.Definition.Name}' timed out after {FormatSeconds(timeout!.Value)}.");
        }
        catch (ToolInputException e)
        {
            result = ToolResult.Error(e.Message);
        }
        catch (Exception e)
        {
            // The type and message help the model recover; the stack trace would only leak host paths.
            result = ToolResult.Error($"Tool '{tool.Definition.Name}' failed: {e.GetType().Name}: {e.Message}");
        }

        IReadOnlyList<CostLine> fromContext = context.CostLines;
        if (fromContext.Count == 0)
        {
            return result;
        }

        var lines = new List<CostLine>(fromContext.Count + result.CostLines.Count);
        lines.AddRange(fromContext);
        lines.AddRange(result.CostLines);
        return new ToolResult(result.Parts)
        {
            CostLines = lines,
            IsError = result.IsError,
            EndsLoop = result.EndsLoop,
            TypedResult = result.TypedResult,
        };
    }

    private static string FormatSeconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
}
