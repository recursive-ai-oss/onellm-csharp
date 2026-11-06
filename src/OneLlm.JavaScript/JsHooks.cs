using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Definitions;
using OneLlm.Hooks;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.JavaScript;

/// <summary>
/// Loads the JavaScript hook files a definition's <c>hooks:</c> list names. Set it on <see cref="Runtime.DefinitionHooks"/>; host hooks on the
/// runtime wrap these.
/// </summary>
/// <remarks>
/// <para>A hook file is a module that exports one function per point it handles, named for the point: <c>beforeInference</c>, <c>afterInference</c>,
/// <c>beforeToolCall</c>, <c>afterToolCall</c>, <c>beforeCompaction</c> (interceptors) and <c>onMessage</c>, <c>onStateChange</c>, <c>onCost</c> (observers).
/// Each is called with a JSON snapshot of the point and <c>ctx</c>. An optional <c>export const hook = { requires: [...] }</c> names the capabilities
/// (<c>fileRead</c>, <c>fileWrite</c>, <c>network</c>) its <c>ctx</c> may use; the run's grants still apply.</para>
/// <para>An interceptor returns nothing (continue) or a decision object <c>{ action, reason?, ... }</c> where <c>action</c> is <c>continue</c>, <c>modify</c>,
/// <c>block</c>, <c>requireApproval</c> or <c>respond</c>. The payload fields by point: <c>beforeInference</c> modify takes any of <c>system</c>,
/// <c>maxOutputTokens</c>, <c>temperature</c>, <c>messages</c> (message objects as stored in an instance); respond takes <c>message</c> or <c>text</c>.
/// <c>afterInference</c> modify takes <c>message</c> or <c>text</c>. <c>beforeToolCall</c> modify takes <c>inputs</c>; respond takes <c>result</c> or <c>text</c>.
/// <c>afterToolCall</c> modify takes <c>result</c> or <c>text</c>, where a result is <c>{ text }</c> or <c>{ json }</c> with optional <c>isError</c>.
/// Anything else, including an unknown action or a payload of the wrong shape, throws, and an interceptor that throws blocks the step.
/// Observer return values are ignored and their failures are recorded, not fatal.</para>
/// <para>Every call runs in a fresh sandbox engine under <see cref="JsSandboxOptions"/>; a hook cannot keep state between calls.</para>
/// </remarks>
public sealed class JsHookSource : IDefinitionHookSource
{
    private static readonly (string Function, HookPoints Point)[] s_points =
    [
        ("beforeInference", HookPoints.BeforeInference),
        ("afterInference", HookPoints.AfterInference),
        ("beforeToolCall", HookPoints.BeforeToolCall),
        ("afterToolCall", HookPoints.AfterToolCall),
        ("beforeCompaction", HookPoints.BeforeCompaction),
        ("onMessage", HookPoints.OnMessage),
        ("onStateChange", HookPoints.OnStateChange),
        ("onCost", HookPoints.OnCost),
    ];

    private const int MaxHookFileBytes = 1024 * 1024;

    private readonly JsSandbox _sandbox;
    private readonly TimeProvider _time;
    private readonly string? _baseDirectory;

    /// <summary>Creates the source.</summary>
    /// <param name="options">The sandbox limits for hook calls, or null for the defaults.</param>
    /// <param name="timeProvider">The clock for hook calls; pass the runtime's. Default: the system clock.</param>
    /// <param name="baseDirectory">Where a definition's relative <c>file</c> paths start when the definition does not know its own folder. Default: the current directory.</param>
    public JsHookSource(JsSandboxOptions? options = null, TimeProvider? timeProvider = null, string? baseDirectory = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _sandbox = new JsSandbox(options, _time);
        _baseDirectory = baseDirectory;
    }

    /// <summary>Loads one hook file.</summary>
    /// <param name="path">The <c>.js</c> file.</param>
    /// <param name="points">The points to run it at; <see cref="HookPoints.None"/> means every point the file handles.</param>
    /// <param name="capabilities">The grants its <c>ctx</c> works under.</param>
    /// <returns>The hook.</returns>
    /// <exception cref="BadDefinitionException">The file cannot be read, does not parse, handles no point, or names a point it does not export.</exception>
    public IHook LoadFile(string path, HookPoints points, ICapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(capabilities);
        string file = Path.GetFileName(path);
        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // The reason stays in the inner exception; the message is for whoever wrote the definition, which may be a model.
            throw new BadDefinitionException("cannot read the hook file.", file, 0, 0, e);
        }

