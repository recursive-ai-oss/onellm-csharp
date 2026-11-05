using System.Text.Json;
using OneLlm.Definitions;
using OneLlm.Templating;

namespace OneLlm.Deciders;

/// <summary>
/// The code half of a decider: an optional step that turns the caller's inputs into the inputs the templates read (<c>prepare</c>),
/// and an optional step that turns the answers into the outputs (<c>decide</c>). A decider with no logic renders its templates with
/// the caller's inputs and returns its answers as its outputs. C# logic is generated (<c>[Decider]</c>); the JavaScript package
/// implements this interface over <c>decider.js</c>.
/// </summary>
public interface IDeciderLogic
{
    /// <summary>True when <see cref="PrepareAsync"/> does something. When false it is not called.</summary>
    bool HasPrepare { get; }

    /// <summary>True when <see cref="DecideAsync"/> does something. When false it is not called and the answers are the outputs.</summary>
    bool HasDecide { get; }

    /// <summary>Turns the caller's inputs into the inputs the templates read. Must be pure.</summary>
    /// <param name="inputs">The caller's inputs as JSON.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>The prepared inputs as JSON.</returns>
    ValueTask<JsonElement> PrepareAsync(JsonElement inputs, CancellationToken cancellationToken);

    /// <summary>Turns the answers into the outputs. Must be pure.</summary>
    /// <param name="answers">The answers by question id; a repeated question's answer is a list.</param>
    /// <param name="inputs">The prepared inputs.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>The outputs as JSON.</returns>
    ValueTask<JsonElement> DecideAsync(JsonElement answers, JsonElement inputs, CancellationToken cancellationToken);
}

/// <summary>
/// A decider ready to run: its definition, its logic (or none), and a name. Load one from a directory or a <c>decider.yaml</c>, or take
/// the one the <c>[Decider]</c> generator emits for a C# class. Run it with <see cref="Runtime.DecideAsync(Decider, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>
/// or <c>Llm.DecideAsync</c>.
/// </summary>
public sealed class Decider
{
    private readonly object _gate = new();
    private DecisionTemplate? _template;
    private int _functionsVersion = -1;
    private TemplateFunctions? _functionsSource;

    private Decider(string name, DeciderDefinition definition, IDeciderLogic? logic)
    {
        Name = name;
        Definition = definition;
        Logic = logic;
    }

    /// <summary>The decider's name: its directory's name, or what the creator gave. Names the instances its runs create.</summary>
    public string Name { get; }

    /// <summary>The definition.</summary>
    public DeciderDefinition Definition { get; }

    /// <summary>The logic, or null for a decider that only answers.</summary>
    public IDeciderLogic? Logic { get; }

    /// <summary>Reads a decider from a directory holding <c>decider.yaml</c>, or from a <c>decider.yaml</c> file.</summary>
    /// <param name="path">The directory or the file.</param>
    /// <param name="logic">The logic to run with it, or null.</param>
    /// <returns>The decider.</returns>
    /// <exception cref="BadDefinitionException">The file is invalid; carries file, line and column.</exception>
    /// <exception cref="FileNotFoundException">There is no <c>decider.yaml</c>.</exception>
    public static Decider Load(string path, IDeciderLogic? logic = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string file = Directory.Exists(path) ? Path.Combine(path, "decider.yaml") : path;
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"There is no decider at '{path}': expected a directory with decider.yaml, or the file itself.", file);
        }

        string full = Path.GetFullPath(file);
        string name = Path.GetFileName(Path.GetDirectoryName(full)) ?? "decider";
        return new Decider(name, DeciderDefinition.Parse(File.ReadAllText(full), full), logic);
    }

    /// <summary>Reads a decider from YAML text.</summary>
    /// <param name="yaml">The <c>decider.yaml</c> contents.</param>
    /// <param name="name">The decider's name.</param>
    /// <param name="logic">The logic to run with it, or null.</param>
    /// <returns>The decider.</returns>
    /// <exception cref="BadDefinitionException">The YAML is invalid.</exception>
    public static Decider FromYaml(string yaml, string name, IDeciderLogic? logic = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new Decider(name, DeciderDefinition.Parse(yaml, name + "/decider.yaml"), logic);
    }

    /// <summary>The same decider with logic attached (or replaced).</summary>
    /// <param name="logic">The logic.</param>
    /// <returns>A new decider over the same definition.</returns>
    public Decider WithLogic(IDeciderLogic? logic) => new(Name, Definition, logic);

    internal DecisionTemplate GetTemplate(TemplateFunctions functions)
    {
        lock (_gate)
        {
            if (_template is null || !ReferenceEquals(_functionsSource, functions) || _functionsVersion != functions.Version)
            {
                _template = DecisionTemplate.Compile(Definition.RawState, Definition.RawQuestions, DecisionTemplate.FunctionsFor(functions), Definition.SourceName);
                _functionsSource = functions;
                _functionsVersion = functions.Version;
            }

            return _template;
        }
    }
}
