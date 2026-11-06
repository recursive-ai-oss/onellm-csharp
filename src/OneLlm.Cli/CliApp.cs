using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using OneLlm.JavaScript;
using OneLlm.Web;

namespace OneLlm.Cli;

/// <summary>Exit codes of the <c>onellm</c> tool.</summary>
internal static class ExitCodes
{
    /// <summary>The command did what was asked. An agent that answered and is waiting for the next message also exits 0.</summary>
    public const int Ok = 0;

    /// <summary>The run failed: a provider error after retries, a spend cap, a hook block, an instance that ended in <c>error</c>.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 2;

    /// <summary>Something named on the command line cannot be used: a missing file, an invalid definition, an ungranted tool reference, an unknown model, a missing API key.</summary>
    public const int Setup = 3;

    /// <summary>The agent used all its iterations without finishing.</summary>
    public const int MaxIterations = 4;

    /// <summary>The run was cancelled (Ctrl-C).</summary>
    public const int Cancelled = 130;
}

/// <summary>The <c>onellm</c> command line: parses a verb and runs it on a <see cref="Runtime"/> built from the flags.</summary>
internal sealed partial class CliApp
{
    private static readonly string[] s_common = ["root", "state", "max-cost", "tools"];

    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly object _errGate = new();

    public CliApp(TextReader input, TextWriter output, TextWriter error)
    {
        _in = input;
        _out = output;
        _err = error;
    }

    /// <summary>Changes the runtime after the flags have configured it. Tests use it to put a scripted provider and a fake clock in.</summary>
    public Action<Runtime>? ConfigureRuntime { get; init; }

    /// <summary>The directory relative paths and the default root are taken from.</summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Whether input is a person at a terminal: prompts are printed only then.</summary>
    public bool Interactive { get; init; }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
            {
                _out.Write(Help.Text);
                return args.Count == 0 ? ExitCodes.Usage : ExitCodes.Ok;
            }