        return LoadSource(source, file, points, capabilities);
    }

    private JsHook LoadSource(string source, string file, HookPoints points, ICapabilities capabilities)
    {

        JsonNode? meta;
        HashSet<string> exported;
        try
        {
            (meta, exported) = _sandbox.Inspect(source, file, "hook", [.. s_points.Select(p => p.Function)]);
        }
        catch (JsSandboxException e)
        {
            throw new BadDefinitionException(e.Message, file, e.Line, e.Column, e);
        }

        HookPoints handled = HookPoints.None;
        foreach ((string function, HookPoints point) in s_points)
        {
            if (exported.Contains(function))
            {
                handled |= point;
            }
        }

        if (handled == HookPoints.None)
        {
            throw new BadDefinitionException("a hook file must export at least one of: " + string.Join(", ", s_points.Select(p => p.Function)) + ".", file);
        }

        HookPoints active = points == HookPoints.None ? handled : points & handled;
        if (points != HookPoints.None && (points & ~handled) != HookPoints.None)
        {
            throw new BadDefinitionException($"the definition asks for {points & ~handled}, which the file does not export a function for.", file);
        }

        ToolCapability declared = JsTools.ParseRequires(meta is JsonObject m ? m["requires"] : null, file);
        return new JsHook(_sandbox, _time, source, file, active, declared, capabilities);
    }

    /// <inheritdoc />
    public IReadOnlyList<IHook> Load(AgentDefinition definition, ICapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var hooks = new List<IHook>(definition.Hooks.Count);
        string baseDir = ResolveBase(definition);
        foreach (HookReference reference in definition.Hooks)
        {
            string path = Path.GetFullPath(Path.Combine(baseDir, reference.File));
            // A definition names the file, and a definition can be written by a model, so the file is read through the run's read grants (roots,
            // symlinks, protected names), not as the host. LoadFile stays the host's own entry point for files the host chose.
            string file = Path.GetFileName(path);
            string source;
            try
            {
                using FileStream stream = GuardedFile.OpenRead(capabilities, path);
                byte[] bytes = new byte[checked((int)Math.Min(stream.Length, MaxHookFileBytes + 1))];
                stream.ReadExactly(bytes);
                if (bytes.Length > MaxHookFileBytes)
                {
                    throw new BadDefinitionException("the hook file is too large.", file);
                }

                source = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                throw new BadDefinitionException("cannot read the hook file.", file, 0, 0, e);
            }

            hooks.Add(LoadSource(source, file, ParsePoints(reference, path), capabilities));
        }

        return hooks;
    }

    private string ResolveBase(AgentDefinition definition)
    {
        if (!string.IsNullOrEmpty(definition.SourceName) && Path.IsPathRooted(definition.SourceName))
        {
            return Path.GetDirectoryName(definition.SourceName) ?? ".";
        }

        string root = _baseDirectory ?? Directory.GetCurrentDirectory();
        return string.IsNullOrEmpty(definition.SourceName) ? root : Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, definition.SourceName))) ?? root;
    }

    private static HookPoints ParsePoints(HookReference reference, string path)
    {
        if (reference.Points is null)
        {
            return HookPoints.None;
        }

        HookPoints result = HookPoints.None;
        foreach (string name in reference.Points)
        {
            (string Function, HookPoints Point) match = s_points.FirstOrDefault(p => string.Equals(p.Function, name, StringComparison.OrdinalIgnoreCase));
            result |= match.Function is null
                ? throw new BadDefinitionException($"'{name}' is not a hook point; the points are {string.Join(", ", s_points.Select(p => p.Function))}.", Path.GetFileName(path))
                : match.Point;
        }

        return result;
    }
}

/// <summary>A hook written in JavaScript. Each call runs the matching exported function in a fresh engine.</summary>
internal sealed class JsHook : IHook
{
    private readonly JsSandbox _sandbox;
    private readonly TimeProvider _time;
    private readonly string _source;
    private readonly string _file;
    private readonly ToolCapability _declared;
    private readonly ICapabilities _capabilities;

    public JsHook(JsSandbox sandbox, TimeProvider time, string source, string file, HookPoints points, ToolCapability declared, ICapabilities capabilities)
    {
        _sandbox = sandbox;
        _time = time;
        _source = source;
        _file = file;
        Points = points;
        _declared = declared;
        _capabilities = capabilities;
    }

    public string Name => _file;

    public HookPoints Points { get; }

