using OneLlm.Templating;

namespace OneLlm.Tests.Templating;

public sealed class EmbedTests : IDisposable
{
    private readonly string _dir;

    public EmbedTests()
    {
        string tmp = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
        _dir = Path.Combine(tmp, "onellm-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string rel, string text)
    {
        string full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static TemplateRenderOptions Opts(params IEmbedSource[] sources) => new() { EmbedSources = sources };

    [Fact]
    public void Embed_without_a_source_fails_with_a_positioned_error()
    {
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("a\n{{ embed \"x\" }}").Render(null));
        Assert.Equal(TemplateErrorKind.Embed, ex.Kind);
        Assert.Equal(2, ex.Line);
        Assert.Contains("no embed source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sources_are_tried_in_order_and_the_first_hit_wins()
    {
        Write("p.md", "from file");
        var prompts = new PromptEmbedSource(new Dictionary<string, string> { ["p.md"] = "from prompt", ["only"] = "prompt only" });
        var file = new FileEmbedSource(_dir);
        Assert.Equal("from file|prompt only", Template.Parse("""{{embed "p.md"}}|{{embed "only"}}""").Render(null, Opts(file, prompts)));
        Assert.Equal("from prompt|prompt only", Template.Parse("""{{embed "p.md"}}|{{embed "only"}}""").Render(null, Opts(prompts, file)));
    }

    [Fact]
    public void File_embeds_resolve_relative_to_the_including_file()
    {
        Write("a/main.md", """top {{embed "sub/part.md"}}""");
        Write("a/sub/part.md", """part {{embed "../side.md"}} {{.v}}""");
        Write("a/side.md", "side");
        var file = new FileEmbedSource(_dir);
        string text = File.ReadAllText(Path.Combine(_dir, "a/main.md"));
        string got = Template.Parse(text).Render(new Dictionary<string, object?> { ["v"] = 9 }, new TemplateRenderOptions
        {
            EmbedSources = new IEmbedSource[] { file },
            RootKey = "file:" + Path.GetFullPath(Path.Combine(_dir, "a/main.md")),
        });
        Assert.Equal("top part side 9", got);
    }

    [Fact]
    public void File_embeds_cannot_leave_the_root()
    {
        string outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            string root = Path.Combine(_dir, "root");
            Directory.CreateDirectory(root);
            var file = new FileEmbedSource(root);
            var opts = Opts(file);
            string name = Path.GetFileName(outside);
            TemplateException dots = Assert.Throws<TemplateException>(() => Template.Parse("{{embed \"../../" + name + "\"}}").Render(null, opts));
            Assert.Equal(TemplateErrorKind.Embed, dots.Kind);
            Assert.DoesNotContain("secret", dots.Message, StringComparison.Ordinal);

            // An absolute reference is not this source's to answer.
            TemplateException abs = Assert.Throws<TemplateException>(() => Template.Parse("{{embed " + System.Text.Json.JsonSerializer.Serialize(outside) + "}}").Render(null, opts));
            Assert.Contains("not found", abs.Message, StringComparison.Ordinal);

            // A link inside the root that points outside is followed before the check.
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside);
            TemplateException link = Assert.Throws<TemplateException>(() => Template.Parse("{{embed \"link.txt\"}}").Render(null, opts));
            Assert.Contains("escapes", link.Message, StringComparison.Ordinal);

            Directory.CreateSymbolicLink(Path.Combine(root, "dirlink"), Path.GetDirectoryName(outside)!);
            Assert.Throws<TemplateException>(() => Template.Parse("{{embed \"dirlink/" + name + "\"}}").Render(null, opts));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Cycles_report_the_chain()
    {
        var prompts = new PromptEmbedSource(new Dictionary<string, string>
        {
            ["a"] = """A{{embed "b"}}""",
            ["b"] = """B{{embed "a"}}""",
        });
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("""{{embed "a"}}""").Render(null, Opts(prompts)));
        Assert.Equal(TemplateErrorKind.Embed, ex.Kind);
        Assert.Contains("cycle detected: prompt:a -> prompt:b -> prompt:a", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void A_template_embedding_its_own_root_key_is_a_cycle()
    {
        var prompts = new PromptEmbedSource(new Dictionary<string, string> { ["self"] = "x" });
        var opts = new TemplateRenderOptions { EmbedSources = new IEmbedSource[] { prompts }, RootKey = "prompt:self" };
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("""{{embed "self"}}""").Render(null, opts));
        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth_limit_applies_to_chains_that_are_not_cycles()
    {
        var map = new Dictionary<string, string>();
        for (int i = 0; i < 10; i++)
        {
            map["n" + i] = i == 9 ? "end" : "{{embed \"n" + (i + 1) + "\"}}";
        }

        var opts = new TemplateRenderOptions { EmbedSources = new IEmbedSource[] { new PromptEmbedSource(map) }, MaxEmbedDepth = 4 };
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("""{{embed "n0"}}""").Render(null, opts));
        Assert.Contains("deep", ex.Message, StringComparison.Ordinal);
        Assert.Equal("end", Template.Parse("""{{embed "n0"}}""").Render(null, new TemplateRenderOptions { EmbedSources = opts.EmbedSources, MaxEmbedDepth = 16 }));
    }

    [Fact]
    public void Errors_inside_an_embed_are_positioned_in_the_embedded_text_and_wrapped_at_the_call()
    {
        var prompts = new PromptEmbedSource(new Dictionary<string, string> { ["bad"] = "line1\n{{ lower 5 }}" });
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("x\n{{ embed \"bad\" }}", new TemplateParseOptions { Name = "outer.md" }).Render(null, Opts(prompts)));
        Assert.Equal("outer.md", ex.TemplateName);
        Assert.Equal(2, ex.Line);
        var inner = Assert.IsType<TemplateException>(ex.InnerException);
        Assert.Equal("bad", inner.TemplateName);
        Assert.Equal(2, inner.Line);
    }

    [Fact]
    public void Embedded_text_renders_in_the_same_scope_with_code_regions_escaped()
    {
        var prompts = new PromptEmbedSource(new Dictionary<string, string> { ["e"] = "`{{.keep}}` {{.name}}" });
        Assert.Equal("`{{.keep}}` n", Template.Parse("""{{embed "e"}}""").Render(new Dictionary<string, object?> { ["name"] = "n" }, Opts(prompts)));
    }

    [Fact]
    public void A_source_can_refuse_a_reference_it_recognises()
    {
        var refuse = new RefusingSource();
        TemplateException ex = Assert.Throws<TemplateException>(() => Template.Parse("""{{embed "x"}}""").Render(null, Opts(refuse)));
        Assert.Contains("denied", ex.Message, StringComparison.Ordinal);
    }

    private sealed class RefusingSource : IEmbedSource
    {
        public EmbedContent? Resolve(string reference, string? referrerKey) => throw new TemplateFunctionException("denied by policy");
    }
}
