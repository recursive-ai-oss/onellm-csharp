using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Deciders;
using OneLlm.Messages;

namespace OneLlm.Tools.Builtins;

/// <summary>
/// run_decider: runs a decider (a directory with <c>decider.yaml</c>, or the file) through the runtime and returns its answers, outputs and
/// the cost lines of the engine calls as JSON.
/// </summary>
/// <remarks>
/// The path is resolved against the file grants before anything is read, so a decider outside the granted roots is refused. The engine
/// call itself goes to the decider's model provider, which the host configured; the tool grants nothing. Runs are recorded with origin
/// <c>agent</c> (or the one the caller gives, such as <c>eval</c>), so they can be kept out of production statistics.
/// </remarks>
internal sealed class RunDeciderTool : BuiltinTool
{
    private const string Description =
        "Run a decider on the given inputs and return its answers and outputs as JSON, with the cost of the run. " +
        "The decider is a directory holding decider.yaml, or the decider.yaml file. Use origin \"eval\" for test runs so they stay out of production statistics.";

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "decider": { "type": "string", "description": "Path of the decider directory or its decider.yaml, relative to the working directory." },
            "inputs": { "type": "object", "description": "The decider's inputs, as a JSON object." },
            "origin": { "type": "string", "description": "Where the run comes from, recorded with it: agent (default), eval, api, ...", "default": "agent" }
          },
          "required": ["decider"]
        }
        """;

    public RunDeciderTool()
        : base(Spec("run_decider", Description, Schema, TimeSpan.FromMinutes(5)), ToolCapability.FileRead)
    {
    }

    // Decider.Load reads decider.yaml, the schema and the logic file by plain path. A link among them that leads outside the read
    // roots would be read despite the check above, so every link in the decider's directory is resolved against the grants first.
    private static void RefuseLinksOutside(ICapabilities caps, string real)
    {
        string dir = Directory.Exists(real) ? real : Path.GetDirectoryName(real) ?? real;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget is not null)
                {
                    _ = caps.ResolveRead(file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory fails in Load with its own message.
        }
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = Args.RequiredString(inputs, "decider");
        string origin = Args.OptionalString(inputs, "origin") is { Length: > 0 } o ? o : "agent";
        JsonObject deciderInputs = inputs["inputs"] switch
        {
            null => [],
            JsonObject obj => obj,
            _ => throw new ToolInputException("inputs", "must be an object"),
        };

        if (context.Runtime is not { } runtime)
        {
            return Fail("run_decider needs a runtime; call it from an agent or a ToolInvoker given one.");
        }

        // Resolve first: a path outside the granted roots (or through a symlink out of them) is refused before a byte is read.
        string real = context.Capabilities.ResolveRead(path);
        RefuseLinksOutside(context.Capabilities, real);
        Decider decider;
        DecisionResult result;
        try
        {
            decider = Decider.Load(real);
            result = await runtime.DecideAsync(decider, PlainJson.ToElement(deciderInputs), origin, cancellationToken).ConfigureAwait(false);
        }
        catch (OneLlmException e)
        {
            return Fail(e.Message);
        }
        catch (FileNotFoundException e)
        {
            return Fail(e.Message);
        }

        foreach (CostLine line in result.Cost.Lines)
        {
            context.AddCost(line);
        }

        var lines = new JsonArray();
        foreach (CostLine l in result.Cost.Lines)
        {
            JsonNode item = new JsonObject
            {
                ["counter"] = l.Counter,
                ["quantity"] = l.Quantity,
                ["unitPrice"] = l.UnitPrice,
                ["amount"] = l.Amount,
            };
            lines.Add(item);
        }

        var output = new JsonObject
        {
            ["decider"] = decider.Name,
            ["instanceId"] = result.Instance.InstanceId,
            ["engine"] = result.Engine,
            ["model"] = result.Model,
            ["answers"] = JsonNode.Parse(result.Answers.GetRawText()),
            ["outputs"] = JsonNode.Parse(result.Outputs.GetRawText()),
            ["cost"] = new JsonObject { ["total"] = result.Cost.Total, ["lines"] = lines },
        };
        return ToolResult.FromJson(output);
    }
}
