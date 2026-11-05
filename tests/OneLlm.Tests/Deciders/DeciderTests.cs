using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Deciders;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;
using OneLlm.Providers.TypeSafe;
using OneLlm.Testing;

namespace OneLlm.Tests.Deciders;

public sealed class DeciderTests
{
    private const string Triage = """
        model: dec-1
        state:
          subject: "{{ .inputs.subject }}"
          customer: "{{ .inputs.customer }}"
          fixed: [a, b]
        questions:
          topic:
            type: choice
            instructions: "Which team{{ if .inputs.customer }} (known){{ end }}?"
            criteria:
              billing: Charges
              orders: Delivery
          urgency:
            type: score
            instructions: How urgent?
            criteria: [Low, High]
          angry:
            type: noul
            instructions: Is the customer angry?
        """;

    [Fact]
    public async Task The_state_is_rendered_as_Go_marshals_it()
    {
        var h = new DeciderHarness();
        Decider d = Decider.FromYaml(Triage, "triage");
        await h.Runtime.DecideAsync(d, DeciderHarness.Json("""{"subject":"a <b> & c","customer":{"zeta":1,"alpha":2.5,"big":1e21,"small":0.0000001}}"""), "eval");
        DecisionRequest sent = Assert.Single(h.Engine.Requests);
        // Map keys sorted, <, > and & escaped as \u003c \u003e \u0026, floats shortest ('f' unless below 1e-6 or from 1e21).
        Assert.Equal(
            """{"subject":"a \u003cb\u003e \u0026 c","customer":{"alpha":2.5,"big":1e+21,"small":1e-7,"zeta":1},"fixed":["a","b"]}""",
            DeciderHarness.Text(sent.State));
        Assert.Equal(["topic", "urgency", "angry"], sent.Questions.Select(static q => q.Id));
        Assert.Equal("dec-1", sent.Model);
    }

    [Fact]
    public async Task A_whole_value_template_that_gives_nothing_is_null()
    {
        var h = new DeciderHarness();
        await h.Runtime.DecideAsync(Decider.FromYaml(Triage, "triage"), DeciderHarness.Json("""{"subject":"s","customer":null}"""));
        Assert.Equal("""{"subject":"s","customer":null,"fixed":["a","b"]}""", DeciderHarness.Text(h.Engine.Requests[0].State));
    }

    [Fact]
    public async Task A_missing_input_is_an_error_not_no_value()
    {
        var h = new DeciderHarness();
        Exception e = await Assert.ThrowsAnyAsync<Exception>(() => h.Runtime.DecideAsync(Decider.FromYaml(Triage, "triage"), DeciderHarness.Json("""{"subject":"s"}""")));
        Assert.Contains("customer", e.Message);
        Assert.Empty(h.Engine.Requests);
    }

