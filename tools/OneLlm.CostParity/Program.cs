using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;

// Cost parity driver.
//
//   onellm-costparity --providers <dir> --oracle <command> [--report <file>]
//
// <dir> holds one sub-directory per provider, each with an llm-provider.yaml. <command> is a reference calculator started once per case: it
// reads {"body": {...}} on standard input and answers {"_json": {...}} on standard output. For every model that has prices the driver builds a
// grid of usages (plain, cache read, cache write, over and exactly at the long-context threshold, every service tier, odd small numbers),
// prices each with OneLLM and with the reference, and compares input, output, cache write, cache read and total cost.
//
// A line "agrees" when it differs by less than half a cent; "exact" when it differs by less than the reference's own 8-decimal rounding.
// Exit code 0 when every comparable line agrees, 1 otherwise, 2 for a usage error.

string? providersDir = null;
string? oracle = null;
string? reportPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--providers" when i + 1 < args.Length:
            providersDir = args[++i];
            break;
        case "--oracle" when i + 1 < args.Length:
            oracle = args[++i];
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        default:
            Console.Error.WriteLine("usage: onellm-costparity --providers <dir> --oracle <command> [--report <file>]");
            return 2;
    }
}

if (providersDir is null || oracle is null)
{
    Console.Error.WriteLine("usage: onellm-costparity --providers <dir> --oracle <command> [--report <file>]");
    return 2;
}

var layers = new List<CatalogLayer>();
foreach (string file in Directory.EnumerateFiles(providersDir, "llm-provider.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    layers.Add(CatalogLayer.FromFile("shipped", file));
}

ModelCatalog catalog = ModelCatalog.Create(layers);
var rows = new List<Row>();
int cases = 0;
int skippedShadowed = 0;
int errors = 0;

foreach (ProviderSpec provider in catalog.Providers)
{
    foreach (ModelSpec model in provider.Models ?? [])
    {
        if (model.Pricing is not { } pricing || (pricing.InputPerMillion is null && pricing.OutputPerMillion is null))
        {
            continue;
        }

        ResolvedModel resolved = catalog.Resolve(provider.Name + ":" + model.Name);
        foreach ((string label, Usage usage, string? tier) in Grid(model))
        {
            cases++;
            Reference? reference = await RunReference(oracle, model.Name, usage, tier);
            if (reference is null || reference.Error is not null)
            {
                errors++;
                rows.Add(new Row(provider.Name, model.Name, label, "error", 0m, 0m, reference?.Error ?? "no answer"));
                continue;
            }

            if (!string.Equals(reference.Provider, provider.Name, StringComparison.Ordinal))
            {
                // The reference looks a model up across all providers and takes the first with that name; a same-named model of a later provider is never reached.
                skippedShadowed++;
                rows.Add(new Row(provider.Name, model.Name, label, "shadowed", 0m, 0m, "reference priced the model of provider " + reference.Provider));
                continue;
            }

            var warnings = new List<string>();
            Cost cost = CostCalculator.Price(resolved, usage, tier is null ? null : new PricingContext { ServiceTier = tier }, warnings)!;
            decimal Of(string counter) => cost.Lines.Where(l => l.Counter == counter).Sum(l => l.Amount);
            Compare(provider.Name, model.Name, label, "input", Of("inputTokens"), reference.Input);
            Compare(provider.Name, model.Name, label, "output", Of("outputTokens"), reference.Output);
            Compare(provider.Name, model.Name, label, "cacheWrite", Of("cacheCreationInputTokens"), reference.CacheWrite);
            Compare(provider.Name, model.Name, label, "cacheRead", Of("cacheReadInputTokens"), reference.CacheRead);
            Compare(provider.Name, model.Name, label, "total", cost.Total, reference.Total);
        }
    }
}

void Compare(string provider, string model, string label, string kind, decimal ours, decimal theirs)
{
    decimal diff = ours - theirs;
    string verdict = Math.Abs(diff) < 0.000000015m ? "exact" : Math.Abs(diff) < 0.005m ? "agree" : "DIFFERENT";
    rows.Add(new Row(provider, model, label, kind, ours, theirs, verdict == "DIFFERENT" ? "differs by " + diff.ToString(CultureInfo.InvariantCulture) : "") { Verdict = verdict });
}

var compared = rows.Where(r => r.Verdict is "exact" or "agree" or "DIFFERENT").ToList();
int different = compared.Count(r => r.Verdict == "DIFFERENT");
var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"cases {cases}, comparisons {compared.Count}, exact {compared.Count(r => r.Verdict == "exact")}, agree {compared.Count(r => r.Verdict == "agree")}, different {different}, shadowed {skippedShadowed}, errors {errors}");
report.AppendLine();
report.AppendLine("provider\tmodel\tkind\tcases\tmax |ours - reference|\tworst verdict");
foreach (IGrouping<(string P, string M, string K), Row> g in compared.GroupBy(r => (r.Provider, r.Model, r.Kind)))
{
    decimal max = g.Max(r => Math.Abs(r.Ours - r.Theirs));
    string worst = g.Any(r => r.Verdict == "DIFFERENT") ? "DIFFERENT" : g.Any(r => r.Verdict == "agree") ? "agree" : "exact";
    report.AppendLine(CultureInfo.InvariantCulture, $"{g.Key.P}\t{g.Key.M}\t{g.Key.K}\t{g.Count()}\t{max:0.############}\t{worst}");
}

