using System.Globalization;
using System.Text;
using OneLlm.Catalog;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;

namespace OneLlm.LiveSmoke;

/// <summary>
/// The release-check smoke console.
///
///   onellm-livesmoke --out report.txt [--spend-out spend.json] [--openai-cap 0.90] [--gemini-cap 0.45] [--anthropic-cap 0.45]
///                    [--providers openai,gemini,anthropic] [--mock-url http://127.0.0.1:5311] [--set openai.chat=gpt-5-nano ...]
///
/// Keys come from the environment (<c>OPENAI_API_KEY</c>, <c>GEMINI_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>), set by the calling script; a
/// provider without a key is skipped. With <c>--mock-url</c> every call goes to a local mock server with a made-up key. Nothing is
/// printed or written before it has been scrubbed of key values, key-shaped strings and request or organisation identifiers.
/// Exit code: 0 all run cases passed (skips are not failures), 1 some case failed, 2 usage.
/// </summary>
public static class Entry
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["openai.chat"] = "gpt-5-nano",
        ["openai.reasoning"] = "gpt-5-mini",
        ["openai.embedding"] = "text-embedding-3-small",
        ["openai.decision"] = "gpt-6-luna-decisions",
        ["gemini.chat"] = "gemini-3.5-flash-lite",
        ["gemini.reasoning"] = "gemini-3.8-flash",
        ["gemini.embedding"] = "gemini-embedding-2",
        ["anthropic.chat"] = "claude-haiku-5-5",
        ["anthropic.reasoning"] = "claude-haiku-5-5",
    };

    /// <summary>Runs the console.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        string? output = null, spendOut = null, mock = null;
        var caps = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["openai"] = 0.90m, ["gemini"] = 0.45m, ["anthropic"] = 0.45m };
        var already = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var models = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        string[] providers = ["openai", "gemini", "anthropic"];
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--out": output = Next(); break;
                case "--spend-out": spendOut = Next(); break;
                case "--mock-url": mock = Next(); break;
                case "--providers": providers = (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--openai-cap": case "--gemini-cap": case "--anthropic-cap":
                    if (!decimal.TryParse(Next(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal cap) || cap < 0)
                    {
                        return Usage("a cap is a non-negative number of dollars");
                    }

                    caps[a[2..^4]] = cap;
                    break;
                case "--set":
                    string[] kv = (Next() ?? "").Split('=', 2);
                    if (kv.Length != 2)
                    {
                        return Usage("--set takes key=value");
                    }

                    models[kv[0]] = kv[1];
                    break;
                default:
                    return Usage("unknown argument " + a);
            }
        }

        if (output is null)
        {
            return Usage("--out is required");
        }

        // A mock server needs no real key; otherwise the keys come from the environment the script prepared.
        string? openaiKey = mock is null ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : "mock-key";
        string? geminiKey = mock is null ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : "mock-key";
        string? anthropicKey = mock is null ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : null;
        var scrub = new Scrubber(new[] { openaiKey, geminiKey, anthropicKey }.Where(static k => !string.IsNullOrWhiteSpace(k)).Select(static k => k!.Trim()));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await using var file = new StreamWriter(output, append: false, new UTF8Encoding(false));
        void Emit(string line)
        {
            string clean = scrub.Scrub(line);
            Console.WriteLine(clean);
            file.WriteLine(clean);
            file.Flush();
        }

        BuiltInProviders.RegisterCatalogs();
        ModelCatalog catalog = CatalogLoader.Load(null, null, static _ => null);
        Emit(mock is null ? "onellm live smoke" : "onellm live smoke (against a mock server; no real calls)");

        var runners = new List<SmokeRunner>();
        var budgets = new Dictionary<string, SmokeBudget>(StringComparer.Ordinal);
        foreach (string name in providers)
        {
            SmokeTarget? target = name switch
            {
                "openai" when !string.IsNullOrWhiteSpace(openaiKey) => OpenAiTarget(openaiKey.Trim(), mock, catalog, models),
                "gemini" when !string.IsNullOrWhiteSpace(geminiKey) => GeminiTarget(geminiKey.Trim(), mock, catalog, models),
                "anthropic" when !string.IsNullOrWhiteSpace(anthropicKey) => AnthropicTarget(anthropicKey.Trim(), catalog, models),
                _ => null,
            };
            if (target is null)
            {
                Emit($"SKIP {name}: no key in the environment (row stays open)");
                continue;
            }

            var budget = new SmokeBudget(name, caps.GetValueOrDefault(name, 0m), already.GetValueOrDefault(name));
            budgets[name] = budget;
            var runner = new SmokeRunner(target, budget, catalog, scrub, Emit);
            runners.Add(runner);
            Emit($"-- {name}: cap ${budget.Cap.ToString("0.00", CultureInfo.InvariantCulture)}");
            await runner.RunAsync();
        }

        foreach (SmokeRunner r in runners)
        {
            Emit(r.Summary());
        }

        if (spendOut is not null)
        {
            File.WriteAllText(spendOut, SpendJson(budgets));
        }

        return runners.Any(static r => r.Results.Any(static x => x.Status == SmokeStatus.Fail)) ? 1 : 0;
    }

    /// <summary>Formats what each provider spent as a JSON object of dollars, for the next script in the chain.</summary>
    /// <param name="budgets">The budgets by provider.</param>
    /// <returns>The JSON text.</returns>
    public static string SpendJson(IReadOnlyDictionary<string, SmokeBudget> budgets) =>
        "{" + string.Join(",", budgets.OrderBy(static b => b.Key, StringComparer.Ordinal).Select(static b =>
            "\"" + b.Key + "\":" + b.Value.Spent.ToString("0.000000", CultureInfo.InvariantCulture))) + "}";

    private static SmokeTarget OpenAiTarget(string key, string? mock, ModelCatalog catalog, Dictionary<string, string> m)
    {
        var options = new OpenAIOptions
        {
            ApiKey = new Secret(key),
            BaseUrl = mock is null ? null : new Uri(mock.TrimEnd('/') + "/v1/"),
            Catalog = catalog,
        };
        var p = new OpenAIProvider(options, TimeProvider.System, OpenAIApi.Responses);
        return new SmokeTarget
        {
            Name = "openai",
            Chat = p,
            Embeddings = p,
            Counter = p,
            Decisions = p,
            ChatModel = m["openai.chat"],
            ReasoningModel = m["openai.reasoning"],
            EmbeddingModel = m["openai.embedding"],
            DecisionModel = m["openai.decision"],
        };
    }

    private static SmokeTarget GeminiTarget(string key, string? mock, ModelCatalog catalog, Dictionary<string, string> m)
    {
        var p = new GeminiProvider(new GeminiOptions
        {
            Time = TimeProvider.System,
            Auth = new GeminiApiKeyAuth(new Secret(key)),
            Endpoint = mock is null ? null : new GeminiDirectEndpoint(new Uri(mock.TrimEnd('/') + "/")),
            Catalog = catalog,
        });
        return new SmokeTarget
        {
            Name = "gemini",
            Chat = p,
            Embeddings = p,
            Counter = p,
            ChatModel = m["gemini.chat"],
            ReasoningModel = m["gemini.reasoning"],
            EmbeddingModel = m["gemini.embedding"],
        };
    }

    private static SmokeTarget AnthropicTarget(string key, ModelCatalog catalog, Dictionary<string, string> m)
    {
        var p = AnthropicProvider.Create(new Secret(key), TimeProvider.System, new AnthropicOptions { Catalog = catalog });
        return new SmokeTarget
        {
            Name = "anthropic",
            Chat = p,
            Counter = p,
            ChatModel = m["anthropic.chat"],
            ReasoningModel = m["anthropic.reasoning"],
        };
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine("usage: onellm-livesmoke --out <file> [--spend-out <file>] [--openai-cap usd] [--gemini-cap usd] [--anthropic-cap usd] [--providers a,b] [--mock-url url] [--set key=model]");
        return 2;
    }
}