    [Fact]
    public async Task Answers_come_back_in_the_declared_shape_and_the_run_is_an_instance()
    {
        var h = new DeciderHarness();
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(Triage, "triage"), DeciderHarness.Json("""{"subject":"s","customer":{}}"""));
        Assert.Equal("billing", r.Answers.GetProperty("topic").GetProperty("choice").GetString());
        Assert.Equal("noul", r.Answers.GetProperty("angry").GetProperty("type").GetString());
        Assert.Equal(InstanceStates.Stopped, r.Instance.State);
        DecisionRun? run = await h.Runtime.GetDecisionAsync(r.Instance.InstanceId);
        Assert.NotNull(run);
        Assert.Equal("api", run.Origin);
    }

    private const string ForEach = """
        model: dec-1
        state:
          items: "{{ .inputs.items }}"
        questions:
          rel:
            type: noul
            forEach: "{{ .inputs.items }}"
            instructions: "Is {{ .item.name }} (number {{ .index }}) relevant?"
        """;

    [Fact]
    public async Task ForEach_sends_one_question_per_element_and_reassembles_lists()
    {
        var h = new DeciderHarness();
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(ForEach, "fe"), DeciderHarness.Json("""{"items":[{"name":"x"},{"name":"y"}]}"""));
        DecisionRequest sent = Assert.Single(h.Engine.Requests);
        Assert.Equal(["rel[0]", "rel[1]"], sent.Questions.Select(static q => q.Id));
        Assert.Contains("Is y (number 1) relevant?", DeciderHarness.Text(sent.Questions[1].Json));
        Assert.False(DeciderHarness.Text(sent.Questions[0].Json).Contains("forEach", StringComparison.Ordinal));
        Assert.Equal(2, r.Answers.GetProperty("rel").GetArrayLength());
    }

    [Fact]
    public async Task An_empty_forEach_list_asks_nothing()
    {
        var h = new DeciderHarness();
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(ForEach, "fe"), DeciderHarness.Json("""{"items":[]}"""));
        Assert.Empty(h.Engine.Requests);
        Assert.Equal(0, r.Answers.GetProperty("rel").GetArrayLength());
    }

    [Fact]
    public async Task More_than_the_model_cap_of_questions_is_split_into_requests()
    {
        var h = new DeciderHarness();
        string items = string.Join(',', Enumerable.Range(0, 450).Select(static i => "{\"name\":\"n" + i + "\"}"));
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(ForEach, "fe"), DeciderHarness.Json("{\"items\":[" + items + "]}"));
        Assert.Equal([400, 50], h.Engine.Requests.Select(static q => q.Questions.Count).Order().Reverse());
        Assert.Equal(450, r.Answers.GetProperty("rel").GetArrayLength());
    }

    [Fact]
    public async Task A_small_window_splits_by_token_budget_and_every_question_is_sent_once()
    {
        var h = new DeciderHarness();
        h.Runtime.Catalog = h.Runtime.Catalog.WithLayer(OneLlm.Catalog.CatalogLayer.FromYaml(OneLlm.Catalog.CatalogLayer.User, "providers:\n  - name: fakedec\n    models:\n      - name: dec-1\n        contextWindow: 600\n"));
        string items = string.Join(',', Enumerable.Range(0, 20).Select(static i => "{\"name\":\"n" + i + "\"}"));
        await h.Runtime.DecideAsync(Decider.FromYaml(ForEach, "fe"), DeciderHarness.Json("{\"items\":[" + items + "]}"));
        Assert.True(h.Engine.Requests.Count > 1);
        Assert.Equal(20, h.Engine.Requests.SelectMany(static q => q.Questions).Select(static q => q.Id).Distinct().Count());
    }

    [Fact]
    public void PlanChunks_packs_greedily_in_order_and_never_exceeds_the_budget()
    {
        List<List<int>> chunks = DecisionEngine.PlanChunks([10, 10, 10, 10, 10], 25);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, chunks.SelectMany(static c => c));
        Assert.All(chunks, c => Assert.True(c.Sum(i => 10) <= 25));
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void Jev_body_has_state_model_questions_in_that_order()
    {
        var request = new DecisionRequest
        {
            Model = "jev-latest",
            State = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"),
            Questions = [new WireQuestion("q", "noul", System.Text.Encoding.UTF8.GetBytes("{\"type\":\"noul\",\"instructions\":\"x\"}"))],
        };
        Assert.Equal(
            """{"state":{"a":1},"model":"jev-latest","questions":{"q":{"type":"noul","instructions":"x"}}}""",
            DeciderHarness.Text(JevProvider.WriteBody(request)));
    }

    [Fact]
    public async Task Decisions_usage_never_counts_cached_tokens_twice_and_output_is_free()
    {
        var h = new DeciderHarness();
        h.Engine.DefaultUsage = new Usage { Model = "dec-1", InputTokens = 300, CacheReadInputTokens = 600, CacheCreationInputTokens = 100, OutputTokens = 50 };
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(Triage, "triage"), DeciderHarness.Json("""{"subject":"s","customer":{}}"""));
        // 300 uncached x 1.00 + 600 read x 0.10 + 100 written x 1.25 per million; output is not charged.
        Assert.Equal(0.0003m + 0.00006m + 0.000125m, r.Cost.Total);
    }

    [Fact]
    public void Definition_load_rejects_bad_questions_with_a_position()
    {
        var e = Assert.Throws<BadDefinitionException>(() => Decider.FromYaml("model: m\nstate: {}\nquestions:\n  q:\n    type: choice\n    instructions: x\n    criteria: { only: one }\n", "bad"));
        Assert.Contains("criteria", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<BadDefinitionException>(() => Decider.FromYaml("model: m\nquestions:\n  q:\n    type: nope\n    instructions: x\n", "bad"));
        Assert.Throws<BadDefinitionException>(() => Decider.FromYaml("model: m\nstate: {}\nquestions:\n  q:\n    type: noul\n    instructions: x\n    forEach: plain\n", "bad"));
    }

    [Fact]
    public void Unknown_fields_survive_a_round_trip()
    {
        Decider d = Decider.FromYaml("model: m\nsubId: abc\nstate: {}\nquestions:\n  q:\n    type: noul\n    instructions: x\n    extra: 1\n", "x");
        Assert.NotNull(d.Definition.ExtensionData);
        Assert.True(d.Definition.ExtensionData!.ContainsKey("subId"));
    }

    [Fact]
    public async Task Images_in_the_inputs_reach_a_jev_style_engine_as_a_capability_error()
    {
        var jev = new JevProvider(new JevOptions { BaseUrl = new Uri("https://stub.invalid/") }, TimeProvider.System);
        var request = new DecisionRequest
        {
            Model = "jev-latest",
            State = "{}"u8.ToArray(),
            Questions = [new WireQuestion("q", "noul", "{}"u8.ToArray())],
            Images = [new DecisionImage("img", new byte[] { 1 }, "image/png")],
        };
        await Assert.ThrowsAsync<CapabilityMissingException>(() => jev.DecideAsync(request));
    }

    [Fact]
    public async Task Inputs_are_checked_against_the_schema_beside_the_decider()
    {
        var h = new DeciderHarness();
        JsonElement schema = DeciderHarness.Json("""
            {"type":"object","required":["subject"],"additionalProperties":false,
             "properties":{"subject":{"type":"string","enum":["open","closed"]},"customer":{"anyOf":[{"type":"null"},{"type":"object"}]},"n":{"type":"integer","minimum":1,"maximum":3},"tags":{"type":"array","items":{"type":"string"}}}}
            """);
        Decider d = Decider.FromYaml(Triage, "triage").WithInputSchema(schema);
        await h.Runtime.DecideAsync(d, DeciderHarness.Json("""{"subject":"open","customer":null,"n":2,"tags":["a"]}"""));
        foreach ((string json, string needle) in new[]
        {
            ("""{"customer":{}}""", "missing the required field 'subject'"),
            ("""{"subject":"pending","customer":{}}""", "not one of"),
            ("""{"subject":"open","customer":3}""", "none of the allowed forms"),
            ("""{"subject":"open","customer":{},"n":4}""", "above the maximum"),
            ("""{"subject":"open","customer":{},"tags":[1]}""", "inputs.tags[0] should be string"),
            ("""{"subject":"open","customer":{},"x":1}""", "does not allow"),
        })
        {
            var e = await Assert.ThrowsAsync<DeciderInputException>(() => h.Runtime.DecideAsync(d, DeciderHarness.Json(json)));
            Assert.Contains(needle, e.Message);
        }
    }

    // ---- OpenAI Decisions wire (written from the create-decision reference) ----

    private static DecisionRequest WireRequest() => new()
    {
        Model = "gpt-6-luna",
        State = "{\"ticket\":\"t\"}"u8.ToArray(),
        Questions =
        [
            new WireQuestion("topic", "choice", """{"type":"choice","instructions":"Which?","criteria":{"billing":"Charges","orders":"Delivery"}}"""u8.ToArray()),
            new WireQuestion("urgency", "score", """{"type":"score","instructions":"How?","criteria":["Low","High"]}"""u8.ToArray()),
            new WireQuestion("angry", "noul", """{"type":"noul","instructions":"Angry?","clarifications":{"true":"shouting"}}"""u8.ToArray()),
        ],
        PromptCacheKey = "triage",
        ExplicitCacheBreakpoint = true,
    };

    [Fact]
    public void Decisions_request_maps_each_question_type_state_and_cache_options()
    {
        JsonObject body = JsonNode.Parse(OpenAIDecisions.WriteBody(WireRequest()).Span)!.AsObject();
        Assert.Equal("gpt-6-luna", (string?)body["model"]);
        Assert.Equal("triage", (string?)body["prompt_cache_key"]);
        Assert.Equal("explicit", (string?)body["prompt_cache_options"]!["mode"]);
        JsonArray content = body["input"]![0]!["content"]!.AsArray();
        Assert.Equal("input_json", (string?)content[0]!["type"]);
        Assert.Equal("t", (string?)content[0]!["data"]!["ticket"]);
        Assert.Equal("explicit", (string?)content[^1]!["prompt_cache_breakpoint"]!["mode"]);
        JsonArray q = body["questions"]!.AsArray();
        Assert.Equal("choice", (string?)q[0]!["type"]);
        Assert.Equal("billing", (string?)q[0]!["choices"]![0]!["value"]);
        Assert.Equal("Charges", (string?)q[0]!["choices"]![0]!["description"]);
        Assert.Equal("score", (string?)q[1]!["type"]);
        Assert.Equal("Low", (string?)q[1]!["levels"]![0]!["label"]);
        Assert.Equal("predicate", (string?)q[2]!["type"]);
    }

    [Fact]
    public void Decisions_response_is_read_by_position_and_usage_is_split()
    {
        const string response = """
            {"model":"gpt-6-luna","answers":[
              {"type":"choice","choice":"orders","confidence":0.8,"probabilities":[{"value":"billing","probability":0.1},{"value":"orders","probability":0.9}]},
              {"type":"score","score":1,"confidence":0.7,"probabilities":[{"label":"Low","probability":0.2,"value":0},{"label":"High","probability":0.8,"value":1}]},
              {"type":"predicate","probability":0.25}],
             "usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":600,"cache_write_tokens":100},"output_tokens":40,"total_tokens":1040}}
            """;
        DecisionResponse r = OpenAIDecisions.Parse(System.Text.Encoding.UTF8.GetBytes(response), WireRequest());
        Assert.Equal("orders", (string?)r.Answers["topic"]!["choice"]);
        Assert.Equal(0.25, (double)r.Answers["angry"]!["noul"]!);
        Assert.Equal(300, r.Usage!.InputTokens);
        Assert.Equal(600, r.Usage.CacheReadInputTokens);
        Assert.Equal(100, r.Usage.CacheCreationInputTokens);
    }

    [Fact]
    public void A_refusal_answer_is_kept_and_the_run_names_the_question()
    {
        const string response = """{"answers":[{"type":"refusal"},{"type":"refusal"},{"type":"refusal"}]}""";
        DecisionResponse r = OpenAIDecisions.Parse(System.Text.Encoding.UTF8.GetBytes(response), WireRequest());
        Assert.Equal("refusal", (string?)r.Answers["topic"]!["type"]);
    }

    [Fact]
    public async Task A_refused_question_is_a_typed_error()
    {
        var h = new DeciderHarness();
        h.Engine.Responder = req => new DecisionResponse { Answers = new JsonObject { ["topic"] = new JsonObject { ["type"] = "refusal" }, ["urgency"] = new JsonObject { ["type"] = "score" }, ["angry"] = new JsonObject { ["type"] = "noul" } } };
        var e = await Assert.ThrowsAsync<RefusedException>(() => h.Runtime.DecideAsync(Decider.FromYaml(Triage, "triage"), DeciderHarness.Json("""{"subject":"s","customer":{}}""")));
        Assert.Contains("topic", e.Message);
    }

    // ---- chat engine ----

    [Fact]
    public async Task The_chat_engine_asks_each_question_alone_over_a_shared_state_prefix()
    {
        var h = new DeciderHarness();
        h.Chat.Fallback = req =>
        {
            string prompt = string.Concat(req.Messages.SelectMany(static m => m.User).OfType<TextPart>().Select(static p => p.Text));
            string all = req.System + prompt;
            if (all.Contains("Is the customer angry?", StringComparison.Ordinal))
            {
                return ScriptStep.Text("{\"p_yes\": 0.9}");
            }

            return all.Contains("How urgent?", StringComparison.Ordinal)
                ? ScriptStep.Text("{\"probabilities\": {\"0\": 0.25, \"1\": 0.75}}")
                : ScriptStep.Text("{\"probabilities\": {\"billing\": 0.8, \"orders\": 0.2}}");
        };
        Decider d = Decider.FromYaml(Triage.Replace("dec-1", "chat-1", StringComparison.Ordinal), "triage");
        DecisionResult r = await h.Runtime.DecideAsync(d, DeciderHarness.Json("""{"subject":"s","customer":{}}"""));
        Assert.Equal("llm", r.Engine);
        Assert.Equal(3, h.Chat.Requests.Count);
        Assert.All(h.Chat.Requests, q => Assert.Contains("# STATE", q.System));
        Assert.Single(h.Chat.Requests.Select(static q => q.System).Distinct());
        Assert.Equal("billing", r.Answers.GetProperty("topic").GetProperty("choice").GetString());
        Assert.Equal(0.9, r.Answers.GetProperty("angry").GetProperty("noul").GetDouble(), 4);
        Assert.Equal(0.75, r.Answers.GetProperty("urgency").GetProperty("score").GetDouble(), 4);
    }

    [Fact]
    public async Task The_chat_engine_averages_samples_and_retries_an_unparseable_reply()
    {
        var h = new DeciderHarness();
        int calls = 0;
        h.Chat.Fallback = req => ++calls switch
        {
            1 => ScriptStep.Text("I think yes"),
            2 => ScriptStep.Text("{\"p_yes\": 0.2}"),
            _ => ScriptStep.Text("{\"p_yes\": 0.6}"),
        };
        const string yaml = "model: chat-1\nllm: { samples: 2 }\nstate: {}\nquestions:\n  q:\n    type: noul\n    instructions: Yes?\n";
        DecisionResult r = await h.Runtime.DecideAsync(Decider.FromYaml(yaml, "s"), DeciderHarness.Json("{}"));
        Assert.Equal(0.4, r.Answers.GetProperty("q").GetProperty("noul").GetDouble(), 4);
        Assert.Equal(3, h.Chat.Requests.Count);
    }

    // ---- run APIs ----

    [Fact]
    public async Task Runs_are_listed_replayed_and_counted_and_eval_runs_stay_out_of_stats()
    {
        var h = new DeciderHarness();
        h.Engine.DefaultUsage = new Usage { Model = "dec-1", InputTokens = 1000, OutputTokens = 1 };
        Decider d = Decider.FromYaml(Triage, "triage");
        JsonElement inputs = DeciderHarness.Json("""{"subject":"s","customer":{}}""");
        DecisionResult a = await h.Runtime.DecideAsync(d, inputs);
        await h.Runtime.DecideAsync(d, inputs, "eval");
        DecisionResult again = await h.Runtime.ReplayDecisionAsync(a.Instance.InstanceId, d);
        var runs = new List<DecisionRun>();
        await foreach (DecisionRun run in h.Runtime.ListDecisionsAsync("triage"))
        {
            runs.Add(run);
        }

        Assert.Equal(3, runs.Count);
        Assert.Equal(a.Instance.InstanceId, runs.Single(static r => r.Origin == "replay").ReplayOf);
        DecisionStats stats = await h.Runtime.DecisionStatsAsync("triage", ["eval", "playground"]);
        Assert.Equal(2, stats.Runs);
        Assert.Equal(0.001m * 2, stats.TotalCost);
        Assert.NotEqual(again.Instance.InstanceId, a.Instance.InstanceId);
    }
}
