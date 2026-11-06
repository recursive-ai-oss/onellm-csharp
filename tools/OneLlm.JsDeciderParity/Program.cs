using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using OneLlm;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.JavaScript;
using OneLlm.Providers;
using OneLlm.Providers.TypeSafe;

// Usage: onellm-jsdeciderparity <casesDir> <answerSets> <outDir>
//   <casesDir>/<case>/decider-dir  the path of a directory holding decider.yaml
//   <casesDir>/<case>/logic-js     the path of the JavaScript module that holds the decider's logic
//   <casesDir>/<case>/logic-ts     optional: the path of a TypeScript module to load instead (types erased in place)
//   A case with no inputs.json is only loaded (counted, not run).
//   <casesDir>/<case>/inputs.json  the caller's inputs
// <answerSets> is how many answer sets to try: set k answers every question the engine is asked, from a fixed pattern of values (the first,
// last and middle choices, the ends and fractions of score levels, probabilities on both sides of the usual thresholds).
// For each answer set, runs the whole decide pipeline (prepare, render, ask the stub engine, assemble the answers, decide) and writes
// <outDir>/<case>/ours-<k>.json = { "prepared": ..., "outputs": ... }. Exit code 1 when a run fails.
int failures = 0;
int runs = 0;
int loads = 0;
int loadFailures = 0;
foreach (string caseDir in Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal))
{
    string name = Path.GetFileName(caseDir);
    string deciderDir = File.ReadAllText(Path.Combine(caseDir, "decider-dir")).Trim();
    string tsFile = Path.Combine(caseDir, "logic-ts");
    string logicPath = File.ReadAllText(File.Exists(tsFile) ? tsFile : Path.Combine(caseDir, "logic-js")).Trim();
    if (!File.Exists(Path.Combine(caseDir, "inputs.json")))
    {
        loads++;
        try
        {
            JsDeciderLogic.Load(logicPath);
        }
        catch (Exception e)
        {
            loadFailures++;
            Console.WriteLine($"{name,-28} load ERROR {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }

        continue;
    }

    using JsonDocument inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "inputs.json")));
    Directory.CreateDirectory(Path.Combine(args[2], name));
    JsDeciderLogic logic = JsDeciderLogic.Load(logicPath);
    for (int set = 0; set < int.Parse(args[1]); set++)
    {
        string k = set.ToString();
        var used = new JsonObject();
        runs++;
        try
        {
            var rt = new Runtime();
            rt.Catalog = rt.Catalog.WithLayer(CatalogLayer.FromYaml(CatalogLayer.User, "name: typesafe\nmodels:\n  - name: jev-1.14.0\n    contextWindow: 64000\n"));
            rt.Providers.Set("typesafe", new JevProvider(new JevOptions { BaseUrl = new Uri("https://stub.invalid/"), HttpClient = new HttpClient(new Stub(set, used)), ApiKey = new Secret("stub") }, TimeProvider.System));
            // The synthetic inputs exercise the logic, not each decider's input contract, so the schema check is off.
            Decider decider = Decider.Load(deciderDir).WithInputSchema(null).WithLogic(logic);
            DecisionResult result = await rt.DecideAsync(decider, inputs.RootElement.Clone(), "eval");
            JsonElement prepared = logic.HasPrepare ? await logic.PrepareAsync(inputs.RootElement.Clone(), default) : inputs.RootElement.Clone();
            var o = new JsonObject { ["prepared"] = JsonNode.Parse(prepared.GetRawText()), ["outputs"] = JsonNode.Parse(result.Outputs.GetRawText()) };
            File.WriteAllText(Path.Combine(args[2], name, $"ours-{k}.json"), o.ToJsonString());
            File.WriteAllText(Path.Combine(args[2], name, $"used-{k}.json"), used.ToJsonString());
            Console.WriteLine($"{name,-28} answers {k,-4} ok");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine($"{name,-28} answers {k,-4} ERROR {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }
    }
}

Console.WriteLine($"runs {runs}, failing {failures}; load-only {loads}, failing {loadFailures}");
return failures + loadFailures == 0 ? 0 : 1;

// Answers each question the request asks for, and remembers what it said so the reference runner can be given the same answers.
internal sealed class Stub(int set, JsonObject used) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        var answers = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> q in JsonNode.Parse(body)!.AsObject()["questions"]!.AsObject())
        {
            JsonObject a = Answer(q.Key, q.Value!.AsObject(), set);
            lock (used)
            {
                used[q.Key] = a.DeepClone();
            }

            answers[q.Key] = a;
        }

        var reply = new JsonObject { ["answers"] = answers, ["model"] = "jev-1.14.0", ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static JsonObject Answer(string id, JsonObject q, int set)
    {
        uint h = 2166136261;
        foreach (char c in id + "|" + set)
        {
            h = (h ^ c) * 16777619;
        }

        double frac = (h % 10000) / 10000.0;
        int m = set % 6;
        string type = (string?)q["type"] ?? "noul";
        if (type == "noul")
        {
            return new JsonObject { ["type"] = "noul", ["noul"] = new[] { 0.1, 0.9, 0.5, frac, 0.5000001, 0.4999999 }[m] };
        }

        double conf = new[] { 0.9, 0.6, 0.3, frac, 0.75, 0.7499 }[m];
        if (type == "choice")
        {
            string[] keys = [.. q["criteria"]!.AsObject().Select(p => p.Key)];
            int pick = new[] { 0, keys.Length - 1, keys.Length / 2, (int)(h % (uint)keys.Length), 0, keys.Length - 1 }[m];
            var probs = new JsonObject();
            for (int i = 0; i < keys.Length; i++)
            {
                probs[keys[i]] = Math.Round(i == pick ? conf : (1 - conf) / Math.Max(1, keys.Length - 1), 4);
            }

            return new JsonObject { ["type"] = "choice", ["choice"] = keys[pick], ["probabilities"] = probs, ["confidence"] = conf };
        }

        JsonArray levels = q["criteria"]!.AsArray();
        int n = levels.Count;
        double score = new[] { 0.0, n - 1, n > 2 ? 1.5 : 0.5, Math.Round(frac * (n - 1), 3), Math.Min(1.5, n - 1), 0.5 }[m];
        var legend = new JsonObject();
        var p2 = new JsonObject();
        for (int i = 0; i < n; i++)
        {
            legend[i.ToString()] = levels[i]!.DeepClone();
            p2[i.ToString()] = Math.Round(1.0 / n, 4);
        }

        return new JsonObject { ["type"] = "score", ["score"] = score, ["legend"] = legend, ["probabilities"] = p2, ["confidence"] = conf };
    }
}
