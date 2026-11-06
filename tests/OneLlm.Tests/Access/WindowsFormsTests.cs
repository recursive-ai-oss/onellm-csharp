using System.Diagnostics;
using System.Text.Json.Nodes;
using OneLlm.Tests.Builtins;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Access;

/// <summary>
/// The Windows forms of the capability model: junctions, 8.3 short names, case, drive-relative and network paths, alternate data streams,
/// device names, trailing dots, and cmd.exe / PowerShell / batch file argument handling. They run on Windows only; the POSIX forms are
/// covered by the other test classes.
/// </summary>
public class WindowsFormsTests
{
    private const string Marker = "OUTSIDE-SECRET-CONTENT";

    private static void WindowsOnly() => Assert.SkipUnless(OperatingSystem.IsWindows(), "These cases are about Windows path and shell forms.");

    private static string Args(string path, string content = "x") => new JsonObject { ["path"] = path, ["content"] = content }.ToJsonString();

    private static string One(string name, string value) => new JsonObject { [name] = value }.ToJsonString();

    private static string Exec(string command, string[]? args = null, int? timeout = null)
    {
        var o = new JsonObject { ["command"] = command };
        if (args is not null)
        {
            o["args"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]);
        }

        if (timeout is int t)
        {
            o["timeout"] = t;
        }

