using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using OneYaml;

namespace OneLlm.JavaScript;

/// <summary>
/// Loads JavaScript tool files as <see cref="ITool"/>s, and makes the <c>run_javascript</c> built-in. A tool file is a module that exports
/// <c>invoke(inputs, ctx)</c> and describes itself either with an exported <c>tool</c> object or in a <c>.tool.yaml</c> file beside it.
/// </summary>
/// <remarks>
/// <para>The description has: <c>name</c> (default: the file name), <c>description</c> (required), <c>inputSchema</c> (a JSON Schema object; default: no arguments),
/// <c>requires</c> (a list of <c>fileRead</c>, <c>fileWrite</c>, <c>network</c>), and optionally <c>outputSchema</c>, <c>endsLoop</c>, <c>requiresApproval</c>, <c>costHint</c>
/// (USD; give a string for an exact decimal), <c>parallelSafe</c> and <c>timeout</c> (seconds).</para>
/// <para>What <c>invoke</c> returns: a string becomes text, <c>undefined</c> becomes an empty result, anything else becomes a JSON part (and the typed result
/// when the tool ends the loop). A throw becomes an error result with a positioned report. Loading a file reads it as the host and runs its top level once under the
/// sandbox limits; it never grants anything: the tool can use only the capabilities its <c>requires</c> list names, and only where the host granted them.</para>
/// </remarks>
public static class JsTools
{
    private static readonly string[] s_requireNames = ["fileRead", "fileWrite", "network"];

    /// <summary>Loads one tool file.</summary>
    /// <param name="path">A <c>.js</c> file. A <c>.tool.yaml</c> beside it (same name) is used for the description when it exists.</param>
    /// <param name="options">The sandbox limits the tool will run under, or null for the defaults.</param>
    /// <param name="timeProvider">The clock for loading. Default: the system clock. Calls use the clock of their <see cref="ToolContext"/>.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="BadDefinitionException">The file is missing, does not parse, has no <c>invoke</c>, or its description is invalid. The message names file, line and column when known.</exception>
    public static ITool Load(string path, JsSandboxOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        string file = Path.GetFileName(path);
        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new BadDefinitionException("cannot read the tool file: " + e.Message, file, 0, 0, e);
        }

        var sandbox = new JsSandbox(options, timeProvider);
        JsonNode? description;
        bool hasInvoke;
        try
        {
            (description, hasInvoke) = sandbox.Inspect(source, file);
        }
        catch (JsSandboxException e)
        {
            throw new BadDefinitionException(e.Message, file, e.Line, e.Column, e);
        }

        string yamlPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path) + ".tool.yaml");
        string describedIn = file;
        if (File.Exists(yamlPath))
        {
            describedIn = Path.GetFileName(yamlPath);
            description = ReadYaml(yamlPath, describedIn);
        }

        if (!hasInvoke)
        {
            throw new BadDefinitionException("the module must export a function named 'invoke(inputs, ctx)'.", file);
        }

        if (description is not JsonObject d)
        {
            throw new BadDefinitionException("a tool needs a description: export a 'tool' object or put a .tool.yaml beside the file.", file);
        }

        return JsTool.Create(d, source, file, describedIn, new JsSandbox(options, timeProvider));
    }

    /// <summary>Loads every <c>.js</c> file directly inside a directory, in name order.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="options">The sandbox limits, or null for the defaults.</param>
    /// <param name="timeProvider">The clock for loading. Default: the system clock.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="BadDefinitionException">A file is not a valid tool, or two files declare the same tool name.</exception>
    public static IReadOnlyList<ITool> LoadDirectory(string directory, JsSandboxOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string[] files = Directory.GetFiles(directory, "*.js");
        Array.Sort(files, StringComparer.Ordinal);
        var tools = new List<ITool>(files.Length);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in files)
        {
            ITool tool = Load(f, options, timeProvider);
            if (!seen.TryAdd(tool.Definition.Name, Path.GetFileName(f)))
            {
                throw new BadDefinitionException($"the tool name '{tool.Definition.Name}' is already used by {seen[tool.Definition.Name]}.", Path.GetFileName(f));
            }

            tools.Add(tool);
        }

        return tools;
    }

    /// <summary>Makes the <c>run_javascript</c> built-in: the model writes a snippet, the sandbox runs it under the caller's grants.</summary>
    /// <param name="options">The sandbox limits, or null for the defaults.</param>
    /// <returns>The tool.</returns>
    public static ITool RunJavaScript(JsSandboxOptions? options = null) => new RunJavaScriptTool(new JsSandbox(options));

    private static JsonNode? ReadYaml(string path, string file)
    {
        try
        {
            using YamlDocument doc = YamlDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
            return JsonBridge.FromYaml(doc.Root);
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, file, 0, 0, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new BadDefinitionException("cannot read the description: " + e.Message, file, 0, 0, e);
        }
    }

    internal static ToolCapability ParseRequires(JsonNode? node, string file)
    {
        ToolCapability result = ToolCapability.None;
        if (node is null)
        {
            return result;
        }

        if (node is not JsonArray list)
        {
            throw new BadDefinitionException("'requires' must be a list of fileRead, fileWrite, network.", file);
        }

        foreach (JsonNode? item in list)
        {
            string? name = item is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            result |= name switch
            {
                "fileRead" => ToolCapability.FileRead,
                "fileWrite" => ToolCapability.FileWrite,
                "network" => ToolCapability.Network,
                _ => throw new BadDefinitionException($"'requires' has '{name}'; the choices are {string.Join(", ", s_requireNames)}. JavaScript tools cannot run shell commands.", file),
            };
        }

        return result;
    }

    internal static decimal ParseDecimal(JsonNode? node, string key, string file)
    {
        if (node is null)
        {
            return 0m;
        }

        if (node is JsonValue v)
        {
            string? text = v.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
                System.Text.Json.JsonValueKind.Number => v.ToJsonString(),
                _ => null,
            };
            if (text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
            {
                return d;
            }
        }

        throw new BadDefinitionException($"'{key}' must be a number or a numeric string.", file);
    }
}

