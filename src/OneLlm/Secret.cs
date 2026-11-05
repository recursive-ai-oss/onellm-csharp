using System.Diagnostics;
using System.Text;

namespace OneLlm;

/// <summary>
/// Holds an API key or token so that it cannot leak by accident: it prints as <c>[redacted]</c> from <see cref="ToString"/> and in the
/// debugger, and <see cref="Scrub(string)"/> removes it from any text that came from a server.
/// </summary>
[DebuggerDisplay("[redacted]")]
public sealed class Secret
{
    /// <summary>The text that replaces a secret.</summary>
    public const string Mask = "[redacted]";

    private const int MinFragment = 8;
    private const int MaxEchoed = 400;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _value;

    /// <summary>Wraps a secret value.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public Secret(string value) => _value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The secret itself. Use it only to build the request that needs it, never for logging.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Value => _value;

    /// <summary>True when the value is empty or whitespace, that is, no key was configured.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(_value);

    /// <summary>Returns <c>[redacted]</c>.</summary>
    /// <returns>The mask.</returns>
    public override string ToString() => Mask;

    /// <summary>
    /// Replaces the key (also trimmed of surrounding whitespace) and every run of at least 8 characters that occurs in both the
    /// key and the text with <c>[redacted]</c>, then cuts the result to a few hundred characters, so an error message that echoes
    /// part of a key (some servers print the first and last characters) does not leak it.
    /// </summary>
    /// <param name="text">Text from a server or other outside source.</param>
    /// <returns>The cleaned text, at most 400 characters.</returns>
    public string Scrub(string text) => Cap(ScrubAll(text));

    /// <summary>As <see cref="Scrub(string)"/> without the length cut.</summary>
    /// <param name="text">Text from a server or other outside source.</param>
    /// <returns>The cleaned text.</returns>
    public string ScrubAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string key = _value.Trim();
        if (key.Length == 0 || text.Length == 0)
        {
            return text;
        }

        var hidden = new bool[text.Length];
        int window = Math.Min(MinFragment, key.Length);
        for (int i = 0; i + window <= key.Length; i++)
        {
            ReadOnlySpan<char> piece = key.AsSpan(i, window);
            int from = 0;
            while (from <= text.Length - window)
            {
                int at = text.AsSpan(from).IndexOf(piece, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                at += from;
                hidden.AsSpan(at, window).Fill(true);
                from = at + 1;
            }
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (!hidden[i])
            {
                sb.Append(text[i]);
            }
            else if (i == 0 || !hidden[i - 1])
            {
                sb.Append(Mask);
            }
        }

        return sb.ToString();
    }

    /// <summary>Applies <see cref="ScrubAll(string)"/> for every secret in turn.</summary>
    /// <param name="text">Text from a server or other outside source.</param>
    /// <param name="secrets">The secrets to remove.</param>
    /// <returns>The cleaned text.</returns>
    public static string ScrubAll(string text, IEnumerable<Secret> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);
        foreach (Secret secret in secrets)
        {
            text = secret.ScrubAll(text);
        }

        return text;
    }

    private static string Cap(string s) => s.Length <= MaxEchoed ? s : s[..MaxEchoed];
}
