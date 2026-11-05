using System.Diagnostics;
using System.Text.Json;

namespace OneLlm.TemplateDiff;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: TemplateDiff fuzz --go <driver> [--seed N] [--seconds N] [--batch N] [--out dir]\n       TemplateDiff gen --seed N --count N     (cases as JSON lines on stdout)\n       TemplateDiff ours                       (reads cases on stdin, prints results)");
            return 2;
        }

        var opts = new Dictionary<string, string>();
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            opts[args[i].TrimStart('-')] = args[i + 1];
        }

        switch (args[0])
        {
            case "gen":
            {
                var g = new Gen(int.Parse(opts.GetValueOrDefault("seed", "1")));
                int n = int.Parse(opts.GetValueOrDefault("count", "10"));
                for (int i = 0; i < n; i++)
                {
                    Console.WriteLine(g.NextCase().ToJsonLine());
                }

                return 0;
            }

            case "ours":
            {
                string? line;
                while ((line = Console.ReadLine()) is not null)
                {
                    using JsonDocument d = JsonDocument.Parse(line);
                    Outcome o = OursRunner.Run(Case.FromJson(d.RootElement));
                    Console.WriteLine(o.Ok ? $"OK {JsonSerializer.Serialize(o.Out)}" : $"ERR {o.Stage}");
                }

                return 0;
            }

            case "fuzz":
                return Fuzz(opts);
            case "golden":
                return Golden(opts);
            default:
                Console.Error.WriteLine("unknown mode");
                return 2;
        }
    }

    private static int Fuzz(Dictionary<string, string> opts)
    {
        string goPath = opts["go"];
        int seed = int.Parse(opts.GetValueOrDefault("seed", "1"));
        int seconds = int.Parse(opts.GetValueOrDefault("seconds", "60"));
        string outDir = opts.GetValueOrDefault("out", ".");
        Directory.CreateDirectory(outDir);

        using var go = new GoDriver(goPath);
        var gen = new Gen(seed);
        var sw = Stopwatch.StartNew();
        long execs = 0;
        long bothOk = 0;
        long bothErr = 0;
        var disagreements = new List<string>();
        var seen = new HashSet<string>();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Case c = gen.NextCase();
            Outcome g = go.Run(c);
            Outcome o = OursRunner.Run(c);
            execs++;
            if (g.Ok && o.Ok)
            {
                bothOk++;
            }
            else if (!g.Ok && !o.Ok)
            {
                bothErr++;
            }

            if (Differs(g, o))
            {
                Case min = Minimize(go, c);
                Outcome mg = go.Run(min);
                Outcome mo = OursRunner.Run(min);
                string key = Signature(mg, mo, min);
                if (seen.Add(key) && disagreements.Count < 200)
                {
                    disagreements.Add(Describe(min, mg, mo));
                }
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "disagreements.jsonl"), disagreements);
        string summary = $"executions={execs} bothOk={bothOk} bothError={bothErr} disagreements(unique)={disagreements.Count} seconds={sw.Elapsed.TotalSeconds:F0} seed={seed}";
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary + "\n");
        Console.WriteLine(summary);
        return 0;
    }

    // Reads the curated case file, asks the reference implementation for each expected result and writes golden.jsonl.
    private static int Golden(Dictionary<string, string> opts)
    {
        using var go = new GoDriver(opts["go"]);
        var lines = new List<string>();
        int disagreements = 0;
        foreach (Case c in CuratedFile.Read(opts["in"]))
        {
            Outcome g = go.Run(c);
            Outcome o = OursRunner.Run(c);
            if (Differs(g, o))
            {
                disagreements++;
                Console.Error.WriteLine($"DIFF {c.Name}: go={(g.Ok ? "OK " + g.Out : "ERR " + g.Stage)} ours={(o.Ok ? "OK " + o.Out : "ERR " + o.Stage + " " + o.Err)}");
            }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteString("t", c.Text);
                w.WritePropertyName("data");
                using (JsonDocument d = JsonDocument.Parse(c.DataJson))
                {
                    d.RootElement.WriteTo(w);
                }

                w.WriteString("missingKey", c.MissingError ? "error" : "default");
                if (c.NoEscape)
                {
                    w.WriteBoolean("escape", false);
                }

                w.WriteStartObject("embeds");
                foreach (var kv in c.Embeds)
                {
                    w.WriteString(kv.Key, kv.Value);
                }

                w.WriteEndObject();
                w.WriteBoolean("ok", g.Ok);
                if (g.Ok)
                {
                    w.WriteString("out", g.Out);
                }
                else
                {
                    w.WriteString("stage", g.Stage);
                }

                w.WriteEndObject();
            }

            lines.Add(Encoding.UTF8.GetString(ms.ToArray()));
        }

        File.WriteAllLines(opts["out"], lines);
        Console.WriteLine($"cases={lines.Count} disagreements={disagreements}");
        return disagreements == 0 ? 0 : 1;
    }

    private static bool Differs(Outcome g, Outcome o)
    {
        if (g.Ok != o.Ok)
        {
            return true;
        }

        return g.Ok ? g.Out != o.Out : g.Stage != o.Stage;
    }

    private static string Signature(Outcome g, Outcome o, Case c)
    {
        // Group repeats of the same symptom: which side failed and how, plus the leading shape of the minimized text.
        string head = c.Text.Length > 40 ? c.Text.Substring(0, 40) : c.Text;
        return (g.Ok ? "ok" : g.Stage) + "/" + (o.Ok ? "ok" : o.Stage) + "/" + head;
    }

    private static string Describe(Case c, Outcome g, Outcome o)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("case", c.ToJsonLine());
            w.WriteString("go", g.Ok ? "OK " + g.Out : "ERR " + g.Stage + ": " + g.Err);
            w.WriteString("ours", o.Ok ? "OK " + o.Out : "ERR " + o.Stage + ": " + o.Err);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Shrinks the template text (and drops embeds/data keys) while the two engines still disagree.
    private static Case Minimize(GoDriver go, Case c)
    {
        Case cur = Clone(c);
        bool Still(Case x) => Differs(go.Run(x), OursRunner.Run(x));
        if (cur.Embeds.Count > 0)
        {
            Case t = Clone(cur);
            t.Embeds.Clear();
            if (Still(t))
            {
                cur = t;
            }
        }

        foreach (string newData in new[] { "{}", "null" })
        {
            Case t = Clone(cur);
            t.DataJson = newData;
            if (Still(t))
            {
                cur = t;
                break;
            }
        }

        int budget = 400;
        for (int chunk = Math.Max(cur.Text.Length / 2, 1); chunk >= 1 && budget > 0; chunk /= 2)
        {
            int i = 0;
            while (i < cur.Text.Length && budget > 0)
            {
                int len = Math.Min(chunk, cur.Text.Length - i);
                Case t = Clone(cur);
                t.Text = cur.Text.Remove(i, len);
                budget--;
                if (Still(t))
                {
                    cur = t;
                }
                else
                {
                    i += len;
                }
            }
        }

        return cur;
    }

    private static Case Clone(Case c) => new() { Text = c.Text, DataJson = c.DataJson, MissingError = c.MissingError, Embeds = new Dictionary<string, string>(c.Embeds) };
}

/// <summary>A long-lived reference process answering one case per line.</summary>
internal sealed class GoDriver : IDisposable
{
    private readonly Process _p;

    public GoDriver(string path)
    {
        _p = Process.Start(new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        })!;
    }

    public Outcome Run(Case c)
    {
        _p.StandardInput.WriteLine(c.ToJsonLine());
        _p.StandardInput.Flush();
        string line = _p.StandardOutput.ReadLine() ?? throw new InvalidOperationException("reference driver exited");
        using JsonDocument d = JsonDocument.Parse(line);
        JsonElement e = d.RootElement;
        bool ok = e.GetProperty("ok").GetBoolean();
        string o = e.TryGetProperty("out", out var ov) ? ov.GetString()! : "";
        string stage = e.TryGetProperty("stage", out var sv) ? sv.GetString()! : "";
        string err = e.TryGetProperty("err", out var ev) ? ev.GetString()! : "";
        return new Outcome(ok, o, stage, err);
    }

    public void Dispose()
    {
        try
        {
            _p.StandardInput.Close();
            _p.WaitForExit(2000);
        }
        finally
        {
            _p.Dispose();
        }
    }
}
