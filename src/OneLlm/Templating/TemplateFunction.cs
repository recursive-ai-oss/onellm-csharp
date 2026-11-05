using System.Collections;

namespace OneLlm.Templating;

/// <summary>The type a template function parameter accepts. Template values are strings, integers (<c>long</c>), floats (<c>double</c>), booleans, lists, maps and null.</summary>
public enum ArgKind
{
    /// <summary>Any value, including null.</summary>
    Any,
    /// <summary>A string.</summary>
    Text,
    /// <summary>An integer (<see cref="long"/>).</summary>
    Whole,
    /// <summary>A float (<see cref="double"/>); integer literals in the template convert, integer values do not.</summary>
    Real,
    /// <summary>A boolean.</summary>
    Bool,
}

/// <summary>
/// A function callable from a template. Arguments arrive already checked against <see cref="Parameters"/>
/// (and <see cref="Variadic"/>), as <c>string</c>, <c>long</c>, <c>double</c>, <c>bool</c>, lists, maps or null, so the delegate
/// needs no reflection and is trim- and AOT-safe. The result may be any template value; throw
/// <see cref="TemplateFunctionException"/> to fail the render with a positioned error.
/// </summary>
public sealed class TemplateFunction
{
    /// <summary>The name used in templates.</summary>
    public string Name { get; }

    /// <summary>The fixed parameters, in order.</summary>
    public IReadOnlyList<ArgKind> Parameters { get; }

    /// <summary>The kind of any further arguments, or <c>null</c> when the function takes exactly <see cref="Parameters"/>.</summary>
    public ArgKind? Variadic { get; }

    /// <summary>Called with one entry per argument (variadic arguments follow the fixed ones).</summary>
    public Func<object?[], object?> Invoke { get; }

    internal FunctionRole Role { get; }

    /// <summary>Set for functions that need the render in progress (embed, now); called instead of <see cref="Invoke"/>.</summary>
    internal ContextFunc? Context { get; init; }

    /// <summary>Creates a function.</summary>
    /// <param name="name">The name; must be a template identifier (letters, digits, underscore, not starting with a digit).</param>
    /// <param name="parameters">The fixed parameter kinds.</param>
    /// <param name="invoke">The implementation.</param>
    /// <param name="variadic">The kind of trailing arguments, if the function accepts any.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid identifier.</exception>
    public TemplateFunction(string name, IReadOnlyList<ArgKind> parameters, Func<object?[], object?> invoke, ArgKind? variadic = null)
        : this(name, parameters, invoke, variadic, FunctionRole.Normal)
    {
    }

    internal TemplateFunction(string name, IReadOnlyList<ArgKind> parameters, Func<object?[], object?> invoke, ArgKind? variadic, FunctionRole role)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(invoke);
        if (!IsIdentifier(name))
        {
            throw new ArgumentException($"'{name}' is not a valid template function name.", nameof(name));
        }

        Name = name;
        Parameters = parameters;
        Variadic = variadic;
        Invoke = invoke;
        Role = role;
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c != '_' && !char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

internal enum FunctionRole { Normal, And, Or }

internal delegate object? ContextFunc(Exec ctx, object?[] args);

/// <summary>
/// The functions a template can call besides the language builtins. A new instance starts with the standard set
/// (path, string and logic helpers plus the OneLLM extensions); host functions are added with <c>Add</c>.
/// Registration is not thread-safe; finish adding before rendering.
/// </summary>
public sealed class TemplateFunctions : IEnumerable<TemplateFunction>
{
    private readonly Dictionary<string, TemplateFunction> _map;

    /// <summary>Changes whenever a function is added; part of the parse-cache key.</summary>
    internal int Version { get; private set; }

    /// <summary>Creates a registry holding the standard functions.</summary>
    public TemplateFunctions()
    {
        _map = new Dictionary<string, TemplateFunction>(StandardFunctions.All, StringComparer.Ordinal);
    }

    private TemplateFunctions(bool empty)
    {
        _map = new Dictionary<string, TemplateFunction>(StringComparer.Ordinal);
        _ = empty;
    }

    /// <summary>Creates a registry with no functions at all (the language builtins still work).</summary>
    public static TemplateFunctions Empty() => new(true);

    /// <summary>The shared standard registry used when no registry is given. Do not modify it; create a new <see cref="TemplateFunctions"/> to add functions.</summary>
    public static TemplateFunctions Standard { get; } = new();

    /// <summary>Adds or replaces a function.</summary>
    public TemplateFunctions Add(TemplateFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (ReferenceEquals(this, Standard))
        {
            throw new InvalidOperationException("The shared standard registry is read-only; create a new TemplateFunctions.");
        }

        _map[function.Name] = function;
        Version++;
        return this;
    }

    /// <summary>Adds a one-string-argument function.</summary>
    public TemplateFunctions Add(string name, Func<string, object?> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Add(new TemplateFunction(name, new[] { ArgKind.Text }, a => function((string)a[0]!)));
    }

    /// <summary>Adds a two-string-argument function.</summary>
    public TemplateFunctions Add(string name, Func<string, string, object?> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Add(new TemplateFunction(name, new[] { ArgKind.Text, ArgKind.Text }, a => function((string)a[0]!, (string)a[1]!)));
    }

    /// <summary>Adds a function taking any number of arguments of any type (the delegate receives them as an array).</summary>
    public TemplateFunctions AddVariadic(string name, Func<object?[], object?> function)
    {
        return Add(new TemplateFunction(name, Array.Empty<ArgKind>(), function, ArgKind.Any));
    }

    /// <summary>Looks a function up by name.</summary>
    public bool TryGet(string name, out TemplateFunction? function) => _map.TryGetValue(name, out function);

    /// <inheritdoc />
    public IEnumerator<TemplateFunction> GetEnumerator() => _map.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
