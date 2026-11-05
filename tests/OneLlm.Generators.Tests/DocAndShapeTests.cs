using System.Text.Json.Nodes;
using OneLlm.Tools;

namespace OneLlm.Generators.Tests;

public class DocAndShapeTests
{
    [Fact]
    public void Doc_comments_become_descriptions_even_when_the_compilation_does_not_parse_docs()
    {
        var r = Harness.Run("""
            /// <summary>Holds an address.</summary>
            /// <param name="Street">Street and number.</param>
            /// <param name="Unit">Apartment, if any.</param>
            public sealed record Address(string Street, string? Unit = null);

            public sealed record Line
            {
                /// <summary>The <c>stock</c> code, see <see cref="Line"/>.</summary>
                public required string Sku { get; init; }
            }

            [JsonSerializable(typeof(Address))] [JsonSerializable(typeof(Line))]
            public partial class Ctx : JsonSerializerContext { public Ctx() : base(null) { } public static Ctx Default { get; } = new(); protected override JsonSerializerOptions? GeneratedSerializerOptions => null; public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type) => null; }

            public static partial class T
            {
                /// <summary>
                /// Looks up a docket entry.
                /// Second line.
                /// </summary>
                /// <param name="caseNo">Case number, e.g. 24-1234.</param>
                /// <param name="where">Where to look, with <paramref name="caseNo"/> in mind.</param>
                /// <param name="line">The line.</param>
                [Tool]
                public static string GetDocketAsync(string caseNo, Address where, Line line, [Description("wins over the doc")] string other) => "";
            }
            """);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        ToolSpec spec = r.Tools("T").Single().Definition;
        Assert.Equal("get_docket", spec.Name);
        Assert.Equal("Looks up a docket entry. Second line.", spec.Description);
        JsonObject props = spec.InputSchema["properties"]!.AsObject();
        Assert.Equal("Case number, e.g. 24-1234.", props["caseNo"]!["description"]!.GetValue<string>());
        Assert.Equal("Where to look, with caseNo in mind.", props["where"]!["description"]!.GetValue<string>());
        Assert.Equal("Apartment, if any.", props["where"]!["properties"]!["Unit"]!["description"]!.GetValue<string>());
        Assert.Equal("The stock code, see Line.", props["line"]!["properties"]!["Sku"]!["description"]!.GetValue<string>());
        Assert.Equal("wins over the doc", props["other"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void Attribute_description_wins_over_the_summary()
    {
        var r = Harness.Run("""
            public static partial class T
            {
                /// <summary>From the doc.</summary>
                [Tool(Description = "From the attribute.")]
                public static string A() => "";

                /// <summary>From the doc.</summary>
                [Tool, Description("From DescriptionAttribute.")]
                public static string B() => "";

                /// <summary>From the doc.</summary>
                [Tool]
                public static string C() => "";
            }
            """);
        string[] d = [.. r.Tools("T").Select(t => t.Definition.Description)];
        Assert.Equal(["From the attribute.", "From DescriptionAttribute.", "From the doc."], d);
    }

    [Fact]
    public void Instance_tools_generate_for_nested_namespaced_classes_and_overloads()
    {
        var r = Harness.Run("""
            namespace Acme.Tools
            {
                public partial class Outer
                {
                    public partial class Inner
                    {
                        private readonly string _p;
                        public Inner() => _p = "p";
                        [Tool] private string Hello(string who) => _p + who;
                        [Tool(Name = "hello_two")] private string Hello(string who, int n) => _p + who + n;
                        [Tool] public static int Twice(int n) => n * 2;
                    }
                }
            }
            """);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        string[] names = [.. r.Tools("Acme.Tools.Outer+Inner").Select(t => t.Definition.Name)];
        Assert.Equal(["hello", "hello_two", "twice"], names);
        Assert.Contains("IToolSource<global::Acme.Tools.Outer.Inner>", r.Sources.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_without_a_parameterless_constructor_takes_the_instance()
    {
        var r = Harness.Run("""
            public partial class T
            {
                private readonly int _k;
                public T(int k) => _k = k;
                [Tool] public int Add(int n) => _k + n;
            }
            """);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        Assert.DoesNotContain("IToolSource", r.Sources.Single(), StringComparison.Ordinal);
        Assert.Contains("GetTools(global::T instance)", r.Sources.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_code_uses_no_reflection_and_compiles_without_warnings()
    {
        var r = Harness.Run("""
            public sealed record Out(string A);
            [JsonSerializable(typeof(Out))] public partial class Ctx : JsonSerializerContext { public Ctx() : base(null) { } public static Ctx Default { get; } = new(); protected override JsonSerializerOptions? GeneratedSerializerOptions => null; public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type) => null; }
            public static partial class T
            {
                [Tool(EndsLoop = true, RequiresApproval = true, ParallelSafe = false, TimeoutSeconds = 3)]
                public static async Task<Out> M(string a, int b = 2, ToolContext? c = null, CancellationToken ct = default) { await Task.Yield(); return new(a); }
            }
            """);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        string code = r.Sources.Single();
        foreach (string banned in new[] { "typeof(", "GetType()", "Activator", "dynamic", "Expression", "Reflection" })
        {
            Assert.DoesNotContain(banned, code.Replace("typeof(global::Out)", "").Replace("typeof(T)", ""), StringComparison.Ordinal);
        }

        ToolSpec spec = r.Tools("T").Single().Definition;
        Assert.True(spec.EndsLoop);
        Assert.True(spec.RequiresApproval);
        Assert.False(spec.ParallelSafe);
        Assert.Equal(TimeSpan.FromSeconds(3), spec.Timeout);
        Assert.Equal("""{"type":"object","properties":{"A":{"type":"string"}},"required":["A"],"additionalProperties":false}""", spec.OutputSchema!.ToJsonString());
    }

    [Fact]
    public void The_same_source_gives_the_same_output()
    {
        const string Source = "public static partial class T { [Tool] public static string M(int a, List<string> b) => \"\"; }";
        Assert.Equal(Harness.Run(Source).Sources, Harness.Run(Source).Sources);
    }
}
