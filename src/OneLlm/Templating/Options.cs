namespace OneLlm.Templating;

/// <summary>What a lookup of a key that is not in a map does.</summary>
public enum MissingKeyMode
{
    /// <summary>The lookup yields no value, which prints as <c>&lt;no value&gt;</c>. This is Go's default.</summary>
    Default,
    /// <summary>The render fails with an error that names the key.</summary>
    Error,
}

/// <summary>Text found for an <c>embed</c> reference.</summary>
/// <param name="Key">A stable identity for the content (a full path, a prompt name, a URL). Cycle detection compares keys.</param>
/// <param name="Text">The template text to render in place.</param>
/// <param name="Name">The name used in error positions; defaults to the key.</param>
public readonly record struct EmbedContent(string Key, string Text, string? Name = null);

/// <summary>
/// Finds the text for <c>{{ embed "reference" }}</c>. Sources are consulted in order and the first that returns content wins,
/// so list them relative path first, then registered prompts, then any remote source (for example HTTP). None is configured by
/// default: without a source <c>embed</c> fails, so a template can never read files or the network the host did not give it.
/// </summary>
public interface IEmbedSource
{
    /// <summary>Resolves a reference, or returns <c>null</c> when this source does not have it.</summary>
    /// <param name="reference">The string passed to <c>embed</c>.</param>
    /// <param name="referrerKey">The key of the content that contains the <c>embed</c> call, or <c>null</c> for the top-level template.</param>
    /// <exception cref="TemplateFunctionException">The source recognises the reference but cannot supply it (access denied, I/O failure).</exception>
    EmbedContent? Resolve(string reference, string? referrerKey);
}

/// <summary>Embeds registered prompts by name (a dictionary lookup; names are matched exactly).</summary>
public sealed class PromptEmbedSource : IEmbedSource
{
    private readonly IReadOnlyDictionary<string, string> _prompts;

    /// <summary>Creates a source over a name → template text map. The map is read at render time, so it may change between renders.</summary>
    public PromptEmbedSource(IReadOnlyDictionary<string, string> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
    }

    /// <inheritdoc />
    public EmbedContent? Resolve(string reference, string? referrerKey)
    {
        return _prompts.TryGetValue(reference, out string? text) ? new EmbedContent("prompt:" + reference, text, reference) : null;
    }
}

/// <summary>
/// Embeds files by relative path, resolved against the directory of the including file (or the root for the top-level template),
/// and never outside <c>root</c>. Symbolic links are resolved before the containment check. Absolute references are not handled
/// here (they are left to other sources).
/// </summary>
public sealed class FileEmbedSource : IEmbedSource
{
    private readonly string _root;

    /// <summary>Creates a source confined to <paramref name="root"/>.</summary>
    /// <param name="root">The directory embedded files must live under.</param>
    public FileEmbedSource(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = ResolveFully(Path.GetFullPath(root));
    }

    /// <inheritdoc />
    public EmbedContent? Resolve(string reference, string? referrerKey)
    {
        if (reference.Length == 0 || Path.IsPathRooted(reference) || reference.StartsWith('/') || reference.StartsWith('\\') || reference.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        string baseDir = _root;
        if (referrerKey is not null && referrerKey.StartsWith("file:", StringComparison.Ordinal))
        {
            baseDir = Path.GetDirectoryName(referrerKey.Substring(5)) ?? _root;
        }

        string candidate = Path.GetFullPath(Path.Combine(baseDir, reference));
        if (!File.Exists(candidate))
        {
            return null;
        }

        string real = ResolveFully(candidate);
        if (!IsUnder(real, _root))
        {
            throw new TemplateFunctionException($"embed path escapes the allowed directory: {reference}");
        }

        return new EmbedContent("file:" + real, File.ReadAllText(real), reference);
    }

    private static bool IsUnder(string path, string root)
    {
        string r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.Ordinal);
    }

    // Resolves every symbolic link along the path, so a link inside the root that points outside is caught.
    private static string ResolveFully(string path)
    {
        string full = Path.GetFullPath(path);
        string? resolvedParent = null;
        string? parent = Path.GetDirectoryName(full);
        if (parent is not null && parent != full)
        {
            resolvedParent = ResolveFully(parent);
        }

        string name = Path.GetFileName(full);
        string combined = resolvedParent is null ? full : Path.Combine(resolvedParent, name);
        try
        {
            FileSystemInfo info = Directory.Exists(combined) ? new DirectoryInfo(combined) : new FileInfo(combined);
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                return ResolveFully(target.FullName);
            }
        }
        catch (IOException)
        {
            // not a link, or not readable: use the path as is
        }

        return combined;
    }
}

/// <summary>Settings that affect how a template is parsed. Parsed templates are cached by text, name, function registry and these settings.</summary>
public sealed class TemplateParseOptions
{
    /// <summary>The template's name, shown in errors (usually a file name). Default <c>"template"</c>. Also the name <c>{{template "name"}}</c> uses to call the top-level body.</summary>
    public string? Name { get; init; }

    /// <summary>The functions available. Default: <see cref="TemplateFunctions.Standard"/>.</summary>
    public TemplateFunctions? Functions { get; init; }

    /// <summary>
    /// Escape <c>{{</c> and <c>}}</c> inside fenced and inline Markdown code before parsing so examples render literally.
    /// Default <c>true</c>.
    /// </summary>
    public bool EscapeCodeRegions { get; init; } = true;
}

/// <summary>Settings for one render.</summary>
public sealed class TemplateRenderOptions
{
    /// <summary>Default <see cref="MissingKeyMode.Default"/>.</summary>
    public MissingKeyMode MissingKey { get; init; }

    /// <summary>Where <c>embed</c> looks, in order. Default: none (<c>embed</c> fails).</summary>
    public IReadOnlyList<IEmbedSource>? EmbedSources { get; init; }

    /// <summary>Identity of the top-level template for cycle detection and relative embeds, for example <c>"file:/prompts/a.md"</c>. Default none.</summary>
    public string? RootKey { get; init; }

    /// <summary>The deepest chain of nested <c>embed</c> calls. Default 16.</summary>
    public int MaxEmbedDepth { get; init; } = 16;

    /// <summary>Output longer than this many UTF-16 code units fails the render. Default 64 Mi.</summary>
    public int MaxOutputLength { get; init; } = 64 * 1024 * 1024;

    /// <summary>The clock behind <c>now</c>. Default none: calling <c>now</c> then fails, so rendering never reads the system clock unasked.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Checked between actions and loop iterations; cancelling throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken CancellationToken { get; init; }
}
