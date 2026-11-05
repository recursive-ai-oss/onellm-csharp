using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OneLlm.Deciders;

namespace OneLlm.Generators.Tests;

public class DeciderGeneratorTests
{
    private const string Good = """
        model: dec-1
        state: {}
        questions:
          topic:
            type: choice
            instructions: Which?
            criteria:
              billing: Charges
              "in-store": Shop
          rel:
            type: noul
            forEach: "{{ .inputs.items }}"
            instructions: Relevant?
          urgency:
            type: score
            instructions: How?
            criteria: [Low, High]
        """;

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }

    private static (Compilation Output, ImmutableArray<Diagnostic> Generator) Run(string source, string yaml, string file = "d.yaml")
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator).ToList();
        paths.Add(typeof(DeciderAttribute).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "g" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("using System.Text.Json; using OneLlm.Deciders;\n#nullable enable\n" + source)],
            paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new DeciderGenerator().AsSourceGenerator()], additionalTexts: [new Text("/p/" + file, yaml)]);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (output, diagnostics);
    }

    [Fact]
    public void A_decider_class_gets_typed_answers_enums_and_the_decider()
    {
        var (output, generator) = Run(
            "[Decider(\"d.yaml\")] public static partial class Triage { static JsonElement Decide(Answers a, JsonElement i) => i; }",
            Good);
        Assert.Empty(generator);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning));
        string text = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.Contains("public enum TopicChoice", text);
        Assert.Contains("InStore", text);
        Assert.Contains("IReadOnlyList<global::OneLlm.Deciders.NoulAnswer> Rel", text);
        Assert.Contains("ScoreAnswer Urgency", text);
    }

    [Theory]
    [InlineData("ODG001", "[Decider(\"d.yaml\")] public class T { }", Good)]
    [InlineData("ODG002", "[Decider(\"missing.yaml\")] public partial class T { }", Good)]
    [InlineData("ODG003", "[Decider(\"d.yaml\")] public partial class T { }", "model: m\n")]
    [InlineData("ODG003", "[Decider(\"d.yaml\")] public partial class T { }", "questions:\n  q:\n    type: choice\n    criteria: { a: x, b: y }\n")]
    [InlineData("ODG004", "[Decider(\"d.yaml\")] public partial class T { }", "questions:\n  a-b:\n    type: noul\n  a_b:\n    type: noul\n")]
    public void A_definition_the_generator_cannot_use_is_an_error(string id, string source, string yaml)
    {
        var (_, generator) = Run(source, yaml);
        Assert.Contains(id, generator.Select(static d => d.Id));
    }
}
