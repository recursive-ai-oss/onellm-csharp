using System.Text.Json;
using OneLlm.Access;
using OneLlm.Messages;
using OneLlm.Tests.Support;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tests.Builtins;

public class ShellToolTests
{
    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "These cases use POSIX programs; the Windows run covers the Windows forms.");

    private static string J(string command, string[]? args = null, string? dir = null, int? timeout = null, string[]? env = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["args"] = args,
            ["working_dir"] = dir,
            ["timeout"] = timeout,
            ["env"] = env,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));

    [Fact]
    public async Task Shell_is_refused_when_it_is_not_granted()
    {
        UnixOnly();
        using var s = new Scratch();
        ToolResult r = await Kit.Run("execute_terminal_command", J("echo hi"), Capabilities.Files(s.Root));
        Assert.True(r.IsError);
        Assert.Contains("Running shell commands is not available", Kit.Text(r), StringComparison.Ordinal);
        ToolResult none = await Kit.Run("execute_terminal_command", J("echo", ["hi"]), Capabilities.None);
        Assert.True(none.IsError);
    }

    [Fact]
    public async Task An_open_shell_runs_lines_and_reports_stdout_stderr_and_exit_code()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
        Assert.Equal("hi there", Kit.Text(await Kit.Run("execute_terminal_command", J("echo hi there"), caps)));
        Assert.Equal("out\nSTDERR:\nerr\nExit code: 3", Kit.Text(await Kit.Run("execute_terminal_command", J("echo out; echo err >&2; exit 3"), caps)));
        Assert.Equal(s.Root, Kit.Text(await Kit.Run("execute_terminal_command", J("pwd"), caps)));
        Assert.Equal("a b", Kit.Text(await Kit.Run("execute_terminal_command", J("echo", ["a", "b"]), caps)));
    }

    [Fact]
    public async Task The_working_directory_override_must_be_inside_the_grant()
    {
        UnixOnly();
        using var s = new Scratch();
        Directory.CreateDirectory(s.In("sub"));
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
        Assert.Equal(s.In("sub"), Kit.Text(await Kit.Run("execute_terminal_command", J("pwd", dir: "sub"), caps)));
        ToolResult outside = await Kit.Run("execute_terminal_command", J("pwd", dir: s.Outside), caps);
        Assert.True(outside.IsError);
        Assert.DoesNotContain(s.Outside + "\n", Kit.Text(outside), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_killed_and_reported()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ToolResult r = await Kit.Run("execute_terminal_command", J("sleep 30", timeout: 1), caps);
        Assert.True(r.IsError);
        Assert.Equal("Error: Command timed out after 1 seconds", Kit.Text(r));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_timeout_above_the_maximum_is_capped_with_a_note()
    {
        UnixOnly();
        using var s = new Scratch();
        var options = new BuiltinOptions { ShellMaxTimeoutSeconds = 5 };
        ToolResult r = await Kit.Run("execute_terminal_command", J("echo ok", timeout: 99), Capabilities.Files(s.Root) + Capabilities.Shell, options);
        Assert.Equal("ok\nNote: requested timeout of 99 s exceeds the maximum; capped at 5 s", Kit.Text(r));
    }

    [Fact]
    public async Task Cancelling_kills_the_command_and_the_cancellation_propagates()
    {
        UnixOnly();
        using var s = new Scratch();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.Run("execute_terminal_command", J("sleep 30"), Capabilities.Files(s.Root) + Capabilities.Shell, ct: cts.Token));
    }

    [Fact]
    public async Task Long_output_keeps_the_head_and_the_tail_with_a_marker()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
        string text = Kit.Text(await Kit.Run("execute_terminal_command", J("i=0; while [ $i -lt 3000 ]; do echo line-$i-xxxxxxxxxx; i=$((i+1)); done"), caps));
        Assert.StartsWith("line-0-xxxxxxxxxx\nline-1-", text, StringComparison.Ordinal);
        Assert.EndsWith("line-2999-xxxxxxxxxx", text, StringComparison.Ordinal);
        Assert.Matches(@"\.\.\. \[\d+ bytes elided: output exceeded the 10 KB capture limit\] \.\.\.", text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 10 * 1024);

        var small = new BuiltinOptions { ShellOutputLimitBytes = 1000 };
        string tiny = Kit.Text(await Kit.Run("execute_terminal_command", J("head -c 5000 /dev/zero | tr '\\0' a"), caps, small));
        Assert.True(tiny.Length <= 1000, tiny.Length.ToString());
        Assert.Contains("bytes elided: output exceeded the 1000 bytes capture limit", tiny, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_that_fits_is_returned_whole()
    {
        UnixOnly();
        using var s = new Scratch();
        string text = Kit.Text(await Kit.Run("execute_terminal_command", J("head -c 10240 /dev/zero | tr '\\0' b"), Capabilities.Files(s.Root) + Capabilities.Shell));
        Assert.Equal(10240, text.Length);
    }

    [Fact]
    public async Task Secret_looking_variables_are_not_passed_to_commands_unless_asked()
    {
        UnixOnly();
        using var s = new Scratch();
        Environment.SetEnvironmentVariable("ONELLM_U10_TEST_API_KEY", "super-secret-value");
        try
        {
            Capabilities caps = Capabilities.Files(s.Root) + Capabilities.Shell;
            Assert.Equal("", Kit.Text(await Kit.Run("execute_terminal_command", J("echo \"$ONELLM_U10_TEST_API_KEY\""), caps)));
            Assert.Equal("super-secret-value", Kit.Text(await Kit.Run("execute_terminal_command", J("echo \"$ONELLM_U10_TEST_API_KEY\""), caps, new BuiltinOptions { ShellInheritSecrets = true })));
            Assert.Equal("v", Kit.Text(await Kit.Run("execute_terminal_command", J("echo \"$MY_VAR\"", env: ["MY_VAR=v"]), caps)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ONELLM_U10_TEST_API_KEY", null);
        }
    }

    // --- allow-list ---

    private static Capabilities AllowList(Scratch s, params string[] commands) => Capabilities.Files(s.Root) + Capabilities.ShellCommands(commands);

    [Fact]
    public async Task An_allow_listed_program_runs_without_a_shell()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = AllowList(s, "echo", "ls");
        Assert.Equal("a b", Kit.Text(await Kit.Run("execute_terminal_command", J("echo", ["a", "b"]), caps)));
        s.Write("f.txt", "x");
        Assert.Contains("f.txt", Kit.Text(await Kit.Run("execute_terminal_command", J("ls"), caps)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ls; touch marker")]
    [InlineData("ls && touch marker")]
    [InlineData("ls | touch marker")]
    [InlineData("ls\ntouch marker")]
    [InlineData("$(touch marker)")]
    [InlineData("`touch marker`")]
    [InlineData("ls > marker")]
    [InlineData("'ls'")]
    [InlineData("l\"s\"")]
    [InlineData("ls -la")]
    [InlineData("ls\ttouch marker")]
    [InlineData("${IFS}ls")]
    [InlineData("l*")]
    [InlineData("~/ls")]
    public async Task Shell_syntax_in_the_command_is_refused_and_nothing_runs(string command)
    {
        UnixOnly();
        using var s = new Scratch();
        ToolResult r = await Kit.Run("execute_terminal_command", J(command), AllowList(s, "ls", "echo", "touch"));
        Assert.True(r.IsError, command);
        Assert.False(File.Exists(s.In("marker")), command);
        Assert.Contains("allow-list mode", Kit.Text(r), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("; touch marker")]
    [InlineData("$(touch marker)")]
    [InlineData("`touch marker`")]
    [InlineData("x && touch marker")]
    [InlineData("--help; touch marker")]
    public async Task Metacharacters_in_arguments_are_plain_text(string arg)
    {
        UnixOnly();
        using var s = new Scratch();
        ToolResult r = await Kit.Run("execute_terminal_command", J("echo", [arg]), AllowList(s, "echo"));
        Assert.False(r.IsError);
        Assert.Equal(arg, Kit.Text(r));
        Assert.False(File.Exists(s.In("marker")));
    }

    [Fact]
    public async Task A_command_not_on_the_list_is_refused()
    {
        UnixOnly();
        using var s = new Scratch();
        s.Write("victim.txt", "x");
        Capabilities caps = AllowList(s, "ls");
        foreach (string cmd in new[] { "rm", "cat", "sh", "/bin/ls", "./ls", "../ls", "LS" })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", J(cmd, ["victim.txt"]), caps);
            Assert.True(r.IsError, cmd);
        }

        Assert.True(File.Exists(s.In("victim.txt")));
    }

    [Fact]
    public async Task A_program_in_the_working_directory_is_never_found_by_name()
    {
        UnixOnly();
        using var s = new Scratch();
        string script = s.Write("evilcmd", "#!/bin/sh\ntouch marker\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ToolResult r = await Kit.Run("execute_terminal_command", J("evilcmd"), AllowList(s, "evilcmd"));
        Assert.True(r.IsError);
        Assert.Contains("not found", Kit.Text(r), StringComparison.Ordinal);
        Assert.False(File.Exists(s.In("marker")));
    }

    [Fact]
    public async Task An_entry_with_leading_arguments_allows_only_those_subcommands()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = AllowList(s, "echo safe");
        Assert.Equal("safe more", Kit.Text(await Kit.Run("execute_terminal_command", J("echo", ["safe", "more"]), caps)));
        Assert.True((await Kit.Run("execute_terminal_command", J("echo", ["unsafe"]), caps)).IsError);
        Assert.True((await Kit.Run("execute_terminal_command", J("echo"), caps)).IsError);
    }

    [Fact]
    public async Task The_model_cannot_change_the_search_path_or_loader_in_a_restricted_shell()
    {
        UnixOnly();
        using var s = new Scratch();
        Capabilities caps = AllowList(s, "echo");
        foreach (string e in new[] { "PATH=/tmp", "LD_PRELOAD=/x.so", "DYLD_INSERT_LIBRARIES=/x", "BASH_ENV=/x" })
        {
            ToolResult r = await Kit.Run("execute_terminal_command", J("echo", ["hi"], env: [e]), caps);
            Assert.True(r.IsError, e);
        }

        // No variable at all, unless the host names it.
        Assert.True((await Kit.Run("execute_terminal_command", J("echo", ["hi"], env: ["HARMLESS=1"]), caps)).IsError);
        Assert.False((await Kit.Run("execute_terminal_command", J("echo", ["hi"], env: ["HARMLESS=1"]), caps, new BuiltinOptions { ShellModelEnvironmentNames = ["HARMLESS"] })).IsError);
    }

    // --- approval ---

    [Fact]
    public async Task Approval_gates_every_command()
    {
        UnixOnly();
        using var s = new Scratch();
        var asked = new List<ShellRequest>();
        bool answer = false;
        Capabilities caps = Capabilities.Files(s.Root) + Capabilities.ShellWithApproval((req, _) => { asked.Add(req); return ValueTask.FromResult(answer); });

        ToolResult denied = await Kit.Run("execute_terminal_command", J("touch marker"), caps);
        Assert.True(denied.IsError);
        Assert.False(File.Exists(s.In("marker")));
        Assert.Equal(new ShellRequest("touch marker", [], s.Root, true), asked.Single(), new RequestComparer());

        answer = true;
        Assert.False((await Kit.Run("execute_terminal_command", J("touch marker"), caps)).IsError);
        Assert.True(File.Exists(s.In("marker")));
        Assert.False((await Kit.Run("execute_terminal_command", J("echo", ["x"]), caps)).IsError);
        Assert.Equal(["touch marker", "touch marker", "echo"], asked.Select(a => a.Program));
        Assert.False(asked[2].UsesShell);
    }

    private sealed class RequestComparer : IEqualityComparer<ShellRequest>
    {
        public bool Equals(ShellRequest? x, ShellRequest? y) => x?.Program == y?.Program && x?.UsesShell == y?.UsesShell && x?.WorkingDirectory == y?.WorkingDirectory;

        public int GetHashCode(ShellRequest obj) => obj.Program.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_program_names_are_told_apart_from_shell_syntax()
    {
        Assert.True(ShellTool.IsPlainProgram("git"));
        Assert.True(ShellTool.IsPlainProgram("/usr/bin/git"));
        Assert.True(ShellTool.IsPlainProgram("dotnet-ef"));
        Assert.False(ShellTool.IsPlainProgram(""));
        Assert.False(ShellTool.IsPlainProgram("git status"));
        Assert.False(ShellTool.IsPlainProgram("git;ls"));
        Assert.False(ShellTool.IsPlainProgram("a|b"));
        Assert.False(ShellTool.IsPlainProgram("a%PATH%"));
    }

    [Fact]
    public void Secret_names_are_recognised()
    {
        Assert.True(ShellTool.IsSecretName("OPENAI_API_KEY"));
        Assert.True(ShellTool.IsSecretName("github_token"));
        Assert.True(ShellTool.IsSecretName("AWS_SECRET_ACCESS_KEY"));
        Assert.False(ShellTool.IsSecretName("PATH"));
        Assert.False(ShellTool.IsSecretName("HOME"));
    }
}
