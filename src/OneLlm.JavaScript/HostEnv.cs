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
/// checks two things before it acts: the tool declared the capability, and the host granted it (through <see cref="ICapabilities"/>, which
/// resolves symlinks before comparing paths). Nothing else of the host is reachable: no process, environment, clock or network object.
/// </summary>
internal sealed class HostEnv
{
    private readonly ToolContext _context;
    private readonly ToolCapability _declared;
    private readonly JsSandboxOptions _options;
    private readonly OutputBuffer _output;
    private readonly CancellationToken _token;
    private int _costLines;

    // The longest counter, label, source or asOf text of one cost line.
    private const int MaxCostText = 256;

    public HostEnv(ToolContext context, ToolCapability declared, JsSandboxOptions options, OutputBuffer output, CancellationToken token)
    {
        _context = context;
        _declared = declared;
        _options = options;
        _output = output;
        _token = token;
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
            _context.Log(Format(realm, stringify, args));
            return JsValue.Undefined;
        });

        Define(realm, ctx, "readFile", 1, (_, args) =>
        {
            string path = Str(realm, args, 0, "readFile", "path");
            Require(ToolCapability.FileRead, "readFile", "fileRead");
            return JsValue.FromString(ReadText(_context.Capabilities, path, _options.MaxFileBytes));
        });

        Define(realm, ctx, "writeFile", 2, (_, args) =>
        {
            string path = Str(realm, args, 0, "writeFile", "path");
            string text = Str(realm, args, 1, "writeFile", "text");
            Require(ToolCapability.FileWrite, "writeFile", "fileWrite");
            WriteText(_context.Capabilities, path, text, _options.MaxFileBytes);
            return JsValue.Undefined;
        });

        Define(realm, ctx, "addCost", 1, (_, args) =>
        {
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw TypeError(realm, "ctx.addCost: expected an object like { counter, quantity, unitPrice }");
            }

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
            // The continuation after this await resumes on the engine's thread (OneEcma routes it), so no ConfigureAwait here.
            return JsMarshal.FromTask(realm, "ctx.fetch", () => FetchAsync(realm, url, method, headers, body), (r, res) => FetchResultToJs(r, res));
        });

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

    private static JsException TypeError(JsRealm realm, string message) => new(realm.NewError("TypeError", message));

    private static string Str(JsRealm realm, ReadOnlySpan<JsValue> args, int index, string fn, string name) =>
        index < args.Length && args[index].IsString ? args[index].AsString : throw TypeError(realm, $"ctx.{fn}: '{name}' must be a string");

    private void Require(ToolCapability needed, string fn, string word)
    {
        if ((_declared & needed) != needed)
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

    private static string ReadText(ICapabilities caps, string path, int max)
    {
        using FileStream stream = GuardedFile.OpenRead(caps, path);
        if (stream.Length > max)
        {
            throw new HostMessage($"'{path}' is {stream.Length} bytes; ctx.readFile reads at most {max}.");
        }

        byte[] bytes = new byte[(int)stream.Length];
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

    private static void WriteText(ICapabilities caps, string path, string text, int max)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > max)
        {
            throw new HostMessage($"ctx.writeFile writes at most {max} bytes; the text is {bytes.Length}.");
        }

        using FileStream stream = GuardedFile.OpenWrite(caps, path, append: false);
        stream.Write(bytes);
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

        using HttpResponseMessage response = await EgressClient.SendAsync(_context.Capabilities, new HttpMethod(method), uri, headers, body, _token);
        (byte[] bytes, bool truncated) = await EgressClient.ReadBodyAsync(response, _options.MaxFetchBytes, _token);
        var map = new List<KeyValuePair<string, string>>();
        foreach (var h in response.Headers)
        {
            map.Add(new(h.Key.ToLowerInvariant(), string.Join(", ", h.Value)));
        }

        foreach (var h in response.Content.Headers)
        {
            map.Add(new(h.Key.ToLowerInvariant(), string.Join(", ", h.Value)));
        }

        return new FetchResult((int)response.StatusCode, response.IsSuccessStatusCode, map, new UTF8Encoding(false, false).GetString(bytes), truncated);
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
            return CostLine.Create(counter, quantity, per, unit, mult, OptionalText(realm, o, "label"), OptionalText(realm, o, "source"), OptionalText(realm, o, "asOf"));
        }
        catch (OverflowException)
        {
            throw TypeError(realm, "ctx.addCost: the amount is too large");
        }
    }

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
