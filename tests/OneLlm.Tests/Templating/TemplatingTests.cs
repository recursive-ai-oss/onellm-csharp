using System.Text;
using System.Text.Json;
using OneLlm.Templating;

namespace OneLlm.Tests.Templating;

/// <summary>
/// Replays the curated cases whose expected results were recorded from the reference implementation (Go's text/template,
/// run through the differential tool). A case passes when the output is byte-identical, or when both fail in the same stage.
/// </summary>
public class GoldenTests
{
    [Fact]
    public void Curated_cases_match_the_recorded_reference_results()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "templating", "golden.jsonl");
        var failures = new List<string>();
        int count = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            count++;
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            string name = e.GetProperty("name").GetString()!;
            var embeds = new Dictionary<string, string>();
            foreach (JsonProperty p in e.GetProperty("embeds").EnumerateObject())
            {
                embeds[p.Name] = p.Value.GetString()!;
            }

            var parse = new TemplateParseOptions { EscapeCodeRegions = !(e.TryGetProperty("escape", out JsonElement es) && !es.GetBoolean()) };
            var render = new TemplateRenderOptions
            {
                MissingKey = e.GetProperty("missingKey").GetString() == "error" ? MissingKeyMode.Error : MissingKeyMode.Default,
                EmbedSources = new IEmbedSource[] { new PromptEmbedSource(embeds) },
            };

            bool expectOk = e.GetProperty("ok").GetBoolean();
            try
            {
                string got = Template.Render(e.GetProperty("t").GetString()!, e.GetProperty("data"), parse, render);
                if (!expectOk)
                {
                    failures.Add($"{name}: expected a {e.GetProperty("stage").GetString()} error, got output");
                }
                else if (got != e.GetProperty("out").GetString())
                {
                    failures.Add($"{name}: output differs\n  want: {e.GetProperty("out").GetString()}\n  got:  {got}");
                }
            }
            catch (TemplateException ex)
            {
                if (expectOk)
                {
                    failures.Add($"{name}: unexpected {ex.Kind} error: {ex.Message}");
                }
                else
                {
                    string stage = ex.Kind == TemplateErrorKind.Parse ? "parse" : "exec";
                    if (stage != e.GetProperty("stage").GetString())
                    {
                        failures.Add($"{name}: expected {e.GetProperty("stage").GetString()} error, got {stage}: {ex.Message}");
                    }
                }
            }
        }

        Assert.True(count > 150, "golden file looks empty");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}

public class TemplateBehaviourTests
{
    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static string Render(string text, object? scope, TemplateRenderOptions? options = null, TemplateParseOptions? parse = null)
        => Template.Parse(text, parse).Render(scope, options);

    [Fact]
    public void Scope_accepts_plain_collections_arrays_json_and_mixed_numbers()
    {
        var scope = new Dictionary<string, object?>
        {
            ["s"] = "x",
            ["i"] = 3,
            ["l"] = 4L,
            ["d"] = 1.5,
            ["m"] = 2.25m,
            ["list"] = new List<object?> { 1, "a", null },
            ["arr"] = new[] { "p", "q" },
            ["typed"] = new List<int> { 7, 8 },
            ["nested"] = new Dictionary<string, object?> { ["k"] = new Dictionary<string, string> { ["z"] = "deep" } },
            ["ro"] = new Dictionary<string, int> { ["a"] = 1 },
            ["json"] = JsonDocument.Parse("""{"a":[1,2.0,{"b":"c"}],"n":null}""").RootElement,
        };
        string got = Render("{{.s}} {{.i}} {{.l}} {{.d}} {{.m}} {{.list}} {{index .arr 1}} {{.typed}} {{.nested.k.z}} {{.ro.a}} {{.json.a}} {{(index .json.a 2).b}} {{.json.n}}", scope);
        Assert.Equal("x 3 4 1.5 2.25 [1 a <nil>] q [7 8] deep 1 [1 2 map[b:c]] c <no value>", got);
    }

    [Fact]
    public void Json_numbers_with_a_point_or_exponent_are_floats_and_others_are_ints()
    {
        JsonElement e = JsonDocument.Parse("""{"a":1,"b":1.0,"c":1e2,"d":12345678901234567890}""").RootElement;
        Assert.Equal("int float64 float64 float64", Render("""{{printf "%T %T %T %T" .a .b .c .d}}""", e));
    }

