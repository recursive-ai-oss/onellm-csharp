using System.Globalization;
using System.Text;
using OneLlm.Access;
using OneLlm.Messages;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using RecursiveAI.OneEcma;

namespace OneLlm.JavaScript;

/// <summary>
/// Builds the only host surface a script sees: <c>ctx</c> (log, file reads and writes, fetch, cost) and a <c>console</c>. Each function
/// checks two things before it acts in the default restricted mode: the tool declared the capability, and the host granted it (through <see cref="ICapabilities"/>, which
/// resolves symlinks before comparing paths). The opt-in trusted mode bypasses those checks and extends ctx with process and registry calls.
/// </summary>
internal sealed partial class HostEnv
{
    private readonly ToolContext _context;
    private readonly ToolCapability _declared;
    private readonly JsSandboxOptions _options;
    private readonly OutputBuffer _output;
    private readonly RunScope _scope;
    private int _costLines;

    // The longest counter, label, source or asOf text of one cost line.
    private const int MaxCostText = 256;

    // How long opening a file may take. A pipe or other special file blocks in open until something connects, so an open that takes longer than
    // this is refused instead of holding a thread for good.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(2);

    public HostEnv(ToolContext context, ToolCapability declared, JsSandboxOptions options, OutputBuffer output, RunScope scope)
    {
        _context = context;
        _declared = declared;
        _options = options;
        _output = output;
        _scope = scope;
    }

    /// <summary>Installs <c>ctx</c> and <c>console</c> as globals and, when given, <c>input</c>. Runs again, on a fresh engine, when the engine replays a failed run to explain it.</summary>
    public void Install(ScriptEngine engine, System.Text.Json.Nodes.JsonNode? input, bool hasInput)
    {
        JsRealm realm = engine.MainRealm;
        JsObject json = realm.Get(realm.Global, "JSON").AsObject!;
        JsValue stringify = realm.Get(json, "stringify");

        JsObject ctx = realm.NewObject();
        realm.DefineData(ctx, "instanceId", JsValue.FromString(_context.InstanceId), JsAttributes.Enumerable);
        realm.DefineData(ctx, "toolCallId", JsValue.FromString(_context.ToolCallId), JsAttributes.Enumerable);

        Define(realm, ctx, "log", 1, (_, args) =>
        {
            using RunOp op = Enter(realm);
            string line = Format(realm, stringify, args);
            switch (_scope.CountLog(line.Length))
            {
                case 0:
                    _context.Log(Scrub(line));
                    break;
                case 1:
                    _context.Log("[ctx.log: the output limit for this run was reached; later lines are dropped]");
                    break;
            }

            return JsValue.Undefined;
        });

        Define(realm, ctx, "readFile", 1, (_, args) =>
        {
            string path = Str(realm, args, 0, "readFile", "path");
            Require(ToolCapability.FileRead, "readFile", "fileRead");
            using RunOp op = Enter(realm);
            return JsValue.FromString(ReadText(realm, path));
        });

        Define(realm, ctx, "writeFile", 2, (_, args) =>
        {
            string path = Str(realm, args, 0, "writeFile", "path");
            string text = Str(realm, args, 1, "writeFile", "text");
            Require(ToolCapability.FileWrite, "writeFile", "fileWrite");
            using RunOp op = Enter(realm);
            WriteText(realm, path, text);
            return JsValue.Undefined;
        });

        Define(realm, ctx, "addCost", 1, (_, args) =>
        {
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw TypeError(realm, "ctx.addCost: expected an object like { counter, quantity, unitPrice }");
            }

            using RunOp op = Enter(realm);
            _context.AddCost(ReadCost(realm, args[0].AsObject!));
            return JsValue.Undefined;
        });

        Define(realm, ctx, "fetch", 2, (_, args) =>
        {
            string url = Str(realm, args, 0, "fetch", "url");
            string method = "GET";
            string? body = null;
            var headers = new List<KeyValuePair<string, string>>();
            if (args.Length > 1 && args[1].IsObject)
            {
                JsObject init = args[1].AsObject!;
                JsValue m = realm.Get(init, "method");
                if (m.IsString)
                {
                    method = m.AsString.ToUpperInvariant();
                }

                JsValue b = realm.Get(init, "body");
                if (b.IsString)
                {
                    body = b.AsString;
                }

                JsValue h = realm.Get(init, "headers");
                if (h.IsObject)
                {
                    foreach (JsValue key in realm.OwnKeys(h.AsObject!))
                    {
                        JsValue v = realm.Get(h.AsObject!, key);
                        if (key.IsString && v.IsString)
                        {
                            headers.Add(new(key.AsString, v.AsString));
                        }
                    }
                }
            }

            Require(ToolCapability.Network, "fetch", "network");
            using RunOp op = Enter(realm);
            if (_scope.CountFetch() is { } over)
            {
                throw RangeError(realm, "ctx.fetch: " + over);
            }

            // The continuation after this await resumes on the engine's thread (OneEcma routes it), so no ConfigureAwait here.
            return JsMarshal.FromTask(realm, "ctx.fetch", () => FetchAsync(realm, url, method, headers, body), (r, res) => FetchResultToJs(r, res));
        });

