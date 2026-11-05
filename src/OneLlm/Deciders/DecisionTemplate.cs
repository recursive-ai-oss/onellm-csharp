using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OneLlm.Providers;
using OneLlm.Templating;

namespace OneLlm.Deciders;

/// <summary>A decider rendered with one set of inputs: the state and the questions that go to the engine, and how they map back to the declared questions.</summary>
public sealed class RenderedDecision
{
    internal RenderedDecision(ReadOnlyMemory<byte> state, IReadOnlyList<WireQuestion> questions, IReadOnlyList<DeclaredQuestion> declared)
    {
        State = state;
        Questions = questions;
        Declared = declared;
    }

    /// <summary>The state as JSON.</summary>
    public ReadOnlyMemory<byte> State { get; }

    /// <summary>The questions to ask, in declared order, repeated questions expanded.</summary>
    public IReadOnlyList<WireQuestion> Questions { get; }

    /// <summary>The declared questions in order, each with how many wire questions it became (null when it is not repeated).</summary>
    internal IReadOnlyList<DeclaredQuestion> Declared { get; }
}

internal sealed record DeclaredQuestion(string Id, string Type, int? Count);

/// <summary>
/// The state and questions of a decider compiled to a render plan. The rules are the ones the decider format specifies: every string
/// containing <c>{{</c> is a template rendered with <c>missingkey=error</c>; a string that is exactly one action passes the action's value
/// through as structured JSON; keys are never templated; code regions are not escaped (a <c>{{</c> in backticks is live).
/// </summary>
internal sealed class DecisionTemplate
{
    private enum Kind { Literal, Text, Whole, Object, Array }

    private sealed class Node
    {
        public Kind Kind;
        public string Path = "";
        public byte[]? Raw;
        public Template? Template;
        public string[] Keys = [];
        public Node[] Values = [];
    }

    private sealed record Spec(string Id, string Type, Node Body, Node? ForEach);

    private static readonly Regex s_declaration = new(@"^\$\w*\s*(:=|=|,)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] s_keywords = ["if", "else", "end", "range", "with", "define", "template", "block", "break", "continue"];

    private readonly Node _state;
    private readonly List<Spec> _specs = [];

    private DecisionTemplate(Node state) => _state = state;

    /// <summary>True when any question repeats over a list.</summary>
    public bool HasRepeated { get; private set; }

    /// <summary>The functions a decider's templates may call: the runtime's set without <c>embed</c> (a decider is pure), plus the internal passthrough.</summary>
    public static TemplateFunctions FunctionsFor(TemplateFunctions source)
    {
        var result = TemplateFunctions.Empty();
        foreach (TemplateFunction f in source)
        {
            if (f.Name != "embed")
            {
                result.Add(f);
            }
        }

        result.Add(new TemplateFunction("__json", [ArgKind.Any], static a =>
        {
            var sb = new StringBuilder();
            GoJson.AppendValue(sb, a[0]);
            return sb.ToString();
        }));
        return result;
    }

    public static DecisionTemplate Compile(object? state, IReadOnlyDictionary<string, object?> questions, TemplateFunctions functions, string? sourceName)
    {
        var template = new DecisionTemplate(CompileNode(state, "state", functions, sourceName));
        foreach ((string id, object? body) in questions)
        {
            if (body is not IDictionary<string, object?> map)
            {
                throw new BadDefinitionException($"Question '{id}' must be a mapping.", sourceName);
            }

            string type = map.TryGetValue("type", out object? t) && t is string s ? s : "";
            Node? forEach = null;
            var keys = new List<string>();
            var values = new List<Node>();
            string path = "questions." + id;
            foreach ((string key, object? value) in map)
            {
                Node child = CompileNode(value, path + "." + key, functions, sourceName);
                if (key == "forEach")
                {
                    forEach = child;
                    template.HasRepeated = true;
                    continue;
                }

                keys.Add(key);
                values.Add(child);
            }

            var body2 = new Node { Kind = Kind.Object, Path = path, Keys = [.. keys], Values = [.. values] };
            template._specs.Add(new Spec(id, type, body2, forEach));
        }

        return template;
    }

    private static Node CompileNode(object? value, string path, TemplateFunctions functions, string? sourceName)
    {
        switch (value)
        {
            case null:
                return Literal(path, "null");
            case bool b:
                return Literal(path, b ? "true" : "false");
            case string text:
                return CompileString(text, path, functions, sourceName);
            case long l:
                return Literal(path, l.ToString(CultureInfo.InvariantCulture));
            case int i:
                return Literal(path, i.ToString(CultureInfo.InvariantCulture));
            case double d:
                {
                    var sb = new StringBuilder();
                    // A JSON number as the file's JSON form prints it; doubles in a definition are plain decimals.
                    GoJson.AppendDouble(sb, d);
                    return Literal(path, sb.ToString());
                }

            case decimal m:
                return Literal(path, m.ToString(CultureInfo.InvariantCulture));
            case IDictionary<string, object?> map:
                {
                    var keys = new List<string>(map.Count);
                    var values = new List<Node>(map.Count);
                    foreach ((string k, object? v) in map)
                    {
                        keys.Add(k);
                        values.Add(CompileNode(v, path + "." + k, functions, sourceName));
                    }

                    return new Node { Kind = Kind.Object, Path = path, Keys = [.. keys], Values = [.. values] };
                }

            case System.Collections.IEnumerable list:
                {
                    var values = new List<Node>();
                    int n = 0;
                    foreach (object? item in list)
                    {
                        values.Add(CompileNode(item, path + "[" + n++.ToString(CultureInfo.InvariantCulture) + "]", functions, sourceName));
                    }

                    return new Node { Kind = Kind.Array, Path = path, Values = [.. values] };
                }

            default:
                throw new BadDefinitionException($"The value at {path} has an unsupported type ({value.GetType().Name}).", sourceName);
        }
    }

    private static Node Literal(string path, string json) => new() { Kind = Kind.Literal, Path = path, Raw = Encoding.UTF8.GetBytes(json) };

    private static Node CompileString(string text, string path, TemplateFunctions functions, string? sourceName)
    {
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            GoJson.AppendString(sb, text);
            return Literal(path, sb.ToString());
        }

        Template parsed;
        try
        {
            parsed = Template.Parse(text, new TemplateParseOptions { Name = path, Functions = functions, EscapeCodeRegions = false });
        }
        catch (TemplateException e)
        {
            throw new BadDefinitionException($"The template at {path} does not compile: {e.Message}", sourceName, 0, 0, e);
        }

        if (WholeAction(text) is { } action)
        {
            try
            {
                Template whole = Template.Parse("{{ __json (" + action + ") }}", new TemplateParseOptions { Name = path, Functions = functions, EscapeCodeRegions = false });
                return new Node { Kind = Kind.Whole, Path = path, Template = whole };
            }
            catch (TemplateException)
            {
                // Not a plain pipeline after all: render it as text.
            }
        }

        return new Node { Kind = Kind.Text, Path = path, Template = parsed };
    }

