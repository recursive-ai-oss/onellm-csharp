using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OneLlm.Messages;

namespace OneLlm.Tools;

/// <summary>
/// Makes tools from delegates by reflection. This is the quick path for scripts and tests; it is not trim- or AOT-safe. For shipping code,
/// mark methods with <see cref="ToolAttribute"/> and let the generator write the tool: the schema is the same for the same signature.
/// </summary>
public static class Tool
{
    /// <summary>Makes a tool from a delegate.</summary>
    /// <param name="name">The tool's name: letters, digits, underscores and hyphens, at most 64 characters.</param>
    /// <param name="function">
    /// The function. Parameters of type <see cref="ToolContext"/> and <see cref="CancellationToken"/> are injected; every other parameter becomes an argument.
    /// It may return <c>void</c>, a value, a <see cref="Task"/> or a <see cref="ValueTask"/> of either. A <see cref="ToolResult"/> is passed through,
    /// a string becomes text, a <see cref="Part"/> a single part, anything else JSON.
    /// </param>
    /// <param name="description">What the tool does. Default: the function's <see cref="DescriptionAttribute"/>, or empty.</param>
    /// <param name="endsLoop">True when a successful call ends the agent loop with the return value as the typed result.</param>
    /// <param name="requiresApproval">True when each call needs approval.</param>
    /// <param name="parallelSafe">False when the tool must not run beside other calls.</param>
    /// <param name="timeout">The longest one call may run. Null means no limit.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="NotSupportedException">A parameter or the return type has no schema in the signature table.</exception>
    [RequiresUnreferencedCode("Reads the delegate's signature and attributes, and serializes types by reflection.")]
    [RequiresDynamicCode("Serializes types by reflection, which may need code that is not available ahead of time.")]
    public static ITool Create(
        string name,
        Delegate function,
        string? description = null,
        bool endsLoop = false,
        bool requiresApproval = false,
        bool parallelSafe = true,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new DelegateTool(name, function, description, endsLoop, requiresApproval, parallelSafe, timeout);
    }

    /// <summary>Makes a tool from a method group or a local function, naming it after the method in snake_case without an <c>Async</c> suffix.</summary>
    /// <param name="function">The function; see <see cref="Create(string, Delegate, string?, bool, bool, bool, TimeSpan?)"/>.</param>
    /// <param name="description">What the tool does.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentException">The delegate is a lambda, whose compiler-made name is not usable: use the overload that takes a name.</exception>
    [RequiresUnreferencedCode("Reads the delegate's signature and attributes, and serializes types by reflection.")]
    [RequiresDynamicCode("Serializes types by reflection, which may need code that is not available ahead of time.")]
    public static ITool Create(Delegate function, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        string method = function.Method.Name;
        if (method.Contains('<', StringComparison.Ordinal))
        {
            throw new ArgumentException("A lambda has no usable name; pass one to Tool.Create(name, function).", nameof(function));
        }

        if (method.EndsWith("Async", StringComparison.Ordinal) && method.Length > 5)
        {
            method = method[..^5];
        }

        return new DelegateTool(ReflectionSchema.SnakeCase(method), function, description, false, false, true, null);
    }

    [RequiresUnreferencedCode("Reads the delegate's signature and attributes, and serializes types by reflection.")]
    [RequiresDynamicCode("Serializes types by reflection, which may need code that is not available ahead of time.")]
    private sealed class DelegateTool : ITool
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private readonly Delegate _function;
        private readonly ParameterInfo[] _parameters;

        public DelegateTool(string name, Delegate function, string? description, bool endsLoop, bool requiresApproval, bool parallelSafe, TimeSpan? timeout)
        {
            _function = function;
            MethodInfo method = function.Method;
            _parameters = method.GetParameters();
            var schemaBuilder = new ReflectionSchema();
            JsonObject input = schemaBuilder.ForParameters(_parameters.Where(IsArgument));

            Type returned = Unwrap(method.ReturnType);
            JsonObject? output = null;
            if (returned != typeof(void) && !IsPassThrough(returned))
            {
                output = schemaBuilder.ForValue(returned, null);
            }

            Definition = new ToolSpec(name, description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty, input)
            {
                OutputSchema = output,
                EndsLoop = endsLoop,
                RequiresApproval = requiresApproval,
                ParallelSafe = parallelSafe,
                Timeout = timeout,
            };
        }

