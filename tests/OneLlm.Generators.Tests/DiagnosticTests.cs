using Microsoft.CodeAnalysis;

namespace OneLlm.Generators.Tests;

public class DiagnosticTests
{
    private const string Ctx = """
        public sealed record Node(Node? Next);
        [JsonSerializable(typeof(Node))]
        public partial class NodeCtx : JsonSerializerContext { public NodeCtx() : base(null) { } public static NodeCtx Default { get; } = new(); protected override JsonSerializerOptions? GeneratedSerializerOptions => null; public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type) => null; }
        """;

    [Theory]
    [InlineData("OLT001", "public class T { [Tool] public static string M() => \"\"; }")]
    [InlineData("OLT001", "public partial struct T { [Tool] public static string M() => \"\"; }")]
    [InlineData("OLT001", "public partial class Outer { public class Inner { [Tool] public static string M() => \"\"; } }")]
    [InlineData("OLT002", "public static partial class T { [Tool] public static string M(object x) => \"\"; }")]
    [InlineData("OLT002", "public static partial class T { [Tool] public static string M(ref int x) => \"\"; }")]
    [InlineData("OLT002", "public static partial class T { [Tool] public static string M(Dictionary<int, string> x) => \"\"; }")]
    [InlineData("OLT002", "public static partial class T { [Tool] public static string M(int[,] x) => \"\"; }")]
    [InlineData("OLT002", "public static partial class T { [Tool] public static string M(Func<int> x) => \"\"; }")]
    [InlineData("OLT003", "public static partial class T { [Tool] public static async void M() { await Task.Yield(); } }")]
    [InlineData("OLT003", "public static partial class T { [Tool] public static object M() => 1; }")]
    [InlineData("OLT004", "public static partial class T { [Tool] public static string M<X>() => \"\"; }")]
    [InlineData("OLT005", "public sealed record Plain(string A); public static partial class T { [Tool] public static string M(Plain x) => \"\"; }")]
    [InlineData("OLT005", "public sealed record Plain(string A); public static partial class T { [Tool] public static Plain M() => new(\"\"); }")]
    [InlineData("OLT006", Ctx + " public static partial class T { [Tool] public static string M(Node x) => \"\"; }")]
    [InlineData("OLT007", "public static partial class T { [Tool(Name = \"x\")] public static string A() => \"\"; [Tool(Name = \"x\")] public static string B() => \"\"; }")]
    [InlineData("OLT008", "public static partial class T { [Tool(Name = \"bad name\")] public static string M() => \"\"; }")]
    [InlineData("OLT009", "public static partial class T { [Tool] public static string M(ToolContext a, ToolContext b) => \"\"; }")]
    [InlineData("OLT009", "public static partial class T { [Tool] public static string M(CancellationToken a, CancellationToken b) => \"\"; }")]
    [InlineData("OLT010", "public partial class T<X> { [Tool] public static string M() => \"\"; }")]
    public void Unsupported_signatures_get_a_clear_error(string id, string source)
    {
        var r = Harness.Run(source);
        Assert.Contains(id, r.Ids);
        Diagnostic d = r.GeneratorDiagnostics.First(x => x.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.NotEmpty(d.GetMessage());
        Assert.NotEqual(Location.None, d.Location);
    }

    [Fact]
    public void Messages_name_the_parameter()
    {
        var r = Harness.Run("public static partial class T { [Tool] public static string M(int ok, object bad) => \"\"; }");
        Assert.Contains("'bad'", r.GeneratorDiagnostics.Single().GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void One_bad_method_does_not_stop_the_others()
    {
        var r = Harness.Run("public static partial class T { [Tool] public static string Good(int x) => \"\"; [Tool] public static string Bad(object x) => \"\"; }");
        Assert.Equal(["OLT002"], r.Ids.ToArray());
        Assert.Equal("good", r.Tools("T").Single().Definition.Name);
    }
}
