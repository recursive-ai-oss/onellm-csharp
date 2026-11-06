using System.Net.Http;
using System.Threading;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.Providers;
using OneLlm.Providers.TypeSafe;

// Usage: onellm-deciderparity <casesDir> <referenceDir> [window ...]
//   <casesDir>/<case>/decider-dir  holds the path of a directory with decider.yaml
//   <casesDir>/<case>/inputs.json  the decider's inputs
//   <referenceDir>/<case>/request-w<window>-<i>.json  the request bodies the reference server sent, sorted by body
// Prints one line per case and window; exit code 1 when any request differs.
int failures = 0;
int[] windows = args.Length > 2 ? [.. args[2..].Select(int.Parse)] : [64000, 3000, 600];
int compared = 0, equal = 0;
foreach (string caseDir in Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal))
{
    string name = Path.GetFileName(caseDir);
    string deciderDir = File.ReadAllText(Path.Combine(caseDir, "decider-dir")).Trim();
    using JsonDocument inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "inputs.json")));
    foreach (int window in windows)
    {
        var capture = new Capture();
        var rt = new Runtime();
        string yaml = $"name: typesafe\nmodels:\n  - name: jev-1.14.0\n    contextWindow: {window}\n";
        rt.Catalog = rt.Catalog.WithLayer(CatalogLayer.FromYaml(CatalogLayer.User, yaml));
        rt.Providers.Set("typesafe", new JevProvider(new JevOptions { BaseUrl = new Uri("https://stub.invalid/"), HttpClient = new HttpClient(capture), ApiKey = new Secret("stub") }, TimeProvider.System));
        string outcome;
        try
        {
            // The synthetic inputs exercise encoding and splitting, not each decider's input contract, so the schema check is off here.
            Decider decider = Decider.Load(deciderDir).WithInputSchema(null).WithOutputSchema(null);
            await rt.DecideAsync(decider, inputs.RootElement.Clone(), "eval");
            outcome = "ok";
        }
        catch (Exception e)
        {
            outcome = "error " + e.GetType().Name + ": " + e.Message.Split('\n')[0];
        }

        List<byte[]> mine = [.. capture.Bodies.OrderBy(b => b, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))];
        var theirs = new List<byte[]>();
        for (int i = 0; File.Exists(Path.Combine(args[1], name, $"request-w{window}-{i}.json")); i++)
        {
            theirs.Add(File.ReadAllBytes(Path.Combine(args[1], name, $"request-w{window}-{i}.json")));
        }

        // An empty list renders to no questions: the reference sends nothing, and so do we.
        var notes = new List<string>();
        if (mine.Count != theirs.Count)
        {
            notes.Add($"request count {mine.Count} vs {theirs.Count}");
        }

        for (int i = 0; i < Math.Min(mine.Count, theirs.Count); i++)
        {
            compared++;
            if (mine[i].AsSpan().SequenceEqual(theirs[i]))
            {
                equal++;
                continue;
            }

            int at = mine[i].AsSpan().CommonPrefixLength(theirs[i]);
            notes.Add($"request {i}: first byte difference at offset {at}; path {PathOf(mine[i], theirs[i]) ?? "(bytes only)"}");
        }

        if (notes.Count > 0 || outcome != "ok")
        {
            failures++;
        }

        Console.WriteLine($"{name,-28} window {window,6}: {mine.Count} requests, {outcome}{(notes.Count > 0 ? "  MISMATCH " + string.Join("; ", notes) : "")}");
    }
}

Console.WriteLine($"requests compared {compared}, byte-equal {equal}, case-windows failing {failures}");
return failures == 0 ? 0 : 1;

static string? PathOf(byte[] a, byte[] b)
{
    try
    {
        return Walk(JsonNode.Parse(a), JsonNode.Parse(b), "$");
    }
    catch (JsonException)
    {
        return null;
    }
}

static string? Walk(JsonNode? a, JsonNode? b, string path)
{
    if (a is JsonObject oa && b is JsonObject ob)
    {
        var keysA = oa.Select(p => p.Key).ToList();
        var keysB = ob.Select(p => p.Key).ToList();
        for (int i = 0; i < Math.Max(keysA.Count, keysB.Count); i++)
        {
            if (i >= keysA.Count || i >= keysB.Count || keysA[i] != keysB[i])
            {
                return path + " (key order or set)";
            }

            if (Walk(oa[keysA[i]], ob[keysA[i]], path + "." + keysA[i]) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    if (a is JsonArray aa && b is JsonArray ab)
    {
        for (int i = 0; i < Math.Min(aa.Count, ab.Count); i++)
        {
            if (Walk(aa[i], ab[i], path + "[" + i + "]") is { } found)
            {
                return found;
            }
        }

        return aa.Count == ab.Count ? null : path + " (length)";
    }

    return a?.ToJsonString() == b?.ToJsonString() ? null : path + " (value or encoding)";
}

// Answers every question of a request with a well-formed answer of its type and records the body.
internal sealed class Capture : HttpMessageHandler
{
    public List<byte[]> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        lock (Bodies)
        {
            Bodies.Add(body);
        }

        var answers = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> q in JsonNode.Parse(body)!.AsObject()["questions"]!.AsObject())
        {
            string type = (string?)q.Value!["type"] ?? "noul";
            answers[q.Key] = type switch
            {
                "choice" => new JsonObject { ["type"] = "choice", ["choice"] = q.Value!["criteria"]!.AsObject().First().Key, ["confidence"] = 0.5, ["probabilities"] = new JsonObject() },
                "score" => new JsonObject { ["type"] = "score", ["score"] = 0, ["confidence"] = 0.5, ["probabilities"] = new JsonArray() },
                _ => new JsonObject { ["type"] = "noul", ["noul"] = 0.5 },
            };
        }

        var reply = new JsonObject { ["answers"] = answers, ["model"] = "jev-1.14.0", ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
