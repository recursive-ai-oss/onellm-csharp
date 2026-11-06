using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Builtins;

internal static class Kit
{
    public static ITool Tool(string name, BuiltinOptions? options = null) =>
        BuiltinTools.Create(options).Single(t => t.Definition.Name == name);

    public static async Task<ToolResult> Run(string tool, string json, ICapabilities caps, BuiltinOptions? options = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        var ctx = new ToolContext("inst-1", "call-1", time ?? TimeProvider.System) { Capabilities = caps };
        return await ToolInvoker.InvokeAsync(Tool(tool, options), (JsonObject)JsonNode.Parse(json)!, ctx, ct);
    }

    public static string Text(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));

    public static string One(string name, string jsonValue) => "{\"" + name + "\":" + jsonValue + "}";

    public static string Js(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}

public class FileToolTests
{
    [Fact]
    public async Task Read_returns_text_with_normalized_line_endings_and_optional_line_numbers()
    {
        using var s = new Scratch();
        File.WriteAllText(s.In("a.txt"), "one\r\ntwo\rthree\n");
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("one\ntwo\nthree\n", Kit.Text(await Kit.Run("read_file", """{"path":"a.txt"}""", caps)));
        Assert.Equal("   1 | one\n   2 | two\n   3 | three\n", Kit.Text(await Kit.Run("read_file", """{"path":"a.txt","includeLineNumbers":true}""", caps)));
    }

    [Fact]
    public void Line_number_gutter_grows_with_the_line_count()
    {
        string text = string.Join('\n', Enumerable.Range(1, 12000).Select(i => "x"));
        string numbered = ReadFileTool.AddLineNumbers(text);
        Assert.StartsWith("    1 | x\n", numbered, StringComparison.Ordinal);
        Assert.EndsWith("12000 | x", numbered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_lists_a_directory_one_level_with_sizes_and_a_more_count()
    {
        using var s = new Scratch();
        Directory.CreateDirectory(s.In("d/sub"));
        File.WriteAllBytes(s.In("d/big.bin"), new byte[1536]);
        File.WriteAllText(s.In("d/small.txt"), "hi");
        Capabilities caps = Capabilities.Files(s.Root);
        string listing = Kit.Text(await Kit.Run("read_file", """{"path":"d"}""", caps));
        Assert.Equal("**Directory Listing: d**\n\n- big.bin - 1.5 KB\n- small.txt - 2 B\n- sub/", listing);

        for (int i = 0; i < 60; i++)
        {
            s.Write($"many/f{i:00}.txt", "");
        }

        string many = Kit.Text(await Kit.Run("read_file", """{"path":"many"}""", caps));
        Assert.EndsWith("... and 10 more", many, StringComparison.Ordinal);
        Assert.Equal(50, many.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));

        Directory.CreateDirectory(s.In("empty"));
        Assert.Equal("**Directory Listing: empty**\n\n*(empty)*", Kit.Text(await Kit.Run("read_file", """{"path":"empty"}""", caps)));
    }

    [Fact]
    public async Task Read_returns_images_as_assets_and_binary_as_octet_stream()
    {
        using var s = new Scratch();
        File.WriteAllBytes(s.In("p.png"), [1, 2, 3, 4]);
        File.WriteAllBytes(s.In("blob.dat"), [0, 1, 2, 255]);
        Capabilities caps = Capabilities.Files(s.Root);
        var png = (AssetPart)(await Kit.Run("read_file", """{"path":"p.png"}""", caps)).Parts.Single();
        Assert.Equal("image/png", png.Asset!.ContentType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), png.Asset.Data);
        var bin = (AssetPart)(await Kit.Run("read_file", """{"path":"blob.dat"}""", caps)).Parts.Single();
        Assert.Equal("application/octet-stream", bin.Asset!.ContentType);
    }