    public async ValueTask<HookDecision> BeforeInferenceAsync(BeforeInferenceContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["model"] = c.Request.Model;
        snap["system"] = c.Request.System;
        snap["maxOutputTokens"] = c.Request.MaxOutputTokens;
        snap["temperature"] = c.Request.Temperature;
        snap["messages"] = HookJson.MessagesToJson(c.Request.Messages);
        snap["tools"] = new JsonArray([.. c.Request.Tools.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["description"] = t.Description })]);
        JsonObject? d = await RunAsync("beforeInference", snap, c.InstanceId, ct);
        return Decide(d, HookPoints.BeforeInference, (action, o) => action switch
        {
            "modify" => HookDecision.ModifyRequest(HookJson.ApplyRequest(c.Request, o), Reason(o)),
            "respond" => HookDecision.Respond(HookJson.MessageFrom(o), Reason(o)),
            _ => null,
        });
    }

    public async ValueTask<HookDecision> AfterInferenceAsync(AfterInferenceContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["message"] = HookJson.MessageToJson(c.Message);
        snap["cost"] = c.Cost.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        JsonObject? d = await RunAsync("afterInference", snap, c.InstanceId, ct);
        return Decide(d, HookPoints.AfterInference, (action, o) => action == "modify" ? HookDecision.ModifyMessage(HookJson.MessageFrom(o), Reason(o)) : null);
    }

    public async ValueTask<HookDecision> BeforeToolCallAsync(BeforeToolCallContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["call"] = HookJson.Call(c.Call);
        snap["tool"] = c.Tool is null ? null : new JsonObject { ["name"] = c.Tool.Name, ["description"] = c.Tool.Description };
        snap["inputs"] = c.Inputs.DeepClone();
        JsonObject? d = await RunAsync("beforeToolCall", snap, c.InstanceId, ct);
        return Decide(d, HookPoints.BeforeToolCall, (action, o) => action switch
        {
            "modify" => HookDecision.ModifyInputs(o["inputs"] as JsonObject ?? throw new InvalidOperationException("a modify decision at beforeToolCall needs an 'inputs' object."), Reason(o)),
            "respond" => HookDecision.Respond(HookJson.ResultFrom(o), Reason(o)),
            "requireapproval" => HookDecision.RequireApproval(Reason(o) ?? "A hook requires approval."),
            _ => null,
        });
    }

    public async ValueTask<HookDecision> AfterToolCallAsync(AfterToolCallContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["call"] = HookJson.Call(c.Call);
        snap["tool"] = c.Tool is null ? null : new JsonObject { ["name"] = c.Tool.Name };
        snap["result"] = HookJson.Result(c.Result);
        JsonObject? d = await RunAsync("afterToolCall", snap, c.InstanceId, ct);
        return Decide(d, HookPoints.AfterToolCall, (action, o) => action == "modify" ? HookDecision.ModifyResult(HookJson.ResultFrom(o), Reason(o)) : null);
    }

    public async ValueTask<HookDecision> BeforeCompactionAsync(BeforeCompactionHookContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["strategy"] = c.Strategy;
        snap["messageIds"] = new JsonArray([.. c.MessageIds.Select(i => (JsonNode?)i)]);
        JsonObject? d = await RunAsync("beforeCompaction", snap, c.InstanceId, ct);
        return Decide(d, HookPoints.BeforeCompaction, (_, _) => null);
    }

    public async ValueTask OnMessageAsync(MessageHookContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["message"] = HookJson.MessageToJson(c.Message);
        await RunAsync("onMessage", snap, c.InstanceId, ct);
    }

    public async ValueTask OnStateChangeAsync(StateChangeHookContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["from"] = c.From;
        snap["to"] = c.To;
        await RunAsync("onStateChange", snap, c.InstanceId, ct);
    }

    public async ValueTask OnCostAsync(CostHookContext c, CancellationToken ct)
    {
        JsonObject snap = Snapshot(c);
        snap["source"] = c.Source;
        snap["total"] = c.Cost.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunAsync("onCost", snap, c.InstanceId, ct);
    }

    private static JsonObject Snapshot(HookContext c) => new()
    {
        ["point"] = c.Point.ToString(),
        ["instanceId"] = c.InstanceId,
        ["iteration"] = c.Iteration,
    };

    private async Task<JsonObject?> RunAsync(string function, JsonObject snapshot, string instanceId, CancellationToken ct)
    {
        var context = new ToolContext(instanceId, "hook:" + _file, _time) { Capabilities = _capabilities };
        // A JsSandboxException here is the interceptor's failure: the hook set blocks the step (interceptors) or records it (observers).
        JsRunResult run = await _sandbox.CallAsync(_source, _file, function, [snapshot], passCtx: true, context, _declared, null, ct);
        return run.Value as JsonObject ?? (run.HasValue && run.Value is not null ? throw new InvalidOperationException($"{function} must return nothing or a decision object.") : null);
    }

    private static string? Reason(JsonObject o) => o["reason"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // The action is matched without regard to case; a payload builder returns null for an action the point does not take.
    private static HookDecision Decide(JsonObject? d, HookPoints point, Func<string, JsonObject, HookDecision?> payload)
    {
        if (d is null)
        {
            return HookDecision.Continue;
        }

        string action = (d["action"] is JsonValue v && v.TryGetValue(out string? s) ? s : null)?.ToLowerInvariant()
            ?? throw new InvalidOperationException("a decision needs an 'action'.");
        if (action == "continue")
        {
            return HookDecision.Continue;
        }

        if (action == "block")
        {
            return HookDecision.Block(Reason(d) ?? "A hook blocked this step.");
        }

        return payload(action, d) ?? throw new InvalidOperationException($"'{action}' is not a decision {point} takes.");
    }
}