/// <summary>A tool written in JavaScript. Each call runs <c>invoke</c> in a fresh sandbox engine.</summary>
internal sealed class JsTool : ICapabilityTool
{
    private readonly string _source;
    private readonly string _file;
    private readonly JsSandbox _sandbox;

    private JsTool(ToolSpec spec, ToolCapability requires, string source, string file, JsSandbox sandbox)
    {
        Definition = spec;
        Requires = requires;
        _source = source;
        _file = file;
        _sandbox = sandbox;
    }

    public ToolSpec Definition { get; }

    public ToolCapability Requires { get; }

    public static JsTool Create(JsonObject d, string source, string file, string describedIn, JsSandbox sandbox)
    {
        string name = Text(d, "name", describedIn) ?? Path.GetFileNameWithoutExtension(file);
        string description = Text(d, "description", describedIn) ?? throw new BadDefinitionException("'description' is required.", describedIn);
        JsonObject schema = d["inputSchema"] switch
        {
            null => new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => throw new BadDefinitionException("'inputSchema' must be a JSON Schema object.", describedIn),
        };

        ToolSpec spec;
        try
        {
            spec = new ToolSpec(name, description, schema)
            {
                OutputSchema = d["outputSchema"] is JsonObject os ? (JsonObject)os.DeepClone() : null,
                EndsLoop = Flag(d, "endsLoop", false, describedIn),
                RequiresApproval = Flag(d, "requiresApproval", false, describedIn),
                ParallelSafe = Flag(d, "parallelSafe", true, describedIn),
                CostHint = JsTools.ParseDecimal(d["costHint"], "costHint", describedIn),
                Timeout = d["timeout"] is null ? null : TimeSpan.FromSeconds((double)JsTools.ParseDecimal(d["timeout"], "timeout", describedIn)),
            };
        }
        catch (ArgumentException e)
        {
            throw new BadDefinitionException(e.Message, describedIn, 0, 0, e);
        }

        return new JsTool(spec, JsTools.ParseRequires(d["requires"], describedIn), source, file, sandbox);
    }

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        JsRunResult run;
        try
        {
            run = await _sandbox.InvokeAsync(_source, _file, inputs, context, Requires, Definition.Timeout, cancellationToken);
        }
        catch (JsSandboxException e)
        {
            return ToolResult.Error("Error: " + e.Message);
        }

        if (run.Output.Length > 0)
        {
            context.Log(run.Output);
        }

        if (!run.HasValue)
        {
            return ToolResult.Empty;
        }

        string text = run.Value is JsonValue v && v.TryGetValue(out string? s) ? s : run.Value?.ToJsonString() ?? "null";
        if (Definition.EndsLoop)
        {
            return new ToolResult([new TextPart(text)]) { EndsLoop = true, TypedResult = run.Value };
        }

        return run.Value is JsonValue sv && sv.TryGetValue(out string? str) ? ToolResult.FromText(str) : ToolResult.FromJson(run.Value);
    }

    private static string? Text(JsonObject d, string key, string file) =>
        d[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => throw new BadDefinitionException($"'{key}' must be a string.", file),
        };

    private static bool Flag(JsonObject d, string key, bool fallback, string file) =>
        d[key] switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue(out bool b) => b,
            _ => throw new BadDefinitionException($"'{key}' must be true or false.", file),
        };
}

/// <summary>The <c>run_javascript</c> built-in.</summary>
internal sealed class RunJavaScriptTool : ICapabilityTool
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "code": { "type": "string", "description": "JavaScript to run as the body of an async function: await and return work at the top level. Use console.log for output. 'input' holds the optional input value; 'ctx' offers readFile(path), writeFile(path, text), fetch(url, {method, headers, body}) and log(text), each only where the host allows it." },
            "input": { "description": "Any JSON value, available to the code as 'input'." }
          },
          "required": ["code"],
          "additionalProperties": false
        }
        """;

    private readonly JsSandbox _sandbox;

    public RunJavaScriptTool(JsSandbox sandbox)
    {
        _sandbox = sandbox;
        Definition = new ToolSpec(
            "run_javascript",
            "Runs a JavaScript snippet in a sandbox with no access to the machine beyond what the host has granted, and returns what it printed and returned. Use it for calculations, parsing and reshaping data.",
            JsonNode.Parse(Schema)!.AsObject())
        {
            // The sandbox's own limit fires first, so the model gets a report instead of a bare timeout.
            Timeout = sandbox.Options.TimeLimit + TimeSpan.FromSeconds(5),
        };
    }

    public ToolSpec Definition { get; }

    // The snippet may use files and the network, but only where the host's grants allow them; the tool itself needs nothing granted to run.
    public ToolCapability Requires => ToolCapability.None;

    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string code = inputs["code"] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0
            ? s
            : throw new ToolInputException("code", "is required");
        try
        {
            JsRunResult run = await _sandbox.RunSnippetAsync(code, inputs["input"], context, ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.Network, "snippet.js", cancellationToken);
            var sb = new StringBuilder(run.Output);
            if (run.HasValue)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append("=> ").Append(run.Value?.ToJsonString() ?? "null");
            }

            return ToolResult.FromText(sb.Length == 0 ? "(no output)" : sb.ToString());
        }
        catch (JsSandboxException e)
        {
            return ToolResult.Error("Error: " + e.Message);
        }
    }
}
