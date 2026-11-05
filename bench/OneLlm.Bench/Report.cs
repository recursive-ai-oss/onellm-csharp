using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace OneLlm.Bench;

/// <summary>
/// <c>report &lt;results-dir&gt;</c>: reads the JSON files BenchmarkDotNet wrote under the directory, plus the optional <c>peak.tsv</c>
/// (see <see cref="Peak"/>) and the runner's <c>machine.txt</c>, <c>load.tsv</c> and <c>versions.txt</c>, and prints and writes
/// <c>results.md</c>.
///
/// <para>Benchmarks are grouped by their <c>BenchmarkCategory</c> and parameters. In a group, methods named <c>Ours_*</c> are OneLLM
/// and <c>Ref_*</c> are black-box references; everything else (<c>Own_*</c>, <c>Info_*</c>, unprefixed) is reported without a
/// comparison. The ratio is ours divided by the fastest reference in the group on mean time: 1.00 or lower meets the bar, above 1.25 is
/// flagged, and the geometric mean of the group ratios must not exceed 1.00.</para>
/// </summary>
public static class Report
{
    private sealed record Row(string Type, string Method, string Parameters, string Category, string FullName, double MeanNs, double? AllocBytes, double? PeakMb);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: report <results-dir> [--label text]");
            return 2;
        }

        string dir = args[1];
        string label = args.Length >= 4 && args[2] == "--label" ? args[3] : "";
        var peaks = new Dictionary<string, double>(StringComparer.Ordinal);
        string peakFile = Path.Combine(dir, "peak.tsv");
        if (File.Exists(peakFile))
        {
            foreach (string line in File.ReadAllLines(peakFile))
            {
                string[] p = line.Split('\t');
                if (p.Length == 2 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double mb))
                {
                    peaks[p[0]] = Math.Max(peaks.GetValueOrDefault(p[0]), mb);
                }
            }
        }

        var rows = new List<Row>();
        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(dir, "*-report*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("HostEnvironmentInfo", out JsonElement host))
            {
                hosts.Add($"{Str(host, "ProcessorName")}; {Str(host, "PhysicalCoreCount")} physical / {Str(host, "LogicalCoreCount")} logical cores; {Str(host, "OsVersion")}; runtime {Str(host, "RuntimeVersion")}; SDK {Str(host, "DotNetCliVersion")}; BenchmarkDotNet {Str(host, "BenchmarkDotNetVersion")}");
            }

            foreach (JsonElement b in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                if (!b.TryGetProperty("Statistics", out JsonElement stats) || stats.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string type = Str(b, "Type");
                string method = Str(b, "Method");
                string full = Str(b, "FullName");
                double? alloc = b.TryGetProperty("Memory", out JsonElement mem) && mem.TryGetProperty("BytesAllocatedPerOperation", out JsonElement ba) ? ba.GetDouble() : null;
                rows.Add(new Row(type, method, Str(b, "Parameters"), CategoryOf(Str(b, "Namespace"), type, method), full, stats.GetProperty("Mean").GetDouble(), alloc, peaks.TryGetValue(full, out double pk) ? pk : null));
            }
        }

        var sb = new StringBuilder();
        sb.Append("# Speed rows").Append(string.IsNullOrEmpty(label) ? "" : " (" + label + ")").Append('\n').Append('\n');
        foreach (string h in hosts)
        {
            sb.Append("- Host: ").Append(h).Append('\n');
        }

        foreach (string name in new[] { "machine.txt", "versions.txt" })
        {
            string f = Path.Combine(dir, name);
            if (File.Exists(f))
            {
                sb.Append('\n').Append("```text\n").Append(File.ReadAllText(f).TrimEnd()).Append("\n```\n");
            }
        }

        string loadFile = Path.Combine(dir, "load.tsv");
        if (File.Exists(loadFile))
        {
            sb.Append("\nLoad (1-minute average, sampled before, during and after each group):\n\n```text\n").Append(File.ReadAllText(loadFile).TrimEnd()).Append("\n```\n");
        }

        var compared = new List<double>();
        int flagged = 0;
        sb.Append("\n## Compared rows\n\n");
        sb.Append("| Row | Parameters | OneLLM mean | Reference | Reference mean | Ratio (ours / reference) | Alloc ours | Alloc reference | Peak MB ours / ref | Status |\n");
        sb.Append("|---|---|---:|---|---:|---:|---:|---:|---:|---|\n");
        foreach (var g in rows.GroupBy(r => (r.Category, r.Parameters)).OrderBy(g => g.Key.Category, StringComparer.Ordinal).ThenBy(g => g.Key.Parameters, StringComparer.Ordinal))
        {
            Row? ours = g.FirstOrDefault(r => r.Method.StartsWith("Ours_", StringComparison.Ordinal));
            var refs = g.Where(r => r.Method.StartsWith("Ref_", StringComparison.Ordinal)).OrderBy(r => r.MeanNs).ToList();
            if (ours is null || refs.Count == 0)
            {
                continue;
            }

            Row fastest = refs[0];
            double gate = ours.MeanNs / fastest.MeanNs;
            compared.Add(gate);
            string status = gate <= 1.0 ? "meets" : gate <= 1.25 ? "slower than the fastest reference, within 1.25x" : "AT RISK (> 1.25x)";
            flagged += gate > 1.25 ? 1 : 0;
            foreach (Row r in refs)
            {
                bool first = ReferenceEquals(r, fastest);
                sb.Append("| ").Append(g.Key.Category).Append(" | ").Append(g.Key.Parameters).Append(" | ").Append(Time(ours.MeanNs))
                  .Append(" | ").Append(r.Method["Ref_".Length..]).Append(" | ").Append(Time(r.MeanNs))
                  .Append(" | ").Append((ours.MeanNs / r.MeanNs).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(Bytes(ours.AllocBytes)).Append(" | ").Append(Bytes(r.AllocBytes))
                  .Append(" | ").Append(Mb(ours.PeakMb)).Append(" / ").Append(Mb(r.PeakMb))
                  .Append(" | ").Append(first ? status : "").Append(" |\n");
            }
        }

        if (compared.Count > 0)
        {
            double geo = Math.Exp(compared.Average(Math.Log));
            sb.Append("\nGeometric mean of the per-row ratios against the fastest reference: **")
              .Append(geo.ToString("0.000", CultureInfo.InvariantCulture)).Append("** over ").Append(compared.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" rows; rows above 1.25: **").Append(flagged.ToString(CultureInfo.InvariantCulture)).Append("**. Bar: geometric mean at most 1.00 and no row above 1.25.\n");
        }

        sb.Append("\n## Rows without a comparison\n\n| Benchmark | Parameters | Mean | Alloc | Peak MB |\n|---|---|---:|---:|---:|\n");
        foreach (Row r in rows.Where(r => !r.Method.StartsWith("Ours_", StringComparison.Ordinal) && !r.Method.StartsWith("Ref_", StringComparison.Ordinal))
                     .OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ThenBy(r => r.Parameters, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(r.Type).Append('.').Append(r.Method).Append(" | ").Append(r.Parameters).Append(" | ").Append(Time(r.MeanNs))
              .Append(" | ").Append(Bytes(r.AllocBytes)).Append(" | ").Append(Mb(r.PeakMb)).Append(" |\n");
        }

        sb.Append("\nStreaming and first-event rows read a fixed stream of 2,000 (50 for first event) small text events: per-event overhead is the mean divided by that count.\n");
        File.WriteAllText(Path.Combine(dir, "results.md"), sb.ToString());
        Console.Write(sb.ToString());
        return 0;
    }

    private static string CategoryOf(string ns, string type, string method)
    {
        Type? t = typeof(VersionBench).Assembly.GetType(ns + "." + type);
        MethodInfo? m = t?.GetMethod(method);
        var attr = m?.GetCustomAttribute<BenchmarkCategoryAttribute>();
        return attr?.Categories is { Length: > 0 } c ? c[0] : type;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static string Time(double ns) => ns switch
    {
        >= 1e9 => (ns / 1e9).ToString("0.000", CultureInfo.InvariantCulture) + " s",
        >= 1e6 => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " ms",
        >= 1e3 => (ns / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " us",
        _ => ns.ToString("0", CultureInfo.InvariantCulture) + " ns",
    };

    private static string Bytes(double? b) => b switch
    {
        null => "n/a",
        >= 1024 * 1024 => (b.Value / (1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (b.Value / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => b.Value.ToString("0", CultureInfo.InvariantCulture) + " B",
    };

    private static string Mb(double? mb) => mb is null ? "n/a" : mb.Value.ToString("0", CultureInfo.InvariantCulture);
}
