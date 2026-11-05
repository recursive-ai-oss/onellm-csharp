using System.Net;
using OneLlm.Access;
using OneLlm.Tools;

namespace OneLlm;

/// <summary>
/// What the host lets tools do: which directories they may read and write, whether and how they may run shell commands, and which
/// network hosts they may reach. Only the host builds these; a definition can name tools but never grants anything.
/// </summary>
/// <remarks>
/// <para>Compose grants with <c>+</c>: <c>Capabilities.Files("work") + Capabilities.Shell</c>. A sub-agent gets
/// <c>parent.Intersect(requested)</c>, which never allows more than the parent does. Instances are immutable.</para>
/// <para>The default (<see cref="Default"/>) grants the working directory for reading and writing, and nothing else: no shell, no network.</para>
/// </remarks>
public sealed class Capabilities : ICapabilities
{
    private readonly string[] _readRoots;
    private readonly string[] _writeRoots;
    private readonly HostPattern[] _hosts;

    private Capabilities(string[] readRoots, string[] writeRoots, string? workingDirectory, ShellPolicy shell, HostPattern[] hosts, bool anyPublic)
    {
        _readRoots = readRoots;
        _writeRoots = writeRoots;
        WorkingDirectory = workingDirectory;
        ShellAccess = shell;
        _hosts = hosts;
        AnyPublicNetwork = anyPublic;
    }

    /// <summary>Grants nothing: no files, no shell, no network.</summary>
    public static Capabilities None { get; } = new([], [], null, ShellPolicy.Off, [], false);

    /// <summary>Any command through the system shell. Combine with files: <c>Capabilities.Files(".") + Capabilities.Shell</c>.</summary>
    public static Capabilities Shell { get; } = new([], [], null, ShellPolicy.Open, [], false);

    /// <summary>Every public host over http and https. Loopback, private and link-local addresses stay refused unless a <see cref="Network(string[])"/> entry names them.</summary>
    public static Capabilities AnyPublicHosts { get; } = new([], [], null, ShellPolicy.Off, [], true);

    /// <summary>The safe default for a runtime: read and write under the current working directory, nothing else.</summary>
    /// <param name="workingDirectory">The directory, or null for the process's current directory.</param>
    /// <returns>The grants.</returns>
    public static Capabilities Default(string? workingDirectory = null) => Files(workingDirectory ?? Directory.GetCurrentDirectory());

    /// <summary>Read and write access under <paramref name="root"/>, which also becomes the working directory.</summary>
    /// <param name="root">A directory; relative paths are made absolute against the current directory.</param>
    /// <returns>The grants.</returns>
    public static Capabilities Files(string root)
    {
        string full = Full(root);
        return new([full], [full], full, ShellPolicy.Off, [], false);
    }

    /// <summary>Read-only access under <paramref name="root"/>, which also becomes the working directory.</summary>
    /// <param name="root">A directory.</param>
    /// <returns>The grants.</returns>
    public static Capabilities ReadOnlyFiles(string root)
    {
        string full = Full(root);
        return new([full], [], full, ShellPolicy.Off, [], false);
    }

    /// <summary>Only the listed programs, run without a shell. See <see cref="ShellPolicy"/> for the entry format.</summary>
    /// <param name="commands">Program names, program and leading arguments, or absolute program paths.</param>
    /// <returns>The grants.</returns>
    public static Capabilities ShellCommands(params string[] commands) =>
        new([], [], null, ShellPolicy.AllowList(commands), [], false);

    /// <summary>Any command, but each one needs <paramref name="approver"/> to agree first.</summary>
    /// <param name="approver">The question to ask before each command.</param>
    /// <returns>The grants.</returns>
    public static Capabilities ShellWithApproval(ShellApprover approver) =>
        new([], [], null, ShellPolicy.WithApproval(approver), [], false);

    /// <summary>Network access to the listed hosts. See <see cref="HostPattern"/> for the entry format.</summary>
    /// <param name="hosts">Host names, <c>*.</c> subdomain wildcards, addresses or ranges, each with an optional port.</param>
    /// <returns>The grants.</returns>
    /// <exception cref="ArgumentException">An entry is not valid.</exception>
    public static Capabilities Network(params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return new([], [], null, ShellPolicy.Off, [.. hosts.Select(HostPattern.Parse).Distinct()], false);
    }

