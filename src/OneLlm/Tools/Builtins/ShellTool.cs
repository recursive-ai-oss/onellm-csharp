using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OneLlm.Tools.Builtins;

/// <summary>
/// execute_terminal_command: runs a command and returns its output. What may run is decided by the grants' <see cref="ShellPolicy"/>:
/// open (through the system shell), an allow-list (never through a shell), or with approval.
/// </summary>
/// <remarks>
/// A timeout or cancellation kills the process and every process it started that is still its descendant. .NET has no way to put a child in its own
/// process group without native calls, so a process that detached itself (a daemon) is not reached.
/// </remarks>
internal sealed class ShellTool : BuiltinTool
{
    private static readonly string[] s_blockedEnvPrefixes = ["LD_", "DYLD_"];
    private static readonly string[] s_blockedEnvNames = ["PATH", "PATHEXT", "BASH_ENV", "ENV", "IFS", "SHELLOPTS", "BASHOPTS", "PS4", "PROMPT_COMMAND", "NODE_OPTIONS", "PYTHONPATH", "PYTHONSTARTUP", "RUBYOPT", "PERL5OPT", "DOTNET_STARTUP_HOOKS", "COMSPEC"];
    private static readonly string[] s_secretSuffixes = ["_API_KEY", "_APIKEY", "_TOKEN", "_SECRET", "_PASSWORD", "_PASSWD", "_CREDENTIALS", "_ACCESS_KEY", "_PRIVATE_KEY"];

    private readonly BuiltinOptions _options;

    public ShellTool(BuiltinOptions options)
        : base(Spec("execute_terminal_command", BuiltinSchemas.ExecuteTerminalCommandDescription + OsNote(), BuiltinSchemas.ExecuteTerminalCommandSchema, TimeSpan.FromSeconds(3660)), ToolCapability.Shell)
    {
        _options = options;
    }

    private static string OsNote() => OperatingSystem.IsWindows()
        ? "\n\nSYSTEM INFO: Running on Windows.\n- Shell: commands without args are executed via 'cmd /c'\n- Use 'dir' instead of 'ls', 'type' instead of 'cat'\n- For PowerShell commands, use: powershell -Command \"your command\""
        : OperatingSystem.IsMacOS()
            ? "\n\nSYSTEM INFO: Running on macOS.\n- Shell: commands without args are executed via 'sh -c'\n- BSD-style commands (some flags differ from GNU/Linux)"
            : "\n\nSYSTEM INFO: Running on " + (OperatingSystem.IsLinux() ? "Linux" : "a Unix-like system") + ".\n- Shell: commands without args are executed via 'sh -c'\n- GNU coreutils are usually available (ls, cat, grep, find)";

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string command = Args.RequiredString(inputs, "command");
        List<string> args = Args.OptionalStringList(inputs, "args") ?? [];
        string? workingDir = Args.OptionalString(inputs, "working_dir");
        int requested = Args.OptionalInt(inputs, "timeout") ?? 0;
        List<string> envPairs = Args.OptionalStringList(inputs, "env") ?? [];

        ICapabilities caps = context.Capabilities;
        ShellPolicy policy = caps.ShellAccess;
        if (!policy.Enabled)
        {
            throw new CapabilityMissingException("Running shell commands", "shell access to the runtime, with Capabilities.Shell, Capabilities.ShellCommands(...) or Capabilities.ShellWithApproval(...)");
        }

        if (command.Contains('\0', StringComparison.Ordinal) || args.Any(a => a.Contains('\0', StringComparison.Ordinal)))
        {
            return Fail("Commands and arguments cannot contain NUL characters.");
        }

        int timeout = requested <= 0 ? _options.ShellDefaultTimeoutSeconds : Math.Min(requested, _options.ShellMaxTimeoutSeconds);
        string? warning = requested > _options.ShellMaxTimeoutSeconds
            ? string.Create(CultureInfo.InvariantCulture, $"requested timeout of {requested} s exceeds the maximum; capped at {_options.ShellMaxTimeoutSeconds} s")
            : null;