    /// <summary>True when the text is exactly one action (no variable declaration, no control keyword), the form <c>forEach</c> must take.</summary>
    internal static bool IsWholeAction(string text) => WholeAction(text) is not null;

    // A value whose whole trimmed text is one {{ ... }} action with no variable declaration passes the action's value through as JSON.
    private static string? WholeAction(string text)
    {
        string t = text.Trim();
        if (t.Length < 5 || !t.StartsWith("{{", StringComparison.Ordinal) || !t.EndsWith("}}", StringComparison.Ordinal))
        {
            return null;
        }

        string inner = t[2..^2];
        if (inner.Contains("{{", StringComparison.Ordinal) || inner.Contains("}}", StringComparison.Ordinal))
        {
            return null;
        }

        // Trim markers only remove whitespace around the action, and the text is already trimmed.
        if (inner.StartsWith("- ", StringComparison.Ordinal))
        {
            inner = inner[2..];
        }

        if (inner.EndsWith(" -", StringComparison.Ordinal))
        {
            inner = inner[..^2];
        }

        inner = inner.Trim();
        if (inner.Length == 0 || inner.StartsWith("/*", StringComparison.Ordinal) || s_declaration.IsMatch(inner))
        {
            return null;
        }

        int space = 0;
        while (space < inner.Length && char.IsLetter(inner[space]))
        {
            space++;
        }

        string word = inner[..space];
        if (Array.IndexOf(s_keywords, word) >= 0 && (space == inner.Length || !char.IsLetterOrDigit(inner[space])))
        {
            return null;
        }

        return inner;
    }