    /// <summary>The directories tools may read under, as absolute paths.</summary>
    public IReadOnlyList<string> ReadRoots => _readRoots;

    /// <summary>The directories tools may write under, as absolute paths. Every write root is also a read root.</summary>
    public IReadOnlyList<string> WriteRoots => _writeRoots;

    /// <inheritdoc />
    /// <remarks>The roots that can be both read and written, which is <see cref="WriteRoots"/>.</remarks>
    public IReadOnlyList<string> FileRoots => _writeRoots;

    /// <summary>The directory relative paths and commands start from, or null when no files are granted.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>What shell commands may run.</summary>
    public ShellPolicy ShellAccess { get; }

    /// <inheritdoc />
    public bool ShellEnabled => ShellAccess.Enabled;

    /// <summary>The network allow-list entries.</summary>
    public IReadOnlyList<HostPattern> NetworkHosts => _hosts;

    /// <summary>True when every public host is allowed.</summary>
    public bool AnyPublicNetwork { get; }

    /// <inheritdoc />
    public bool NetworkEnabled => AnyPublicNetwork || _hosts.Length > 0;

    /// <summary>The same grants with a different working directory.</summary>
    /// <param name="directory">The directory. It must lie under a read root.</param>
    /// <returns>The new grants.</returns>
    /// <exception cref="CapabilityMissingException">The directory is outside every read root.</exception>
    public Capabilities WithWorkingDirectory(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string real = ResolveRead(Path.IsPathFullyQualified(directory) ? directory : Path.Join(WorkingDirectory, directory));
        return new(_readRoots, _writeRoots, real, ShellAccess, _hosts, AnyPublicNetwork);
    }

    /// <summary>Adds one set of grants to another: roots and hosts are unioned and the more permissive shell policy that one policy can express wins.</summary>
    /// <param name="left">The first grants.</param>
    /// <param name="right">The second grants.</param>
    /// <returns>The combined grants. The working directory is the first one set.</returns>
    public static Capabilities operator +(Capabilities left, Capabilities right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Add(right);
    }