            if (args[0] is "--version" or "version")
            {
                _out.WriteLine(typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0");
                return ExitCodes.Ok;
            }

            string verb = args[0];
            List<string> rest = [.. args.Skip(1)];
            if (rest.Contains("--help") || rest.Contains("-h"))
            {
                _out.Write(Help.For(verb));
                return ExitCodes.Ok;
            }

            return verb switch
            {
                "run" => await RunVerbAsync(rest, cancellationToken).ConfigureAwait(false),
                "chat" => await ChatVerbAsync(rest, cancellationToken).ConfigureAwait(false),
                "instances" => await InstancesVerbAsync(rest, cancellationToken).ConfigureAwait(false),
                "cost" => await CostVerbAsync(rest, cancellationToken).ConfigureAwait(false),
                "catalog" => CatalogVerb(rest),
                "examples" => ExamplesVerb(rest),
                _ => throw new UsageException($"Unknown command '{verb}'. Try 'onellm --help'."),
            };
        }
        catch (UsageException e)
        {
            _err.WriteLine("error: " + e.Message);
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            _err.WriteLine("cancelled");
            return ExitCodes.Cancelled;
        }
        catch (Exception e) when (e is BadDefinitionException or ModelNotFoundException or CapabilityMissingException or AuthException or FileNotFoundException or DirectoryNotFoundException)
        {
            _err.WriteLine("error: " + e.Message);
            return ExitCodes.Setup;
        }
        catch (Exception e) when (e is OneLlmException or KeyNotFoundException or InstanceLockedException or IOException)
        {
            _err.WriteLine("error: " + e.Message);
            return ExitCodes.Failed;
        }
    }

    /// <summary>Builds the runtime for a verb: files under the root, the state directory for instances and records, and only the grants the flags give.</summary>
    private Runtime OpenRuntime(CliArgs args, bool withGrants = true)
    {
        string root = Path.GetFullPath(args.Get("root") ?? ".", WorkingDirectory);
        string state = Path.GetFullPath(args.Get("state") ?? Path.Combine(root, ".onellm"), WorkingDirectory);
        var rt = new Runtime
        {
            Store = new FileInstanceStore(Path.Combine(state, "instances")),
            Capabilities = withGrants ? Grants.FromFlags(root, args, TerminalShellApprover) : Capabilities.ReadOnlyFiles(root),
        };
        rt.Sinks.Add(new JsonlRecordSink(Path.Combine(state, "records")));
        rt.Tools.Add(new FetchWebpageMarkdownTool());

        // Sub-agents can start the embedded examples by name (examples/<name>), as well as definition files under the root.
        rt.AgentDefinitions.Resolver = static name => Examples.IsExample(name) && Examples.Names.Contains(name["examples/".Length..], StringComparer.Ordinal) ? Examples.Load(name) : null;
        if (rt.GetEnvironmentVariable(BraveSearchProvider.KeyVariable) is { Length: > 0 } brave)
        {
            rt.Tools.UseBuiltins(new BuiltinOptions { SearchProvider = new BraveSearchProvider(new Secret(brave)) });
        }

        if (args.Get("max-cost") is { } cap)
        {
            rt.SpendCap = decimal.TryParse(cap, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && d > 0
                ? d
                : throw new UsageException("--max-cost needs a positive amount in US dollars, for example 0.50.");
        }

        // JavaScript: run_javascript is always offered (it is sandboxed and holds only the grants the run has); tool files are loaded
        // only when named with --tools, and hook files listed in a definition run on the runtime's clock.
        rt.Tools.Add(JsTools.RunJavaScript());
        ConfigureRuntime?.Invoke(rt);
        rt.DefinitionHooks ??= new JsHookSource(timeProvider: rt.TimeProvider, baseDirectory: root);
        foreach (string tools in args.GetAll("tools"))
        {
            string full = Path.GetFullPath(tools, WorkingDirectory);
            if (Directory.Exists(full))
            {
                foreach (ITool tool in JsTools.LoadDirectory(full, timeProvider: rt.TimeProvider))
                {
                    rt.Tools.Add(tool);
                }
            }
            else if (File.Exists(full))
            {
                rt.Tools.Add(JsTools.Load(full, timeProvider: rt.TimeProvider));
            }
            else
            {
                throw new FileNotFoundException($"--tools: there is no file or directory '{tools}'.");
            }
        }

        return rt;
    }

    private async ValueTask<bool> TerminalShellApprover(ShellRequest request, CancellationToken cancellationToken) =>
        await AskAsync($"Run `{Printable(request.Program)} {string.Join(' ', request.Arguments.Select(Printable))}` in {Printable(request.WorkingDirectory)}?", cancellationToken).ConfigureAwait(false);

    // A command comes from the model. Control characters (a carriage return, an escape sequence) could rewrite what the person reads
    // on this line, so they are shown as \u escapes.
    internal static string Printable(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Asks a yes/no question on the terminal. The default is no, and end of input is no.</summary>
    private async ValueTask<bool> AskAsync(string question, CancellationToken cancellationToken)
    {
        lock (_errGate)
        {
            _err.Write(question + " [y/N] ");
            _err.Flush();
        }

        string? line = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            lock (_errGate)
            {
                _err.WriteLine("(no input: no)");
            }

            return false;
        }

        return line.Trim().ToUpperInvariant() is "Y" or "YES";
    }

    private void Note(string text)
    {
        lock (_errGate)
        {
            _err.WriteLine(text);
        }
    }

    /// <summary>US dollars to six places, halves away from zero. The catalog's prices are per million tokens, so small calls need the places.</summary>
    internal static string Usd(decimal amount) => "$" + Math.Round(amount, 6, MidpointRounding.AwayFromZero).ToString("F6", CultureInfo.InvariantCulture);

    internal static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Adds up what an instance cost by itself (not its sub-agents): the usage of each turn plus what its tool calls cost.</summary>
    internal static (decimal Total, SortedDictionary<string, ModelTotals> ByModel) OwnCost(Instance instance)
    {
        var byModel = new SortedDictionary<string, ModelTotals>(StringComparer.Ordinal);
        decimal total = 0m;
        foreach (Message m in instance.Messages)
        {
            if (m.Usage is { } u)
            {
                string key = string.IsNullOrEmpty(u.Model) ? "(unknown)" : u.Model;
                byModel.TryGetValue(key, out ModelTotals t);
                byModel[key] = new ModelTotals(t.Calls + 1, t.InputTokens + u.TotalInputTokens, t.OutputTokens + u.OutputTokens, t.Cost + u.Cost);
                total += u.Cost;
            }

            foreach (ToolCall c in m.ToolCalls)
            {
                total += c.Cost;
            }
        }

        return (total, byModel);
    }

    internal readonly record struct ModelTotals(int Calls, long InputTokens, long OutputTokens, decimal Cost);
}
