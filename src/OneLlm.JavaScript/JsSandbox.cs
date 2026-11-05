using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace OneLlm.JavaScript;

/// <summary>
/// Runs JavaScript on OneEcma under limits, with nothing reachable but <c>ctx</c> and <c>console</c>. One sandbox object holds only options and
/// may be shared; every run builds its own engine, so no state, global or promise survives from one call to the next.
/// </summary>
/// <remarks>
/// <para>What a script can do is exactly what the engine gives it (the ECMAScript built-ins) plus <c>ctx</c>:
/// <c>ctx.log</c>, <c>ctx.readFile</c> / <c>ctx.writeFile</c> (declared <c>fileRead</c> / <c>fileWrite</c> and granted by the host, symlinks resolved),
/// <c>ctx.fetch</c> (declared <c>network</c> and allowed by the egress policy, redirects re-checked) and <c>ctx.addCost</c>.
/// There is no <c>require</c>, <c>process</c>, <c>import</c> of other files, timers or environment.</para>
/// <para>Limits: steps, allocation volume, call depth, string length and wall-clock time, all from <see cref="JsSandboxOptions"/>.
/// The engine has no per-run cap on live memory; the allocation budget bounds bytes requested over the run, which stops allocation bombs
/// but is a looser bound than a resident-set limit.</para>
/// </remarks>
public sealed class JsSandbox
{
    /// <summary>Creates a sandbox.</summary>
    /// <param name="options">The limits and switches, or null for the defaults.</param>
    /// <param name="timeProvider">The clock used when a tool file is loaded and its top level runs. Runs use the clock of the <see cref="ToolContext"/> they are given. Default: the system clock.</param>
    public JsSandbox(JsSandboxOptions? options = null, TimeProvider? timeProvider = null)
    {
        Options = options ?? new JsSandboxOptions();
        _loadClock = timeProvider ?? TimeProvider.System;
    }

    private readonly TimeProvider _loadClock;

    /// <summary>The limits and switches every run uses.</summary>
    public JsSandboxOptions Options { get; }

