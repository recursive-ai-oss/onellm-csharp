namespace OneLlm.Tools;

/// <summary>
/// What the host lets tools do. This is the interface tools and built-ins check; the runtime supplies the implementation and
/// definitions never grant anything. Paths and hosts are checked by the implementation, which resolves symlinks before comparing.
/// </summary>
public interface ICapabilities
{
    /// <summary>The directories file tools may read and write under, as absolute paths. Empty means no file access.</summary>
    IReadOnlyList<string> FileRoots { get; }

    /// <summary>True when shell commands may run at all (they may still be allow-listed or need approval).</summary>
    bool ShellEnabled { get; }

    /// <summary>Whether tools may open a connection to <paramref name="uri"/>.</summary>
    /// <param name="uri">The absolute URI a tool is about to request.</param>
    /// <returns>True when network egress to that host is granted.</returns>
    bool IsNetworkAllowed(Uri uri);
}

/// <summary>Grants nothing: no file roots, no shell, no network. The safe default for a context built without a runtime.</summary>
public sealed class NoCapabilities : ICapabilities
{
    private NoCapabilities()
    {
    }

    /// <summary>The one instance.</summary>
    public static NoCapabilities Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<string> FileRoots => [];

    /// <inheritdoc />
    public bool ShellEnabled => false;

    /// <inheritdoc />
    public bool IsNetworkAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return false;
    }
}

/// <summary>How serious a line a tool reported is.</summary>
public enum ToolLogLevel
{
    /// <summary>Ordinary information.</summary>
    Information = 0,

    /// <summary>Something odd that did not stop the call.</summary>
    Warning = 1,

    /// <summary>A progress note for a person watching a long call.</summary>
    Progress = 2,
}

/// <summary>Where a tool's log and progress lines go. The runtime decides whether they reach a console, a record sink or nowhere.</summary>
public interface IToolLog
{
    /// <summary>Receives one line.</summary>
    /// <param name="toolCallId">The call that wrote it.</param>
    /// <param name="level">How serious the line is.</param>
    /// <param name="message">The line. Tools must not put secrets in it.</param>
    void Write(string toolCallId, ToolLogLevel level, string message);
}
