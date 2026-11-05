using System.Globalization;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.Cli.Tests;

/// <summary>A CLI over a scripted provider, with a scratch working directory, captured output and typed input.</summary>
internal sealed class CliEnv : IDisposable
{
    public const string CatalogYaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        aliases: { demo: demo-1, claude-sonnet: demo-1 }
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing:
              asOf: "2026-11-01"
              source: https://example.test/prices
              inputPerMillion: 1.00
              outputPerMillion: 2.00
        """;

    private int _ids;

    public CliEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "onellm-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Provider = new ScriptedProvider();
    }

    public string Root { get; }

    public ScriptedProvider Provider { get; }

    public Action<Runtime>? Extra { get; set; }

    public CancellationToken Token { get; set; }

    public string Out { get; private set; } = "";

    public string Err { get; private set; } = "";

    public async Task<int> RunAsync(string input, params string[] args)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CliApp(new StringReader(input), stdout, stderr)
        {
            WorkingDirectory = Root,
            ConfigureRuntime = rt =>
            {
                rt.Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]);
                rt.Providers = new ProviderRegistry(_ => null);
                rt.Providers.Set("scripted", Provider);
                rt.GetEnvironmentVariable = _ => null;
                rt.IdSource = () => "id" + (++_ids).ToString(CultureInfo.InvariantCulture);
                Extra?.Invoke(rt);
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        int code = await app.RunAsync(args, cts.Token);
        Out = stdout.ToString();
        Err = stderr.ToString();
        return code;
    }

    public static ScriptStep Calls(params (string Id, string Tool, string Json)[] calls) => new()
    {
        ToolCalls = [.. calls.Select(static c => new ScriptedToolCall(c.Id, c.Tool, c.Json))],
        Usage = new Usage { InputTokens = 1000, OutputTokens = 100 },
    };

    public static ScriptStep Complete(string result) => Calls(("done", "task_complete", new JsonObject { ["result"] = result, ["summary"] = "s" }.ToJsonString()));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
