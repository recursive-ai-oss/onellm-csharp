using System.Text.Json.Serialization;
using OneLlm.Messages;

namespace OneLlm.Definitions;

/// <summary>
/// An agent definition: the model, the prompts, the tool sources and the loop limits of one agent, scripted flow or simple call.
/// It is the contents of an <c>agent.yaml</c> or <c>.flow</c> file. Every key the file carries that OneLLM does not model is kept in
/// <see cref="ExtensionData"/> and written back, so a file loaded and saved by OneLLM keeps what other tools put in it.
/// </summary>
/// <remarks>See <c>spec/definitions.md</c> for the format. Loading never grants a capability; <see cref="References"/> only name tool sources.</remarks>
public class AgentDefinition
{
    /// <summary>The display name. Empty when the file has none.</summary>
    public string Name { get; set; } = "";

    /// <summary>A one-line description, or null.</summary>
    public string? Description { get; set; }

    /// <summary>The model name or alias; null when the file does not say (the runtime then uses its default).</summary>
    public string? Model { get; set; }

    /// <summary>The service tier to request (<c>flex</c>, <c>priority</c>, ...), or null.</summary>
    public string? ServiceTier { get; set; }

    /// <summary>The reasoning controls to request, or null for the model's default.</summary>
    public ThinkingConfig? Thinking { get; set; }

    /// <summary>The sampling temperature, or null to leave it to the model's default.</summary>
    public double? Temperature { get; set; }

    /// <summary>The most model turns one run may take, or null for the runtime's default.</summary>
    public int? MaxIterations { get; set; }

    /// <summary>The most output tokens per turn, or null for the model's default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>The system prompt template. Rendered by the template engine; stored here as written.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>The tool sources the agent asks for, in file order.</summary>
    public List<ToolReference> References { get; set; } = [];

    /// <summary>The working directory template, or null for the runtime's working directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>The messages that open every run, in order. They may carry <c>when</c> conditions and tool calls to run before the first turn.</summary>
    public List<Message> InitialPrompts { get; set; } = [];

    /// <summary>Default input values for the templates; run inputs are merged over them.</summary>
    public Dictionary<string, object?>? Inputs { get; set; }

    /// <summary>Extra HTTP headers for HTTP tool sources, as templates.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>The invocation mode (<c>interactive</c>, <c>delegated</c> or <c>autonomous</c>) the definition defaults to, or null.</summary>
    public string? InvocationMode { get; set; }

    /// <summary>Whether to keep raw request and response logs for each turn; null for the runtime's setting.</summary>
    public bool? LlmTurnLogs { get; set; }

    /// <summary>What a template does with a key it cannot find: <c>default</c> renders <c>&lt;no value&gt;</c>, <c>error</c> fails. Null for the kind's default.</summary>
    public string? MissingKey { get; set; }

    /// <summary>Hook files that observe or intercept the run. See <c>spec/definitions.md</c>.</summary>
    public List<HookReference> Hooks { get; set; } = [];

    /// <summary>Keys the file carries that OneLLM does not model, in file order.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }

    /// <summary>Where the definition was loaded from (a file path or a name given by the caller), or null.</summary>
    [JsonIgnore]
    public string? SourceName { get; set; }

    /// <summary>Problems a lenient load stepped over (stripped references, ignored values). Empty after a clean load.</summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = [];

    /// <summary>The metadata of the <c>flow.yaml</c> beside a <c>.flow</c> file, or null. Kept whole; OneLLM does not interpret it.</summary>
    [JsonIgnore]
    public FlowMetadata? Flow { get; set; }

    /// <summary>Keys whose value was an empty list in the loaded file, so that saving writes them back as empty lists.</summary>
    [JsonIgnore]
    internal HashSet<string>? EmptyKeys { get; set; }

    /// <summary>The exact text this definition was loaded from, or null when it was built in code.</summary>
    [JsonIgnore]
    internal string? OriginalText { get; set; }

    /// <summary>The text <see cref="ToYaml"/> would have written at load time; when it still matches, saving returns <see cref="OriginalText"/>.</summary>
    [JsonIgnore]
    internal string? LoadedSerialization { get; set; }

