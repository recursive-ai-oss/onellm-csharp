using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Deciders;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.JavaScript;

/// <summary>
/// The logic of a decider written in JavaScript: a module that exports <c>prepare(inputs)</c> and/or <c>decide({ answers, inputs })</c>, the same
/// functions a compiled decider bundle exports. Both are pure: they run in a fresh sandbox engine with no file, network or clock grants, and
/// return JSON. A function may be <c>async</c>.
/// </summary>
/// <remarks>
/// <c>prepare</c> receives the caller's inputs and returns the inputs the templates read. <c>decide</c> receives the answers by question id (a
/// repeated question's answers are a list, in order) and the prepared inputs, and returns the outputs. Returning <c>undefined</c> gives JSON <c>null</c>.
/// Only JavaScript runs here. A <c>decider.ts</c> must be compiled to <c>decider.js</c> first, because OneEcma does not yet strip TypeScript types.
/// </remarks>
public sealed class JsDeciderLogic : IDeciderLogic
{
    private readonly JsSandbox _sandbox;
    private readonly TimeProvider _time;
    private readonly string _source;
    private readonly string _file;

    private JsDeciderLogic(JsSandbox sandbox, TimeProvider time, string source, string file, bool hasPrepare, bool hasDecide)
    {
        _sandbox = sandbox;
        _time = time;
        _source = source;
        _file = file;
        HasPrepare = hasPrepare;
        HasDecide = hasDecide;
    }

    /// <inheritdoc />
    public bool HasPrepare { get; }

    /// <inheritdoc />
    public bool HasDecide { get; }

    /// <summary>Loads a decider's logic from a <c>.js</c> file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The sandbox limits, or null for the defaults.</param>
    /// <param name="timeProvider">The clock for the sandbox. Default: the system clock.</param>
    /// <returns>The logic.</returns>
    /// <exception cref="BadDefinitionException">The file cannot be read, does not parse, or exports neither <c>prepare</c> nor <c>decide</c>.</exception>
    public static JsDeciderLogic Load(string path, JsSandboxOptions? options = null, TimeProvider? timeProvider = null)
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
            throw new BadDefinitionException("cannot read the decider logic: " + e.Message, file, 0, 0, e);
        }

        TimeProvider time = timeProvider ?? TimeProvider.System;
        var sandbox = new JsSandbox(options, time);
        HashSet<string> fns;
        try
        {
            (_, fns) = sandbox.Inspect(source, file, null, "prepare", "decide");
        }
        catch (JsSandboxException e)
        {
            throw new BadDefinitionException(e.Message, file, e.Line, e.Column, e);
        }

        return fns.Count == 0
            ? throw new BadDefinitionException("a decider module must export 'prepare' and/or 'decide'.", file)
            : new JsDeciderLogic(sandbox, time, source, file, fns.Contains("prepare"), fns.Contains("decide"));
    }

    /// <summary>
    /// Loads a decider from a directory: <c>decider.yaml</c> with the logic in <c>decider.js</c> beside it when that file exists.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="options">The sandbox limits, or null for the defaults.</param>
    /// <param name="timeProvider">The clock for the sandbox. Default: the system clock.</param>
    /// <returns>The decider.</returns>
    /// <exception cref="BadDefinitionException">The directory has only a <c>decider.ts</c> (not supported yet), or a file is invalid.</exception>
    public static Decider LoadDecider(string directory, JsSandboxOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string js = Path.Combine(directory, "decider.js");
        if (File.Exists(js))
        {
            return Decider.Load(directory, Load(js, options, timeProvider));
        }

        if (File.Exists(Path.Combine(directory, "decider.ts")))
        {
            throw new BadDefinitionException("decider.ts needs TypeScript type stripping, which OneEcma does not have yet; compile it to decider.js.", "decider.ts");
        }

        return Decider.Load(directory);
    }

    /// <inheritdoc />
    public async ValueTask<JsonElement> PrepareAsync(JsonElement inputs, CancellationToken cancellationToken) =>
        await RunAsync("prepare", [ToNode(inputs)], cancellationToken);

    /// <inheritdoc />
    public async ValueTask<JsonElement> DecideAsync(JsonElement answers, JsonElement inputs, CancellationToken cancellationToken) =>
        await RunAsync("decide", [new JsonObject { ["answers"] = ToNode(answers), ["inputs"] = ToNode(inputs) }], cancellationToken);

    private static JsonNode? ToNode(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());

    private async Task<JsonElement> RunAsync(string function, JsonNode?[] args, CancellationToken cancellationToken)
    {
        // Logic is pure: no instance, no grants.
        var context = new ToolContext("decider", "decider:" + function, _time);
        JsRunResult run = await _sandbox.CallAsync(_source, _file, function, args, passCtx: false, context, ToolCapability.None, null, cancellationToken);
        string json = run.HasValue ? run.Value?.ToJsonString() ?? "null" : "null";
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
