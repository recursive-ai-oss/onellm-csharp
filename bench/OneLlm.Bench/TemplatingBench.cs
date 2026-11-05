using BenchmarkDotNet.Attributes;
using OneLlm.Templating;

namespace OneLlm.Bench;

/// <summary>
/// Template rendering: the parse cache lookup, a plain prompt, field substitution, control flow over a list, and an embedded
/// prompt. Each case renders from a cached parse, which is how prompts are used at run time; <see cref="ColdParse"/> bypasses
/// the cache by changing the text. Run: <c>dotnet run -c Release --project bench/OneLlm.Bench -- --filter '*Templating*'</c>.
/// </summary>
[MemoryDiagnoser]
public class TemplatingBench
{
    private const string Plain = "You are a careful assistant. Answer briefly and cite sources.\n";
    private const string Fields = "Hello {{ .inputs.name }}, today is {{ .time.today }}. Mode: {{ .invocationMode | upper }}.\n";
    private const string Control = "{{ range $i, $t := .inputs.tasks }}{{ if gt $i 0 }}, {{ end }}{{ $i }}: {{ $t.title | lower }}{{ if $t.done }} (done){{ end }}{{ end }}\n";
    private const string Embedding = "Intro\n{{ embed \"shared/rules\" }}\nOutro {{ .inputs.name }}\n";

    private Dictionary<string, object?> _scope = null!;
    private Template _plain = null!;
    private Template _fields = null!;
    private Template _control = null!;
    private Template _embedding = null!;
    private TemplateRenderOptions _embedOptions = null!;
    private int _n;

    [GlobalSetup]
    public void Setup()
    {
        var tasks = new List<object?>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(new Dictionary<string, object?> { ["title"] = "Task Number " + i, ["done"] = i % 3 == 0 });
        }

        _scope = new Dictionary<string, object?>
        {
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "Ada", ["tasks"] = tasks },
            ["time"] = new Dictionary<string, object?> { ["today"] = "2026-01-02" },
            ["invocationMode"] = "interactive",
        };
        _plain = Template.Parse(Plain);
        _fields = Template.Parse(Fields);
        _control = Template.Parse(Control);
        _embedding = Template.Parse(Embedding);
        _embedOptions = new TemplateRenderOptions
        {
            EmbedSources = new IEmbedSource[]
            {
                new PromptEmbedSource(new Dictionary<string, string> { ["shared/rules"] = "Rules for {{ .inputs.name }}: be kind.\n" }),
            },
        };
    }

    [Benchmark]
    public string PlainText() => _plain.Render(_scope);

    [Benchmark]
    public string FieldSubstitution() => _fields.Render(_scope);

    [Benchmark]
    public string RangeAndIf() => _control.Render(_scope);

    [Benchmark]
    public string Embed() => _embedding.Render(_scope, _embedOptions);

    [Benchmark]
    public Template CachedParse() => Template.Parse(Fields);

    [Benchmark]
    public Template ColdParse() => Template.Parse(Fields + "{{/* " + Interlocked.Increment(ref _n) + " */}}");
}
