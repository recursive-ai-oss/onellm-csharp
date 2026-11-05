using OneLlm.Definitions;

namespace OneLlm.Tests.Definitions;

public class DefinitionTests
{
    private const string Agent = """
        # A reviewer.
        name: Reviewer
        model: sonnet
        temperature: 0.5
        maxIterations: 20
        systemPrompt: |
          You review {{ .inputs.repo }}.
        references:
          - routine: files/read
            tools: [read_file]
          - swarm: /tools/web
            vendorKey: kept
        initialPrompts:
          - user:
              - text: "Start"
          - when: "{{ .inputs.go }}"
            toolCalls:
              - tool: list_files
                inputs: { path: . }
        hooks:
          - file: hooks/audit.js
            points: [beforeToolCall]
        missingKey: error
        futureField:
          nested: [1, 2]
        """;

    [Fact]
    public void Parses_known_fields_and_keeps_unknown_ones()
    {
        AgentDefinition d = AgentDefinition.Parse(Agent, "reviewer/agent.yaml");
        Assert.Equal("Reviewer", d.Name);
        Assert.Equal("sonnet", d.Model);
        Assert.Equal(0.5, d.Temperature);
        Assert.Equal(20, d.MaxIterations);
        Assert.Equal(2, d.References.Count);
        Assert.Equal(["read_file"], d.References[0].Tools);
        Assert.NotNull(d.References[1].ExtensionData);
        Assert.Equal("kept", d.References[1].ExtensionData!["vendorKey"]);
        Assert.Equal(2, d.InitialPrompts.Count);
        Assert.Equal("{{ .inputs.go }}", d.InitialPrompts[1].When);
        Assert.Equal("list_files", d.InitialPrompts[1].ToolCalls[0].Tool);
        Assert.Equal("beforeToolCall", d.Hooks[0].Points![0]);
        Assert.Equal("error", d.MissingKey);
        Assert.True(d.ExtensionData!.ContainsKey("futureField"));
    }

    [Fact]
    public void An_unchanged_definition_saves_byte_for_byte_and_a_changed_one_keeps_unknown_keys()
    {
        AgentDefinition d = AgentDefinition.Parse(Agent);
        Assert.Equal(Agent, d.ToYaml());

        d.Model = "haiku";
        string saved = d.ToYaml();
        Assert.NotEqual(Agent, saved);
        AgentDefinition again = AgentDefinition.Parse(saved);
        Assert.Equal("haiku", again.Model);
        Assert.True(again.ExtensionData!.ContainsKey("futureField"));
        Assert.Equal("kept", again.References[1].ExtensionData!["vendorKey"]);
        Assert.Equal(d.InitialPrompts[1].When, again.InitialPrompts[1].When);
        Assert.Equal("You review {{ .inputs.repo }}.\n", again.SystemPrompt);
    }

    [Fact]
    public void Malformed_yaml_fails_with_the_file_and_a_position()
    {
        var e = Assert.Throws<BadDefinitionException>(() => AgentDefinition.Parse("name: x\nmodel: [unclosed\n", "agents/bad.yaml"));
        Assert.Equal("agents/bad.yaml", e.File);
        Assert.True(e.Line > 0, "line");
        Assert.True(e.Column > 0, "column");
        Assert.Contains("agents/bad.yaml:", e.Message);
    }

    [Fact]
    public void An_empty_file_is_a_bad_definition()
    {
        Assert.Throws<BadDefinitionException>(() => AgentDefinition.Parse("", "empty.yaml"));
    }

    [Fact]
    public void Strict_load_fails_on_an_ungranted_reference_at_its_position()
    {
        var options = new DefinitionLoadOptions { IsGranted = r => r.Routine is null || r.Routine.StartsWith("files/", StringComparison.Ordinal) };
        var ok = AgentDefinition.Parse(Agent, "a.yaml", options);
        Assert.Equal(2, ok.References.Count);

        string denied = Agent.Replace("files/read", "shell/run");
        var e = Assert.Throws<BadDefinitionException>(() => AgentDefinition.Parse(denied, "a.yaml", options));
        Assert.Contains("shell/run", e.Message);
        Assert.Equal(9, e.Line);
    }

