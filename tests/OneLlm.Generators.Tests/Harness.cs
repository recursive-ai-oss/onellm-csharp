using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OneLlm.Tools;

namespace OneLlm.Generators.Tests;

/// <summary>Compiles a source with the tool generator and loads the result.</summary>
internal static class Harness
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(ITool).Assembly.Location);
        return [.. paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }

    public sealed class Result
    {
        public required Compilation Output { get; init; }

        public required ImmutableArray<Diagnostic> GeneratorDiagnostics { get; init; }

        public required string[] Sources { get; init; }

        public IEnumerable<string> Ids => GeneratorDiagnostics.Select(d => d.Id);

        public Diagnostic[] CompileProblems => [.. Output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning)];

        public ITool[] Tools(string typeName)
        {
            using var ms = new MemoryStream();
            var emit = Output.Emit(ms);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            ms.Position = 0;
            Assembly assembly = new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(ms);
            Type type = assembly.GetType(typeName, throwOnError: true)!;
            MethodInfo getTools = type.GetMethod("GetTools", BindingFlags.Public | BindingFlags.Static)!;
            object?[] args = getTools.GetParameters().Length == 0 ? [] : [null];
            return [.. (IEnumerable<ITool>)getTools.Invoke(null, args)!];
        }
    }

    public static Result Run(string source, params string[] extraSources)
    {
        const string Header = "using System; using System.Collections.Generic; using System.ComponentModel; using System.ComponentModel.DataAnnotations; using System.Text.Json; using System.Text.Json.Nodes; using System.Text.Json.Serialization; using System.Threading; using System.Threading.Tasks; using OneLlm.Tools; using OneLlm.Messages;\n#nullable enable\n";
        SyntaxTree[] trees = [.. new[] { source }.Concat(extraSources).Select(s => CSharpSyntaxTree.ParseText(Header + s, new CSharpParseOptions(LanguageVersion.Latest)))];
        var compilation = CSharpCompilation.Create(
            "generated_" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ToolGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        GeneratorDriverRunResult run = driver.GetRunResult();
        return new Result
        {
            Output = output,
            GeneratorDiagnostics = diagnostics,
            Sources = [.. run.GeneratedTrees.Select(t => t.GetText().ToString())],
        };
    }
}
