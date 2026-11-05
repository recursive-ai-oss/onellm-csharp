using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;

namespace OneLlm.Web.Tests;

public class HtmlToMarkdownTests
{
    [Fact]
    public void Headings_paragraphs_emphasis_links_and_code_become_markdown()
    {
        string md = HtmlToMarkdown.Convert("""
            <html><head><title>T</title><style>p{color:red}</style><script>alert(1)</script></head>
            <body><h1>Main  title</h1><p>Hello <b>bold</b> and <em>soft</em> with <a href="/docs/x">a link</a>
            and <code>code()</code>.</p><h3>Sub</h3><p>Second<br>line</p></body></html>
            """, new Uri("https://example.com/page"));
        Assert.Equal("# Main title\n\nHello **bold** and *soft* with [a link](https://example.com/docs/x) and `code()`.\n\n### Sub\n\nSecond\nline", md);
    }

    [Fact]
    public void Lists_nest_and_numbers_follow_the_start_attribute()
    {
        string md = HtmlToMarkdown.Convert("<ul><li>one<ul><li>inner</li></ul></li><li>two</li></ul><ol start=3><li>c</li><li>d</li></ol>");
        Assert.Equal("- one\n  - inner\n- two\n\n3. c\n4. d", md);
    }

    [Fact]
    public void Preformatted_text_quotes_rules_and_tables_are_kept()
    {
        string md = HtmlToMarkdown.Convert("<pre><code>a  b\n  c</code></pre><blockquote><p>quoted</p></blockquote><hr><table><tr><th>A</th><th>B|x</th></tr><tr><td>1</td><td>2</td></tr></table>");
        Assert.Equal("```\na  b\n  c\n```\n\n> quoted\n\n---\n\n| A | B\\|x |\n| --- | --- |\n| 1 | 2 |", md);
    }

    [Fact]
    public void Hidden_content_scripts_and_unsafe_links_are_left_out()
    {
        string md = HtmlToMarkdown.Convert("""
            <p>shown</p><p hidden>secret</p><div style="display: none">gone</div><p aria-hidden="true">no</p>
            <noscript>x</noscript><p><a href="javascript:alert(1)">click</a> <a href="#top">top</a> <img src="/i.png" alt="pic"></p>
            """, new Uri("https://e.com/"));
        Assert.Equal("shown\n\nclick top ![pic](https://e.com/i.png)", md);
    }

    [Fact]
    public void Malformed_markup_is_read_as_a_browser_would_and_deep_nesting_does_not_overflow()
    {
        Assert.Equal("one\n\ntwo\n\nthree", HtmlToMarkdown.Convert("<p>one<p>two</p>three"));
        string deep = string.Concat(Enumerable.Repeat("<div>", 3000)) + "bottom" + string.Concat(Enumerable.Repeat("</div>", 3000));
        Assert.Contains("bottom", HtmlToMarkdown.Convert(deep), StringComparison.Ordinal);
    }
}

public class FetchWebpageMarkdownToolTests
{
    private static async Task<ToolResult> Run(string json, ICapabilities caps, FetchWebpageMarkdownTool? tool = null)
    {
        var ctx = new ToolContext("i", "c", TimeProvider.System) { Capabilities = caps };
        return await ToolInvoker.InvokeAsync(tool ?? new FetchWebpageMarkdownTool(), (JsonObject)JsonNode.Parse(json)!, ctx, CancellationToken.None);
    }

