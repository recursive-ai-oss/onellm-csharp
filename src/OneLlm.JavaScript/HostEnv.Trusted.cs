using System.Diagnostics;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tools;
using RecursiveAI.OneEcma;

namespace OneLlm.JavaScript;

internal sealed partial class HostEnv
{
    private JsHostContext? _services;

    internal bool NeedsRedaction => _context.HasRegisteredSecrets;

    internal string Scrub(string text) => _context.ScrubSecrets(text);

    private void InstallTrusted(ScriptEngine engine, JsObject ctx, JsValue stringify)
    {
        if (!_options.TrustedHost && _options.ResolveHostContext is null)
        {
            return;
        }

        JsRealm realm = engine.MainRealm;
        _services = _options.ResolveHostContext?.Invoke(_context);
        realm.DefineData(ctx, "identity", JsonBridge.ToJs(realm, _services?.Identity), JsAttributes.Enumerable);
        Define(realm, ctx, "secret", 1, (_, args) =>
        {
            string name = Str(realm, args, 0, "secret", "name");
            using RunOp op = Enter(realm);
            return JsMarshal.FromTask(realm, "ctx.secret", async () =>
            {
                Secret? value = _services?.Secrets is { } scope ? await scope.GetAsync(name, _scope.RunToken) : null;
                if (value is null)
                {
                    throw TypeError(realm, "ctx.secret: name is unavailable");
                }

                _context.RegisterSecret(value);
                return value.Value;
            }, (_, value) => JsValue.FromString(value));
        });

        if (!_options.TrustedHost)
        {
            return;
        }

        Define(realm, ctx, "callTool", 2, (_, args) =>
        {
            string name = Str(realm, args, 0, "callTool", "name");
            JsonObject inputs = args.Length > 1 && args[1].IsObject
                ? JsonNode.Parse(JsonBridge.Stringify(realm, stringify, args[1])!) as JsonObject ?? throw TypeError(realm, "ctx.callTool: expected an input object")
                : new JsonObject();
            using RunOp op = Enter(realm);
            return JsMarshal.FromTask(realm, "ctx.callTool", () => CallToolAsync(name, inputs, _scope.RunToken), (r, value) => JsonBridge.ToJs(r, value));
        });

        Define(realm, ctx, "exec", 2, (_, args) =>
        {
            string command = Str(realm, args, 0, "exec", "command");
            var arguments = new List<string>();
            if (args.Length > 1 && args[1].IsObject)
            {
                JsObject array = args[1].AsObject!;
                int length = (int)realm.Get(array, "length").AsNumber;
                if (length < 0 || length > 4096)
                {
                    throw TypeError(realm, "ctx.exec: too many arguments");
                }

                for (int i = 0; i < length; i++)
                {
                    arguments.Add(realm.Get(array, i.ToString(System.Globalization.CultureInfo.InvariantCulture)).AsString);
                }
            }

            bool shell = args.Length < 2 || args[1].IsUndefined;
            using RunOp op = Enter(realm);
            return JsMarshal.FromTask(realm, "ctx.exec", () => ExecAsync(command, arguments, shell), (r, value) => JsonBridge.ToJs(r, value));
        });
    }

    private async Task<JsonNode?> CallToolAsync(string name, JsonObject inputs, CancellationToken token)
    {
        ToolRegistry? registry = _services?.Tools ?? _context.Runtime?.Tools;
        if (registry is null || !registry.TryGet(name, out ITool tool))
        {
            throw new InvalidOperationException("Tool is unavailable.");
        }

        // Keep the exact caller context: identity, host services, clock, grants and cost accounting travel together.
        var called = new CalledTool(tool);
        ToolResult result = await ToolInvoker.InvokeAsync(called, inputs, _context, token).AsTask().WaitAsync(token).ConfigureAwait(false);
        foreach (CostLine line in called.ReturnedCosts)
        {
            _context.AddCost(new CostLine
            {
                Counter = Scrub(line.Counter), Quantity = line.Quantity, Per = line.Per, UnitPrice = line.UnitPrice,
                Multiplier = line.Multiplier, Amount = line.Amount, Label = ScrubOptional(line.Label),
                Source = ScrubOptional(line.Source), AsOf = ScrubOptional(line.AsOf), ExtensionData = line.ExtensionData,
            });
        }

        var parts = new JsonArray();
        foreach (Part part in result.Parts)
        {
            parts.Add((JsonNode)(part switch
            {
                TextPart text => new JsonObject { ["text"] = text.Text },
                JsonPart json => new JsonObject { ["json"] = json.Value.DeepClone() },
                _ => new JsonObject { ["unsupported"] = true },
            }));
        }

        return ScrubNode(new JsonObject { ["isError"] = result.IsError, ["endsLoop"] = result.EndsLoop, ["parts"] = parts });
    }

