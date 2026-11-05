using System.Text;

namespace OneLlm.Templating;

/// <summary>
/// Escapes <c>{{</c> and <c>}}</c> inside Markdown code regions (fenced blocks and inline backtick spans) so they render literally.
/// Prompts often carry code or template examples; those must reach the model unchanged instead of being evaluated.
/// </summary>
internal static class CodeRegions
{
    /// <summary>Length of <c>{{"{{"}}</c> and <c>{{"}}"}}</c>, the replacements for the two-character delimiters.</summary>
    public const int ReplacementLength = 8;

    /// <summary>
    /// Returns the escaped text. <paramref name="replacements"/> receives the offsets (in the returned text) where each
    /// replacement starts, so positions can be mapped back to the original.
    /// </summary>
    public static string Escape(string text, out int[] replacements)
    {
        replacements = Array.Empty<int>();
        if (!text.Contains("{{", StringComparison.Ordinal) && !text.Contains("}}", StringComparison.Ordinal))
        {
            return text;
        }

        if (text.AsSpan().IndexOfAny('`', '~') < 0)
        {
            return text;
        }

        var starts = new List<int>();
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        bool atLineStart = true;
        while (i < text.Length)
        {
            // A fenced block opens on a line whose first content is three or more backticks or tildes, indented by at most
            // three spaces (CommonMark).
            if (atLineStart)
            {
                int j = i;
                int spaces = 0;
                while (j < text.Length && text[j] == ' ' && spaces < 4)
                {
                    j++;
                    spaces++;
                }

                if (spaces < 4 && j < text.Length && (text[j] == '`' || text[j] == '~'))
                {
                    char fenceChar = text[j];
                    int fenceLen = 0;
                    int k = j;
                    while (k < text.Length && text[k] == fenceChar)
                    {
                        fenceLen++;
                        k++;
                    }

                    if (fenceLen >= 3)
                    {
                        int lineEnd = k;
                        while (lineEnd < text.Length && text[lineEnd] != '\n')
                        {
                            lineEnd++;
                        }

                        if (lineEnd < text.Length)
                        {
                            lineEnd++;
                        }

                        sb.Append(text, i, lineEnd - i);

                        int bodyStart = lineEnd;
                        int bodyEnd = bodyStart;
                        int closeStart = -1;
                        int closeEnd = -1;
                        while (bodyEnd < text.Length)
                        {
                            int lineStart = bodyEnd;
                            int nl = lineStart;
                            while (nl < text.Length && text[nl] != '\n')
                            {
                                nl++;
                            }

                            if (IsClosingFence(text, lineStart, nl, fenceChar, fenceLen))
                            {
                                closeStart = lineStart;
                                closeEnd = nl;
                                if (closeEnd < text.Length)
                                {
                                    closeEnd++;
                                }

                                break;
                            }

                            bodyEnd = nl;
                            if (bodyEnd < text.Length)
                            {
                                bodyEnd++;
                            }
                        }

                        // An unclosed fence runs to the end of the text.
                        int bodyLimit = closeStart >= 0 ? closeStart : text.Length;
                        WriteEscaped(sb, starts, text.AsSpan(bodyStart, bodyLimit - bodyStart));
                        if (closeStart >= 0)
                        {
                            sb.Append(text, closeStart, closeEnd - closeStart);
                            i = closeEnd;
                        }
                        else
                        {
                            i = text.Length;
                        }

                        atLineStart = true;
                        continue;
                    }
                }
            }

            char c = text[i];

            if (c == '`')
            {
                int runLen = 0;
                int j = i;
                while (j < text.Length && text[j] == '`')
                {
                    runLen++;
                    j++;
                }

                // The span closes at the next backtick run of exactly the same length.
                int closeStart = -1;
                int k = j;
                while (k < text.Length)
                {
                    if (text[k] == '`')
                    {
                        int runStart = k;
                        int closeRun = 0;
                        while (k < text.Length && text[k] == '`')
                        {
                            closeRun++;
                            k++;
                        }

                        if (closeRun == runLen)
                        {
                            closeStart = runStart;
                            break;
                        }
                    }
                    else
                    {
                        k++;
                    }
                }

                if (closeStart >= 0)
                {
                    sb.Append(text, i, j - i);
                    WriteEscaped(sb, starts, text.AsSpan(j, closeStart - j));
                    sb.Append(text, closeStart, runLen);
                    i = closeStart + runLen;
                    atLineStart = false;
                    continue;
                }

                sb.Append(text, i, j - i);
                i = j;
                atLineStart = false;
                continue;
            }

            sb.Append(c);
            atLineStart = c == '\n';
            i++;
        }

        replacements = starts.ToArray();
        return sb.ToString();
    }

    private static bool IsClosingFence(string text, int lineStart, int lineEnd, char fenceChar, int openLen)
    {
        int j = lineStart;
        int spaces = 0;
        while (j < lineEnd && text[j] == ' ' && spaces < 4)
        {
            j++;
            spaces++;
        }

        if (spaces >= 4)
        {
            return false;
        }

        int run = 0;
        while (j < lineEnd && text[j] == fenceChar)
        {
            run++;
            j++;
        }

        if (run < openLen)
        {
            return false;
        }

        while (j < lineEnd)
        {
            if (text[j] != ' ' && text[j] != '\t')
            {
                return false;
            }

            j++;
        }

        return true;
    }

    private static void WriteEscaped(StringBuilder sb, List<int> starts, ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '{' && s[i + 1] == '{')
            {
                starts.Add(sb.Length);
                sb.Append("{{\"{{\"}}");
                i += 2;
                continue;
            }

            if (i + 1 < s.Length && s[i] == '}' && s[i + 1] == '}')
            {
                starts.Add(sb.Length);
                sb.Append("{{\"}}\"}}");
                i += 2;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }
    }
}
