using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Deciders;
using OneLlm.Hooks;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Testing;
using OneLlm.Tests.Support;
using OneLlm.Tools;

namespace OneLlm.JavaScript.Tests;

internal sealed class CountingTool(string name, string text) : ITool
{
    public ToolSpec Definition { get; } = new(name, "test tool", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""")!);

    public int Calls { get; private set; }

    public JsonObject? LastInputs { get; private set; }

    public ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        Calls++;
        LastInputs = inputs;
        return ValueTask.FromResult(ToolResult.FromText(text));
    }
}

internal sealed class HookEnv : IDisposable
{
    private const string CatalogYaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        aliases: { demo: demo-1 }
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
        """;

    private int _ids;

    public HookEnv()
    {
        Dir = Path.Combine(Path.GetTempPath(), "onellm-jshook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Provider = new ScriptedProvider();
        Rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]),
            IdSource = () => "i" + (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
            DefinitionHooks = new JsHookSource(),
        };
        // Hook files a definition names are read through the read grants, so the folder that holds them is a granted root.
        Rt.Capabilities = Capabilities.Files(Dir);
        Rt.Providers.Set("scripted", Provider);
        Records = new JsonlRecordSink(Path.Combine(Dir, "records"));
        Rt.Sinks.Add(Records);
    }

    public string Dir { get; }

    public ScriptedProvider Provider { get; }

    public Runtime Rt { get; }

    public JsonlRecordSink Records { get; }

    public string Hook(string name, string source)
    {
        string p = Path.Combine(Dir, name);
        File.WriteAllText(p, source);
        return p;
    }

    /// <summary>An agent definition in this environment's folder whose hooks are the given files.</summary>
    public AgentDefinition Def(string[] tools, params string[] hookFiles)
    {
        string hooks = hookFiles.Length == 0 ? "" : "hooks:\n" + string.Concat(hookFiles.Select(f => $"  - file: {f}\n"));
        string refs = tools.Length == 0 ? "references: []\n" : $"references:\n  - tools: [{string.Join(", ", tools)}]\n";
        return AgentDefinition.Parse($"name: a\nmodel: demo\nsystemPrompt: You are an agent.\n{hooks}{refs}", Path.Combine(Dir, "a.yaml"));
    }

    public List<string> RecordKinds(AgentResult r) => [.. RecordReader.ReadFile(Records.PathOf(r.Instance.InstanceId)).Select(static e => e.Kind)];

    public static ScriptStep Say(string text) => ScriptStep.TextWithUsage(text, 1000, 100);

    public static ScriptStep Calls(string id, string tool, string json) => new()
    {
        ToolCalls = [new ScriptedToolCall(id, tool, json)],
        Usage = new Usage { InputTokens = 1000, OutputTokens = 100 },
    };

    public static Task<AgentResult> Run(HookEnv env, AgentDefinition def) =>
        env.Rt.RunAgentAsync(def, null, new AgentStartOptions { Prompt = "go" }).WaitAsync(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static string ToolText(ToolCall c) => string.Concat(c.Result.OfType<TextPart>().Select(p => p.Text));
}

public class JsHookTests
{
    [Fact]
    public async Task A_js_interceptor_can_block_a_tool_call_and_the_block_is_recorded()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Hook("gate.js", "export function beforeToolCall(s) { return s.call.tool === 't' ? { action: 'block', reason: 'not today' } : undefined; }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));

        AgentResult r = await HookEnv.Run(env, env.Def(["t"], "gate.js"));

        Assert.Equal(0, tool.Calls);
        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("not today", HookEnv.ToolText(call), StringComparison.Ordinal);
        Assert.Contains("hook", env.RecordKinds(r), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_js_hook_can_modify_inputs_require_approval_and_respond()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "real");
        env.Rt.Tools.Add(tool);
        env.Hook("rewrite.js", "export function beforeToolCall(s) { return { action: 'modify', inputs: { x: s.inputs.x.toUpperCase() }, reason: 'shout' }; }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", """{"x":"quiet"}"""), HookEnv.Say("ok"));
        await HookEnv.Run(env, env.Def(["t"], "rewrite.js"));
        Assert.Equal("QUIET", tool.LastInputs!["x"]!.GetValue<string>());