    /// <summary>Renders the state and every question with the template data.</summary>
    /// <param name="data">The template data: <c>inputs</c> and the other scope names, as plain values.</param>
    /// <param name="options">Render options (missing keys are always errors for deciders).</param>
    /// <returns>The rendered decision.</returns>
    /// <exception cref="DeciderRenderException">A template fails to render (a missing key is an error), or a <c>forEach</c> does not give a list.</exception>
    public RenderedDecision Render(IDictionary<string, object?> data, TemplateRenderOptions options)
    {
        var stateBuffer = new ArrayBufferWriter<byte>(256);
        RenderNode(_state, stateBuffer, data, options);
        var questions = new List<WireQuestion>(_specs.Count);
        var declared = new List<DeclaredQuestion>(_specs.Count);
        foreach (Spec spec in _specs)
        {
            if (spec.ForEach is null)
            {
                var buffer = new ArrayBufferWriter<byte>(128);
                RenderNode(spec.Body, buffer, data, options);
                questions.Add(new WireQuestion(spec.Id, spec.Type, buffer.WrittenMemory.ToArray()));
                declared.Add(new DeclaredQuestion(spec.Id, spec.Type, null));
                continue;
            }

            var listBuffer = new ArrayBufferWriter<byte>(64);
            RenderNode(spec.ForEach, listBuffer, data, options);
            List<object?> list = ReadList(listBuffer.WrittenSpan, spec.Body.Path);
            declared.Add(new DeclaredQuestion(spec.Id, spec.Type, list.Count));
            // .item and .index sit beside the shared names; one small map per element.
            var element = new Dictionary<string, object?>(data, StringComparer.Ordinal);
            for (int n = 0; n < list.Count; n++)
            {
                element["item"] = list[n];
                element["index"] = (long)n;
                var buffer = new ArrayBufferWriter<byte>(128);
                try
                {
                    RenderNode(spec.Body, buffer, element, options);
                }
                catch (DeciderRenderException e)
                {
                    throw new DeciderRenderException($"{e.Message} (forEach element {n.ToString(CultureInfo.InvariantCulture)} of {spec.Id})", spec.Body.Path, e);
                }

                questions.Add(new WireQuestion(spec.Id + "[" + n.ToString(CultureInfo.InvariantCulture) + "]", spec.Type, buffer.WrittenMemory.ToArray()));
            }
        }

        return new RenderedDecision(stateBuffer.WrittenMemory.ToArray(), questions, declared);
    }

    private static List<object?> ReadList(ReadOnlySpan<byte> json, string path)
    {
        object? value = null;
        bool ok = false;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(json);
            using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
            value = DeciderValues.FromElement(doc.RootElement);
            ok = true;
        }
        catch (System.Text.Json.JsonException)
        {
            // Reported below.
        }

        if (ok && value is List<object?> list)
        {
            return list;
        }

        string what = !ok ? "nothing" : value switch { null => "null", string => "a string", bool => "a boolean", IDictionary<string, object?> => "an object", _ => "a number" };
        throw new DeciderRenderException($"rendering {path}.forEach: must resolve to a list, got {what}. Make it a required array in the inputs, or normalize a missing list to [] in the decider's prepare step.", path + ".forEach");
    }

    private static void RenderNode(Node node, IBufferWriter<byte> output, IDictionary<string, object?> data, TemplateRenderOptions options)
    {
        switch (node.Kind)
        {
            case Kind.Literal:
                GoJson.WriteBytes(output, node.Raw);
                break;
            case Kind.Text:
                GoJson.WriteString(output, RenderTemplate(node, data, options));
                break;
            case Kind.Whole:
                {
                    string text = RenderTemplate(node, data, options).Trim();
                    GoJson.WriteText(output, text.Length == 0 ? "null" : text);
                    break;
                }

            case Kind.Object:
                GoJson.WriteText(output, "{");
                for (int i = 0; i < node.Keys.Length; i++)
                {
                    if (i > 0)
                    {
                        GoJson.WriteText(output, ",");
                    }

                    GoJson.WriteString(output, node.Keys[i]);
                    GoJson.WriteText(output, ":");
                    RenderNode(node.Values[i], output, data, options);
                }

                GoJson.WriteText(output, "}");
                break;
            default:
                GoJson.WriteText(output, "[");
                for (int i = 0; i < node.Values.Length; i++)
                {
                    if (i > 0)
                    {
                        GoJson.WriteText(output, ",");
                    }

                    RenderNode(node.Values[i], output, data, options);
                }

                GoJson.WriteText(output, "]");
                break;
        }
    }

    private static string RenderTemplate(Node node, IDictionary<string, object?> data, TemplateRenderOptions options)
    {
        try
        {
            return node.Template!.Render(data, options);
        }
        catch (TemplateException e)
        {
            throw new DeciderRenderException($"rendering {node.Path}: {e.Detail}", node.Path, e);
        }
    }
}
