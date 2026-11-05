using System.Collections.Concurrent;
using System.Text;

namespace OneLlm.Templating;

/// <summary>
/// A parsed template in Go's <c>text/template</c> language, with the standard function set. Parse once, render many times:
/// a <see cref="Template"/> is immutable and safe to render from several threads at once. <see cref="Parse(string, TemplateParseOptions?)"/>
/// keeps a bounded cache keyed by text, name, function registry and options, so re-parsing the same prompt is a lookup.
/// </summary>
/// <remarks>
/// Scope values are <c>null</c>, strings, booleans, numbers, lists and string-keyed maps: <see cref="Dictionary{TKey, TValue}"/>,
/// <see cref="List{T}"/>, arrays, <see cref="System.Text.Json.JsonElement"/> and the other BCL collections are read directly with no reflection,
/// so rendering is trim- and AOT-safe. Integers read as Go's <c>int</c> and other numbers as <c>float64</c>.
/// </remarks>
public sealed class Template
{
    private const int MaxCacheEntries = 512;

    private static readonly ConcurrentDictionary<CacheKey, ParsedTemplate> Cache = new();

    [ThreadStatic]
    private static StringBuilder? t_buffer;

    private readonly ParsedTemplate _parsed;

    private Template(ParsedTemplate parsed)
    {
        _parsed = parsed;
    }

    /// <summary>The template's name, as given at parse time (default <c>"template"</c>).</summary>
    public string Name => _parsed.Name;

    /// <summary>Parses template text.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="options">Parse settings; <c>null</c> for the defaults.</param>
    /// <exception cref="TemplateException">The text is not a valid template; carries the line and column.</exception>
    public static Template Parse(string text, TemplateParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Template(ParseCached(text, options?.Name ?? "template", options?.Functions ?? TemplateFunctions.Standard, options?.EscapeCodeRegions ?? true));
    }

    /// <summary>Parses and renders in one call (the parse is cached).</summary>
    /// <param name="text">The template text.</param>
    /// <param name="scope">The data the template reads as <c>.</c>.</param>
    /// <param name="parse">Parse settings, or <c>null</c>.</param>
    /// <param name="render">Render settings, or <c>null</c>.</param>
    /// <exception cref="TemplateException">The text does not parse or the render fails.</exception>
    public static string Render(string text, object? scope, TemplateParseOptions? parse = null, TemplateRenderOptions? render = null)
        => Parse(text, parse).Render(scope, render);

    /// <summary>Renders to a string.</summary>
    /// <param name="scope">The data the template reads as <c>.</c> (a map such as <c>Dictionary&lt;string, object?&gt;</c> for field access).</param>
    /// <param name="options">Render settings; <c>null</c> for the defaults.</param>
    /// <exception cref="TemplateException">The render fails: a bad value, a missing key under <see cref="MissingKeyMode.Error"/>, a function error, an embed failure or a limit.</exception>
    /// <exception cref="OperationCanceledException">The render's cancellation token was cancelled.</exception>
    public string Render(object? scope, TemplateRenderOptions? options = null)
    {
        StringBuilder sb = t_buffer ?? new StringBuilder(1024);
        t_buffer = null;
        sb.Clear();
        try
        {
            RenderCore(scope, sb, options);
            return sb.ToString();
        }
        finally
        {
            // Keep a modest buffer for the next render on this thread; drop an oversized one.
            if (sb.Capacity <= 256 * 1024)
            {
                t_buffer = sb;
            }
        }
    }

    /// <summary>Renders into an existing builder, appending. On failure the builder is restored to its length before the call.</summary>
    /// <param name="scope">The data the template reads as <c>.</c>.</param>
    /// <param name="output">Receives the text.</param>
    /// <param name="options">Render settings; <c>null</c> for the defaults.</param>
    /// <exception cref="TemplateException">The render fails.</exception>
    public void Render(object? scope, StringBuilder output, TemplateRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        int start = output.Length;
        try
        {
            RenderCore(scope, output, options);
        }
        catch
        {
            output.Length = start;
            throw;
        }
    }

    private void RenderCore(object? scope, StringBuilder sb, TemplateRenderOptions? options)
    {
        options ??= DefaultRender;
        var embedStack = new List<string>();
        var exec = new Exec(_parsed, scope, sb, options, embedStack);
        exec.Run();
    }

    private static readonly TemplateRenderOptions DefaultRender = new();

    internal static ParsedTemplate ParseCached(string text, string name, TemplateFunctions functions, bool escape)
    {
        var key = new CacheKey(text, name, functions, functions.Version, escape);
        if (Cache.TryGetValue(key, out ParsedTemplate? hit))
        {
            return hit;
        }

        ParsedTemplate parsed = ParseUncached(text, name, functions, escape);
        if (Cache.Count >= MaxCacheEntries)
        {
            Cache.Clear();
        }

        Cache[key] = parsed;
        return parsed;
    }

    private static ParsedTemplate ParseUncached(string text, string name, TemplateFunctions functions, bool escape)
    {
        var parsed = new ParsedTemplate { Name = name, FileName = name, Original = text, Functions = functions };
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            // Nothing to evaluate: the text renders as itself.
            parsed.Parsed = text;
            var root = new ListNode();
            if (text.Length > 0)
            {
                root.Nodes.Add(new TextNode { Text = text });
            }

            parsed.Root = root;
            return parsed;
        }

        int[] reps = Array.Empty<int>();
        parsed.Parsed = escape ? CodeRegions.Escape(text, out reps) : text;
        parsed.Replacements = reps;
        Parser.Parse(parsed);
        return parsed;
    }

    private readonly record struct CacheKey(string Text, string Name, TemplateFunctions Functions, int Version, bool Escape);
}
