using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Templating;
using OneLlm.Testing;

namespace OneLlm.Tests.Running;

[Collection("llm-default")]
public class ScriptedRunnerTests
{
    private const string Header = "model: demo\n";

    private static AgentDefinition Define(string yaml) => AgentDefinition.Parse(Header + yaml, "test.flow", DefinitionLoadOptions.Lenient);

    [Fact]
    public async Task Prompts_run_in_order_one_inference_each_and_later_prompts_read_earlier_turns()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.TextWithUsage("""{"k":"v1"}""", 100, 10), ScriptStep.TextWithUsage("done", 200, 20));
        AgentDefinition def = Define("""
            systemPrompt: "You help {{ .inputs.name }}. Mode: {{ .invocationMode }}."
            initialPrompts:
              - user:
                  - text: "Hello {{ .inputs.name }}"
              - user:
                  - text: "Earlier: {{ (index .turns 0).text }} / {{ (index .turns 0).json.k }}"
            """);

        ScriptResult r = await h.Runtime.RunScriptAsync(def, new Dictionary<string, object?> { ["name"] = "Ada" });

        Assert.Equal(2, h.Provider.Requests.Count);
        Assert.Equal("You help Ada. Mode: autonomous.", h.Provider.Requests[0].System);
        Assert.Equal("Hello Ada", h.Provider.Requests[0].Messages[0].GetUserText());
        // The second request carries the first exchange, then the prompt rendered from it.
        ChatRequest second = h.Provider.Requests[1];
        Assert.Equal(3, second.Messages.Count);
        Assert.Equal("""{"k":"v1"}""", second.Messages[1].GetAssistantText());
        Assert.Equal("""Earlier: {"k":"v1"} / v1""", second.Messages[2].GetUserText());

        Assert.Equal("done", r.Text);
        Assert.Equal(2, r.Turns.Count);
        // (100 + 200) input at $1/M plus (10 + 20) output at $2/M.
        Assert.Equal(0.00036m, r.Cost.Total);
        Assert.Equal(300, r.Usage!.InputTokens);
        Assert.Equal("You help Ada. Mode: autonomous.", r.Instance.RenderedSystemPrompt);

        Instance saved = (await h.Runtime.Store.LoadAsync(r.Instance.InstanceId))!;
        Assert.Equal("You help Ada. Mode: autonomous.", saved.RenderedSystemPrompt);
        Assert.Equal(4, saved.Messages.Count);
        Assert.Equal(InstanceStates.Stopped, saved.State);
        Assert.Equal("Ada", saved.Inputs!["name"]);
        Assert.Equal(["run", "message", "inference", "message", "message", "inference", "message", "run"], h.ReadRecords(saved.InstanceId).Select(e => e.Kind).ToList());
    }

    [Fact]
    public async Task Definition_input_defaults_are_overridden_by_the_caller()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("ok");
        AgentDefinition def = Define("""
            inputs: { who: default-who, n: 3 }
            initialPrompts:
              - user:
                  - text: "{{ .inputs.who }} {{ .inputs.n }}"
            """);

        await h.Runtime.RunScriptAsync(def, new Dictionary<string, object?> { ["who"] = "caller" });

        Assert.Equal("caller 3", h.Provider.Requests[0].Messages[0].GetUserText());
    }

    [Fact]
    public async Task Missing_keys_print_no_value_by_default_and_fail_when_the_definition_says_error()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("ok");
        AgentDefinition lenient = Define("initialPrompts:\n  - user:\n      - text: \"[{{ .inputs.absent }}]\"\n");
        await h.Runtime.RunScriptAsync(lenient);
        Assert.Equal("[<no value>]", h.Provider.Requests[0].Messages[0].GetUserText());

        AgentDefinition strict = Define("missingKey: error\ninitialPrompts:\n  - user:\n      - text: \"[{{ .inputs.absent }}]\"\n");
        TemplateException e = await Assert.ThrowsAsync<TemplateException>(() => h.Runtime.RunScriptAsync(strict));
        Assert.Contains("absent", e.Message, StringComparison.Ordinal);
        Assert.Single(h.Provider.Requests);
        // The failed run is kept, in error, for inspection.
        Assert.Contains(await h.Runtime.Store.ListAsync().ToListAsync2(), s => s.State == InstanceStates.Error);
    }

    [Fact]
    public async Task Env_is_empty_unless_the_host_allow_lists_names()
    {
        using var h = new Harness();
        h.Environment["VISIBLE"] = "yes";
        h.Environment["SECRET_TOKEN"] = "hunter2";
        h.Provider.EnqueueText("a").EnqueueText("b");
        AgentDefinition def = Define("initialPrompts:\n  - user:\n      - text: \"{{ .env.VISIBLE }}|{{ .env.SECRET_TOKEN }}\"\n");

        await h.Runtime.RunScriptAsync(def);
        Assert.Equal("<no value>|<no value>", h.Provider.Requests[0].Messages[0].GetUserText());

        h.Runtime.AllowedEnvironment.Add("VISIBLE");
        await h.Runtime.RunScriptAsync(def);
        Assert.Equal("yes|<no value>", h.Provider.Requests[1].Messages[0].GetUserText());
    }

    [Fact]
    public async Task A_false_when_skips_the_prompt_and_tool_references_are_ignored_with_warnings()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("only");
        AgentDefinition def = Define("""
            references:
              - swarm: /tools/web
            initialPrompts:
              - when: "{{ if .inputs.go }}yes{{ end }}"
                user:
                  - text: skipped
              - user:
                  - text: kept
            """);

        ScriptResult r = await h.Runtime.RunScriptAsync(def);

        Assert.Single(h.Provider.Requests);
        Assert.Equal("kept", h.Provider.Requests[0].Messages[0].GetUserText());
        Assert.Contains(r.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("tool references", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_definition_with_an_output_schema_asks_for_it_on_the_last_prompt_and_exposes_the_json()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("first").EnqueueText("""{"customer":"Ada","total":3}""");
        AgentDefinition def = Define("""
            outputSchema:
              type: object
              properties: { customer: { type: string }, total: { type: number } }
              required: [customer, total]
            initialPrompts:
              - user: [ { text: one } ]
              - user: [ { text: two } ]
            """);

        ScriptResult r = await h.Runtime.RunScriptAsync(def);

        Assert.Null(h.Provider.Requests[0].ResponseFormat);
        Assert.Equal(ResponseFormatKind.JsonSchema, h.Provider.Requests[1].ResponseFormat!.Kind);
        Assert.Equal("Ada", r.Output!.Value.GetProperty("customer").GetString());
        Invoice typed = r.GetOutput(RunningJson.Default.Invoice);
        Assert.Equal(3m, typed.Total);
    }

    [Fact]
    public async Task A_definition_without_a_model_is_a_bad_definition()
    {
        using var h = new Harness();
        AgentDefinition def = AgentDefinition.Parse("initialPrompts:\n  - user: [ { text: hi } ]\n", "nomodel.flow");
        BadDefinitionException e = await Assert.ThrowsAsync<BadDefinitionException>(() => h.Runtime.RunScriptAsync(def));
        Assert.Contains("nomodel.flow", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_mid_script_keeps_the_finished_turns()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Provider.EnqueueText("first");
        h.Provider.Enqueue(new ScriptStep { Chunks = ["x"], BeforeChunk = async (_, ct) => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); } });
        AgentDefinition def = Define("initialPrompts:\n  - user: [ { text: one } ]\n  - user: [ { text: two } ]\n");

        RunCancelledException e = await Assert.ThrowsAsync<RunCancelledException>(() => h.Runtime.RunScriptAsync(def, null, cts.Token));

        Assert.Equal(InstanceStates.Stopped, e.Instance.State);
        Assert.Equal(["one", "first", "two"], e.Instance.Messages.Select(m => m.GetUserText() + m.GetAssistantText()).ToArray());
    }

    [Fact]
    public async Task Embed_reads_files_beside_the_definition_but_not_outside_it()
    {
        using var h = new Harness();
        string dir = Path.Combine(h.Directory, "flows");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "part.md"), "shared {{ .inputs.x }}");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.flow"), "model: demo\ninitialPrompts:\n  - user:\n      - text: '{{ embed \"part.md\" }}'\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "b.flow"), "model: demo\ninitialPrompts:\n  - user:\n      - text: '{{ embed \"../secret.md\" }}'\n");
        await File.WriteAllTextAsync(Path.Combine(h.Directory, "secret.md"), "outside");
        h.Provider.EnqueueText("ok");

        await h.Runtime.RunScriptAsync(Path.Combine(dir, "a.flow"), new Dictionary<string, object?> { ["x"] = 1 });
        Assert.Equal("shared 1", h.Provider.Requests[0].Messages[0].GetUserText());

        await Assert.ThrowsAsync<TemplateException>(() => h.Runtime.RunScriptAsync(Path.Combine(dir, "b.flow")));
    }

    [Fact]
    public async Task Inputs_of_unsupported_types_are_refused_with_a_hint()
    {
        using var h = new Harness();
        AgentDefinition def = Define("initialPrompts:\n  - user: [ { text: hi } ]\n");
        ArgumentException e = await Assert.ThrowsAsync<ArgumentException>(() => h.Runtime.RunScriptAsync(def, new Dictionary<string, object?> { ["x"] = new Invoice("a", 1m) }));
        Assert.Contains("JsonTypeInfo", e.Message, StringComparison.Ordinal);
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync2<T>(this IAsyncEnumerable<T> items)
    {
        var list = new List<T>();
        await foreach (T item in items)
        {
            list.Add(item);
        }

        return list;
    }
}