report.AppendLine();
report.AppendLine("Disagreements, shadowed models and errors:");
foreach (Row r in rows.Where(r => r.Verdict is "DIFFERENT" or "error" or "shadowed"))
{
    report.AppendLine(CultureInfo.InvariantCulture, $"{r.Provider}\t{r.Model}\t{r.Case}\t{r.Kind}\tours {r.Ours}\treference {r.Theirs}\t{r.Note}");
}

if (reportPath is not null)
{
    await File.WriteAllTextAsync(reportPath, report.ToString());
}

string text = report.ToString();
Console.Out.WriteLine(text.Length > 6000 ? string.Concat(text.AsSpan(0, 6000), "\n... (see the report file)") : text);
return different == 0 && errors == 0 ? 0 : 1;

static IEnumerable<(string Label, Usage Usage, string? Tier)> Grid(ModelSpec model)
{
    PricingSpec p = model.Pricing!;
    yield return ("plain", new Usage { InputTokens = 1_000_000, OutputTokens = 500_000 }, null);
    yield return ("cache-read", new Usage { InputTokens = 10_000, CacheReadInputTokens = 50_000, OutputTokens = 1000 }, null);
    yield return ("cache-write", new Usage { InputTokens = 5000, CacheCreationInputTokens = 20_000, OutputTokens = 2000 }, null);
    yield return ("odd", new Usage { InputTokens = 123, OutputTokens = 7, CacheReadInputTokens = 11, CacheCreationInputTokens = 13 }, null);
    if (p.LongContextInputPerMillion is > 0m)
    {
        long threshold = p.LongContextThreshold is > 0 ? p.LongContextThreshold.Value : CostCalculator.DefaultLongContextThreshold;
        yield return ("at-threshold", new Usage { InputTokens = threshold, OutputTokens = 5000 }, null);
        yield return ("long-context", new Usage { InputTokens = threshold, CacheReadInputTokens = 30_000, CacheCreationInputTokens = 20_000, OutputTokens = 5000 }, null);
    }

    foreach (string tier in (IEnumerable<string>?)model.ServiceTiers?.Keys ?? [])
    {
        yield return ("tier-" + tier, new Usage { InputTokens = 100_000, CacheReadInputTokens = 20_000, CacheCreationInputTokens = 10_000, OutputTokens = 4000 }, tier);
    }
}

static async Task<Reference?> RunReference(string command, string model, Usage usage, string? tier)
{
    var body = new Dictionary<string, object?>
    {
        ["model"] = model,
        ["inputTokens"] = usage.InputTokens,
        ["outputTokens"] = usage.OutputTokens,
        ["cacheCreationInputTokens"] = usage.CacheCreationInputTokens,
        ["cacheReadInputTokens"] = usage.CacheReadInputTokens,
    };
    if (tier is not null)
    {
        body["serviceTier"] = tier;
    }

    string input = JsonSerializer.Serialize(new Dictionary<string, object?> { ["body"] = body });
    var start = new ProcessStartInfo(command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    using Process process = Process.Start(start)!;
    await process.StandardInput.WriteAsync(input);
    process.StandardInput.Close();
    string output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    if (line.Length == 0)
    {
        return null;
    }

    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement json = doc.RootElement.GetProperty("_json");
    if (json.TryGetProperty("error", out JsonElement error))
    {
        return new Reference("", 0, 0, 0, 0, 0) { Error = error.GetString() };
    }

    decimal Money(string name) => json.GetProperty(name).GetDecimal();
    return new Reference(json.GetProperty("provider").GetString() ?? "", Money("inputCost"), Money("outputCost"), Money("cacheCreationCost"), Money("cacheReadCost"), Money("totalCost"));
}

internal sealed record Reference(string Provider, decimal Input, decimal Output, decimal CacheWrite, decimal CacheRead, decimal Total)
{
    public string? Error { get; init; }
}

internal sealed record Row(string Provider, string Model, string Case, string Kind, decimal Ours, decimal Theirs, string Note)
{
    public string Verdict { get; init; } = Kind;
}
