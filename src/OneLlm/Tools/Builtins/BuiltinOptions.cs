namespace OneLlm.Tools.Builtins;

/// <summary>Limits and services the built-in tools use. The defaults suit an interactive agent; a host changes them per runtime.</summary>
public sealed class BuiltinOptions
{
    /// <summary>The most bytes of a command's stdout, and separately of its stderr, kept in a result. The first 70% and the last 30% are kept with a marker between. Default 10 KiB.</summary>
    public int ShellOutputLimitBytes { get; init; } = 10 * 1024;

    /// <summary>The timeout, in seconds, a shell command gets when the model gives none. Default 30.</summary>
    public int ShellDefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>The longest timeout, in seconds, the model may ask for; larger values are capped and the result says so. Default 3600.</summary>
    public int ShellMaxTimeoutSeconds { get; init; } = 3600;

    /// <summary>True to pass the host's secret-looking environment variables (names ending in <c>_API_KEY</c>, <c>_TOKEN</c>, <c>_SECRET</c>, <c>_PASSWORD</c>, cloud credentials) to commands. Default false: they are removed.</summary>
    public bool ShellInheritSecrets { get; init; }

    /// <summary>
    /// Environment variable names the model may set (through the <c>env</c> argument) when commands are allow-listed or need approval. Default none:
    /// a variable such as <c>GIT_CONFIG_COUNT</c>, <c>PAGER</c> or <c>PYTHONPATH</c> changes what an allow-listed program runs, so the model gets no say
    /// unless the host names the variables. With an open shell the model may set any variable except the few that replace the program search path.
    /// </summary>
    public IReadOnlyList<string> ShellModelEnvironmentNames { get; init; } = [];

    /// <summary>
    /// Extra host environment variables passed to allow-listed and approval-gated commands, by name. Those commands start from a minimal environment
    /// (<c>PATH</c>, <c>HOME</c>, <c>USER</c>, <c>LOGNAME</c>, <c>LANG</c>, <c>LC_*</c>, <c>TERM</c>, <c>TZ</c>, <c>TMPDIR</c> and the Windows system variables) and nothing else of the host's
    /// environment reaches them. Default none.
    /// </summary>
    public IReadOnlyList<string> ShellInheritedEnvironmentNames { get; init; } = [];

    /// <summary>The largest file or web response, in bytes, <c>read_file</c> will load. Larger files get an error that suggests a search or a ranged read. Default 10 MiB.</summary>
    public long MaxReadBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>The most bytes of a response body <c>http_request</c> returns. The rest is cut with a note. Default 100 KiB.</summary>
    public int HttpMaxBodyBytes { get; init; } = 100 * 1024;

    /// <summary>The most entries <c>read_file</c> lists for a directory. Default 50.</summary>
    public int MaxDirectoryEntries { get; init; } = 50;

    /// <summary>Where <c>todo</c> and <c>memory</c> keep their files, relative to the working directory unless absolute. It must lie under a write root. Default <c>.onellm</c>.</summary>
    public string StateDirectory { get; init; } = ".onellm";

    /// <summary>The search service for <c>search_web</c>. Null leaves the tool out of <see cref="BuiltinTools.Create"/>.</summary>
    public ISearchProvider? SearchProvider { get; init; }
}