/// <summary>Converts the model types a hook sees and returns to and from the JSON shapes the file format uses.</summary>
internal static class HookJson
{
    public static JsonNode MessageToJson(Message m) => JsonSerializer.SerializeToNode(m, OneLlmJsonContext.Default.Message)!;

    public static JsonArray MessagesToJson(IReadOnlyList<Message> messages) => new([.. messages.Select(MessageToJson)]);

    public static JsonObject Call(ToolCall call) => new()
    {
        ["id"] = call.Id,
        ["tool"] = call.Tool,
        ["inputs"] = call.Inputs?.DeepClone(),
    };

    public static JsonObject Result(ToolResult r)
    {
        var o = new JsonObject { ["isError"] = r.IsError };
        string text = string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));
        JsonPart? json = r.Parts.OfType<JsonPart>().FirstOrDefault();
        if (json is not null)
        {
            o["json"] = json.Value.DeepClone();
        }

        o["text"] = text;
        return o;
    }

    public static Message MessageFrom(JsonObject o)
    {
        if (o["message"] is JsonObject m)
        {
            return JsonSerializer.Deserialize(m, OneLlmJsonContext.Default.Message) ?? throw new InvalidOperationException("'message' is not a message.");
        }

        return o["text"] is JsonValue v && v.TryGetValue(out string? text)
            ? Messages.Message.FromAssistant(text)
            : throw new InvalidOperationException("the decision needs a 'message' object or a 'text' string.");
    }

    public static ToolResult ResultFrom(JsonObject o)
    {
        JsonObject? r = o["result"] as JsonObject;
        bool isError = r?["isError"] is JsonValue e && e.TryGetValue(out bool b) && b;
        JsonNode? json = r?["json"];
        string? text = (r?["text"] ?? o["text"]) is JsonValue t && t.TryGetValue(out string? s) ? s : null;
        if (json is not null)
        {
            return new ToolResult([new JsonPart(json.DeepClone())]) { IsError = isError };
        }

        if (text is null)
        {
            throw new InvalidOperationException("the decision needs a 'result' with 'text' or 'json', or a 'text' string.");
        }

        return new ToolResult([new TextPart(text)]) { IsError = isError };
    }

    public static ChatRequest ApplyRequest(ChatRequest request, JsonObject o)
    {
        ChatRequest result = request;
        if (o["system"] is JsonValue sys && sys.TryGetValue(out string? system))
        {
            result = result with { System = system };
        }

        if (o["maxOutputTokens"] is JsonValue mt && mt.TryGetValue(out int max))
        {
            result = result with { MaxOutputTokens = max };
        }

        if (o["temperature"] is JsonValue tp && tp.TryGetValue(out double temperature))
        {
            result = result with { Temperature = temperature };
        }

        if (o["messages"] is JsonArray arr)
        {
            var list = new List<Message>(arr.Count);
            foreach (JsonNode? n in arr)
            {
                list.Add(JsonSerializer.Deserialize(n, OneLlmJsonContext.Default.Message) ?? throw new InvalidOperationException("'messages' holds something that is not a message."));
            }

            result = result with { Messages = list };
        }

        return result;
    }
}