        public ToolSpec Definition { get; }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                // The schema says integer; a quoted number is a wrong-typed argument.
                NumberHandling = JsonNumberHandling.Strict,
                PropertyNameCaseInsensitive = false,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static bool IsArgument(ParameterInfo p) => p.ParameterType != typeof(ToolContext) && p.ParameterType != typeof(CancellationToken);

        private static bool IsPassThrough(Type t) =>
            t == typeof(string) || typeof(ToolResult).IsAssignableFrom(t) || typeof(Part).IsAssignableFrom(t) || typeof(JsonNode).IsAssignableFrom(t)
            || typeof(IEnumerable<Part>).IsAssignableFrom(t);

        private static Type Unwrap(Type t)
        {
            if (t == typeof(Task) || t == typeof(ValueTask))
            {
                return typeof(void);
            }

            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            {
                return t.GetGenericArguments()[0];
            }

            return t;
        }

        public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            var args = new object?[_parameters.Length];
            var names = new List<string>();
            for (int i = 0; i < _parameters.Length; i++)
            {
                ParameterInfo p = _parameters[i];
                if (p.ParameterType == typeof(ToolContext))
                {
                    args[i] = context;
                }
                else if (p.ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                }
                else
                {
                    args[i] = Bind(inputs, p);
                    names.Add(p.Name!);
                }
            }

            ToolBinder.RejectUnknown(inputs, [.. names]);

            object? raw;
            try
            {
                raw = _function.DynamicInvoke(args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }

            raw = await Await(raw).ConfigureAwait(false);
            return Convert(raw, _function.Method.ReturnType);
        }

        private static object? Bind(JsonObject inputs, ParameterInfo p)
        {
            string name = p.Name!;
            if (!inputs.TryGetPropertyValue(name, out JsonNode? node))
            {
                return p.HasDefaultValue ? p.DefaultValue : throw new ToolInputException(name, "is required but missing.");
            }

            bool nullable = Nullable.GetUnderlyingType(p.ParameterType) is not null
                || (!p.ParameterType.IsValueType && new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable);
            if (node is null)
            {
                return nullable ? null : throw new ToolInputException(name, "must not be null.");
            }

            try
            {
                return node.Deserialize(p.ParameterType, Options);
            }
            catch (JsonException e)
            {
                throw new ToolInputException(name, "does not match its schema: " + e.Message);
            }
            catch (InvalidOperationException e)
            {
                throw new ToolInputException(name, "does not match its schema: " + e.Message);
            }
        }

        private static async ValueTask<object?> Await(object? raw)
        {
            switch (raw)
            {
                case Task task:
                    await task.ConfigureAwait(false);
                    return TaskResult(task);
                case null:
                    return null;
            }

            Type t = raw.GetType();
            if (t == typeof(ValueTask))
            {
                await ((ValueTask)raw).ConfigureAwait(false);
                return null;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var task = (Task)t.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(raw, null)!;
                await task.ConfigureAwait(false);
                return TaskResult(task);
            }

            return raw;
        }

        private static object? TaskResult(Task task)
        {
            Type t = task.GetType();
            return t.IsGenericType && t.GetProperty(nameof(Task<int>.Result))!.PropertyType.FullName != "System.Threading.Tasks.VoidTaskResult"
                ? t.GetProperty(nameof(Task<int>.Result))!.GetValue(task)
                : null;
        }

        private ToolResult Convert(object? value, Type declared)
        {
            Type returned = Unwrap(declared);
            ToolSpec spec = Definition;
            ToolResult plain;
            switch (value)
            {
                case null:
                    return ToolResult.Empty;
                case ToolResult r:
                    return r;
                case string s:
                    plain = ToolResult.FromText(s);
                    return spec.EndsLoop ? new ToolResult(plain.Parts) { EndsLoop = true, TypedResult = JsonValue.Create(s) } : plain;
                case Part part:
                    return new ToolResult([part]) { EndsLoop = spec.EndsLoop };
                case IEnumerable<Part> parts:
                    return new ToolResult([.. parts]) { EndsLoop = spec.EndsLoop };
            }

            JsonNode? node = value is JsonNode jn
                ? jn
                : JsonSerializer.SerializeToNode(value, returned == typeof(void) ? value.GetType() : returned, Options);
            ToolResult result = ToolResult.FromJson(node);
            return spec.EndsLoop ? new ToolResult(result.Parts) { EndsLoop = true, TypedResult = node } : result;
        }
    }
}
