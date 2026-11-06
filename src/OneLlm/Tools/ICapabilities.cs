using System.Net;
using OneLlm.Access;

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

    /// <summary>The directories file tools may read under. Defaults to <see cref="FileRoots"/>; a read-only grant lists its roots here only.</summary>
    IReadOnlyList<string> ReadRoots => FileRoots;

    /// <summary>The directories file tools may write under. Defaults to <see cref="FileRoots"/>.</summary>
    IReadOnlyList<string> WriteRoots => FileRoots;

    /// <summary>The directory relative paths and commands start from. Defaults to the first file root; null when there is none.</summary>
    string? WorkingDirectory => FileRoots.Count > 0 ? FileRoots[0] : null;

    /// <summary>What shell commands may run. Defaults to open when <see cref="ShellEnabled"/> and off otherwise.</summary>
    ShellPolicy ShellAccess => ShellEnabled ? ShellPolicy.Open : ShellPolicy.Off;

    /// <summary>True when any network egress is granted at all. Defaults to false.</summary>
    bool NetworkEnabled => false;

    /// <summary>
    /// Whether a connection to <paramref name="address"/> may be made for a request to <paramref name="uri"/>. It is asked after the host name has been
    /// resolved, so a name that resolves to a loopback, private or link-local address can be refused. The default refuses those ranges.
    /// </summary>
    /// <param name="uri">The URI of the request.</param>
    /// <param name="address">An address the host resolved to.</param>
    /// <returns>True when connecting is allowed.</returns>
    bool IsAddressAllowed(Uri uri, IPAddress address) => !EgressPolicy.IsSpecialAddress(address);

    /// <summary>Resolves <paramref name="path"/> for reading: symlinks and <c>..</c> are resolved first, then the result must lie under a read root.</summary>
    /// <param name="path">An absolute path, or one relative to <see cref="WorkingDirectory"/>.</param>
    /// <returns>The real, absolute path to open.</returns>
    /// <exception cref="CapabilityMissingException">The path is outside every read root, or no file access is granted.</exception>
    string ResolveRead(string path)
    {
        string real = PathGuard.Resolve(ReadRoots, WorkingDirectory, path, write: false);
        PathGuard.CheckReadProtection(real, ProtectedPaths, path);
        return real;
    }

    /// <summary>Resolves <paramref name="path"/> for writing. The last component may not exist yet; its parent chain is resolved and must lie under a write root.</summary>
    /// <param name="path">An absolute path, or one relative to <see cref="WorkingDirectory"/>.</param>
    /// <returns>The real, absolute path to open.</returns>
    /// <exception cref="CapabilityMissingException">The path is outside every write root, or no write access is granted.</exception>
    string ResolveWrite(string path)
    {
        string real = PathGuard.Resolve(WriteRoots, WorkingDirectory, path, write: true);
        PathGuard.CheckWriteProtection(WriteRoots, real, ProtectedPaths, AllowRepoControlWrites, path);
        return real;
    }

    /// <summary>Directories file tools may neither read nor write even though a root contains them (the runtime's own state: saved instances and records). Defaults to none.</summary>
    IReadOnlyList<string> ProtectedPaths => [];

    /// <summary>True when file tools may write into repository control directories such as <c>.git</c>. Defaults to false.</summary>
    bool AllowRepoControlWrites => false;
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