    [Fact]
    public void Lenient_load_strips_an_ungranted_reference_and_warns()
    {
        string denied = Agent.Replace("files/read", "shell/run");
        var options = new DefinitionLoadOptions { IsStrict = false, IsGranted = r => r.Routine != "shell/run" };
        AgentDefinition d = AgentDefinition.Parse(denied, "a.yaml", options);
        Assert.Single(d.References);
        Assert.Equal("/tools/web", d.References[0].Swarm);
        Assert.Single(d.Warnings);
        Assert.Contains("shell/run", d.Warnings[0]);
    }

    [Fact]
    public void An_invalid_missingKey_fails_strict_and_is_dropped_lenient()
    {
        string bad = "model: m\nmissingKey: zero\n";
        var e = Assert.Throws<BadDefinitionException>(() => AgentDefinition.Parse(bad, "m.yaml"));
        Assert.Equal(2, e.Line);
        AgentDefinition d = AgentDefinition.Parse(bad, "m.yaml", DefinitionLoadOptions.Lenient);
        Assert.Null(d.MissingKey);
        Assert.Single(d.Warnings);
    }

    [Fact]
    public void Empty_lists_that_the_file_wrote_stay_in_the_file()
    {
        AgentDefinition d = AgentDefinition.Parse("name: x\nreferences: []\nmodel: m\n");
        d.Model = "n";
        string saved = d.ToYaml();
        Assert.Contains("references: []", saved);
        Assert.DoesNotContain("initialPrompts", saved);
    }

    [Fact]
    public void A_flow_file_takes_its_metadata_from_flow_yaml()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onellm-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "greet.flow"), "model: m\ninitialPrompts:\n  - user:\n      - text: Hello {{ .inputs.who }}\n");
            File.WriteAllText(Path.Combine(dir, "flow.yaml"), "name: Greeter\nversion: 2\ninputExamples:\n  - who: Ada\n");
            AgentDefinition d = AgentDefinition.Load(Path.Combine(dir, "greet.flow"));
            Assert.NotNull(d.Flow);
            Assert.Equal("Greeter", d.Flow!.Name);
            Assert.Equal(2, d.Flow.Version);
            Assert.True(d.Flow.Other!.ContainsKey("inputExamples"));
            Assert.Contains("inputExamples", d.Flow.ToYaml());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string Decider = """
        model: jev-latest
        state:
          ticket: "{{ .inputs.ticket }}"
        questions:
          topic:
            type: choice
            instructions: Which team?
            criteria:
              billing: Charges
              orders: Delivery
          mood:
            type: score
            instructions:
              question: How upset?
            criteria:
              - Calm
              - Angry
          urgent:
            type: noul
            instructions: Is it urgent?
            forEach: "{{ .inputs.items }}"
        samples: 3
        """;

    [Fact]
    public void A_decider_loads_checks_its_shape_and_saves_unchanged()
    {
        DeciderDefinition d = DeciderDefinition.Parse(Decider, "triage/decider.yaml");
        Assert.Equal(3, d.Questions.Count);
        Assert.Equal("score", d.Questions["mood"].Type);
        Assert.True(d.ExtensionData!.ContainsKey("samples"));
        Assert.Equal(Decider, d.ToYaml());
        Assert.Contains("samples", d.WriteTyped());
    }

    [Theory]
    [InlineData("questions:\n  q:\n    type: choice\n    instructions: x\n    criteria: {a: b}\nmodel: m\n", "2 to 255 options")]
    [InlineData("model: m\nquestions:\n  q:\n    type: score\n    instructions: x\n    criteria: [a]\n", "2 to 10")]
    [InlineData("model: m\nquestions:\n  q:\n    type: guess\n    instructions: x\n", "'guess'")]
    [InlineData("questions:\n  q:\n    type: noul\n    instructions: x\n", "'model'")]
    public void A_decider_with_a_bad_shape_is_refused_with_a_position(string yaml, string fragment)
    {
        var e = Assert.Throws<BadDefinitionException>(() => DeciderDefinition.Parse(yaml, "d.yaml"));
        Assert.Contains(fragment, e.Message);
        Assert.Equal("d.yaml", e.File);
    }
}
