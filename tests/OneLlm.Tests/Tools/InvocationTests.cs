using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tools;

namespace OneLlm.Tests.Tools;

public class InvocationTests
{
    private static ToolContext Context(ManualTimeProvider? clock = null, IToolLog? log = null) =>
        new("inst-1", "call-1", clock ?? new ManualTimeProvider()) { LogSink = log };

    private static ITool Tool(string name) => Calc.GetTools().Single(t => t.Definition.Name == name);

    private static async Task<ToolResult> Run(string name, string json, ManualTimeProvider? clock = null)
    {
        var inputs = (JsonObject)JsonNode.Parse(json)!;
        return await ToolInvoker.InvokeAsync(Tool(name), inputs, Context(clock), CancellationToken.None);
    }

    private static string TextOf(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));

    [Fact]
    public void Tools_come_in_declaration_order_with_snake_case_names()
    {
        string[] names = Calc.GetTools().Select(t => t.Definition.Name).ToArray();
        string[] expected = ["add", "echo", "finish", "nothing", "boom", "slow", "wait_for_cancel", "priced", "raw", "several", "where", "do_it", "later", "node", "pick", "maybe_guid", "slow_guard"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Tools_register_through_the_source_interface()
    {
        static int Count<T>()
            where T : class, IToolSource<T> => T.GetTools().Count;
        Assert.Equal(17, Count<Calc>());
    }

    [Fact]
    public async Task Json_return_values_become_json_parts()
    {
        ToolResult r = await Run("add", """{"a":2,"b":3}""");
        Assert.False(r.IsError);
        Assert.Equal(5, ((JsonPart)r.Parts.Single()).Value.GetValue<int>());
    }

    [Fact]
    public async Task Async_methods_with_defaults_and_cancellation_bind()
    {
        Assert.Equal("ab", TextOf(await Run("echo", """{"text":"ab"}""")));
        Assert.Equal("ababab", TextOf(await Run("echo", """{"text":"ab","times":3}""")));
        Assert.Equal(8, ((JsonPart)(await Run("later", """{"n":4}""")).Parts.Single()).Value.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"a":1}""", "'b'", "missing")]
    [InlineData("""{"a":"x","b":1}""", "'a'", "integer")]
    [InlineData("""{"a":1.5,"b":1}""", "'a'", "integer")]
    [InlineData("""{"a":3000000000,"b":1}""", "'a'", "integer")]
    [InlineData("""{"a":null,"b":1}""", "'a'", "null")]
    [InlineData("""{"a":1,"b":2,"c":3}""", "'c'", "not a parameter")]
    public async Task Bad_arguments_give_error_results_that_name_the_parameter(string json, string name, string reason)
    {
        ToolResult r = await Run("add", json);
        Assert.True(r.IsError);
        Assert.Contains(name, TextOf(r), StringComparison.Ordinal);
        Assert.Contains(reason, TextOf(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nested_argument_paths_are_reported()
    {
        var tool = SchemaCases.GetTools().Single(t => t.Definition.Name == "complex");
        var inputs = (JsonObject)JsonNode.Parse("""{"item":{"sku":"a","qty":"two"},"addr":{"street":"s"},"items":[]}""")!;
        ToolResult r = await ToolInvoker.InvokeAsync(tool, inputs, Context(), CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("'item'", TextOf(r), StringComparison.Ordinal);

        var tool2 = SchemaCases.GetTools().Single(t => t.Definition.Name == "collections");
        var inputs2 = (JsonObject)JsonNode.Parse("""{"arr":[1,"x"],"list":[],"ro":[],"set":[],"en":[],"map":{},"ro2":{},"nested":[]}""")!;
        ToolResult r2 = await ToolInvoker.InvokeAsync(tool2, inputs2, Context(), CancellationToken.None);
        Assert.Contains("'arr[1]'", TextOf(r2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_set_items_are_refused()
    {
        var tool = SchemaCases.GetTools().Single(t => t.Definition.Name == "collections");
        var inputs = (JsonObject)JsonNode.Parse("""{"arr":[],"list":[],"ro":[],"set":[1,1],"en":[],"map":{},"ro2":{},"nested":[]}""")!;
        ToolResult r = await ToolInvoker.InvokeAsync(tool, inputs, Context(), CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("'set'", TextOf(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thrown_exception_becomes_an_error_result_without_a_stack_trace()
    {
        ToolResult r = await Run("boom", """{"why":"disk on fire"}""");
        Assert.True(r.IsError);
        Assert.Contains("InvalidOperationException: disk on fire", TextOf(r), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", TextOf(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_becomes_an_error_result_on_the_injected_clock()
    {
        var clock = new ManualTimeProvider();
        Task<ToolResult> running = Run("slow", "{}", clock);
        while (clock.PendingCount == 0)
        {
            await Task.Delay(5);
        }

        Assert.Equal(TimeSpan.FromSeconds(5), clock.CreatedDueTimes[0]);
        clock.Advance(TimeSpan.FromSeconds(5));
        ToolResult r = await running;
        Assert.True(r.IsError);
        Assert.Contains("timed out after 5 s", TextOf(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_an_error_result()
    {
        using var cts = new CancellationTokenSource();
        Task<ToolResult> running = ToolInvoker.InvokeAsync(Tool("wait_for_cancel"), new JsonObject(), Context(), cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Cost_lines_added_to_the_context_reach_the_result()
    {
        ToolResult r = await Run("priced", "{}");
        CostLine line = Assert.Single(r.CostLines);
        Assert.Equal("search_query", line.Counter);
        Assert.Equal(0.005m, line.Amount);
    }

    [Fact]
    public async Task Cost_added_before_a_failure_is_kept()
    {
        var tool = Calc.GetTools().Single(t => t.Definition.Name == "priced");
        var ctx = Context();
        ctx.AddCost(CostLine.Create("earlier", 1m, 1m, 1m));
        ToolResult r = await ToolInvoker.InvokeAsync(tool, new JsonObject { ["bogus"] = 1 }, ctx, CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Equal("earlier", Assert.Single(r.CostLines).Counter);
    }

    [Fact]
    public async Task Ending_the_loop_carries_the_typed_result()
    {
        ToolResult r = await Run("finish", """{"decision":"ship","score":9}""");
        Assert.True(r.EndsLoop);
        Assert.Equal("""{"decision":"ship","score":9}""", r.TypedResult!.ToJsonString());
        ITool tool = Tool("finish");
        Assert.True(tool.Definition.EndsLoop);
        Assert.NotNull(tool.Definition.OutputSchema);
    }

    [Fact]
    public async Task Metadata_comes_from_the_attribute()
    {
        ToolSpec spec = Tool("do_it").Definition;
        Assert.True(spec.RequiresApproval);
        Assert.False(spec.ParallelSafe);
        Assert.Equal("Does it.", spec.Description);
        Assert.Equal(TimeSpan.FromSeconds(5), Tool("slow").Definition.Timeout);
        Assert.Equal("do_it", spec.ToProviderDefinition().Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Return_kinds_convert_to_parts()
    {
        Assert.Empty((await Run("nothing", "{}")).Parts);
        Assert.Equal("raw", TextOf(await Run("raw", "{}")));
        Assert.Equal("ab", TextOf(await Run("several", "{}")));
        Assert.Equal("x", ((JsonPart)(await Run("where", """{"q":"x"}""")).Parts.Single()).Value["street"]!.GetValue<string>());
        Assert.Equal("""{"k":1}""", ((JsonPart)(await Run("node", "{}")).Parts.Single()).Value.ToJsonString());
        Assert.Equal("Blue", ((JsonPart)(await Run("pick", """{"c":"Blue"}""")).Parts.Single()).Value.GetValue<string>());
        Assert.True((await Run("pick", """{"c":"Purple"}""")).IsError);
        Assert.Equal("\"11111111-1111-1111-1111-111111111111\"", ((JsonPart)(await Run("maybe_guid", """{"have":true}""")).Parts.Single()).Value.ToJsonString());
        Assert.Equal("null", TextOf(await Run("maybe_guid", """{"have":false}""")));
    }

    private sealed class Lines : IToolLog
    {
        public List<(ToolLogLevel Level, string Message)> Entries { get; } = [];

        public void Write(string toolCallId, ToolLogLevel level, string message) => Entries.Add((level, toolCallId + ":" + message));
    }

    [Fact]
    public async Task Context_gives_identity_log_and_progress()
    {
        var log = new Lines();
        ToolResult r = await ToolInvoker.InvokeAsync(Tool("slow_guard"), new JsonObject(), Context(log: log), CancellationToken.None);
        Assert.Equal("inst-1", TextOf(r));
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal((ToolLogLevel.Information, "call-1:hello"), log.Entries[0]);
        Assert.Equal((ToolLogLevel.Progress, "call-1:half"), log.Entries[1]);
    }

    [Fact]
    public void Default_context_grants_nothing()
    {
        var ctx = Context();
        Assert.Empty(ctx.Capabilities.FileRoots);
        Assert.False(ctx.Capabilities.ShellEnabled);
        Assert.False(ctx.Capabilities.IsNetworkAllowed(new Uri("https://example.com")));
    }

    // --- Tool.Create parity ---

    private static Delegate Delegate(MethodInfo m)
    {
        Type[] types = [.. m.GetParameters().Select(p => p.ParameterType), m.ReturnType];
        object? target = m.IsStatic ? null : System.Activator.CreateInstance(m.DeclaringType!);
        return System.Delegate.CreateDelegate(Expression.GetDelegateType(types), target, m);
    }

    [Fact]
    public void Tool_Create_gives_the_same_schema_as_the_generator_for_every_case()
    {
        ITool[] generated = [.. SchemaCases.GetTools(), .. Calc.GetTools()];
        MethodInfo[] methods = [.. typeof(SchemaCases).GetMethods().Concat(typeof(Calc).GetMethods()).Where(m => m.GetCustomAttribute<ToolAttribute>() is not null)];
        Assert.Equal(generated.Length, methods.Length);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo m = methods[i];
            ToolAttribute attribute = m.GetCustomAttribute<ToolAttribute>()!;
            ITool made = attribute.Name is { } n ? OneLlm.Tools.Tool.Create(n, Delegate(m), attribute.Description) : OneLlm.Tools.Tool.Create(Delegate(m), attribute.Description);
            ToolSpec g = generated[i].Definition;
            ToolSpec r = made.Definition;
            Assert.Equal(g.Name, r.Name);
            Assert.Equal(g.Description, r.Description);
            Assert.Equal(g.InputSchema.ToJsonString(), r.InputSchema.ToJsonString());
            if (g.OutputSchema is not null && r.OutputSchema is not null)
            {
                Assert.Equal(g.OutputSchema.ToJsonString(), r.OutputSchema.ToJsonString());
            }
        }
    }

    [Fact]
    public async Task Tool_Create_runs_delegates_and_reports_errors_like_the_generator()
    {
        ITool add = OneLlm.Tools.Tool.Create("add", (int a, int b) => a + b);
        ToolResult ok = await ToolInvoker.InvokeAsync(add, (JsonObject)JsonNode.Parse("""{"a":2,"b":3}""")!, Context(), CancellationToken.None);
        Assert.Equal(5, ((JsonPart)ok.Parts.Single()).Value.GetValue<int>());

        ToolResult bad = await ToolInvoker.InvokeAsync(add, (JsonObject)JsonNode.Parse("""{"a":"x","b":3}""")!, Context(), CancellationToken.None);
        Assert.True(bad.IsError);
        Assert.Contains("'a'", TextOf(bad), StringComparison.Ordinal);

        ToolResult missing = await ToolInvoker.InvokeAsync(add, (JsonObject)JsonNode.Parse("""{"a":1}""")!, Context(), CancellationToken.None);
        Assert.Contains("'b'", TextOf(missing), StringComparison.Ordinal);

        ToolResult extra = await ToolInvoker.InvokeAsync(add, (JsonObject)JsonNode.Parse("""{"a":1,"b":2,"z":1}""")!, Context(), CancellationToken.None);
        Assert.Contains("'z'", TextOf(extra), StringComparison.Ordinal);

        ITool boom = OneLlm.Tools.Tool.Create("boom", (Func<string>)(() => throw new InvalidOperationException("nope")));
        ToolResult failed = await ToolInvoker.InvokeAsync(boom, new JsonObject(), Context(), CancellationToken.None);
        Assert.True(failed.IsError);
        Assert.Contains("nope", TextOf(failed), StringComparison.Ordinal);

        ITool echo = OneLlm.Tools.Tool.Create("echo", async (string text, ToolContext ctx, CancellationToken ct) =>
        {
            await Task.Yield();
            return text + ctx.ToolCallId;
        });
        Assert.Equal("hi" + "call-1", TextOf(await ToolInvoker.InvokeAsync(echo, new JsonObject { ["text"] = "hi" }, Context(), CancellationToken.None)));
    }

    [Fact]
    public void Tool_Create_refuses_a_lambda_without_a_name()
    {
        Assert.Throws<ArgumentException>(() => OneLlm.Tools.Tool.Create((Func<int>)(() => 1)));
    }
}