        string cwd = workingDir is { Length: > 0 } ? caps.ResolveRead(workingDir) : caps.WorkingDirectory ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(cwd))
        {
            return Fail("Failed to start command: the working directory does not exist.");
        }

        Dictionary<string, string> extraEnv = ParseEnv(envPairs, policy, _options);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };

        bool usesShell = args.Count == 0 && !policy.IsAllowList;
        string display;
        if (usesShell)
        {
            display = command;
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"" + command + "\"";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
        }
        else
        {
            if (policy.IsAllowList && !IsPlainProgram(command))
            {
                return Fail("In allow-list mode the program goes in 'command' and each argument in 'args'; shell syntax is not interpreted. "
                    + $"'{Clip(command)}' is not a single program name.");
            }

            string? program = ResolveProgram(command, cwd, allowScripts: !policy.IsAllowList);
            if (program is null)
            {
                return Fail("Failed to start command: executable file not found in PATH");
            }

            // The allow-list is checked against the name the model wrote (so "git" matches the entry "git") and, for a path, the resolved path.
            if (!policy.AllowsCommand(command, args))
            {
                throw new CapabilityMissingException($"Running '{Clip(command)}'", $"it to the shell allow-list (currently: {string.Join(", ", policy.Commands ?? [])})");
            }

            // A batch file or a shell given as the program re-parses its arguments, so "&", "|" or "%VAR%" in one would run or expand something
            // the allow-list never saw. Plain text arguments are fine; these characters are refused rather than escaped.
            if (OperatingSystem.IsWindows() && policy.IsAllowList && ReparsesArguments(program) is { } risky)
            {
                string? bad = args.FirstOrDefault(a => a.AsSpan().IndexOfAny(risky) >= 0);
                if (bad is not null)
                {
                    return Fail($"'{Clip(command)}' re-reads its arguments as commands, so the argument '{Clip(bad)}' (with one of {new string(risky)}) is not allowed. Pass plain text only.");
                }
            }

            psi.FileName = program;
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            display = command;
        }

        if (policy.RequiresApproval)
        {
            // With no approver the agent loop holds the call in waiting_for_approval and sets ApprovedByLoop when a person agrees.
            bool approved = policy.Approver is { } approver
                ? await approver(new ShellRequest(display, usesShell ? [] : args, cwd, usesShell), cancellationToken).ConfigureAwait(false)
                : context.ApprovedByLoop;
            if (!approved)
            {
                return Fail("The command was not approved, so it did not run.");
            }
        }

        BuildEnvironment(psi, extraEnv, policy);
        return await RunProcessAsync(psi, timeout, warning, context.TimeProvider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunProcessAsync(ProcessStartInfo psi, int timeoutSeconds, string? warning, TimeProvider time, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return Fail("Failed to start command");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Fail("Failed to start command: " + e.Message);
        }

        process.StandardInput.Close();
        var stdout = new BoundedCapture(_options.ShellOutputLimitBytes);
        var stderr = new BoundedCapture(_options.ShellOutputLimitBytes);
        Task pumpOut = stdout.PumpAsync(process.StandardOutput.BaseStream);
        Task pumpErr = stderr.PumpAsync(process.StandardError.BaseStream);

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // Output pipes close when the process and its children end. If a child outlives a kill and keeps them open, stop waiting.
        await Task.WhenAny(Task.WhenAll(pumpOut, pumpErr), Task.Delay(TimeSpan.FromSeconds(2), time, CancellationToken.None)).ConfigureAwait(false);

        if (timedOut)
        {
            return Fail($"Command timed out after {timeoutSeconds} seconds" + (warning is null ? "" : "\nNote: " + warning));
        }

        string so = stdout.ToText().TrimEnd('\n');
        string se = stderr.ToText().TrimEnd('\n');
        int exit = process.HasExited ? process.ExitCode : -1;
        var sb = new StringBuilder();
        sb.Append(so);
        if (se.Length > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append("STDERR:\n").Append(se);
        }

        if (exit != 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append("Exit code: ").Append(exit.ToString(CultureInfo.InvariantCulture));
        }

        if (warning is not null)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append("Note: ").Append(warning);
        }

        return ToolResult.FromText(sb.ToString());
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static Dictionary<string, string> ParseEnv(List<string> pairs, ShellPolicy policy, BuiltinOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new ToolInputException("env", $"entry '{Clip(pair)}' must look like KEY=VALUE");
            }

            string key = pair[..eq];
            if (key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            {
                throw new ToolInputException("env", $"name '{Clip(key)}' may only contain letters, digits and underscores");
            }

            // With an allow-list or approval the model sets no variable the host did not name: GIT_CONFIG_*, PAGER, HOME, PYTHON* and
            // many more make an allowed program run something else.
            bool restricted = policy.IsAllowList || policy.RequiresApproval;
            if (restricted && !options.ShellModelEnvironmentNames.Contains(key, StringComparer.Ordinal))
            {
                throw new CapabilityMissingException($"Setting the environment variable {key}", "it to BuiltinOptions.ShellModelEnvironmentNames (commands here are allow-listed or approval-gated)");
            }

            // Variables that change which program or library runs are not the model's to set unless it already has an open shell.
            bool blocked = s_blockedEnvNames.Contains(key, StringComparer.OrdinalIgnoreCase) || s_blockedEnvPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (blocked && !(policy.Enabled && !policy.IsAllowList && !policy.RequiresApproval))
            {
                throw new CapabilityMissingException($"Setting the environment variable {key}", "an open shell, with Capabilities.Shell");
            }

            env[key] = pair[(eq + 1)..];
        }

        return env;
    }

    // Variables an allow-listed or approval-gated command inherits. Everything else of the host's environment (tokens, cloud
    // credentials, tool configuration such as GIT_* or PYTHON*) stays out.
    private static readonly string[] s_baseEnvNames = ["PATH", "HOME", "USER", "LOGNAME", "LANG", "LANGUAGE", "TERM", "TZ", "TMPDIR", "TEMP", "TMP", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "USERNAME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ALLUSERSPROFILE", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)", "COMMONPROGRAMW6432", "PROGRAMDATA", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "OS"];

    private void BuildEnvironment(ProcessStartInfo psi, Dictionary<string, string> extra, ShellPolicy policy)
    {
        if (policy.IsAllowList || policy.RequiresApproval)
        {
            var keep = new HashSet<string>(s_baseEnvNames.Concat(_options.ShellInheritedEnvironmentNames), StringComparer.OrdinalIgnoreCase);
            foreach (string key in psi.Environment.Keys.ToArray())
            {
                if (!keep.Contains(key) && !key.StartsWith("LC_", StringComparison.Ordinal))
                {
                    psi.Environment.Remove(key);
                }
            }
        }
        else if (!_options.ShellInheritSecrets)
        {
            foreach (string key in psi.Environment.Keys.ToArray())
            {
                if (IsSecretName(key))
                {
                    psi.Environment.Remove(key);
                }
            }
        }

        foreach ((string k, string v) in extra)
        {
            psi.Environment[k] = v;
        }
    }

    internal static bool IsSecretName(string name) =>
        s_secretSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("AWS_SECRET", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AWS_SESSION", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GOOGLE_APPLICATION_CREDENTIALS", StringComparison.OrdinalIgnoreCase);

    // A program name or path with no whitespace and none of the characters a shell gives meaning to.
    internal static bool IsPlainProgram(string command)
    {
        if (command.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            // '~' means the home directory only at the start; inside a path it is an ordinary character (8.3 names such as RUNNER~1).
            if (c == '~' && i == 0 || char.IsWhiteSpace(c) || char.IsControl(c) || ";|&$`<>()*?[]{}!#\"'^%".Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Looks the program up in the host's own PATH (never the model's), skipping relative entries so the working directory is never searched.
    private static string? ResolveProgram(string command, string cwd, bool allowScripts)
    {
        bool hasSeparator = command.AsSpan().IndexOfAny('/', '\\') >= 0;
        string[] extensions = OperatingSystem.IsWindows()
            ? [.. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE").Split(';', StringSplitOptions.RemoveEmptyEntries).Where(e => allowScripts || !(e.Equals(".BAT", StringComparison.OrdinalIgnoreCase) || e.Equals(".CMD", StringComparison.OrdinalIgnoreCase)))]
            : [""];
        IEnumerable<string> candidates;
        if (hasSeparator)
        {
            string full = Path.GetFullPath(command, cwd);
            candidates = extensions.Select(e => full + (Path.HasExtension(full) && e.Length > 0 ? "" : e)).Distinct();
        }
        else
        {
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            candidates = dirs.Where(Path.IsPathFullyQualified).SelectMany(d => extensions.Select(e => Path.Join(d, command + e)));
        }

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    // The characters that make cmd.exe or PowerShell treat part of an argument as another command or as a variable.
    private static readonly char[] s_cmdMeta = ['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n', '`'];
    private static readonly char[] s_powershellMeta = [';', '&', '|', '<', '>', '$', '`', '(', ')', '{', '}', '"', '\'', '\r', '\n', '@'];

    // The characters to refuse in arguments when the program interprets them (cmd.exe, a .bat/.cmd file, PowerShell), or null for an ordinary program.
    internal static char[]? ReparsesArguments(string program)
    {
        string name = Path.GetFileName(program);
        string ext = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || stem.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return s_cmdMeta;
        }

        return stem.Equals("powershell", StringComparison.OrdinalIgnoreCase) || stem.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? s_powershellMeta : null;
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..80] + "...";

    /// <summary>Keeps the first part and the last part of a stream and counts what was dropped between them.</summary>
    internal sealed class BoundedCapture
    {
        private readonly int _limit;
        private readonly int _headLimit;
        private readonly int _tailLimit;
        private readonly byte[] _head;
        private readonly byte[] _tail;
        private int _headLen;
        private int _tailLen;
        private int _tailStart;
        private long _total;

        internal BoundedCapture(int limit)
        {
            _limit = limit;
            _headLimit = limit * 7 / 10;
            _tailLimit = limit - _headLimit;
            _head = new byte[_headLimit];
            _tail = new byte[_tailLimit];
        }

        internal async Task PumpAsync(Stream stream)
        {
            byte[] buffer = new byte[8192];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    Write(buffer.AsSpan(0, n));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The pipe closed under us after a kill.
            }
        }

        internal void Write(ReadOnlySpan<byte> p)
        {
            lock (_head)
            {
                _total += p.Length;
                if (_headLen < _headLimit)
                {
                    int n = Math.Min(p.Length, _headLimit - _headLen);
                    p[..n].CopyTo(_head.AsSpan(_headLen));
                    _headLen += n;
                    p = p[n..];
                }

                AppendTail(p);
            }
        }

        private void AppendTail(ReadOnlySpan<byte> p)
        {
            if (p.Length == 0 || _tailLimit == 0)
            {
                return;
            }

            if (p.Length >= _tailLimit)
            {
                p[^_tailLimit..].CopyTo(_tail);
                _tailLen = _tailLimit;
                _tailStart = 0;
                return;
            }

            if (_tailLen < _tailLimit)
            {
                int n = Math.Min(p.Length, _tailLimit - _tailLen);
                p[..n].CopyTo(_tail.AsSpan(_tailLen));
                _tailLen += n;
                p = p[n..];
                if (p.Length == 0)
                {
                    return;
                }
            }

            // The ring is full: overwrite the oldest bytes and move its start.
            int first = Math.Min(p.Length, _tailLimit - _tailStart);
            p[..first].CopyTo(_tail.AsSpan(_tailStart));
            p[first..].CopyTo(_tail);
            _tailStart = (_tailStart + p.Length) % _tailLimit;
        }

        private byte[] TailBytes(int n)
        {
            n = Math.Clamp(n, 0, _tailLen);
            byte[] output = new byte[n];
            int start = (_tailStart + _tailLen - n) % Math.Max(_tailLimit, 1);
            int first = Math.Min(n, _tailLimit - start);
            Array.Copy(_tail, start, output, 0, first);
            Array.Copy(_tail, 0, output, first, n - first);
            return output;
        }

        internal string ToText()
        {
            lock (_head)
            {
                if (_total <= _limit)
                {
                    return Encoding.UTF8.GetString(_head, 0, _headLen) + Encoding.UTF8.GetString(TailBytes(_tailLen));
                }

                // The marker states how many bytes were dropped, and its own length changes how much tail fits, so settle on a size.
                string label = _limit % 1024 == 0 ? (_limit / 1024).ToString(CultureInfo.InvariantCulture) + " KB" : _limit.ToString(CultureInfo.InvariantCulture) + " bytes";
                int retained = _tailLen;
                string marker;
                for (int attempt = 0; ; attempt++)
                {
                    long dropped = _total - (_headLen + retained);
                    marker = string.Create(CultureInfo.InvariantCulture, $"\n... [{dropped} bytes elided: output exceeded the {label} capture limit] ...\n");
                    int next = Math.Clamp(_limit - _headLen - marker.Length, 0, _tailLen);
                    if (next == retained || attempt >= 8)
                    {
                        break;
                    }

                    retained = next;
                }

                return Encoding.UTF8.GetString(_head, 0, _headLen) + marker + Encoding.UTF8.GetString(TailBytes(retained));
            }
        }
    }
}
