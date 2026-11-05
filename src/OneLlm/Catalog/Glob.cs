namespace OneLlm.Catalog;

/// <summary>Matches model names against patterns with <c>*</c> (any run of characters, also none) and <c>?</c> (one character), ignoring case.</summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>How many characters of the pattern are literal; a longer literal part is the more specific pattern.</summary>
    public static int LiteralLength(string pattern)
    {
        int n = 0;
        foreach (char c in pattern)
        {
            if (c is not ('*' or '?'))
            {
                n++;
            }
        }

        return n;
    }
}
