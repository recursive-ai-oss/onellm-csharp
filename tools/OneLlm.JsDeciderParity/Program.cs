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

// Usage: onellm-jsdeciderparity <casesDir> <answersDir> <outDir>
//   <casesDir>/<case>/decider-dir  the path of a directory holding decider.yaml
//   <casesDir>/<case>/logic-js     the path of the JavaScript module that holds the decider's logic
//   <casesDir>/<case>/inputs.json  the caller's inputs
//   <answersDir>/<case>/answers-<k>.json  an object from wire question id (for example "q" or "q[2]") to an answer
// For each answer set, runs the whole decide pipeline (prepare, render, ask the stub engine, assemble the answers, decide) and writes
// <outDir>/<case>/ours-<k>.json = { "prepared": ..., "outputs": ... }. Exit code 1 when a run fails.
int failures = 0;
int runs = 0;
foreach (string caseDir in Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal))
{
    string name = Path.GetFileName(caseDir);
    string deciderDir = File.ReadAllText(Path.Combine(caseDir, "decider-dir")).Trim();
    string logicPath = File.ReadAllText(Path.Combine(caseDir, "logic-js")).Trim();
    using JsonDocument inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "inputs.json")));
    string answersDir = Path.Combine(args[1], name);
    Directory.CreateDirectory(Path.Combine(args[2], name));
    JsDeciderLogic logic = JsDeciderLogic.Load(logicPath);
    foreach (string answersFile in Directory.GetFiles(answersDir, "answers-*.json").Order(StringComparer.Ordinal))
    {
        string k = Path.GetFileNameWithoutExtension(answersFile)["answers-".Length..];
        JsonObject wire = JsonNode.Parse(File.ReadAllText(answersFile))!.AsObject();
        runs++;
        try
        {
            var rt = new Runtime();
            rt.Catalog = rt.Catalog.WithLayer(CatalogLayer.FromYaml(CatalogLayer.User, "name: typesafe\nmodels:\n  - name: jev-1.14.0\n    contextWindow: 64000\n"));
            rt.Providers.Set("typesafe", new JevProvider(new JevOptions { BaseUrl = new Uri("https://stub.invalid/"), HttpClient = new HttpClient(new Stub(wire)), ApiKey = new Secret("stub") }, TimeProvider.System));
            // The synthetic inputs exercise the logic, not each decider's input contract, so the schema check is off.
            Decider decider = Decider.Load(deciderDir).WithInputSchema(null).WithLogic(logic);
            DecisionResult result = await rt.DecideAsync(decider, inputs.RootElement.Clone(), "eval");
            JsonElement prepared = logic.HasPrepare ? await logic.PrepareAsync(inputs.RootElement.Clone(), default) : inputs.RootElement.Clone();
            var o = new JsonObject { ["prepared"] = JsonNode.Parse(prepared.GetRawText()), ["outputs"] = JsonNode.Parse(result.Outputs.GetRawText()) };
            File.WriteAllText(Path.Combine(args[2], name, $"ours-{k}.json"), o.ToJsonString());
            Console.WriteLine($"{name,-28} answers {k,-4} ok");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine($"{name,-28} answers {k,-4} ERROR {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }
    }
}

Console.WriteLine($"runs {runs}, failing {failures}");
return failures == 0 ? 0 : 1;

// Answers each question the request asks for from the answer file.
internal sealed class Stub(JsonObject wire) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        var answers = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> q in JsonNode.Parse(body)!.AsObject()["questions"]!.AsObject())
        {
            answers[q.Key] = wire[q.Key]!.DeepClone();
        }

        var reply = new JsonObject { ["answers"] = answers, ["model"] = "jev-1.14.0", ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