    /// <summary>
    /// Runs a snippet as the body of an async function: <c>await</c> and <c>return</c> work at the top level, declarations vanish with the call,
    /// and <c>input</c> and <c>ctx</c> are in scope.
    /// </summary>
    /// <param name="code">The JavaScript source.</param>
    /// <param name="input">A JSON value made available as <c>input</c>; null leaves <c>input</c> undefined.</param>
    /// <param name="context">The call's context: its grants, clock, log and cost sink.</param>
    /// <param name="allowed">The capabilities the snippet may use. The host's grants in <paramref name="context"/> still apply on top.</param>
    /// <param name="fileName">The name used in reports, for example <c>snippet.js</c>.</param>
    /// <param name="cancellationToken">Cancels the run; the engine stops at its next step.</param>
    /// <returns>What the snippet returned and printed.</returns>
    /// <exception cref="JsSandboxException">The code did not parse, threw, or hit a limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<JsRunResult> RunSnippetAsync(string code, JsonNode? input, ToolContext context, ToolCapability allowed, string fileName = "snippet.js", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(
            null,
            fileName,
            input,
            hasInput: true,
            context,
            allowed,
            null,
            async (engine, token) =>
            {
                JsValue promise = engine.EvaluateAsyncBody(code, fileName, token);
                return await engine.AwaitAsync(promise, token);
            },
            cancellationToken);
    }

    /// <summary>Runs a tool module's top level once and reads its <c>tool</c> export (as JSON) and whether <c>invoke</c> is a function. Used when a tool file is loaded.</summary>
    internal (JsonNode? Tool, bool HasInvoke) Inspect(string source, string fileName)
    {
        var host = new SandboxHost(_loadClock, Options, new HostModule(fileName, source), new OutputBuffer(1024));
        ScriptEngine engine = CreateEngine(host, null, null, Options.TimeLimit);
        try
        {
            JsValue ns = engine.EvaluateModule(fileName);
            JsRealm realm = engine.MainRealm;
            bool hasInvoke = JsRealm.IsCallable(realm.Get(ns.AsObject!, "invoke"));
            JsValue value = realm.Get(ns.AsObject!, "tool");
            if (value.IsUndefined)
            {
                return (null, hasInvoke);
            }

            JsValue stringify = realm.Get(realm.Get(realm.Global, "JSON").AsObject!, "stringify");
            string? json = JsonBridge.Stringify(realm, stringify, value);
            return (json is null ? null : JsonNode.Parse(json), hasInvoke);
        }
        catch (Exception e) when (e is JsException or JsLimitException)
        {
            throw Translate(e, fileName, CancellationToken.None);
        }
    }

    /// <summary>Runs a tool module's <c>invoke(inputs, ctx)</c> and returns what it returned.</summary>
    internal Task<JsRunResult> InvokeAsync(string source, string fileName, JsonObject inputs, ToolContext context, ToolCapability declared, TimeSpan? limit, CancellationToken cancellationToken) =>
        RunAsync(
            new HostModule(fileName, source),
            fileName,
            null,
            hasInput: false,
            context,
            declared,
            limit,
            async (engine, token) =>
            {
                JsRealm realm = engine.MainRealm;
                JsValue ns = engine.EvaluateModule(fileName, token);
                JsValue invoke = realm.Get(ns.AsObject!, "invoke");
                if (!JsRealm.IsCallable(invoke))
                {
                    throw new BadDefinitionException("the module does not export a function named 'invoke'.", fileName);
                }

                JsValue ctx = realm.Get(realm.Global, "ctx");
                JsValue result = engine.Call(invoke, JsValue.Undefined, [JsonBridge.ToJs(realm, inputs), ctx], token);
                return await engine.AwaitAsync(result, token);
            },
            cancellationToken);

    private ScriptEngine CreateEngine(SandboxHost host, HostEnv? env, JsonNode? input, TimeSpan limit, bool hasInput = false)
    {
        return new ScriptEngine(new ScriptEngineOptions
        {
            Host = host,
            Limits = new EngineLimits
            {
                Fuel = Options.Fuel,
                MaxAllocatedBytes = Options.MaxAllocatedBytes,
                WallClockMs = limit.TotalMilliseconds,
                MaxCallDepth = Options.MaxCallDepth,
                MaxStringLength = Options.MaxStringLength,
            },
            Configure = engine => env?.Install(engine, input, hasInput),
        });
    }

    // The engine computes on the thread that calls it, so the run starts on the thread pool: a caller that awaits a script (a tool in a parallel
    // batch, a caller that cancels) is never blocked by it.
    private Task<JsRunResult> RunAsync(
        HostModule? module,
        string fileName,
        JsonNode? input,
        bool hasInput,
        ToolContext context,
        ToolCapability declared,
        TimeSpan? limit,
        Func<ScriptEngine, CancellationToken, Task<JsValue>> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => RunCoreAsync(module, fileName, input, hasInput, context, declared, limit, body, cancellationToken), CancellationToken.None);
    }

    private async Task<JsRunResult> RunCoreAsync(
        HostModule? module,
        string fileName,
        JsonNode? input,
        bool hasInput,
        ToolContext context,
        ToolCapability declared,
        TimeSpan? limit,
        Func<ScriptEngine, CancellationToken, Task<JsValue>> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan time = limit is { } l && l < Options.TimeLimit ? l : Options.TimeLimit;

        // The time limit is the host's: it also stops a script that is waiting on ctx.fetch, which burns no engine steps.
        using var timeout = new CancellationTokenSource(time, context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var output = new OutputBuffer(Options.MaxOutputChars);
        var host = new SandboxHost(context.TimeProvider, Options, module, output);
        host.OnMemoryExceeded = linked.Cancel;
        var env = new HostEnv(context, declared, Options, output, linked.Token);
        ScriptEngine engine = CreateEngine(host, env, input, time, hasInput);
        JsRealm realm = engine.MainRealm;

        // Take the engine's own JSON.stringify before any script runs, so a script that replaces JSON cannot change how results are read.
        JsValue stringify = realm.Get(realm.Get(realm.Global, "JSON").AsObject!, "stringify");
        try
        {
            JsValue value = await body(engine, linked.Token);
            if (value.IsUndefined)
            {
                return new JsRunResult(null, false, output.ToString());
            }

            string? json = JsonBridge.Stringify(realm, stringify, value);
            if (json is null)
            {
                return new JsRunResult(null, false, output.ToString());
            }

            if (json.Length > Options.MaxResultChars)
            {
                throw new JsSandboxException(JsSandboxFailure.Result, $"The result is {json.Length} characters of JSON; the limit is {Options.MaxResultChars}.", fileName);
            }

            return new JsRunResult(JsonNode.Parse(json), true, output.ToString());
        }
        catch (Exception e) when (e is JsException or JsLimitException or OperationCanceledException)
        {
            if (host.MemoryExceeded && !cancellationToken.IsCancellationRequested)
            {
                throw new JsSandboxException(JsSandboxFailure.Memory, $"The script made the host allocate more than {Options.MaxHostAllocatedBytes} bytes.", fileName, 0, 0, e);
            }

            throw Translate(e, fileName, cancellationToken);
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Internal engine error", StringComparison.Ordinal))
        {
            // The engine reports a host failure inside a run, such as running out of memory, this way.
            JsSandboxFailure kind = e.InnerException is OutOfMemoryException ? JsSandboxFailure.Memory : JsSandboxFailure.Script;
            throw new JsSandboxException(kind, $"{fileName}: the engine stopped the script ({e.InnerException?.GetType().Name ?? "internal error"}).", fileName, 0, 0, e);
        }
    }

    private JsSandboxException Translate(Exception e, string fileName, CancellationToken caller)
    {
        // Callers cancel with OperationCanceledException; everything else becomes a positioned JsSandboxException.
        if (e is OperationCanceledException || (e is JsLimitException { Kind: LimitKind.Cancelled } && caller.IsCancellationRequested))
        {
            caller.ThrowIfCancellationRequested();
            return Failure(JsSandboxFailure.Time, null, "The script ran out of time.", fileName, e);
        }

        if (e is JsLimitException limit)
        {
            JsSandboxFailure kind = limit.Kind switch
            {
                LimitKind.Fuel => JsSandboxFailure.Fuel,
                LimitKind.Memory => JsSandboxFailure.Memory,
                _ => JsSandboxFailure.Time,
            };
            string what = limit.Kind switch
            {
                LimitKind.Fuel => $"The script used more than {Options.Fuel} steps.",
                LimitKind.Memory => $"The script allocated more than {Options.MaxAllocatedBytes} bytes.",
                LimitKind.Cancelled => "The script ran out of time.",
                _ => $"The script ran longer than {Options.TimeLimit.TotalSeconds:0.###} seconds.",
            };
            return Failure(kind, limit.Report, what, fileName, e);
        }

        var js = (JsException)e;
        bool syntax = js.Report is { Kind: "SyntaxError" };
        return Failure(syntax ? JsSandboxFailure.Syntax : JsSandboxFailure.Script, js.Report, js.Message, fileName, e);
    }

    private static JsSandboxException Failure(JsSandboxFailure kind, Diagnostic? report, string fallback, string fileName, Exception inner)
    {
        if (report is null)
        {
            return new JsSandboxException(kind, $"{fileName}: {fallback}", fileName, 0, 0, inner);
        }

        LinePosition pos = report.Source.GetPosition(report.PrimarySpan.Start);
        string text = DiagnosticRenderer.Render(report, new RenderOptions { Compact = true }).TrimEnd();
        // Limits state the budget first, then the report of where the script was.
        string message = kind is JsSandboxFailure.Script or JsSandboxFailure.Syntax ? text : $"{fallback}\n{text}";
        return new JsSandboxException(kind, message, fileName, pos.Line, pos.Column, inner);
    }
}