    // ToolInvoker merges the shared context's costs into its result. Capture only the native return's costs, so those are
    // charged once too, even when several registry calls overlap on the same caller context.
    private sealed class CalledTool(ITool inner) : ITool
    {
        public ToolSpec Definition => inner.Definition;
        public IReadOnlyList<CostLine> ReturnedCosts { get; private set; } = [];
        public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
        {
            ToolResult result = await inner.InvokeAsync(inputs, context, cancellationToken).ConfigureAwait(false);
            ReturnedCosts = result.CostLines;
            return result;
        }
    }

    private async Task<JsonNode?> ExecAsync(string command, List<string> arguments, bool shell)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell ? (OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") : command,
            WorkingDirectory = _context.Capabilities.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (shell)
        {
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            start.ArgumentList.Add(command);
        }
        else
        {
            foreach (string arg in arguments)
            {
                start.ArgumentList.Add(arg);
            }
        }

        await using ToolCallbackEndpoint? endpoint = _options.EnableToolEndpoint
            ? new ToolCallbackEndpoint(CallToolAsync, _options.MaxFileBytes, _scope.RunToken)
            : null;
        if (endpoint is not null)
        {
            _context.RegisterSecret(new Secret(endpoint.Token));
            start.Environment["ONELLM_TOOL_ENDPOINT"] = endpoint.Url;
            start.Environment["ONELLM_TOOL_TOKEN"] = endpoint.Token;
        }

        using var process = new Process { StartInfo = start };
        _scope.RunToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(_options.ExecTimeLimit, _context.TimeProvider);
        process.Start();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _scope.RunToken);
        using CancellationTokenRegistration stop = linked.Token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = new OutputBuffer(_options.MaxOutputChars);
        var stderr = new OutputBuffer(_options.MaxOutputChars);
        Task readOut = DrainAsync(process.StandardOutput, stdout);
        Task readError = DrainAsync(process.StandardError, stderr);
        await process.WaitForExitAsync(_scope.RunToken).ConfigureAwait(false);
        await Task.WhenAll(readOut, readError).WaitAsync(_scope.RunToken).ConfigureAwait(false);
        return new JsonObject { ["stdout"] = stdout.ToString(), ["stderr"] = stderr.ToString(), ["exitCode"] = process.ExitCode, ["timedOut"] = timeout.IsCancellationRequested };
    }

    internal JsonNode? ScrubNode(JsonNode? node)
    {
        if (!NeedsRedaction)
        {
            return node;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return JsonValue.Create(Scrub(text));
        }

        if (node is JsonObject obj)
        {
            var clean = new JsonObject();
            foreach ((string key, JsonNode? child) in obj)
            {
                clean[Scrub(key)] = ScrubNode(child);
            }

            return clean;
        }

        if (node is JsonArray array)
        {
            var clean = new JsonArray();
            foreach (JsonNode? child in array)
            {
                clean.Add(ScrubNode(child));
            }

            return clean;
        }

        return node?.DeepClone();
    }

    private async Task DrainAsync(StreamReader reader, OutputBuffer output)
    {
        char[] buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), _scope.RunToken).ConfigureAwait(false)) > 0)
        {
            output.AddText(new string(buffer, 0, count));
        }
    }
}