    private static string Text(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));

    [Fact]
    public void The_definition_matches_the_name_and_arguments_existing_definitions_use()
    {
        var tool = new FetchWebpageMarkdownTool();
        Assert.Equal("fetch_webpage_markdown", tool.Definition.Name);
        Assert.Equal("string", tool.Definition.InputSchema["properties"]!["url"]!["type"]!.GetValue<string>());
        Assert.Equal(["url"], tool.Definition.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(OneLlm.Tools.Builtins.ToolCapability.Network, tool.Requires);
    }

    [Fact]
    public async Task A_page_on_a_granted_host_comes_back_as_markdown()
    {
        using var server = new StubServer(_ => new StubResponse(200, "<html><body><h1>Hi</h1><p>See <a href=\"/more\">more</a></p></body></html>", "text/html; charset=utf-8"));
        ToolResult r = await Run($$"""{"url":"{{server.BaseUri}}page"}""", Capabilities.Network("127.0.0.1"));
        Assert.False(r.IsError, Text(r));
        Assert.Equal($"# Hi\n\nSee [more]({server.BaseUri}more)", Text(r));
    }

    [Fact]
    public async Task A_page_in_a_legacy_encoding_is_decoded_from_its_header()
    {
        using var server = new StubServer(_ => new StubResponse(200, ContentType: "text/html; charset=iso-8859-1", Bytes: [.. "<p>caf"u8.ToArray(), 0xE9, .. "</p>"u8.ToArray()]));
        Assert.Equal("café", Text(await Run($$"""{"url":"{{server.BaseUri}}"}""", Capabilities.Network("127.0.0.1"))));
    }

    [Fact]
    public async Task An_ungranted_host_and_a_redirect_to_one_are_refused()
    {
        using var hidden = new StubServer(_ => new StubResponse(200, "<p>secret</p>", "text/html"));
        using var server = new StubServer(_ => new StubResponse(302, "", Headers: new() { ["Location"] = $"http://localhost:{hidden.Port}/" }));
        ToolResult off = await Run($$"""{"url":"{{server.BaseUri}}"}""", Capabilities.Files("."));
        Assert.True(off.IsError);
        Assert.Contains("Network access to 127.0.0.1 is not available", Text(off), StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        ToolResult hop = await Run($$"""{"url":"{{server.BaseUri}}"}""", Capabilities.Network("127.0.0.1"));
        Assert.True(hop.IsError);
        Assert.Empty(hidden.Requests);
    }

    [Fact]
    public async Task Other_text_types_pass_through_and_pdfs_and_errors_are_reported()
    {
        using var server = new StubServer(r => r.Target switch
        {
            "/md" => new StubResponse(200, "# already\n", "text/markdown"),
            "/pdf" => new StubResponse(200, "%PDF", "application/pdf"),
            "/img" => new StubResponse(200, ContentType: "image/png", Bytes: [1, 2]),
            _ => new StubResponse(404, "nope"),
        });
        Capabilities caps = Capabilities.Network("127.0.0.1");
        Assert.Equal("# already\n", Text(await Run($$"""{"url":"{{server.BaseUri}}md"}""", caps)));
        Assert.Equal("Error: PDF documents are not supported by fetch_webpage_markdown.", Text(await Run($$"""{"url":"{{server.BaseUri}}pdf"}""", caps)));
        Assert.Contains("image/png", Text(await Run($$"""{"url":"{{server.BaseUri}}img"}""", caps)), StringComparison.Ordinal);
        Assert.Equal("Error: Failed to fetch webpage: the server answered 404 Not Found", Text(await Run($$"""{"url":"{{server.BaseUri}}missing"}""", caps)));
        Assert.True((await Run("""{"url":"ftp://x/y"}""", caps)).IsError);
    }

    [Fact]
    public async Task Long_pages_are_cut_at_the_limit_with_a_note()
    {
        string html = "<body>" + string.Concat(Enumerable.Range(0, 400).Select(i => $"<p>paragraph {i} with some filler words to take space</p>")) + "</body>";
        using var server = new StubServer(_ => new StubResponse(200, html, "text/html"));
        ToolResult r = await Run($$"""{"url":"{{server.BaseUri}}"}""", Capabilities.Network("127.0.0.1"), new FetchWebpageMarkdownTool(maxMarkdownBytes: 2048));
        string text = Text(r);
        Assert.Contains("[Content truncated — page was ", text, StringComparison.Ordinal);
        Assert.Contains("limit is 2048 bytes.", text, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) < 2048 + 300);
    }
}
