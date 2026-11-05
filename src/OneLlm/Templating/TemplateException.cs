namespace OneLlm.Templating;

/// <summary>Where in the template a <see cref="TemplateException"/> came from.</summary>
public enum TemplateErrorKind
{
    /// <summary>The template text is not valid.</summary>
    Parse,
    /// <summary>Rendering failed (bad value, missing key, function error, nil access).</summary>
    Execute,
    /// <summary><c>embed</c> failed: not found, cycle, depth limit, or the embedded text failed to render.</summary>
    Embed,
    /// <summary>A configured limit (output size, nesting, call depth) was exceeded.</summary>
    Limit,
}

/// <summary>
/// A template failed to parse or render. <see cref="Line"/> and <see cref="Column"/> are 1-based positions in the
/// text the caller passed to <see cref="Template.Parse(string, TemplateParseOptions?)"/> (columns count UTF-16 code units).
/// </summary>
public sealed class TemplateException : Exception
{
    /// <summary>The failure category.</summary>
    public TemplateErrorKind Kind { get; }

    /// <summary>The template name given at parse time (usually a file name), or <c>null</c>.</summary>
    public string? TemplateName { get; }

    /// <summary>1-based line, or 0 when the failure has no position.</summary>
    public int Line { get; }

    /// <summary>1-based column in UTF-16 code units, or 0 when the failure has no position.</summary>
    public int Column { get; }

    /// <summary>0-based offset into the original text, or -1 when unknown.</summary>
    public int Offset { get; }

    /// <summary>The message without the position prefix.</summary>
    public string Detail { get; }

    /// <summary>Creates an exception for a position in a template.</summary>
    public TemplateException(TemplateErrorKind kind, string? templateName, int line, int column, int offset, string detail, Exception? inner = null)
        : base(Compose(templateName, line, column, detail), inner)
    {
        Kind = kind;
        TemplateName = templateName;
        Line = line;
        Column = column;
        Offset = offset;
        Detail = detail;
    }

    private static string Compose(string? name, int line, int column, string detail)
    {
        if (line <= 0)
        {
            return name is null ? detail : $"{name}: {detail}";
        }

        return $"{name ?? "template"}:{line}:{column}: {detail}";
    }
}

/// <summary>
/// Thrown by a template function to report a failure. The renderer turns it into a <see cref="TemplateException"/>
/// positioned at the call. Any other exception a function throws is treated the same way.
/// </summary>
public sealed class TemplateFunctionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TemplateFunctionException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public TemplateFunctionException(string message, Exception? inner) : base(message, inner) { }
}