    [Fact]
    public async Task Read_decodes_utf16_with_a_bom_and_errors_read_as_text()
    {
        using var s = new Scratch();
        File.WriteAllBytes(s.In("u16.txt"), [.. new byte[] { 0xFF, 0xFE }, .. System.Text.Encoding.Unicode.GetBytes("héllo")]);
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("héllo", Kit.Text(await Kit.Run("read_file", """{"path":"u16.txt"}""", caps)));
        ToolResult missing = await Kit.Run("read_file", """{"path":"nope.txt"}""", caps);
        Assert.True(missing.IsError);
        Assert.StartsWith("Error: ", Kit.Text(missing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_tools_refuse_paths_outside_the_grant_with_an_error_result()
    {
        using var s = new Scratch();
        File.WriteAllText(Path.Combine(s.Outside, "secret.txt"), "s");
        Capabilities caps = Capabilities.Files(s.Root);
        foreach ((string tool, string args) in new[]
        {
            ("read_file", Kit.One("path", Kit.Js(Path.Combine(s.Outside, "secret.txt")))),
            ("read_file", """{"path":"../outside/secret.txt"}"""),
            ("write_file", """{"path":"../outside/new.txt","content":"x"}"""),
            ("list_files", """{"directory":"../outside"}"""),
            ("search_files_regex", """{"path":"../outside","pattern":"s"}"""),
            ("replace_string", """{"filePath":"../outside/secret.txt","replacements":[{"find":"s","replace":"t"}]}"""),
            ("apply_patch", "{\"patch\":\"--- ../outside/secret.txt\\n+++ ../outside/secret.txt\\n@@ -1 +1 @@\\n-s\\n+t\\n\"}"),
        })
        {
            ToolResult r = await Kit.Run(tool, args, caps);
            Assert.True(r.IsError, tool);
            Assert.Contains("is not available", Kit.Text(r), StringComparison.Ordinal);
        }

        Assert.Equal("s", File.ReadAllText(Path.Combine(s.Outside, "secret.txt")));
        Assert.False(File.Exists(Path.Combine(s.Outside, "new.txt")));
    }

    [Fact]
    public async Task File_tools_refuse_everything_when_no_files_are_granted()
    {
        using var s = new Scratch();
        s.Write("a.txt", "x");
        ToolResult r = await Kit.Run("read_file", Kit.One("path", Kit.Js(s.In("a.txt"))), Capabilities.None);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Write_creates_parents_reports_bytes_and_appends()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        ToolResult r = await Kit.Run("write_file", """{"path":"x/y/z.txt","content":"héllo"}""", caps);
        Assert.Equal($"Successfully wrote 6 bytes to {s.In("x/y/z.txt")}", Kit.Text(r));
        await Kit.Run("write_file", """{"path":"x/y/z.txt","content":"!","append":true}""", caps);
        Assert.Equal("héllo!", File.ReadAllText(s.In("x/y/z.txt")));
        await Kit.Run("write_file", """{"path":"x/y/z.txt","content":"new"}""", caps);
        Assert.Equal("new", File.ReadAllText(s.In("x/y/z.txt")));
        Assert.Equal(3, File.ReadAllBytes(s.In("x/y/z.txt")).Length);
    }

    [Fact]
    public async Task Write_is_refused_under_a_read_only_grant()
    {
        using var s = new Scratch();
        ToolResult r = await Kit.Run("write_file", """{"path":"a.txt","content":"x"}""", Capabilities.ReadOnlyFiles(s.Root));
        Assert.True(r.IsError);
        Assert.False(File.Exists(s.In("a.txt")));
    }

    [Fact]
    public async Task Write_through_a_symlink_that_leaves_the_root_is_refused()
    {
        using var s = new Scratch();
        string secret = Path.Combine(s.Outside, "secret.txt");
        File.WriteAllText(secret, "keep");
        if (!Scratch.TryLink(s.In("l.txt"), secret, directory: false))
        {
            return;
        }

        ToolResult r = await Kit.Run("write_file", """{"path":"l.txt","content":"overwritten"}""", Capabilities.Files(s.Root));
        Assert.True(r.IsError);
        Assert.Equal("keep", File.ReadAllText(secret));
    }

    [Fact]
    public async Task Replace_changes_every_occurrence_in_order_and_reports()
    {
        using var s = new Scratch();
        s.Write("f.txt", "a b a\nc a\n");
        ToolResult r = await Kit.Run("replace_string", """{"filePath":"f.txt","replacements":[{"find":"a","replace":"x"},{"find":"c","replace":"y"},{"find":"zzz","replace":"q"}]}""", Capabilities.Files(s.Root));
        Assert.Equal("x b x\ny x\n", File.ReadAllText(s.In("f.txt")));
        string text = Kit.Text(r);
        Assert.StartsWith($"Successfully made 4 total replacement(s) in {s.In("f.txt")}:\n  - 3 occurrence(s) of \"a\"\n  - 1 occurrence(s) of \"c\"\n\nFile updated: ", text, StringComparison.Ordinal);
        Assert.Contains("\nPrevious file size: 10 bytes\nNew file hash: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_matches_across_line_ending_styles_and_keeps_the_files_style()
    {
        using var s = new Scratch();
        File.WriteAllBytes(s.In("crlf.txt"), System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n"));
        await Kit.Run("replace_string", """{"filePath":"crlf.txt","replacements":[{"find":"one\ntwo","replace":"1\n2\n2b"}]}""", Capabilities.Files(s.Root));
        Assert.Equal("1\r\n2\r\n2b\r\nthree\r\n", File.ReadAllText(s.In("crlf.txt")));
    }

    [Fact]
    public async Task Replace_reports_no_match_and_rejects_identical_or_empty_finds()
    {
        using var s = new Scratch();
        s.Write("f.txt", "hello");
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal($"No matches found for any find strings in {s.In("f.txt")}", Kit.Text(await Kit.Run("replace_string", """{"filePath":"f.txt","replacements":[{"find":"zzz","replace":"y"}]}""", caps)));
        ToolResult same = await Kit.Run("replace_string", """{"filePath":"f.txt","replacements":[{"find":"hello","replace":"hello"}]}""", caps);
        Assert.Equal("Error: replacements[0].find and replace are identical (both \"hello\") - no operation needed", Kit.Text(same));
        Assert.Equal("Error: replacements[0].find is required", Kit.Text(await Kit.Run("replace_string", """{"filePath":"f.txt","replacements":[{"find":"","replace":"y"}]}""", caps)));
    }

    [Fact]
    public async Task Replace_accepts_one_unique_fuzzy_match_of_separator_runs_and_flags_it()
    {
        using var s = new Scratch();
        s.Write("f.md", "title\n==========\nbody\n");
        ToolResult r = await Kit.Run("replace_string", """{"filePath":"f.md","replacements":[{"find":"────────────────","replace":"----"}]}""", Capabilities.Files(s.Root));
        Assert.Equal("title\n----\nbody\n", File.ReadAllText(s.In("f.md")));
        Assert.Contains("(fuzzy match — long runs of separator glyphs collapsed; verify the replacement landed where you expected)", Kit.Text(r), StringComparison.Ordinal);

        s.Write("two.md", "======\nx\n======\n");
        ToolResult ambiguous = await Kit.Run("replace_string", """{"filePath":"two.md","replacements":[{"find":"-------","replace":"+"}]}""", Capabilities.Files(s.Root));
        Assert.StartsWith("No matches found", Kit.Text(ambiguous), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_keeps_a_utf8_bom_writes_utf16_back_as_utf8_and_refuses_binary()
    {
        using var s = new Scratch();
        File.WriteAllBytes(s.In("bom.txt"), [0xEF, 0xBB, 0xBF, .. "abc"u8.ToArray()]);
        File.WriteAllBytes(s.In("u16.txt"), [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("abc")]);
        File.WriteAllBytes(s.In("bin.dat"), [0, 1, 2, 0xFF]);
        Capabilities caps = Capabilities.Files(s.Root);
        await Kit.Run("replace_string", """{"filePath":"bom.txt","replacements":[{"find":"b","replace":"B"}]}""", caps);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'B', (byte)'c'], File.ReadAllBytes(s.In("bom.txt")));
        await Kit.Run("replace_string", """{"filePath":"u16.txt","replacements":[{"find":"b","replace":"B"}]}""", caps);
        Assert.Equal("aBc"u8.ToArray(), File.ReadAllBytes(s.In("u16.txt")));
        ToolResult bin = await Kit.Run("replace_string", """{"filePath":"bin.dat","replacements":[{"find":"a","replace":"b"}]}""", caps);
        Assert.True(bin.IsError);
        Assert.Equal([0, 1, 2, 0xFF], File.ReadAllBytes(s.In("bin.dat")));
    }
}

public class ListAndSearchToolTests
{
    private static Scratch Tree()
    {
        var s = new Scratch();
        s.Write("src/app.cs", new string('x', 1500));
        s.Write("src/util/helper.cs", "class Helper { }\n");
        s.Write("docs/readme.md", "hello\n");
        s.Write("build/out.log", "log\n");
        s.Write("node_modules/pkg/index.js", "x\n");
        s.Write(".git/config", "x\n");
        s.Write(".gitignore", "build/\n*.tmp\n");
        s.Write("scratch.tmp", "t\n");
        return s;
    }

    [Fact]
    public async Task List_prints_a_tree_with_directories_first_and_honours_gitignore_and_git()
    {
        using Scratch s = Tree();
        string tree = Kit.Text(await Kit.Run("list_files", """{"exclusionPatterns":["node_modules/"]}""", Capabilities.Files(s.Root)));
        Assert.Equal(
            "├── docs/\n│   └── readme.md  (6B)\n├── src/\n│   ├── util/\n│   │   └── helper.cs  (17B)\n│   └── app.cs  (1.5KB)\n└── .gitignore  (13B)",
            tree);
    }

    [Fact]
    public async Task List_supports_depth_inclusion_exclusion_and_omitting_empty_directories()
    {
        using Scratch s = Tree();
        Capabilities caps = Capabilities.Files(s.Root);
        string shallow = Kit.Text(await Kit.Run("list_files", """{"maxChildDepth":0,"excludeGitIgnoreMatches":false}""", caps));
        Assert.Contains("├── build/", shallow, StringComparison.Ordinal);
        Assert.DoesNotContain("out.log", shallow, StringComparison.Ordinal);
        Assert.DoesNotContain(".git/", shallow, StringComparison.Ordinal);

        string cs = Kit.Text(await Kit.Run("list_files", """{"inclusionPatterns":["*.cs"],"omitEmptyDirs":true}""", caps));
        Assert.Contains("helper.cs", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("docs", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("readme", cs, StringComparison.Ordinal);

        string anchored = Kit.Text(await Kit.Run("list_files", """{"exclusionPatterns":["/src/","**/readme.md"]}""", caps));
        Assert.DoesNotContain("src", anchored, StringComparison.Ordinal);
        Assert.DoesNotContain("readme.md", anchored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_reports_missing_directories_files_and_empty_results()
    {
        using Scratch s = Tree();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("Error: Directory does not exist: nope", Kit.Text(await Kit.Run("list_files", """{"directory":"nope"}""", caps)));
        Assert.Equal("Error: Path is not a directory: .gitignore", Kit.Text(await Kit.Run("list_files", """{"directory":".gitignore"}""", caps)));
        Directory.CreateDirectory(s.In("blank"));
        Assert.Equal("(empty directory)", Kit.Text(await Kit.Run("list_files", """{"directory":"blank"}""", caps)));
    }

    [Fact]
    public async Task List_shows_symlinks_without_following_them_out_of_the_root()
    {
        using Scratch s = Tree();
        File.WriteAllText(Path.Combine(s.Outside, "o.txt"), "outside");
        if (!Scratch.TryLink(s.In("out-link"), s.Outside, directory: true))
        {
            return;
        }

        string tree = Kit.Text(await Kit.Run("list_files", "{}", Capabilities.Files(s.Root)));
        Assert.Contains("out-link/ (symlink)", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("o.txt", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_finds_matches_in_files_with_line_numbers_and_skips_ignored_and_binary_files()
    {
        using Scratch s = Tree();
        s.Write("src/a.cs", "int x;\nclass Target { }\nint y;\n");
        s.Write("docs/b.md", "no hit\n");
        File.WriteAllBytes(s.In("src/img.png"), "class Target"u8.ToArray());
        s.Write("build/gen.cs", "class Target\n");
        string text = Kit.Text(await Kit.Run("search_files_regex", """{"path":".","pattern":"class target"}""", Capabilities.Files(s.Root)));
        Assert.Equal("1 matches found:\n**src/a.cs**\n    2> class Target { }\n", text);
    }

    [Fact]
    public async Task Search_single_file_shows_context_lines_and_merges_close_hits()
    {
        using var s = new Scratch();
        s.Write("f.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => i is 5 or 8 or 25 ? "hit " + i : "line " + i)) + "\n");
        string text = Kit.Text(await Kit.Run("search_files_regex", """{"path":"f.txt","pattern":"^hit|hit \\d+","context_lines_before":1,"context_lines_after":1}""", Capabilities.Files(s.Root)));
        Assert.Equal("  4: line 4\n  5> hit 5\n  6: line 6\n  7: line 7\n  8> hit 8\n  9: line 9\n...\n 24: line 24\n 25> hit 25\n 26: line 26", text);
    }

    [Fact]
    public async Task Search_reports_name_matches_no_matches_and_bad_patterns()
    {
        using Scratch s = Tree();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal("1 matches found:\n**util/helper.cs**\n", Kit.Text(await Kit.Run("search_files_regex", """{"path":"src","pattern":"helper\\.cs$","search_file_names_only":true}""", caps)));
        Assert.Equal("No matches found.", Kit.Text(await Kit.Run("search_files_regex", """{"path":"docs","pattern":"zzzz"}""", caps)));
        ToolResult bad = await Kit.Run("search_files_regex", """{"path":"docs","pattern":"(unclosed"}""", caps);
        Assert.True(bad.IsError);
        Assert.StartsWith("ERROR: Invalid regex pattern: ", Kit.Text(bad), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_uses_a_linear_time_engine_so_a_hostile_pattern_finishes()
    {
        using var s = new Scratch();
        s.Write("f.txt", new string('a', 50000) + "!\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string text = Kit.Text(await Kit.Run("search_files_regex", """{"path":"f.txt","pattern":"(a+)+$"}""", Capabilities.Files(s.Root)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), text);
    }

    [Fact]
    public async Task Search_does_not_read_through_a_symlink_that_leaves_the_root()
    {
        using var s = new Scratch();
        File.WriteAllText(Path.Combine(s.Outside, "o.txt"), "needle outside\n");
        s.Write("in.txt", "needle inside\n");
        if (!Scratch.TryLink(s.In("link.txt"), Path.Combine(s.Outside, "o.txt"), directory: false))
        {
            return;
        }

        string text = Kit.Text(await Kit.Run("search_files_regex", """{"path":".","pattern":"needle"}""", Capabilities.Files(s.Root)));
        Assert.Contains("in.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("link.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_output_is_cut_to_the_token_budget()
    {
        using var s = new Scratch();
        for (int f = 0; f < 40; f++)
        {
            s.Write($"f{f:00}.txt", string.Join('\n', Enumerable.Range(0, 30).Select(i => $"match number {i} in file {f} with some padding text")));
        }

        string text = Kit.Text(await Kit.Run("search_files_regex", """{"path":".","pattern":"match number"}""", Capabilities.Files(s.Root)));
        Assert.StartsWith("40 matches found:\n", text, StringComparison.Ordinal);
        Assert.Contains("**Output Truncated:**", text, StringComparison.Ordinal);
        Assert.True(text.Length < 12000, text.Length.ToString());
    }
}

public class ApplyPatchToolTests
{
    private static string Patch(string body) => System.Text.Json.JsonSerializer.Serialize(new { patch = body });

    [Fact]
    public async Task A_patch_modifies_creates_and_deletes_files_and_reports_counts()
    {
        using var s = new Scratch();
        s.Write("a.txt", "one\ntwo\nthree\nfour\n");
        s.Write("gone.txt", "bye\n");
        string diff = "--- a/a.txt\n+++ b/a.txt\n@@ -1,4 +1,4 @@\n one\n-two\n+TWO\n three\n four\n--- /dev/null\n+++ b/new/n.txt\n@@ -0,0 +1,2 @@\n+x\n+y\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-bye\n";
        ToolResult r = await Kit.Run("apply_patch", Patch(diff), Capabilities.Files(s.Root));
        Assert.False(r.IsError, Kit.Text(r));
        Assert.Equal("Applied patch to 3 file(s):\n  modified a.txt (+1 -1)\n  created new/n.txt (+2 -0)\n  deleted gone.txt (+0 -1)", Kit.Text(r));
        Assert.Equal("one\nTWO\nthree\nfour\n", File.ReadAllText(s.In("a.txt")));
        Assert.Equal("x\ny\n", File.ReadAllText(s.In("new/n.txt")));
        Assert.False(File.Exists(s.In("gone.txt")));
    }

    [Fact]
    public async Task Hunks_apply_at_the_nearest_match_when_line_numbers_are_off()
    {
        using var s = new Scratch();
        s.Write("a.txt", "pad\npad\npad\nkeep\nold\nkeep2\n");
        string diff = "--- a.txt\n+++ a.txt\n@@ -1,3 +1,3 @@\n keep\n-old\n+new\n keep2\n";
        ToolResult r = await Kit.Run("apply_patch", Patch(diff), Capabilities.Files(s.Root));
        Assert.False(r.IsError, Kit.Text(r));
        Assert.Equal("pad\npad\npad\nkeep\nnew\nkeep2\n", File.ReadAllText(s.In("a.txt")));
    }

    [Fact]
    public async Task A_patch_that_does_not_apply_writes_nothing_even_to_the_files_that_would_have_applied()
    {
        using var s = new Scratch();
        s.Write("a.txt", "one\n");
        s.Write("b.txt", "two\n");
        string diff = "--- a.txt\n+++ a.txt\n@@ -1 +1 @@\n-one\n+ONE\n--- b.txt\n+++ b.txt\n@@ -1 +1 @@\n-different\n+TWO\n";
        ToolResult r = await Kit.Run("apply_patch", Patch(diff), Capabilities.Files(s.Root));
        Assert.True(r.IsError);
        Assert.Contains("b.txt", Kit.Text(r), StringComparison.Ordinal);
        Assert.Equal("one\n", File.ReadAllText(s.In("a.txt")));
        Assert.Equal("two\n", File.ReadAllText(s.In("b.txt")));
    }

    [Fact]
    public async Task Dry_run_checks_without_writing_and_crlf_files_keep_their_endings()
    {
        using var s = new Scratch();
        File.WriteAllBytes(s.In("c.txt"), "a\r\nb\r\n"u8.ToArray());
        string diff = "--- c.txt\n+++ c.txt\n@@ -1,2 +1,2 @@\n a\n-b\n+B\n";
        ToolResult dry = await Kit.Run("apply_patch", System.Text.Json.JsonSerializer.Serialize(new { patch = diff, dry_run = true }), Capabilities.Files(s.Root));
        Assert.StartsWith("Patch applies cleanly to 1 file(s) (dry run, nothing written):", Kit.Text(dry), StringComparison.Ordinal);
        Assert.Equal("a\r\nb\r\n", File.ReadAllText(s.In("c.txt")));
        await Kit.Run("apply_patch", Patch(diff), Capabilities.Files(s.Root));
        Assert.Equal("a\r\nB\r\n", File.ReadAllText(s.In("c.txt")));
    }

    [Fact]
    public async Task No_newline_at_end_of_file_is_honoured_and_creating_an_existing_file_is_refused()
    {
        using var s = new Scratch();
        File.WriteAllText(s.In("n.txt"), "a\nb");
        string diff = "--- n.txt\n+++ n.txt\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n";
        await Kit.Run("apply_patch", Patch(diff), Capabilities.Files(s.Root));
        Assert.Equal("a\nc", File.ReadAllText(s.In("n.txt")));
        ToolResult exists = await Kit.Run("apply_patch", Patch("--- /dev/null\n+++ n.txt\n@@ -0,0 +1 @@\n+z\n"), Capabilities.Files(s.Root));
        Assert.True(exists.IsError);
        Assert.Equal("a\nc", File.ReadAllText(s.In("n.txt")));
    }

    [Fact]
    public async Task Malformed_patches_and_renames_are_errors()
    {
        using var s = new Scratch();
        s.Write("a.txt", "x\n");
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.True((await Kit.Run("apply_patch", Patch("not a patch"), caps)).IsError);
        Assert.True((await Kit.Run("apply_patch", Patch("--- a.txt\n+++ b.txt\n@@ -1 +1 @@\n-x\n+y\n"), caps)).IsError);
        Assert.True((await Kit.Run("apply_patch", Patch("--- a.txt\n+++ a.txt\n@@ -1,3 +1,3 @@\n-x\n+y\n"), caps)).IsError);
        Assert.Equal("x\n", File.ReadAllText(s.In("a.txt")));
    }
}