    [Fact]
    public void Documented_differences_from_Go_stay_as_documented()
    {
        // Found by differential fuzzing and listed under "Known differences from Go" in docs/templating.md; pinned so a change is deliberate.
        // A string cut through a multi-byte character becomes U+FFFD (3 bytes) instead of a raw byte (1 byte).
        Assert.Equal("3", Render("""{{len (slice "ü" 1)}}""", new Dictionary<string, object?>()));

        // A slice of a slice cannot grow back toward the original list's capacity.
        var scope = new Dictionary<string, object?> { ["items"] = new List<object?> { 1L, 2L, 3L } };
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("{{slice (slice .items 1 1) 0 2}}", scope));
        Assert.Equal(TemplateErrorKind.Execute, ex.Kind);
    }

    [Fact]
    public void Missing_key_modes()
    {
        var scope = new Dictionary<string, object?> { ["m"] = new Dictionary<string, object?>() };
        Assert.Equal("<no value>", Render("{{.m.x}}", scope));
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("{{.m.x}}", scope, new TemplateRenderOptions { MissingKey = MissingKeyMode.Error }));
        Assert.Contains("\"x\"", ex.Message, StringComparison.Ordinal);
        Assert.Equal(TemplateErrorKind.Execute, ex.Kind);
    }

    [Fact]
    public void Parse_errors_carry_line_and_column_in_the_original_text_even_after_code_escaping()
    {
        TemplateException a = Assert.Throws<TemplateException>(() => Template.Parse("line one\n  {{ nosuch }}"));
        Assert.Equal(TemplateErrorKind.Parse, a.Kind);
        Assert.Equal(2, a.Line);
        Assert.Equal(6, a.Column);

        // Escaping inside code spans lengthens the parsed text; positions still point into what the author wrote.
        TemplateException b = Assert.Throws<TemplateException>(() => Template.Parse("`{{x}}` {{ nosuch }}", new TemplateParseOptions { Name = "p.md" }));
        Assert.Equal(1, b.Line);
        Assert.Equal(12, b.Column);
        Assert.Equal(11, b.Offset);
        Assert.StartsWith("p.md:1:12:", b.Message, StringComparison.Ordinal);
        Assert.Equal("p.md", b.TemplateName);
    }

    [Fact]
    public void Execution_errors_are_positioned()
    {
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("a\nb {{ lower 5 }}", new Dictionary<string, object?>()));
        Assert.Equal(TemplateErrorKind.Execute, ex.Kind);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Host_functions_are_checked_for_arity_and_type()
    {
        var fns = new TemplateFunctions()
            .Add("shout", s => s.ToUpperInvariant() + "!")
            .Add("cat", (a, b) => a + b)
            .Add(new TemplateFunction("sum", Array.Empty<ArgKind>(), a => a.Cast<long>().Sum(), ArgKind.Whole))
            .Add(new TemplateFunction("half", new[] { ArgKind.Real }, a => (double)a[0]! / 2))
            .Add("boom", new Func<string, object?>(_ => throw new TemplateFunctionException("kaboom")))
            .AddVariadic("any", a => a.Length);
        var parse = new TemplateParseOptions { Functions = fns };
        Assert.Equal("HI! ab 6 1.5 3", Render("""{{shout "hi"}} {{cat "a" "b"}} {{sum 1 2 3}} {{half 3.0}} {{any 1 nil "x"}}""", null, null, parse));
        Assert.Equal("HI!", Render("""{{"hi" | shout}}""", null, null, parse));
        Assert.Throws<TemplateException>(() => Render("{{shout}}", null, null, parse));
        Assert.Throws<TemplateException>(() => Render("{{shout 1}}", null, null, parse));
        Assert.Throws<TemplateException>(() => Render("{{sum 1 \"x\"}}", null, null, parse));
        Assert.Throws<TemplateException>(() => Render("{{half \"x\"}}", null, null, parse));
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("{{boom \"x\"}}", null, null, parse));
        Assert.Contains("kaboom", ex.Message, StringComparison.Ordinal);
        // The shared standard registry stays as it was.
        Assert.Throws<TemplateException>(() => Template.Parse("{{shout \"x\"}}"));
        Assert.Throws<InvalidOperationException>(() => TemplateFunctions.Standard.Add("x", s => s));
    }

    // Differences from the reference that the smoke fuzz found and that are accepted (see docs/templating.md). Each pins
    // what OneLLM does so a change is deliberate.
    [Fact]
    public void Accepted_differences_from_the_reference_are_pinned()
    {
        // Strings are UTF-16: a slice that cuts a multi-byte character becomes U+FFFD (3 bytes) where the reference keeps
        // the 1 raw byte, so the lengths differ.
        Assert.Equal("3", Render("""{{len (slice "é" 1)}}""", null));

        // A slice of a slice cannot grow past its length (the reference allows up to the backing array's capacity).
        Assert.Throws<TemplateException>(() => Render("{{slice (slice (list 1 2 3) 0 0) 0 2}}", null));

        // Complex number constants are not supported.
        Assert.Throws<TemplateException>(() => Render("{{with 1i}}{{end}}", null));
    }

    [Fact]
    public void Host_functions_can_replace_standard_ones_and_builtins()
    {
        var fns = new TemplateFunctions().Add("upper", s => "custom:" + s).AddVariadic("len", _ => "mine");
        Assert.Equal("custom:a mine", Render("""{{upper "a"}} {{len "abc"}}""", null, null, new TemplateParseOptions { Functions = fns }));
    }

    [Fact]
    public void Function_names_must_be_identifiers()
    {
        Assert.Throws<ArgumentException>(() => new TemplateFunctions().Add("bad name", s => s));
        Assert.Throws<ArgumentException>(() => new TemplateFunctions().Add("1x", s => s));
    }

    [Fact]
    public void Call_invokes_function_values_with_checked_arguments()
    {
        var fns = new TemplateFunctions();
        var upper = new TemplateFunction("up", new[] { ArgKind.Text }, a => ((string)a[0]!).ToUpperInvariant());
        var scope = new Dictionary<string, object?> { ["f"] = upper };
        Assert.Equal("ABC", Render("""{{call .f "abc"}}""", scope, null, new TemplateParseOptions { Functions = fns }));
        Assert.Throws<TemplateException>(() => Render("{{call .f 1}}", scope));
        Assert.Throws<TemplateException>(() => Render("{{call .f}}", scope));
    }

    [Fact]
    public void Now_and_date_use_the_injected_clock()
    {
        var clock = new FakeClock(new DateTimeOffset(2030, 7, 4, 9, 5, 6, TimeSpan.Zero));
        var opts = new TemplateRenderOptions { TimeProvider = clock };
        Assert.Equal("2030-07-04 09:05", Render("""{{now | date "2006-01-02 15:04"}}""", null, opts));
        Assert.Equal("2030-07-04 09:05:06 +0000 UTC", Render("{{now}}", null, opts));
        Assert.Equal("2024-03-05", Render("""{{date "2006-01-02" "2024-03-05T14:07:09Z"}}""", null));
        Assert.Equal("1970-01-01", Render("""{{date "2006-01-02" 0}}""", null));
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("{{now}}", null));
        Assert.Contains("clock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToYaml_writes_the_value_model()
    {
        var scope = new Dictionary<string, object?>
        {
            ["v"] = new Dictionary<string, object?> { ["b"] = new List<object?> { 1, "two", null }, ["a"] = true, ["s"] = "multi\nline" },
        };
        string yaml = Render("{{toYaml .v}}", scope);
        Assert.Contains("a: true", yaml, StringComparison.Ordinal);
        Assert.Contains("- two", yaml, StringComparison.Ordinal);
        Assert.True(yaml.IndexOf("a:", StringComparison.Ordinal) < yaml.IndexOf("b:", StringComparison.Ordinal), "keys are sorted");
    }

    [Fact]
    public void Regex_functions_use_linear_time_matching()
    {
        // A pattern that would stall a backtracking engine on this input returns promptly.
        string input = new string('a', 40) + "!";
        Assert.Equal("false", Render("""{{regexMatch "^(a+)+$" .s}}""", new Dictionary<string, object?> { ["s"] = input }));
    }

    [Fact]
    public void Output_limit_is_enforced()
    {
        var opts = new TemplateRenderOptions { MaxOutputLength = 50 };
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("{{range 1000}}0123456789{{end}}", null, opts));
        Assert.Equal(TemplateErrorKind.Limit, ex.Kind);
    }

    [Fact]
    public void Runaway_template_recursion_fails_with_an_error_instead_of_crashing()
    {
        TemplateException ex = Assert.Throws<TemplateException>(() => Render("""{{define "a"}}x{{template "a"}}{{end}}{{template "a"}}""", null));
        Assert.Equal(TemplateErrorKind.Limit, ex.Kind);
    }

    [Fact]
    public void Deeply_nested_template_text_fails_with_an_error()
    {
        string deep = string.Concat(Enumerable.Repeat("{{if true}}", 5000)) + string.Concat(Enumerable.Repeat("{{end}}", 5000));
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse(deep));
        Assert.Equal(TemplateErrorKind.Limit, ex.Kind);
        string parens = "{{" + new string('(', 5000) + "1" + new string(')', 5000) + "}}";
        Assert.Throws<TemplateException>(() => Template.Parse(parens));
    }

    [Fact]
    public void Cancellation_stops_a_render()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Render("{{range 10}}x{{end}}", null, new TemplateRenderOptions { CancellationToken = cts.Token }));
    }

    [Fact]
    public void Render_into_a_builder_appends_and_rolls_back_on_failure()
    {
        Template t = Template.Parse("[{{.a}}]");
        var sb = new StringBuilder("pre:");
        t.Render(new Dictionary<string, object?> { ["a"] = 1 }, sb);
        Assert.Equal("pre:[1]", sb.ToString());
        Template bad = Template.Parse("ok{{lower 5}}");
        Assert.Throws<TemplateException>(() => bad.Render(null, sb));
        Assert.Equal("pre:[1]", sb.ToString());
    }

    [Fact]
    public void Parsed_templates_render_the_same_from_many_threads()
    {
        Template t = Template.Parse("""{{range $i, $e := .l}}{{$i}}:{{$e | upper}},{{end}}{{embed "e"}}""");
        var scope = new Dictionary<string, object?> { ["l"] = new List<object?> { "a", "b", "c" } };
        var opts = new TemplateRenderOptions { EmbedSources = new IEmbedSource[] { new PromptEmbedSource(new Dictionary<string, string> { ["e"] = "{{len .l}}" }) } };
        string expected = t.Render(scope, opts);
        Assert.Equal("0:A,1:B,2:C,3", expected);
        Parallel.For(0, 64, _ =>
        {
            for (int i = 0; i < 200; i++)
            {
                Assert.Equal(expected, t.Render(scope, opts));
            }
        });
    }

    [Fact]
    public void Parse_cache_reuses_trees_and_respects_function_registry_changes()
    {
        var fns = new TemplateFunctions();
        var parse = new TemplateParseOptions { Functions = fns };
        Assert.Throws<TemplateException>(() => Template.Parse("{{late}}", parse));
        fns.Add("late", s => s);
        // The same text now parses because the registry changed (a new cache key).
        Assert.Equal("", Template.Parse("{{define \"x\"}}{{end}}", parse).Render(null));
        fns.AddVariadic("late", _ => "ok");
        Assert.Equal("ok", Template.Parse("{{late}}", parse).Render(null));
    }

    [Fact]
    public void Text_without_actions_renders_as_is_including_code_markers()
    {
        Assert.Equal("`a` ```\nb\n``` }} {", Render("`a` ```\nb\n``` }} {", null));
    }

    [Fact]
    public void Code_region_escaping_can_be_turned_off()
    {
        Assert.Equal("`1`", Render("`{{.a}}`", new Dictionary<string, object?> { ["a"] = 1 }, null, new TemplateParseOptions { EscapeCodeRegions = false }));
        Assert.Equal("`{{.a}}`", Render("`{{.a}}`", new Dictionary<string, object?> { ["a"] = 1 }));
    }

    [Fact]
    public void Index_into_a_string_gives_a_byte_that_prints_as_a_number()
    {
        Assert.Equal("65 true", Render("""{{index "A" 0}} {{eq (index "A" 0) 65}}""", null));
        Assert.Throws<TemplateException>(() => Render("""{{indent (index "A" 0) "x"}}""", null));
    }

    [Fact]
    public void Template_name_is_callable_and_reported()
    {
        Template t = Template.Parse("{{define \"x\"}}[{{template \"main\" .}}]{{end}}a", new TemplateParseOptions { Name = "main" });
        Assert.Equal("main", t.Name);
        Assert.Equal("a", t.Render(null));
    }
}
