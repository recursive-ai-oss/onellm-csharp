using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;

namespace OneLlm.Extensions.AI;

/// <summary>Converts between Microsoft.Extensions.AI functions and OneLLM tools. The JSON schema passes through unchanged, so a round trip keeps it equal.</summary>
public static class ToolBridge
{
    /// <summary>A OneLLM tool as an <see cref="AIFunction"/> that runs it.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="time">The clock handed to the tool's <see cref="ToolContext"/>.</param>
    /// <param name="context">Builds the context for each call from the call id, or <see langword="null"/> for a context with no capabilities. Grant capabilities here.</param>
    /// <returns>A function named and described as the tool, with the tool's input schema. Invoking it throws <see cref="InvalidOperationException"/> when the tool reports an error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tool"/> or <paramref name="time"/> is <see langword="null"/>.</exception>
    public static AIFunction AsAIFunction(this ITool tool, TimeProvider time, Func<string, ToolContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(time);
        return new ToolFunction(tool, time, context);
    }

    /// <summary>An <see cref="AIFunction"/> as a OneLLM tool. An <c>ApprovalRequiredAIFunction</c> becomes a tool that requires approval.</summary>
    /// <param name="function">The function.</param>
    /// <returns>A tool with the function's name, description and input schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The function's name is not a valid OneLLM tool name (letters, digits, <c>_</c> and <c>-</c>, at most 64).</exception>
    public static ITool AsTool(this AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new FunctionTool(function);
    }

    private sealed class ToolFunction(ITool tool, TimeProvider time, Func<string, ToolContext>? context) : AIFunction
    {
        private readonly JsonElement _schema = JsonBridge.ToElement(tool.Definition.InputSchema);
        private readonly JsonElement? _returnSchema = tool.Definition.OutputSchema is { } output ? JsonBridge.ToElement(output) : null;

        public override string Name => tool.Definition.Name;

        public override string Description => tool.Definition.Description;

        public override JsonElement JsonSchema => _schema;

        public override JsonElement? ReturnJsonSchema => _returnSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            string callId = "meai-" + Guid.NewGuid().ToString("N");
            ToolContext ctx = context?.Invoke(callId) ?? new ToolContext("meai", callId, time);
            ToolResult result = await tool.InvokeAsync(JsonBridge.FromArguments(arguments), ctx, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
            {
                throw new InvalidOperationException(string.Concat(result.Parts.OfType<TextPart>().Select(p => p.Text)) is { Length: > 0 } text ? text : $"The tool '{Name}' failed.");
            }

            if (result.TypedResult is { } typed)
            {
                return JsonBridge.ToElement(typed);
            }

            if (result.Parts.Count == 1 && result.Parts[0] is JsonPart only)
            {
                return JsonBridge.ToElement(only.Value);
            }

            if (result.Parts.Count == 1 && result.Parts[0] is TextPart onlyText)
            {
                return onlyText.Text;
            }

            return result.Parts.Select(ContentMapper.ToContent).ToList();
        }
    }

    private sealed class FunctionTool : ITool
    {
        private readonly AIFunction _function;

        public FunctionTool(AIFunction function)
        {
            _function = function;
            JsonObject schema = JsonBridge.ToNode(function.JsonSchema) as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            JsonObject? output = function.ReturnJsonSchema is { } r ? JsonBridge.ToNode(r) as JsonObject : null;
            Definition = new ToolSpec(function.Name, function.Description ?? "", schema)
            {
                OutputSchema = output,
                RequiresApproval = function is ApprovalRequiredAIFunction,
            };
        }

        public ToolSpec Definition { get; }

        public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            var arguments = new AIFunctionArguments(JsonBridge.ToArguments(inputs));
            object? value = await _function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Part> parts = ContentMapper.ResultPartsOf(value);
            return new ToolResult(parts);
        }
    }
}