    /// <summary>Parses a definition from YAML text.</summary>
    /// <param name="yaml">The file contents.</param>
    /// <param name="sourceName">The name used in error positions, usually the file path.</param>
    /// <param name="options">Strictness and the reference check; null for strict with no check.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="BadDefinitionException">The YAML is malformed (with file, line and column), a value has the wrong type, or in strict mode a reference is not granted.</exception>
    public static AgentDefinition Parse(string yaml, string? sourceName = null, DefinitionLoadOptions? options = null) =>
        DefinitionFiles.ParseAgent(yaml, sourceName, options ?? DefinitionLoadOptions.Strict);

    /// <summary>Reads a definition file. A <c>.flow</c> file also picks up the <c>flow.yaml</c> beside it into <see cref="Flow"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Strictness and the reference check; null for strict with no check.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="BadDefinitionException">As <see cref="Parse"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static AgentDefinition Load(string path, DefinitionLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        AgentDefinition def = Parse(File.ReadAllText(path), path, options);
        if (path.EndsWith(".flow", StringComparison.OrdinalIgnoreCase))
        {
            string meta = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "flow.yaml");
            if (File.Exists(meta))
            {
                def.Flow = FlowMetadata.Parse(File.ReadAllText(meta), meta);
            }
        }

        return def;
    }

    /// <summary>Writes the definition as YAML. A definition loaded from text and not changed since comes back byte for byte as it was read.</summary>
    /// <returns>The YAML text.</returns>
    public virtual string ToYaml()
    {
        string text = DefinitionFiles.WriteAgent(this);
        return OriginalText is not null && LoadedSerialization == text ? OriginalText : text;
    }

    /// <summary>Writes the definition to a file, replacing it atomically.</summary>
    /// <param name="path">The file path.</param>
    public void Save(string path) => AtomicFile.WriteAllText(path, ToYaml());
}

/// <summary>The request-side reasoning controls of a definition.</summary>
public sealed class ThinkingConfig
{
    /// <summary>The effort level (<c>low</c>, <c>medium</c>, <c>high</c>, ...), or null.</summary>
    public string? Effort { get; set; }

    /// <summary>The thinking token budget, or null.</summary>
    public int? Budget { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A tool source a definition asks for. Referencing a source does not grant the right to use it; the runtime's grants decide.</summary>
public sealed class ToolReference
{
    /// <summary>A routine to take tools from (a path or URI), or null.</summary>
    public string? Routine { get; set; }

    /// <summary>The tool names wanted from the routine. Null means all of them; an empty list means none.</summary>
    public List<string>? Tools { get; set; }

    /// <summary>True when the agent cannot work without this source.</summary>
    public bool? Required { get; set; }

    /// <summary>A group of routines to take every tool from, or null.</summary>
    public string? Swarm { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }

    /// <summary>Where the reference was in the file (1-based), or 0 when unknown.</summary>
    [JsonIgnore]
    public int Line { get; internal set; }

    /// <summary>The column of the reference in the file (1-based), or 0 when unknown.</summary>
    [JsonIgnore]
    public int Column { get; internal set; }
}

/// <summary>A hook file named by a definition.</summary>
public sealed class HookReference
{
    /// <summary>The hook file, relative to the definition.</summary>
    public string File { get; set; } = "";

    /// <summary>The points the hook runs at; null for every point the file handles.</summary>
    public List<string>? Points { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>How a load treats what it cannot accept.</summary>
public sealed class DefinitionLoadOptions
{
    /// <summary>Fail on an ungranted reference or an invalid value. This is the default.</summary>
    public static DefinitionLoadOptions Strict { get; } = new();

    /// <summary>Strip an ungranted reference or an invalid value and record it in <see cref="AgentDefinition.Warnings"/>.</summary>
    public static DefinitionLoadOptions Lenient { get; } = new() { IsStrict = false };

    /// <summary>True to fail, false to strip and warn.</summary>
    public bool IsStrict { get; init; } = true;

    /// <summary>Decides whether a reference is granted. Null accepts every reference (the runtime checks again when it starts the agent).</summary>
    public Func<ToolReference, bool>? IsGranted { get; init; }
}