        using var env2 = new HookEnv();
        var tool2 = new CountingTool("t", "real");
        env2.Rt.Tools.Add(tool2);
        env2.Hook("cache.js", "export function beforeToolCall() { return { action: 'respond', text: 'cached' }; }");
        env2.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult r2 = await HookEnv.Run(env2, env2.Def(["t"], "cache.js"));
        Assert.Equal(0, tool2.Calls);
        Assert.Equal("cached", HookEnv.ToolText(r2.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0]));

        using var env3 = new HookEnv();
        var tool3 = new CountingTool("t", "real");
        env3.Rt.Tools.Add(tool3);
        env3.Hook("ask.js", "export function beforeToolCall() { return { action: 'requireApproval', reason: 'policy says ask' }; }");
        env3.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult held = await HookEnv.Run(env3, env3.Def(["t"], "ask.js"));
        Assert.Equal(AgentStopReason.WaitingForApproval, held.StopReason);
        Assert.Equal(0, tool3.Calls);
    }

    [Fact]
    public async Task A_js_hook_can_change_the_request_and_the_answer()
    {
        using var env = new HookEnv();
        env.Hook("style.js", """
            export function beforeInference(s) { return { action: 'modify', system: 'Be terse.', maxOutputTokens: 77 }; }
            export function afterInference(s) { return { action: 'modify', text: s.message.assistant[0].text + '!' }; }
            """);
        env.Provider.Enqueue(HookEnv.Say("hello"));
        AgentResult r = await HookEnv.Run(env, env.Def([], "style.js"));
        Assert.Equal("Be terse.", env.Provider.Requests[0].System);
        Assert.Equal(77, env.Provider.Requests[0].MaxOutputTokens);
        Assert.Equal("hello!", r.Text);
    }

    [Fact]
    public async Task An_interceptor_that_throws_blocks_the_step()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Hook("buggy.js", "export function beforeToolCall(s) { return s.nothing.deeper; }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));

        AgentResult r = await HookEnv.Run(env, env.Def(["t"], "buggy.js"));

        Assert.Equal(0, tool.Calls);
        ToolCall call = r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0];
        Assert.True(call.IsError);
        Assert.Contains("buggy.js", HookEnv.ToolText(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decision_the_point_does_not_take_blocks_like_a_failure()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Hook("odd.js", "export function beforeToolCall() { return { action: 'frobnicate' }; }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult r = await HookEnv.Run(env, env.Def(["t"], "odd.js"));
        Assert.Equal(0, tool.Calls);
        Assert.True(r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0].IsError);
    }

    [Fact]
    public async Task An_observer_that_throws_does_not_stop_the_run()
    {
        using var env = new HookEnv();
        env.Hook("watch.js", "export function onMessage(s) { throw new Error('observer bug'); }");
        env.Provider.Enqueue(HookEnv.Say("fine"));

        AgentResult r = await HookEnv.Run(env, env.Def([], "watch.js"));

        Assert.Equal("fine", r.Text);
        Assert.Contains("error", env.RecordKinds(r), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_runaway_hook_is_stopped_and_fails_closed()
    {
        using var env = new HookEnv();
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Rt.DefinitionHooks = new JsHookSource(new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(300) });
        env.Hook("spin.js", "export function beforeToolCall() { for (;;) {} }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult r = await HookEnv.Run(env, env.Def(["t"], "spin.js"));
        Assert.Equal(0, tool.Calls);
        Assert.True(r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0].IsError);
    }

    [Fact]
    public async Task A_hook_has_no_file_access_unless_it_declares_and_is_granted_it()
    {
        using var env = new HookEnv();
        File.WriteAllText(Path.Combine(env.Dir, "policy.txt"), "deny-t");
        var tool = new CountingTool("t", "ran");
        env.Rt.Tools.Add(tool);
        env.Hook("nodeclare.js", "export async function beforeToolCall() { await ctx.readFile('policy.txt'); }");
        env.Provider.Enqueue(HookEnv.Calls("c1", "t", "{}"), HookEnv.Say("ok"));
        AgentResult r = await HookEnv.Run(env, env.Def(["t"], "nodeclare.js"));
        Assert.Equal(0, tool.Calls);
        Assert.Contains("nodeclare.js", HookEnv.ToolText(r.Instance.Messages.Single(m => m.ToolCalls.Count == 1).ToolCalls[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_files_are_validated_when_the_definition_loads()
    {
        using var env = new HookEnv();
        env.Hook("none.js", "export const x = 1;");
        env.Hook("some.js", "export function onCost() {}");
        var source = new JsHookSource();
        Capabilities granted = Capabilities.Files(env.Dir);
        Assert.Contains("at least one", Assert.Throws<BadDefinitionException>(() => source.Load(env.Def([], "none.js"), granted)).Message, StringComparison.Ordinal);
        Assert.Contains("does not export", Assert.Throws<BadDefinitionException>(() => source.LoadFile(Path.Combine(env.Dir, "some.js"), Hooks.HookPoints.BeforeToolCall, Capabilities.None)).Message, StringComparison.Ordinal);
        Assert.Throws<BadDefinitionException>(() => source.Load(env.Def([], "missing.js"), granted));
        IHook ok = source.Load(env.Def([], "some.js"), granted).Single();
        Assert.Equal(Hooks.HookPoints.OnCost, ok.Points);
    }
}

public class JsDeciderLogicTests
{
    private const string Logic = """
        export const prepare = (i) => ({ ticket: { message: i.ticket.message }, n: i.items?.length ?? 0 });
        export const decide = ({ answers, inputs }) => ({ route: answers.topic.choice, n: inputs.n, ok: answers.flag.noul > 0.5 });
        """;

    [Fact]
    public async Task Prepare_and_decide_run_the_module_and_return_json()
    {
        using var dir = new Scratch();
        string path = dir.Write("decider.js", Logic);
        JsDeciderLogic logic = JsDeciderLogic.Load(path);
        Assert.True(logic.HasPrepare);
        Assert.True(logic.HasDecide);
        JsonElement prepared = await logic.PrepareAsync(JsonDocument.Parse("""{"ticket":{"message":"hi","extra":1},"items":[1,2]}""").RootElement, default);
        Assert.Equal("""{"ticket":{"message":"hi"},"n":2}""", prepared.GetRawText());
        JsonElement outputs = await logic.DecideAsync(JsonDocument.Parse("""{"topic":{"choice":"billing"},"flag":{"noul":0.9}}""").RootElement, prepared, default);
        Assert.Equal("""{"route":"billing","n":2,"ok":true}""", outputs.GetRawText());
    }

    [Fact]
    public async Task A_missing_step_is_reported_and_undefined_is_null()
    {
        using var dir = new Scratch();
        JsDeciderLogic only = JsDeciderLogic.Load(dir.Write("a.js", "export const decide = () => undefined;"));
        Assert.False(only.HasPrepare);
        Assert.Equal(JsonValueKind.Null, (await only.DecideAsync(default, default, default)).ValueKind);
        Assert.Contains("prepare", Assert.Throws<BadDefinitionException>(() => JsDeciderLogic.Load(dir.Write("b.js", "export const other = 1;"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logic_that_throws_or_loops_is_an_error_not_a_hang()
    {
        using var dir = new Scratch();
        JsDeciderLogic bad = JsDeciderLogic.Load(dir.Write("c.js", "export const decide = ({ answers }) => answers.missing.score;"));
        var e = await Assert.ThrowsAsync<JsSandboxException>(async () => await bad.DecideAsync(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement, default));
        Assert.Equal(JsSandboxFailure.Script, e.Kind);
        JsDeciderLogic spin = JsDeciderLogic.Load(dir.Write("d.js", "export const prepare = () => { for (;;) {} };"), new JsSandboxOptions { TimeLimit = TimeSpan.FromMilliseconds(300) });
        await Assert.ThrowsAsync<JsSandboxException>(async () => await spin.PrepareAsync(JsonDocument.Parse("{}").RootElement, default));
    }

    private const string Ts = """
        import type { Answers } from './types';
        interface Prepared { n: number }
        type Route = 'a' | 'b';
        export const prepare = (i: { items?: unknown[] }): Prepared => ({ n: i.items?.length ?? 0 });
        export const decide = ({ answers, inputs }: { answers: any; inputs: Prepared }) => ({ route: answers.topic.choice as Route, n: inputs.n } satisfies { route: Route; n: number });
        """;

    [Fact]
    public async Task A_ts_decider_runs_with_its_types_erased()
    {
        using var dir = new Scratch();
        JsDeciderLogic logic = JsDeciderLogic.Load(dir.Write("decider.ts", Ts));
        Assert.True(logic.HasPrepare);
        JsonElement prepared = await logic.PrepareAsync(JsonDocument.Parse("""{"items":[1,2,3]}""").RootElement, default);
        JsonElement outputs = await logic.DecideAsync(JsonDocument.Parse("""{"topic":{"choice":"a"}}""").RootElement, prepared, default);
        Assert.Equal("""{"route":"a","n":3}""", outputs.GetRawText());
    }

    [Theory]
    [InlineData("export const decide = () => 1;\nenum Color { Red }\n", "enum", 2, 1)]
    [InlineData("export class A {\n  constructor(private x: number) {}\n}\nexport const decide = () => 1;\n", "parameter propert", 2, 15)]
    public void A_ts_construct_that_cannot_be_erased_is_refused_with_its_position(string code, string word, int line, int column)
    {
        using var dir = new Scratch();
        var e = Assert.Throws<BadDefinitionException>(() => JsDeciderLogic.Load(dir.Write("decider.ts", code)));
        Assert.Contains(word, e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(line, e.Line);
        Assert.Equal(column, e.Column);
    }

    [Fact]
    public async Task A_runtime_error_in_a_ts_decider_points_into_the_ts_file()
    {
        using var dir = new Scratch();
        JsDeciderLogic logic = JsDeciderLogic.Load(dir.Write("decider.ts", "type T = { a: number };\nexport const decide = (x: { answers: any }): number => x.answers.missing.score;\n"));
        var e = await Assert.ThrowsAsync<JsSandboxException>(async () => await logic.DecideAsync(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement, default));
        Assert.Equal(2, e.Line);
        Assert.Equal("decider.ts", e.File);
    }

    [Fact]
    public void A_decider_directory_loads_its_js_or_ts_logic()
    {
        using var dir = new Scratch();
        const string yaml = "model: jev-latest\nstate: {}\nquestions:\n  q:\n    type: noul\n    instructions: Is it?\n";
        dir.Write("withjs/decider.yaml", yaml);
        dir.Write("withjs/decider.js", Logic);
        Assert.NotNull(JsDeciderLogic.LoadDecider(dir.In("withjs")).Logic);
        dir.Write("withts/decider.yaml", yaml);
        dir.Write("withts/decider.ts", Ts);
        Assert.True(JsDeciderLogic.LoadDecider(dir.In("withts")).Logic!.HasDecide);
        dir.Write("plain/decider.yaml", yaml);
        Assert.Null(JsDeciderLogic.LoadDecider(dir.In("plain")).Logic);
    }
}
