namespace OneLlm;

/// <summary>A command the shell tool is about to run, shown to an approver.</summary>
/// <param name="Program">The program, or for a shell line the whole line.</param>
/// <param name="Arguments">The arguments, empty for a shell line.</param>
/// <param name="WorkingDirectory">The directory it will run in.</param>
/// <param name="UsesShell">True when the line goes through the system shell (so pipes, redirects and expansions apply).</param>
public sealed record ShellRequest(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, bool UsesShell);

/// <summary>Decides whether one shell command may run. Return false to refuse; it is asked before every command that needs approval.</summary>
/// <param name="request">The command.</param>
/// <param name="cancellationToken">Cancels the question.</param>
/// <returns>True to let the command run.</returns>
public delegate ValueTask<bool> ShellApprover(ShellRequest request, CancellationToken cancellationToken);

/// <summary>
/// What the shell tool may run: nothing, anything, only listed programs, or anything a person approves. Allow-list entries are a program
/// name optionally followed by leading arguments (<c>git status</c> allows <c>git status -s</c> but not <c>git push</c>). In allow-list mode
/// no shell is ever used: the program and each argument are passed separately, so <c>;</c>, <c>|</c>, <c>$(...)</c> and quotes are plain text.
/// </summary>
public sealed class ShellPolicy
{
    private readonly string[][]? _entries;

    private ShellPolicy(bool enabled, string[][]? entries, bool requiresApproval, ShellApprover? approver)
    {
        Enabled = enabled;
        _entries = entries;
        RequiresApproval = requiresApproval;
        Approver = approver;
    }

    /// <summary>No shell.</summary>
    public static ShellPolicy Off { get; } = new(false, null, false, null);

    /// <summary>Any command, run through the system shell, with no approval.</summary>
    public static ShellPolicy Open { get; } = new(true, null, false, null);

    /// <summary>True when commands may run at all.</summary>
    public bool Enabled { get; }

    /// <summary>True when every command needs <see cref="Approver"/> to agree first. With no approver, every command is refused.</summary>
    public bool RequiresApproval { get; }

    /// <summary>Asked before each command when <see cref="RequiresApproval"/> is true.</summary>
    public ShellApprover? Approver { get; }

    /// <summary>True when only listed programs may run.</summary>
    public bool IsAllowList => _entries is not null;

    /// <summary>The allow-list entries, or null when any program may run.</summary>
    public IReadOnlyList<string>? Commands => _entries?.Select(e => string.Join(' ', e)).ToArray();

    /// <summary>Only the listed programs, run without a shell.</summary>
    /// <param name="commands">Program names (<c>git</c>), program and leading arguments (<c>dotnet build</c>), or absolute program paths.</param>
    /// <returns>The policy.</returns>
    public static ShellPolicy AllowList(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        string[][] entries = [.. commands.Select(Tokenize).Where(t => t.Length > 0)];
        return new ShellPolicy(entries.Length > 0, entries, false, null);
    }

    /// <summary>Any command, but only after <paramref name="approver"/> agrees to it.</summary>
    /// <param name="approver">The question to ask.</param>
    /// <returns>The policy.</returns>
    public static ShellPolicy WithApproval(ShellApprover approver)
    {
        ArgumentNullException.ThrowIfNull(approver);
        return new ShellPolicy(true, null, true, approver);
    }

    /// <summary>The same policy, but every command also needs approval.</summary>
    /// <param name="approver">The question to ask.</param>
    /// <returns>The stricter policy; <see cref="Off"/> stays off.</returns>
    public ShellPolicy RequireApproval(ShellApprover approver)
    {
        ArgumentNullException.ThrowIfNull(approver);
        return Enabled ? new ShellPolicy(true, _entries, true, Chain(Approver, approver)) : this;
    }

    /// <summary>Whether the list allows running <paramref name="program"/> with <paramref name="arguments"/>. Always true when any program may run.</summary>
    /// <param name="program">A program name, or an absolute path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>True when an entry matches.</returns>
    public bool AllowsCommand(string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Enabled)
        {
            return false;
        }