    /// <summary>Adds one set of grants to another; the named form of the <c>+</c> operator.</summary>
    /// <param name="other">The grants to add.</param>
    /// <returns>The combined grants.</returns>
    public Capabilities Add(Capabilities other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            [.. _readRoots.Union(other._readRoots, StringComparer.Ordinal)],
            [.. _writeRoots.Union(other._writeRoots, StringComparer.Ordinal)],
            WorkingDirectory ?? other.WorkingDirectory,
            ShellAccess.Union(other.ShellAccess),
            [.. _hosts.Union(other._hosts)],
            AnyPublicNetwork || other.AnyPublicNetwork);
    }

    /// <summary>
    /// What both sets of grants allow. Use it to derive a sub-agent's grants from its parent's: the result never allows more than
    /// either input, so a child cannot widen what its parent was given.
    /// </summary>
    /// <param name="other">The grants to intersect with, typically what a definition or caller asks for.</param>
    /// <returns>The narrower grants.</returns>
    public Capabilities Intersect(Capabilities other)
    {
        ArgumentNullException.ThrowIfNull(other);
        string[] read = IntersectRoots(_readRoots, other._readRoots);
        string[] write = IntersectRoots(_writeRoots, other._writeRoots);

        HostPattern[] hosts;
        bool anyPublic = AnyPublicNetwork && other.AnyPublicNetwork;
        if (AnyPublicNetwork && !other.AnyPublicNetwork)
        {
            // Public-only access does not cover address entries, which exist to reach non-public hosts.
            hosts = [.. other._hosts.Where(h => !h.IsAddress)];
        }
        else if (!AnyPublicNetwork && other.AnyPublicNetwork)
        {
            hosts = [.. _hosts.Where(h => !h.IsAddress)];
        }
        else
        {
            hosts = [.. _hosts.Where(h => other._hosts.Any(h.IsCoveredBy)).Concat(other._hosts.Where(h => _hosts.Any(h.IsCoveredBy))).Distinct()];
        }

        string? wd = new[] { WorkingDirectory, other.WorkingDirectory }.FirstOrDefault(d => d is not null && read.Any(r => PathGuard.IsUnder(d, r))) ?? read.FirstOrDefault();
        return new(read, write, wd, ShellAccess.Intersect(other.ShellAccess), hosts, anyPublic);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only http and https URIs without user information are allowed. A name is matched against the allow-list as written (an
    /// IP address, in any spelling the URI parser accepts, is matched as an address); the address it resolves to is checked again
    /// when the connection is made, through <see cref="IsAddressAllowed"/>.
    /// </remarks>
    public bool IsNetworkAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || !NetworkEnabled)
        {
            return false;
        }

        string host = EgressPolicy.CanonicalHost(uri);
        if (host.Length == 0)
        {
            return false;
        }

        if (_hosts.Any(h => h.Matches(host, uri.Port)))
        {
            return true;
        }

        if (!AnyPublicNetwork)
        {
            return false;
        }

        return !IPAddress.TryParse(host, out IPAddress? ip) || !EgressPolicy.IsSpecialAddress(ip);
    }

    /// <inheritdoc />
    /// <remarks>A loopback, private or link-local address is allowed only when an address or range entry contains it, or when the request is to <c>localhost</c> and a <c>localhost</c> entry exists.</remarks>
    public bool IsAddressAllowed(Uri uri, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(address);
        if (!EgressPolicy.IsSpecialAddress(address))
        {
            return true;
        }

        string host = EgressPolicy.CanonicalHost(uri);
        bool loopbackName = host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal);
        return _hosts.Any(h => h.IsAddress
            ? h.MatchesAddress(address) && (h.Port is null || h.Port == uri.Port)
            : loopbackName && IPAddress.IsLoopback(address) && h.Matches(host, uri.Port));
    }

    /// <summary>Resolves <paramref name="path"/> for reading; see <see cref="ICapabilities.ResolveRead"/>.</summary>
    /// <param name="path">An absolute path, or one relative to <see cref="WorkingDirectory"/>.</param>
    /// <returns>The real, absolute path to open.</returns>
    /// <exception cref="CapabilityMissingException">The path is outside every read root.</exception>
    public string ResolveRead(string path) => PathGuard.Resolve(_readRoots, WorkingDirectory, path, write: false);

    /// <summary>Resolves <paramref name="path"/> for writing; see <see cref="ICapabilities.ResolveWrite"/>.</summary>
    /// <param name="path">An absolute path, or one relative to <see cref="WorkingDirectory"/>.</param>
    /// <returns>The real, absolute path to open.</returns>
    /// <exception cref="CapabilityMissingException">The path is outside every write root.</exception>
    public string ResolveWrite(string path) => PathGuard.Resolve(_writeRoots, WorkingDirectory, path, write: true);

    /// <summary>A short description of the grants for logs and error messages. It names no secrets.</summary>
    /// <returns>For example <c>files(rw: /work) shell(off) network(off)</c>.</returns>
    public override string ToString()
    {
        string files = _readRoots.Length == 0 ? "off" : $"rw: {string.Join(", ", _writeRoots)}; ro: {string.Join(", ", _readRoots.Except(_writeRoots))}";
        string net = AnyPublicNetwork ? "any public host" : _hosts.Length == 0 ? "off" : string.Join(", ", _hosts.Select(h => h.Text));
        return $"files({files}) shell({ShellAccess}) network({net})";
    }

    private static string Full(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Path.GetFullPath(root);
    }

    // A root of one side that lies inside a root of the other survives; the narrower of each pair is kept.
    internal static string[] IntersectRoots(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var result = new List<string>();
        foreach (string x in a)
        {
            string rx = Real(x);
            foreach (string y in b)
            {
                string ry = Real(y);
                if (PathGuard.IsUnder(rx, ry))
                {
                    result.Add(x);
                }
                else if (PathGuard.IsUnder(ry, rx))
                {
                    result.Add(y);
                }
            }
        }

        return [.. result.Distinct(StringComparer.Ordinal)];
    }

    private static string Real(string root)
    {
        try
        {
            return PathGuard.Resolve([root], root, root, write: false);
        }
        catch (CapabilityMissingException)
        {
            return root;
        }
    }
}
