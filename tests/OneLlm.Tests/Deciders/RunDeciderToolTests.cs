using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Deciders;

public sealed class RunDeciderToolTests
{
    private const string Yaml = """
        model: dec-1
        state:
          subject: "{{ .inputs.subject }}"
        questions:
          topic:
            type: choice
            instructions: Which team?
            criteria:
              billing: Charges
              orders: Delivery
          angry:
            type: noul
            instructions: Is the customer angry?
        """;

    private static async Task<ToolResult> Run(DeciderHarness h, ICapabilities caps, string json, bool withRuntime = true)
    {
        ITool tool = BuiltinTools.Create().Single(static t => t.Definition.Name == "run_decider");
        var ctx = new ToolContext("agent-1", "call-1", TimeProvider.System) { Capabilities = caps, Runtime = withRuntime ? h.Runtime : null };
        ToolResult result = await ToolInvoker.InvokeAsync(tool, (JsonObject)JsonNode.Parse(json)!, ctx, default);
        return result;
    }

    private static string Text(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(static p => p.Text));

    private static string Write(Scratch s, string name = "triage")
    {
        Directory.CreateDirectory(s.In(name));
        File.WriteAllText(s.In(name + "/decider.yaml"), Yaml);
        return name;
    }

    [Fact]
    public async Task It_is_registered_with_the_other_built_ins_and_needs_file_read()
    {
        Assert.Contains("run_decider", BuiltinTools.Names);
        ITool tool = BuiltinTools.Create().Single(static t => t.Definition.Name == "run_decider");
        Assert.Equal(ToolCapability.FileRead, ((ICapabilityTool)tool).Requires);
        Assert.Equal(["run_decider"], BuiltinTools.ToolsForRoutine("run_decider"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task It_runs_a_decider_under_the_grants_and_returns_answers_and_cost_lines()
    {
        using var scratch = new Scratch();
        var h = new DeciderHarness();
        h.Engine.DefaultUsage = new Usage { Model = "dec-1", InputTokens = 1000, OutputTokens = 1 };
        string dir = Write(scratch);

        ToolResult r = await Run(h, Capabilities.Files(scratch.Root), $$$"""{"decider":"{{{dir}}}","inputs":{"subject":"refund please"},"origin":"eval"}""");

        Assert.False(r.IsError, Text(r));
        JsonObject o = (JsonObject)((JsonPart)Assert.Single(r.Parts)).Value!;
        Assert.Equal("triage", (string?)o["decider"]);
        Assert.Equal("decisions", (string?)o["engine"]);
        Assert.Equal("billing", (string?)o["answers"]!["topic"]!["choice"]);
        Assert.NotNull(o["instanceId"]);
        // 1000 input tokens at $1 per million.
        Assert.Equal(0.001m, (decimal)o["cost"]!["total"]!);
        Assert.NotEmpty(r.CostLines);
        Assert.Contains("refund please", DeciderHarness.Text(h.Engine.Requests.Single().State));

        // The run is recorded with the origin the caller gave, so evaluation runs stay out of statistics.
        var run = await h.Runtime.GetDecisionAsync((string)o["instanceId"]!);
        Assert.Equal("eval", run!.Origin);
    }

    [Fact]
    public async Task A_path_outside_the_granted_roots_is_refused_before_anything_is_read_or_asked()
    {
        using var scratch = new Scratch();
        var h = new DeciderHarness();
        Write(scratch);
        string outside = Path.Combine(scratch.Outside, "evil");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "decider.yaml"), Yaml);

        ToolResult absolute = await Run(h, Capabilities.Files(scratch.Root), $$$"""{"decider":{{{System.Text.Json.JsonSerializer.Serialize(outside)}}},"inputs":{"subject":"x"}}""");
        ToolResult dotdot = await Run(h, Capabilities.Files(scratch.Root), """{"decider":"../outside/evil","inputs":{"subject":"x"}}""");

        Assert.True(absolute.IsError);
        Assert.True(dotdot.IsError);
        Assert.Empty(h.Engine.Requests);
    }

    [Fact]
    public async Task A_symlink_out_of_the_root_is_refused()
    {
        using var scratch = new Scratch();
        var h = new DeciderHarness();
        string outside = Path.Combine(scratch.Outside, "evil");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "decider.yaml"), Yaml);
        Directory.CreateSymbolicLink(scratch.In("link"), outside);

        ToolResult r = await Run(h, Capabilities.Files(scratch.Root), """{"decider":"link","inputs":{"subject":"x"}}""");

        Assert.True(r.IsError);
        Assert.Empty(h.Engine.Requests);
    }

    [Fact]
    public async Task Bad_inputs_missing_deciders_and_a_missing_runtime_come_back_as_error_results()
    {
        using var scratch = new Scratch();
        var h = new DeciderHarness();
        string dir = Write(scratch);
        ICapabilities caps = Capabilities.Files(scratch.Root);

        Assert.True((await Run(h, caps, """{"decider":"nothing-here"}""")).IsError);
        Assert.True((await Run(h, caps, $$$"""{"decider":"{{{dir}}}"}""")).IsError); // the state needs .inputs.subject: a missing input is an error
        Assert.True((await Run(h, caps, $$$"""{"decider":"{{{dir}}}","inputs":{"subject":"x"}}""", withRuntime: false)).IsError);
        Assert.True((await Run(h, caps, $$$"""{"decider":"{{{dir}}}","inputs":[1]}""")).IsError); // malformed arguments come back as an error result, as for every tool
        Assert.True((await Run(h, caps, "{}")).IsError);
        Assert.Empty(h.Engine.Requests);
    }
}