        return o.ToJsonString();
    }

    /// <summary>Runs a program and returns what it printed; used for mklink and for asking Windows for a short name.</summary>
    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    private static void Junction(string link, string target) => Run("cmd.exe", "/c", "mklink", "/J", link, target);

    /// <summary>The 8.3 form of a path, as Windows reports it; equal to the input where the volume has short names switched off.</summary>
    private static string ShortPath(Scratch s, string path)
    {
        string script = Path.Combine(s.Base, "short.cmd");
        File.WriteAllText(script, "@echo off\r\nfor %%I in (\"%~1\") do echo %%~sI\r\n");
        return Run(script, path);
    }

    // ---- junctions and symbolic links

    [Fact]
    public async Task Junctions_and_symlinks_out_of_a_root_are_refused_by_every_file_tool()
    {
        WindowsOnly();
        using var s = new Scratch();
        File.WriteAllText(Path.Combine(s.Outside, "secret.txt"), Marker);
        Junction(s.In("jct"), s.Outside);
        Directory.CreateSymbolicLink(s.In("sdir"), s.Outside);
        File.CreateSymbolicLink(s.In("flink.txt"), Path.Combine(s.Outside, "secret.txt"));
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.True(Directory.Exists(s.In("jct")), "the junction was not created");

        foreach (string link in new[] { "jct", "sdir" })
        {
            Assert.True((await Kit.Run("read_file", One("path", link + "\\secret.txt"), caps)).IsError, link);
            Assert.True((await Kit.Run("read_file", One("path", link + "/secret.txt"), caps)).IsError, link);
            Assert.True((await Kit.Run("write_file", Args(link + "\\new.txt"), caps)).IsError, link);
            Assert.False(File.Exists(Path.Combine(s.Outside, "new.txt")), link);
            Assert.True((await Kit.Run("list_files", One("directory", link), caps)).IsError, link);
            Assert.True((await Kit.Run("search_files_regex", new JsonObject { ["path"] = link, ["pattern"] = "SECRET" }.ToJsonString(), caps)).IsError, link);
            string patch = $"--- /dev/null\n+++ b/{link}/created.txt\n@@ -0,0 +1 @@\n+x\n";
            Assert.True((await Kit.Run("apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString(), caps)).IsError, link);
            Assert.False(File.Exists(Path.Combine(s.Outside, "created.txt")), link);
            string modify = $"--- a/{link}/secret.txt\n+++ b/{link}/secret.txt\n@@ -1 +1 @@\n-{Marker}\n+changed\n";
            Assert.True((await Kit.Run("apply_patch", new JsonObject { ["patch"] = modify }.ToJsonString(), caps)).IsError, link);
            Assert.Equal(Marker, File.ReadAllText(Path.Combine(s.Outside, "secret.txt")));
        }

        Assert.True((await Kit.Run("read_file", One("path", "flink.txt"), caps)).IsError);
        Assert.True((await Kit.Run("write_file", Args("flink.txt", "changed"), caps)).IsError);
        Assert.Equal(Marker, File.ReadAllText(Path.Combine(s.Outside, "secret.txt")));

        // A search or listing from the root does not look through the links either.
        string tree = Kit.Text(await Kit.Run("list_files", "{}", caps));
        Assert.DoesNotContain("secret.txt", tree, StringComparison.Ordinal);
        string hits = Kit.Text(await Kit.Run("search_files_regex", new JsonObject { ["pattern"] = "SECRET" }.ToJsonString(), caps));
        Assert.DoesNotContain(Marker, hits, StringComparison.Ordinal);
    }

    [Fact]
    public void A_junction_to_a_directory_inside_the_root_is_followed_to_its_real_path()
    {
        WindowsOnly();
        using var s = new Scratch();
        Directory.CreateDirectory(s.In("real"));
        Junction(s.In("alias"), s.In("real"));
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal(s.In("real\\a.txt"), caps.ResolveWrite("alias\\a.txt"));
    }

    // ---- 8.3 short names

    [Fact]
    public async Task A_short_name_does_not_walk_around_the_protected_names()
    {
        WindowsOnly();
        using var s = new Scratch();
        Directory.CreateDirectory(s.In(".git"));
        Directory.CreateDirectory(s.In(".onellm\\instances"));
        File.WriteAllText(s.In(".onellm\\instances\\i-1.yaml"), "TRANSCRIPT-OF-ANOTHER-AGENT");
        Directory.CreateDirectory(s.In("Long Directory Name"));
        Capabilities caps = Capabilities.Files(s.Root);

        string gitShort = Path.GetFileName(ShortPath(s, s.In(".git")));
        string stateShort = Path.GetFileName(ShortPath(s, s.In(".onellm")));
        string longShort = Path.GetFileName(ShortPath(s, s.In("Long Directory Name")));
        if (gitShort.Equals(".git", StringComparison.OrdinalIgnoreCase) || !gitShort.Contains('~', StringComparison.Ordinal))
        {
            // The volume has 8.3 names switched off, so no alias exists to test.
            return;
        }

        ToolResult w = await Kit.Run("write_file", Args(gitShort + "\\config", "[core]\n\tfsmonitor = x\n"), caps);
        Assert.True(w.IsError, gitShort);
        Assert.False(File.Exists(s.In(".git\\config")));
        Assert.True((await Kit.Run("write_file", Args(gitShort + "\\hooks\\pre-commit"), caps)).IsError);
        ToolResult stateWrite = await Kit.Run("write_file", Args(stateShort + "\\instances\\evil.yaml"), caps);
        Assert.True(stateWrite.IsError, $"state alias [{stateShort}] root [{s.Root}]: {Kit.Text(stateWrite)}");
        ToolResult read = await Kit.Run("read_file", One("path", stateShort + "\\instances\\i-1.yaml"), caps);
        Assert.True(read.IsError);
        Assert.DoesNotContain("TRANSCRIPT", Kit.Text(read), StringComparison.Ordinal);

        // Any directory reached by its alias is refused; the full name works.
        Assert.True((await Kit.Run("write_file", Args(longShort + "\\a.txt"), caps)).IsError);
        Assert.False((await Kit.Run("write_file", Args("Long Directory Name\\a.txt"), caps)).IsError);
        // And the real names stay protected, as on any platform.
        Assert.True((await Kit.Run("write_file", Args(".git\\config"), caps)).IsError);
        Assert.True((await Kit.Run("read_file", One("path", ".onellm\\instances\\i-1.yaml"), caps)).IsError);
    }

    [Fact]
    public void A_short_name_of_the_root_or_of_an_outside_directory_is_not_inside()
    {
        WindowsOnly();
        using var s = new Scratch();
        string longRoot = Path.Combine(s.Base, "Long Root Name");
        string longOutside = Path.Combine(s.Base, "Long Outside Name");
        Directory.CreateDirectory(longRoot);
        Directory.CreateDirectory(longOutside);
        File.WriteAllText(Path.Combine(longOutside, "o.txt"), Marker);
        Capabilities caps = Capabilities.Files(longRoot);
        string shortRoot = ShortPath(s, longRoot);
        string shortOutside = ShortPath(s, longOutside);
        if (shortRoot.Equals(longRoot, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 names are switched off on this volume
        }

        // The root's alias is a different spelling than the grant, so it is refused rather than guessed at.
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(Path.Combine(shortRoot, "f.txt")));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(Path.Combine(shortOutside, "o.txt")));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(Path.Combine(longRoot, "..", Path.GetFileName(shortOutside), "o.txt")));
    }

    // ---- case

    [Fact]
    public async Task Names_and_roots_compare_without_regard_to_case()
    {
        WindowsOnly();
        using var s = new Scratch();
        Directory.CreateDirectory(s.In(".onellm\\instances"));
        File.WriteAllText(s.In(".onellm\\instances\\i-1.yaml"), "TRANSCRIPT");
        File.WriteAllText(s.In("a.txt"), "inside");
        Capabilities upperRoot = Capabilities.Files(s.Root.ToUpperInvariant());

        // A path in the other case is still inside, and still the same file.
        Assert.Equal("inside", Kit.Text(await Kit.Run("read_file", One("path", s.In("A.TXT")), upperRoot)).TrimEnd());
        Assert.False((await Kit.Run("write_file", Args(s.In("B.txt").ToLowerInvariant()), upperRoot)).IsError);

        foreach (string path in new[] { ".GIT\\config", ".Git\\hooks\\x", ".GITMODULES", ".HG\\hgrc", "sub\\.SVN\\x", "cvs\\Root", "_DARCS\\x", ".ONELLM\\notes.txt", ".OneLlm\\instances\\evil.yaml" })
        {
            ToolResult r = await Kit.Run("write_file", Args(path), upperRoot);
            Assert.True(r.IsError, path);
        }

        Assert.True((await Kit.Run("read_file", One("path", ".ONELLM\\instances\\i-1.yaml"), upperRoot)).IsError);
        Assert.True((await Kit.Run("read_file", One("path", s.In(".OneLlm\\instances\\i-1.yaml")), Capabilities.Files(s.Root))).IsError);

        // The same grant spelled in the other case allows exactly the same paths.
        Capabilities lowerRoot = Capabilities.Files(s.Root.ToLowerInvariant());
        Assert.Equal(upperRoot.ResolveRead("A.txt").ToLowerInvariant(), lowerRoot.ResolveRead("A.txt").ToLowerInvariant());
        Assert.Throws<CapabilityMissingException>(() => lowerRoot.ResolveRead(s.In("..\\x.txt")));
    }

    // ---- drive-relative, UNC and device forms

    [Fact]
    public void Drive_relative_unc_device_and_rooted_forms_are_refused()
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        string drive = Path.GetPathRoot(s.Root)![..2];
        string inside = s.In("a.txt");
        foreach (string path in new[]
        {
            drive + "foo.txt",
            drive + "..\\foo.txt",
            "\\foo.txt",
            "/foo.txt",
            "\\\\?\\" + inside,
            "\\\\.\\" + inside,
            "\\\\?\\UNC\\server\\share\\x",
            "\\\\server\\share\\x.txt",
            "//server/share/x.txt",
            "\\??\\" + inside,
            "\\\\localhost\\" + drive[0] + "$\\Windows\\win.ini",
            "Z:\\anything.txt",
        })
        {
            Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(path));
            Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite(path));
        }

        // Forward slashes and mixed separators inside the root are ordinary.
        Assert.Equal(s.In("sub\\inner\\a.txt"), caps.ResolveWrite("sub/inner\\a.txt"));
        Assert.Equal(inside, caps.ResolveRead(inside.Replace('\\', '/')));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("sub/../../x.txt"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("..\\" + Path.GetFileName(s.Outside) + "\\x.txt"));
    }

    // ---- alternate data streams, device names, trailing dots and spaces

    [Theory]
    [InlineData("a.txt:stream")]
    [InlineData("a.txt::$DATA")]
    [InlineData("dir:x\\f.txt")]
    [InlineData("a.txt:stream:$DATA")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("con")]
    [InlineData("CONOUT$")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("sub\\AUX")]
    [InlineData("LPT1.txt")]
    [InlineData("PRN.")]
    [InlineData("COM1 ")]
    [InlineData("COM\u00B9")]
    [InlineData("LPT\u00B2.txt")]
    public void Streams_and_device_names_are_refused(string path)
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(path));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite(path));
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("COM10")]
    [InlineData("nullable.txt")]
    [InlineData("auxiliary\\x")]
    public void Names_that_only_look_like_devices_are_ordinary(string path)
    {
        WindowsOnly();
        using var s = new Scratch();
        Assert.Equal(s.In(path), Capabilities.Files(s.Root).ResolveWrite(path));
    }

    [Fact]
    public async Task Trailing_dots_and_spaces_do_not_hide_a_protected_name()
    {
        WindowsOnly();
        using var s = new Scratch();
        Directory.CreateDirectory(s.In(".onellm\\instances"));
        File.WriteAllText(s.In(".onellm\\instances\\i-1.yaml"), "TRANSCRIPT");
        Capabilities caps = Capabilities.Files(s.Root);

        foreach (string path in new[] { ".git.\\config", ".git \\config", ".git. .\\config", ".onellm.\\instances\\evil.yaml", ".onellm \\instances\\evil.yaml", ".gitmodules.", ".bzr ." })
        {
            Assert.True((await Kit.Run("write_file", Args(path), caps)).IsError, path);
        }

        Assert.True((await Kit.Run("read_file", One("path", ".onellm.\\instances\\i-1.yaml"), caps)).IsError);
        Assert.True((await Kit.Run("read_file", One("path", ".onellm \\instances\\i-1.yaml"), caps)).IsError);
        Assert.False(File.Exists(s.In(".git\\config")));

        // A name made only of dots and spaces is refused; an ordinary trailing dot names the file Windows will create.
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("...\\x"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite(" \\x"));
        Assert.Equal(s.In("a.txt"), caps.ResolveWrite("a.txt."));
        Assert.Equal(s.In("a.txt"), caps.ResolveWrite("a.txt "));
    }

    // ---- the shell: cmd.exe, PowerShell and batch files

    [Fact]
    public async Task An_allow_listed_program_runs_and_one_that_is_not_listed_does_not()
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("hostname");
        ToolResult ok = await Kit.Run("execute_terminal_command", Exec("hostname"), caps);
        Assert.False(ok.IsError, Kit.Text(ok));
        Assert.False(string.IsNullOrWhiteSpace(Kit.Text(ok)));
        Assert.True((await Kit.Run("execute_terminal_command", Exec("whoami"), caps)).IsError);
        Assert.True((await Kit.Run("execute_terminal_command", Exec("hostname & whoami"), caps)).IsError);
        Assert.True((await Kit.Run("execute_terminal_command", Exec("cmd", ["/c", "whoami"]), caps)).IsError);
        // Not found on PATH is an error result, not a crash; and nothing in the working directory is looked up.
        File.WriteAllText(s.In("hostname.cmd"), "@echo planted\r\n");
        Assert.DoesNotContain("planted", Kit.Text(await Kit.Run("execute_terminal_command", Exec("hostname"), caps)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allow_listed_cmd_cannot_chain_a_second_command()
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("cmd /c echo");
        ToolResult plain = await Kit.Run("execute_terminal_command", Exec("cmd", ["/c", "echo", "hello"]), caps);
        Assert.False(plain.IsError, Kit.Text(plain));
        Assert.Equal("hello", Kit.Text(plain).Trim());

        string marker = s.In("pwned.txt");
        foreach (string[] args in new string[][]
        {
            new[] { "/c", "echo", "hi", "&", "echo", "x>" + marker },
            ["/c", "echo", "hi&echo x>" + marker],
            ["/c", "echo", "hi", "|", "more"],
            ["/c", "echo", "hi", ">", marker],
            ["/c", "echo", "hi", "^&", "whoami"],
            ["/c", "echo", "%PATH%"],
            ["/c", "echo", "!PATH!"],
            ["/c", "echo", "\"hi\" & whoami"],
            ["/c", "echo", "hi\r\nwhoami"],
        })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", Exec("cmd", args), caps);
            Assert.True(r.IsError, string.Join(' ', args));
            Assert.Contains("re-reads its arguments", Kit.Text(r), StringComparison.Ordinal);
        }

        Assert.False(File.Exists(marker));
        Assert.False(File.Exists("pwned.txt"));
    }

    [Fact]
    public async Task An_allow_listed_batch_file_gets_plain_text_arguments_only()
    {
        WindowsOnly();
        using var s = new Scratch();
        string bat = s.Write("tool.bat", "@echo off\r\necho got %1\r\n");
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands(bat);
        ToolResult ok = await Kit.Run("execute_terminal_command", Exec(bat, ["plain"]), caps);
        Assert.False(ok.IsError, Kit.Text(ok));
        Assert.Equal("got plain", Kit.Text(ok).Trim());

        string marker = s.In("pwned.txt");
        foreach (string arg in new[] { "a&echo x>" + marker, "a|more", "%PATH%", "a\"b", "a^b", "a>" + marker })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", Exec(bat, [arg]), caps);
            Assert.True(r.IsError, arg);
        }

        Assert.False(File.Exists(marker));

        // A batch file that is not on the list is not run, and a name without an extension does not pick up a .bat or .cmd from PATHEXT.
        s.Write("other.bat", "@echo off\r\necho other\r\n");
        Assert.True((await Kit.Run("execute_terminal_command", Exec(s.In("other.bat")), caps)).IsError);
        Capabilities named = Capabilities.Files(s.Root) + Capabilities.ShellCommands("tool");
        Assert.True((await Kit.Run("execute_terminal_command", Exec("tool"), named)).IsError);
    }

    [Fact]
    public async Task An_allow_listed_powershell_cannot_be_given_extra_statements()
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("powershell -NoProfile -Command");
        string marker = s.In("pwned.txt");
        foreach (string script in new[] { "Get-Date; New-Item " + marker, "Get-Date | Out-File " + marker, "$(New-Item " + marker + ")", "Get-Date `n calc", "& calc", "Get-Date > " + marker, "{ calc }" })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", Exec("powershell", ["-NoProfile", "-Command", script]), caps);
            Assert.True(r.IsError, script);
        }

        Assert.False(File.Exists(marker));
        Assert.True((await Kit.Run("execute_terminal_command", Exec("powershell", ["-NoProfile", "-EncodedCommand", "AAAA"]), caps)).IsError);
    }

    [Fact]
    public async Task An_open_shell_runs_through_cmd_and_a_timeout_kills_the_whole_tree()
    {
        WindowsOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
        string text = Kit.Text(await Kit.Run("execute_terminal_command", Exec("echo hi & echo there"), caps));
        Assert.Contains("hi", text, StringComparison.Ordinal);
        Assert.Contains("there", text, StringComparison.Ordinal);
        Assert.Equal(s.Root, Kit.Text(await Kit.Run("execute_terminal_command", Exec("cd"), caps)).Trim(), ignoreCase: true);

        var watch = Stopwatch.StartNew();
        ToolResult r = await Kit.Run("execute_terminal_command", Exec("cmd /c ping -n 40 127.0.0.1 > nul", timeout: 1), caps);
        Assert.True(r.IsError);
        Assert.Contains("timed out", Kit.Text(r), StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_command_starts_with_a_minimal_environment_that_still_has_what_windows_needs()
    {
        WindowsOnly();
        using var s = new Scratch();
        const string name = "ONELLM_BOUNDARY_PROBE";
        Environment.SetEnvironmentVariable(name, "inherited-value");
        try
        {
            Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellCommands("cmd /c set");
            ToolResult r = await Kit.Run("execute_terminal_command", Exec("cmd", ["/c", "set"]), caps);
            Assert.False(r.IsError, Kit.Text(r));
            string text = Kit.Text(r);
            Assert.Contains("SystemRoot=", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Path=", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ComSpec=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);

            string named = Kit.Text(await Kit.Run("execute_terminal_command", Exec("cmd", ["/c", "set"]), caps, new BuiltinOptions { ShellInheritedEnvironmentNames = [name] }));
            Assert.Contains(name + "=inherited-value", named, StringComparison.OrdinalIgnoreCase);

            // The model sets no variable of its own, in any case.
            foreach (string pair in new[] { "Path=C:\\evil", "PATHEXT=.X", "ComSpec=C:\\evil.exe", "GIT_CONFIG_COUNT=1", "MY_VAR=1" })
            {
                var o = JsonNode.Parse(Exec("cmd", ["/c", "set"]))!.AsObject();
                o["env"] = new JsonArray(pair);
                ToolResult e = await Kit.Run("execute_terminal_command", o.ToJsonString(), caps);
                Assert.True(e.IsError, pair);
                Assert.Contains("Setting the environment variable", Kit.Text(e), StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task A_denied_command_waiting_for_approval_does_not_run_through_cmd()
    {
        WindowsOnly();
        using var s = new Scratch();
        string marker = s.In("marker.txt");
        bool asked = false;
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellWithApproval((req, _) =>
        {
            asked = true;
            return ValueTask.FromResult(false);
        });
        ToolResult r = await Kit.Run("execute_terminal_command", Exec("echo x> " + marker), caps);
        Assert.True(r.IsError);
        Assert.True(asked);
        Assert.False(File.Exists(marker));
    }
}
