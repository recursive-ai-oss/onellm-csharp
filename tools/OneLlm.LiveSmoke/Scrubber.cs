using System.Text.RegularExpressions;

namespace OneLlm.LiveSmoke;

/// <summary>
/// Removes anything that looks like a credential or an account identifier from text before it is printed or written. It knows the exact
/// key values the run was given and also recognises key-shaped strings, because an error body from a provider can echo a key (or a
/// different one) back. Request, organisation and project identifiers are removed too.
/// </summary>
public sealed class Scrubber
{
    private const string Mask = "[redacted]";

    private static readonly Regex[] Shapes =
    [
        new(@"sk-[A-Za-z0-9_\-]{12,}", RegexOptions.CultureInvariant),
        new(@"AIza[0-9A-Za-z_\-]{20,}", RegexOptions.CultureInvariant),
        new(@"(?i)(bearer\s+)[A-Za-z0-9._~+/\-]{12,}=*", RegexOptions.CultureInvariant),
        new(@"(?i)((?:api[-_ ]?key|x-api-key|x-goog-api-key|authorization)[""']?\s*[:=]\s*[""']?)[A-Za-z0-9._~+/\-]{8,}", RegexOptions.CultureInvariant),
        new(@"(?i)([?&]key=)[^&\s""']+", RegexOptions.CultureInvariant),
    ];

    private static readonly Regex Ids = new(
        @"\b(?:req|request|org|proj|project|resp|msg|chatcmpl|run|dec|decision|batch|file|call)[-_][A-Za-z0-9]{8,}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string[] _secrets;

    /// <summary>Creates a scrubber.</summary>
    /// <param name="secrets">Key values to remove wherever they appear. Values shorter than 8 characters are ignored, because they would match ordinary text.</param>
    public Scrubber(IEnumerable<string> secrets) =>
        _secrets = [.. secrets.Where(static s => s.Length >= 8).OrderByDescending(static s => s.Length)];

    /// <summary>Returns the text with credentials and identifiers replaced.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The scrubbed text.</returns>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        string s = text;
        foreach (string secret in _secrets)
        {
            s = s.Replace(secret, Mask, StringComparison.Ordinal);
        }

        foreach (Regex shape in Shapes)
        {
            s = shape.Replace(s, m => m.Groups.Count > 1 ? m.Groups[1].Value + Mask : Mask);
        }

        return Ids.Replace(s, "[id]");
    }
}