        if (_entries is null)
        {
            return true;
        }

        foreach (string[] entry in _entries)
        {
            if (EntryMatches(entry, program, arguments))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What both policies allow: a command must pass each. Approval from either is kept, and both approvers are asked.</summary>
    /// <param name="other">The other policy.</param>
    /// <returns>A policy that never allows more than either input.</returns>
    public ShellPolicy Intersect(ShellPolicy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Enabled || !other.Enabled)
        {
            return Off;
        }

        string[][]? entries;
        if (_entries is null)
        {
            entries = other._entries;
        }
        else if (other._entries is null)
        {
            entries = _entries;
        }
        else
        {
            // An entry survives when the other list allows everything the entry allows.
            entries = [.. _entries.Where(e => other.CoversEntry(e)).Concat(other._entries.Where(e => CoversEntry(e))).DistinctBy(e => string.Join(' ', e))];
        }

        bool enabled = entries is null || entries.Length > 0;
        if (!enabled)
        {
            return Off;
        }

        bool approval = RequiresApproval || other.RequiresApproval;
        ShellApprover? approver = RequiresApproval && other.RequiresApproval ? Chain(Approver, other.Approver) : RequiresApproval ? Approver : other.Approver;
        return new ShellPolicy(true, entries, approval, approval ? approver : null);
    }

    /// <summary>What either policy allows, as far as one policy can say it. Where the two cannot be merged exactly the stricter one is kept.</summary>
    /// <param name="other">The other policy.</param>
    /// <returns>The combined policy.</returns>
    public ShellPolicy Union(ShellPolicy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Enabled)
        {
            return other;
        }

        if (!other.Enabled)
        {
            return this;
        }

        bool aOpen = _entries is null && !RequiresApproval;
        bool bOpen = other._entries is null && !other.RequiresApproval;
        if (aOpen || bOpen)
        {
            return Open;
        }

        if (!RequiresApproval && !other.RequiresApproval)
        {
            return new ShellPolicy(true, [.. _entries!.Concat(other._entries!).DistinctBy(e => string.Join(' ', e))], false, null);
        }

        if (RequiresApproval != other.RequiresApproval)
        {
            return RequiresApproval ? this : other;
        }

        string[][]? merged = _entries is null || other._entries is null ? null : [.. _entries.Concat(other._entries).DistinctBy(e => string.Join(' ', e))];
        return new ShellPolicy(true, merged, true, Approver ?? other.Approver);
    }

    /// <inheritdoc />
    public override string ToString() => !Enabled ? "off"
        : RequiresApproval ? (IsAllowList ? "allow-list with approval" : "approval")
        : IsAllowList ? "allow-list" : "open";

    private bool CoversEntry(string[] entry) => _entries is null || _entries.Any(mine => mine.Length <= entry.Length && EntryMatches(mine, entry[0], entry[1..]));

    private static bool EntryMatches(string[] entry, string program, IReadOnlyList<string> arguments)
    {
        if (!ProgramMatches(entry[0], program) || arguments.Count < entry.Length - 1)
        {
            return false;
        }

        for (int i = 1; i < entry.Length; i++)
        {
            if (!string.Equals(entry[i], arguments[i - 1], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ProgramMatches(string allowed, string program)
    {
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (allowed.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            // A path entry matches only that exact path, so a program of the same name elsewhere is not allowed.
            return string.Equals(allowed, program, cmp);
        }

        if (program.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return string.Equals(StripExe(allowed), StripExe(program), cmp);
        }

        return string.Equals(allowed, program, cmp);
    }

    private static string StripExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string[] Tokenize(string entry) => (entry ?? throw new ArgumentException("A shell allow-list entry cannot be null.")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ShellApprover? Chain(ShellApprover? first, ShellApprover? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return async (request, ct) => await first(request, ct).ConfigureAwait(false) && await second(request, ct).ConfigureAwait(false);
    }
}