[CollectionDefinition("llm-default", DisableParallelization = true)]
public sealed class LlmDefaultFixture;

[Collection("llm-default")]
public class LlmStaticsTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly Runtime _previous = Llm.Default;

    public LlmStaticsTests() => Llm.Default = _h.Runtime;

    public void Dispose()
    {
        Llm.Default = _previous;
        _h.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Ask_returns_the_text_and_generate_returns_the_result()
    {
        _h.Provider.EnqueueText("plain").Enqueue(ScriptStep.TextWithUsage("rich", 1000, 0));

        Assert.Equal("plain", await Llm.AskAsync("demo", "hi"));
        AskResult r = await Llm.GenerateAsync("demo", "hi", []);

        Assert.Equal("rich", r.Text);
        Assert.Equal(0.001m, r.Cost.Total);
    }

    [Fact]
    public async Task Ask_with_an_asset_sends_it_as_a_part_after_the_text()
    {
        _h.Provider.EnqueueText("a pixel");
        AssetPart png = Asset.FromBytes([1, 2, 3], "image/png", "px.png");

        await Llm.AskAsync("demo", "what is this?", png);

        IReadOnlyList<Part> user = _h.Provider.Requests[0].Messages[0].User;
        Assert.Equal(2, user.Count);
        Assert.IsType<TextPart>(user[0]);
        AssetRef asset = Assert.IsType<AssetPart>(user[1]).Asset!;
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), asset.Data);
    }

    [Fact]
    public async Task Typed_ask_and_stream_and_embed_work_through_the_default_runtime()
    {
        _h.Provider.EnqueueText("""{"customer":"Bo","total":1}""").Enqueue(ScriptStep.Text("x", "y"));

        Invoice inv = await Llm.AskAsync("demo", "extract", RunningJson.Default.Invoice);
        var pieces = new List<string>();
        await foreach (string p in Llm.StreamTextAsync("demo", "go"))
        {
            pieces.Add(p);
        }

        float[] v = await Llm.EmbedAsync("scripted:embed-1", "abc");
        float[][] many = await Llm.EmbedAsync("scripted:embed-1", ["a", "bb"]);

        Assert.Equal("Bo", inv.Customer);
        Assert.Equal(["x", "y"], pieces);
        Assert.Equal([3f, 0f], v);
        Assert.Equal(2, many.Length);
    }

    [Fact]
    public async Task Chat_and_run_script_work_through_the_default_runtime()
    {
        _h.Provider.EnqueueText("A1").EnqueueText("A2").EnqueueText("flow done");
        using ChatSession chat = Llm.Chat("demo", "sys");
        await chat.AskAsync("ask1");
        Assert.Equal("A2", await chat.AskAsync("ask2"));

        string flow = Path.Combine(_h.Directory, "hello.flow");
        await File.WriteAllTextAsync(flow, "model: demo\ninitialPrompts:\n  - user:\n      - text: 'Hi {{ .inputs.who }}'\n");
        ScriptResult r = await Llm.RunScriptAsync(flow, new Dictionary<string, object?> { ["who"] = "Ada" });

        Assert.Equal("flow done", r.Text);
        Assert.Equal("Hi Ada", _h.Provider.Requests[2].Messages[0].GetUserText());
    }

    [Fact]
    public async Task Typed_inputs_go_through_the_source_generated_path()
    {
        _h.Provider.EnqueueText("ok");
        string flow = Path.Combine(_h.Directory, "inv.flow");
        await File.WriteAllTextAsync(flow, "model: demo\ninitialPrompts:\n  - user:\n      - text: '{{ .inputs.customer }} owes {{ .inputs.total }}'\n");

        await Llm.RunScriptAsync(flow, new Invoice("Ada", 12.5m), RunningJson.Default.Invoice);

        Assert.Equal("Ada owes 12.5", _h.Provider.Requests[0].Messages[0].GetUserText());
    }
}