        InstallTrusted(engine, ctx, stringify);

        realm.DefineData(realm.Global, "ctx", JsValue.FromObject(ctx), JsAttributes.Method);

        JsObject console = realm.NewObject();
        foreach (string name in new[] { "log", "info", "debug", "warn", "error" })
        {
            Define(realm, console, name, 0, (_, args) =>
            {
                _output.AddLine(Format(realm, stringify, args));
                return JsValue.Undefined;
            });
        }

        realm.DefineData(realm.Global, "console", JsValue.FromObject(console), JsAttributes.Method);

        if (hasInput)
        {
            realm.DefineData(realm.Global, "input", JsonBridge.ToJs(realm, input), JsAttributes.Method);
        }
    }

    private static void Define(JsRealm realm, JsObject target, string name, int length, HostFunction fn) =>
        realm.DefineData(target, name, JsValue.FromObject(realm.NewFunction(name, length, (thisValue, args) => Guarded(realm, name, thisValue, args, fn))), JsAttributes.Method);

    /// <summary>
    /// Runs a host function and turns any host failure into a plain script error with a short message: no host stack frames, no absolute paths, no
    /// .NET type names. A script can catch the error and a model can read it, so neither may learn the layout of the machine. Errors the engine or
    /// this class raise on purpose (script errors, limits, cancellation, our own messages) pass through.
    /// </summary>
    private static JsValue Guarded(JsRealm realm, string name, JsValue thisValue, ReadOnlySpan<JsValue> args, HostFunction fn)
    {
        try
        {
            return fn(thisValue, args);
        }
        catch (Exception e) when (e is not (JsException or JsLimitException or OperationCanceledException))
        {
            throw new JsException(realm.NewError("Error", PlainMessage(e, "ctx." + name)));
        }
    }

    private static string PlainMessage(Exception e, string fn) => e switch
    {
        HostMessage or CapabilityMissingException => e.Message,
        FileNotFoundException or DirectoryNotFoundException => fn + ": no such file",
        UnauthorizedAccessException => fn + ": access denied",
        System.Net.Http.HttpRequestException => fn + ": the request failed",
        TaskCanceledException => fn + ": the request was cancelled or timed out",
        ArgumentException or FormatException or OverflowException or UriFormatException => fn + ": an argument is not valid",
        IOException => fn + ": the file could not be read or written",
        _ => fn + ": the host could not do that",
    };

    // A message we wrote ourselves, safe to show a script: it names only what the script passed in.
    private sealed class HostMessage(string message) : Exception(message);

    private static JsException RangeError(JsRealm realm, string message) => new(realm.NewError("RangeError", message));

    // Every host function that acts starts here. A closed run (finished, cancelled, timed out or abandoned) gets nothing, and Close waits for the
    // functions that did start, so nothing acts for a run after its result has been handed back.
    private RunOp Enter(JsRealm realm) =>
        _scope.TryEnter() ? new RunOp(_scope) : throw new JsException(realm.NewError("Error", "the run has ended"));

    private static JsException TypeError(JsRealm realm, string message) => new(realm.NewError("TypeError", message));

    private static string Str(JsRealm realm, ReadOnlySpan<JsValue> args, int index, string fn, string name) =>
        index < args.Length && args[index].IsString ? args[index].AsString : throw TypeError(realm, $"ctx.{fn}: '{name}' must be a string");

    private ICapabilities EffectiveCapabilities => _options.TrustedHost ? new TrustedCapabilities(_context.Capabilities.WorkingDirectory) : _context.Capabilities;

    private void Require(ToolCapability needed, string fn, string word)
    {
        if (!_options.TrustedHost && (_declared & needed) != needed)
        {
            throw new CapabilityMissingException($"ctx.{fn}", $"'{word}' in the tool's requires list");
        }
    }

    // console.log style: strings as they are, anything else as JSON (or its display text when JSON cannot hold it).
    private static string Format(JsRealm realm, JsValue stringify, ReadOnlySpan<JsValue> args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            JsValue a = args[i];
            if (a.IsString)
            {
                sb.Append(a.AsString);
            }
            else if (a.IsObject)
            {
                string? text;
                try
                {
                    text = JsonBridge.Stringify(realm, stringify, a);
                }
                catch (JsException)
                {
                    text = null;
                }

                sb.Append(text ?? realm.ToDisplayString(a));
            }
            else
            {
                sb.Append(realm.ToDisplayString(a));
            }
        }

        return sb.ToString();
    }

    private string ReadText(JsRealm realm, string path)
    {
        ICapabilities caps = EffectiveCapabilities;
        using FileStream stream = OpenRegular(caps, path, write: false);
        long length = stream.Length;
        if (length > _options.MaxFileBytes)
        {
            throw new HostMessage($"'{path}' is {length} bytes; ctx.readFile reads at most {_options.MaxFileBytes}.");
        }

        // A string holds two bytes a character; the file's bytes are an upper bound on its characters.
        if (_scope.ChargeHost(length * 2) is { } over)
        {
            throw RangeError(realm, "ctx.readFile: " + over);
        }

        byte[] bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new HostMessage($"'{path}' is not UTF-8 text.");
        }
    }

    private void WriteText(JsRealm realm, string path, string text)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > _options.MaxFileBytes)
        {
            throw new HostMessage($"ctx.writeFile writes at most {_options.MaxFileBytes} bytes; the text is {bytes.Length}.");
        }

        if (_scope.CountWrite(bytes.Length) is { } over)
        {
            throw RangeError(realm, "ctx.writeFile: " + over);
        }

        ICapabilities caps = EffectiveCapabilities;
        using FileStream stream = OpenRegular(caps, path, write: true);
        stream.Write(bytes);
    }

    // Opens a file for the script. Only regular files are allowed: a FIFO or similar blocks in open until a peer appears, which would hold the
    // thread for good, so the open runs on a helper thread under a time limit. When it does not return in time the path is refused and the blocked
    // open is released by opening the other end (a reader's open pairs with a writer's), so no thread is left behind. A special file that does
    // open (a pipe with a peer, a device) is refused because it cannot seek.
    private FileStream OpenRegular(ICapabilities caps, string path, bool write)
    {
        Task<FileStream> open = Task.Factory.StartNew(
            () => write ? GuardedFile.OpenWrite(caps, path, append: false) : GuardedFile.OpenRead(caps, path),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        bool done;
        try
        {
            done = open.Wait(OpenTimeout, _scope.RunToken);
        }
        catch (OperationCanceledException)
        {
            // The run ended (time limit, cancel) while the open was blocked.
            done = false;
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            throw;
        }

        if (!done)
        {
            string real = write ? caps.ResolveWrite(path) : caps.ResolveRead(path);
            _ = Task.Run(() =>
            {
                try
                {
                    using var peer = new FileStream(real, FileMode.Open, write ? FileAccess.Read : FileAccess.Write, FileShare.ReadWrite);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Nothing to release after all.
                }
            });
            _ = open.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                    else
                    {
                        _ = t.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new HostMessage($"'{path}' did not open in time. It may be a pipe or another special file; only regular files can be read or written.");
        }

        FileStream stream = open.Result;
        if (!stream.CanSeek)
        {
            stream.Dispose();
            throw new HostMessage($"'{path}' is not a regular file; only regular files can be read or written.");
        }

        return stream;
    }

    private async Task<FetchResult> FetchAsync(JsRealm realm, string url, string method, List<KeyValuePair<string, string>> headers, string? body)
    {
        try
        {
            return await FetchCoreAsync(url, method, headers, body);
        }
        catch (Exception e) when (e is not (JsException or JsLimitException or OperationCanceledException))
        {
            // The engine would otherwise build the rejection from the host exception: its type name and host stack frames.
            // This continuation runs on the engine's thread, so the error can be made here.
            throw new JsException(realm.NewError("Error", PlainMessage(e, "ctx.fetch")));
        }
    }

    private async Task<FetchResult> FetchCoreAsync(string url, string method, List<KeyValuePair<string, string>> headers, string? body)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new HostMessage($"ctx.fetch: '{url}' is not an absolute URL.");
        }

        using HttpResponseMessage response = await EgressClient.SendAsync(EffectiveCapabilities, new HttpMethod(method), uri, headers, body, _scope.RunToken);
        (byte[] bytes, bool truncated) = await EgressClient.ReadBodyAsync(response, _options.MaxFetchBytes, _scope.RunToken);
        var map = new List<KeyValuePair<string, string>>();
        foreach (var h in response.Headers)
        {
            map.Add(new(h.Key.ToLowerInvariant(), string.Join(", ", h.Value)));
        }

        foreach (var h in response.Content.Headers)
        {
            map.Add(new(h.Key.ToLowerInvariant(), string.Join(", ", h.Value)));
        }

        string text = new UTF8Encoding(false, false).GetString(bytes);
        if (_scope.ChargeHost((long)text.Length * 2) is { } over)
        {
            throw new HostMessage("ctx.fetch: " + over);
        }

        return new FetchResult((int)response.StatusCode, response.IsSuccessStatusCode, map, text, truncated);
    }

    private static JsValue FetchResultToJs(JsRealm realm, FetchResult res)
    {
        JsObject o = realm.NewObject();
        realm.DefineData(o, "status", JsValue.FromNumber(res.Status));
        realm.DefineData(o, "ok", JsValue.FromBoolean(res.Ok));
        JsObject h = realm.NewObject();
        foreach (KeyValuePair<string, string> kv in res.Headers)
        {
            realm.DefineData(h, kv.Key, JsValue.FromString(kv.Value));
        }

        realm.DefineData(o, "headers", JsValue.FromObject(h));
        realm.DefineData(o, "text", JsValue.FromString(res.Text));
        realm.DefineData(o, "truncated", JsValue.FromBoolean(res.Truncated));
        return JsValue.FromObject(o);
    }

    // Money is decimal: numbers arrive as JavaScript doubles, so they are read through their shortest round-trip text; a string is exact.
    // A script chooses these numbers, so they are checked: nothing negative (a negative line would lower the call's, the instance's and its
    // parents' totals), nothing that overflows a decimal, and bounded text and count.
    private CostLine ReadCost(JsRealm realm, JsObject o)
    {
        if (++_costLines > _options.MaxCostLines)
        {
            throw TypeError(realm, $"ctx.addCost: at most {_options.MaxCostLines} cost lines per call");
        }

        string counter = RequiredText(realm, o, "counter");
        decimal quantity = Dec(realm, o, "quantity", 1m);
        decimal per = Dec(realm, o, "per", 1m);
        decimal unit = Dec(realm, o, "unitPrice", 0m);
        decimal mult = Dec(realm, o, "multiplier", 1m);
        if (per <= 0m)
        {
            throw TypeError(realm, "ctx.addCost: 'per' must be positive");
        }

        if (quantity < 0m || unit < 0m || mult < 0m)
        {
            throw TypeError(realm, "ctx.addCost: 'quantity', 'unitPrice' and 'multiplier' must not be negative");
        }

        try
        {
            return CostLine.Create(Scrub(counter), quantity, per, unit, mult, ScrubOptional(OptionalText(realm, o, "label")), ScrubOptional(OptionalText(realm, o, "source")), ScrubOptional(OptionalText(realm, o, "asOf")));
        }
        catch (OverflowException)
        {
            throw TypeError(realm, "ctx.addCost: the amount is too large");
        }
    }

    private string? ScrubOptional(string? value) => value is null ? null : Scrub(value);

    private static string RequiredText(JsRealm realm, JsObject o, string key)
    {
        JsValue v = realm.Get(o, key);
        return v.IsString && v.AsString.Length > 0 ? Bounded(realm, key, v.AsString) : throw TypeError(realm, $"ctx.addCost: '{key}' must be a non-empty string");
    }

    private static string? OptionalText(JsRealm realm, JsObject o, string key)
    {
        JsValue v = realm.Get(o, key);
        return v.IsString ? Bounded(realm, key, v.AsString) : null;
    }

    private static string Bounded(JsRealm realm, string key, string text) =>
        text.Length <= MaxCostText ? text : throw TypeError(realm, $"ctx.addCost: '{key}' is longer than {MaxCostText} characters");

    private static decimal Dec(JsRealm realm, JsObject o, string key, decimal fallback)
    {
        JsValue v = realm.Get(o, key);
        if (v.IsUndefined)
        {
            return fallback;
        }

        string text = v.IsString ? v.AsString : v.IsNumber ? v.AsNumber.ToString("R", CultureInfo.InvariantCulture) : "";
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
        {
            return d;
        }

        throw TypeError(realm, $"ctx.addCost: '{key}' must be a number or a numeric string");
    }

    private sealed record FetchResult(int Status, bool Ok, List<KeyValuePair<string, string>> Headers, string Text, bool Truncated);
}

/// <summary>Marks a host function as under way for its run; disposing it lets the run close.</summary>
internal readonly struct RunOp(RunScope scope) : IDisposable
{
    public void Dispose() => scope.Exit();
}
