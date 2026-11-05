using System.Globalization;
using System.Text;

namespace OneLlm.Templating;

internal enum Tk
{
    Error, Eof, Text, LeftDelim, RightDelim, Pipe, Declare, Assign, Comma, LParen, RParen,
    Dot, Field, Variable, Ident, Bool, Nil, String, Number, CharConst,
    If, Else, End, Range, With, Define, Template, Block, Break, Continue,
}

internal readonly struct Token
{
    public readonly Tk Kind;
    public readonly int Pos;
    public readonly string Val;
    public readonly bool SpaceBefore;

    public Token(Tk kind, int pos, string val, bool spaceBefore)
    {
        Kind = kind;
        Pos = pos;
        Val = val;
        SpaceBefore = spaceBefore;
    }
}

/// <summary>
/// Tokenizes template text: literal text between <c>{{</c> and <c>}}</c>, then the action language inside. Trim markers
/// (<c>{{- </c> and <c> -}}</c>) trim the adjacent text here, so the parser never sees the whitespace. Behaviour follows the
/// package documentation; edge cases were settled by running the reference implementation.
/// </summary>
internal static class Lexer
{
    public static List<Token> Lex(string input)
    {
        var toks = new List<Token>();
        int pos = 0;
        int n = input.Length;
        bool trimNextText = false;
        while (true)
        {
            int left = input.IndexOf("{{", pos, StringComparison.Ordinal);
            int textEnd = left < 0 ? n : left;
            int textStart = pos;
            if (trimNextText)
            {
                while (textStart < textEnd && IsSpace(input[textStart]))
                {
                    textStart++;
                }

                trimNextText = false;
            }

            bool trimLeft = false;
            int afterDelim = 0;
            if (left >= 0)
            {
                afterDelim = left + 2;
                if (afterDelim + 1 < n && input[afterDelim] == '-' && IsSpace(input[afterDelim + 1]))
                {
                    trimLeft = true;
                    afterDelim++; // the dash; the space after it is skipped as ordinary action whitespace
                }

                if (trimLeft)
                {
                    while (textEnd > textStart && IsSpace(input[textEnd - 1]))
                    {
                        textEnd--;
                    }
                }
            }

            if (textEnd > textStart)
            {
                toks.Add(new Token(Tk.Text, textStart, input.Substring(textStart, textEnd - textStart), false));
            }

            if (left < 0)
            {
                toks.Add(new Token(Tk.Eof, n, "", false));
                return toks;
            }

            pos = afterDelim;

            // Comment: {{/* ... */}}. It must hug the delimiters: only a trim marker may sit between them and the comment.
            int c = trimLeft ? pos + 1 : pos;
            if (string.CompareOrdinal(input, c, "/*", 0, 2) == 0)
            {
                int close = input.IndexOf("*/", c + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    toks.Add(new Token(Tk.Error, left, "unclosed comment", false));
                    return toks;
                }

                int after = close + 2;
                bool trimRight = false;
                if (after + 2 < n && IsSpace(input[after]) && input[after + 1] == '-' && string.CompareOrdinal(input, after + 2, "}}", 0, 2) == 0)
                {
                    trimRight = true;
                    after += 2;
                }

                if (string.CompareOrdinal(input, after, "}}", 0, 2) != 0)
                {
                    toks.Add(new Token(Tk.Error, left, "comment closed leaving delim still open", false));
                    return toks;
                }

                pos = after + 2;
                trimNextText = trimRight;
                continue;
            }

            toks.Add(new Token(Tk.LeftDelim, left, "{{", false));
            if (!LexAction(input, ref pos, toks, out bool trimRightMarker))
            {
                return toks;
            }

            trimNextText = trimRightMarker;
        }
    }

    private static bool LexAction(string s, ref int pos, List<Token> toks, out bool trimRight)
    {
        trimRight = false;
        int n = s.Length;
        int parens = 0;
        bool space = false;
        while (true)
        {
            if (pos >= n)
            {
                toks.Add(new Token(Tk.Error, pos, "unclosed action", false));
                return false;
            }

            char ch = s[pos];
            if (IsSpace(ch))
            {
                int run = pos;
                while (run < n && IsSpace(s[run]))
                {
                    run++;
                }

                // A space run followed by "-}}" is the right trim marker.
                if (run < n && s[run] == '-' && string.CompareOrdinal(s, run + 1, "}}", 0, 2) == 0)
                {
                    if (parens > 0)
                    {
                        toks.Add(new Token(Tk.Error, pos, "unclosed left paren", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.RightDelim, run, "}}", space));
                    pos = run + 3;
                    trimRight = true;
                    return true;
                }

                pos = run;
                space = true;
                continue;
            }

            int start = pos;
            bool sp = space;
            space = false;

            if (ch == '}' && pos + 1 < n && s[pos + 1] == '}')
            {
                if (parens > 0)
                {
                    toks.Add(new Token(Tk.Error, pos, "unclosed left paren", false));
                    return false;
                }

                toks.Add(new Token(Tk.RightDelim, pos, "}}", sp));
                pos += 2;
                return true;
            }

            switch (ch)
            {
                case '|':
                    toks.Add(new Token(Tk.Pipe, pos, "|", sp));
                    pos++;
                    continue;
                case ',':
                    toks.Add(new Token(Tk.Comma, pos, ",", sp));
                    pos++;
                    continue;
                case '(':
                    parens++;
                    toks.Add(new Token(Tk.LParen, pos, "(", sp));
                    pos++;
                    continue;
                case ')':
                    parens--;
                    if (parens < 0)
                    {
                        toks.Add(new Token(Tk.Error, pos, "unexpected right paren", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.RParen, pos, ")", sp));
                    pos++;
                    continue;
                case ':':
                    if (pos + 1 < n && s[pos + 1] == '=')
                    {
                        toks.Add(new Token(Tk.Declare, pos, ":=", sp));
                        pos += 2;
                        continue;
                    }

                    toks.Add(new Token(Tk.Error, pos, "expected :=", false));
                    return false;
                case '=':
                    toks.Add(new Token(Tk.Assign, pos, "=", sp));
                    pos++;
                    continue;
                case '"':
                {
                    int e = pos + 1;
                    while (e < n && s[e] != '"')
                    {
                        if (s[e] == '\\')
                        {
                            e++;
                        }
                        else if (s[e] == '\n')
                        {
                            break;
                        }

                        e++;
                    }

                    if (e >= n || s[e] != '"')
                    {
                        toks.Add(new Token(Tk.Error, pos, "unterminated quoted string", false));
                        return false;
                    }

                    if (!GoQuote.TryUnquote(s.AsSpan(pos, e + 1 - pos), out string val))
                    {
                        toks.Add(new Token(Tk.Error, pos, "invalid syntax in quoted string", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.String, pos, val, sp));
                    pos = e + 1;
                    continue;
                }

                case '`':
                {
                    int e = s.IndexOf('`', pos + 1);
                    if (e < 0)
                    {
                        toks.Add(new Token(Tk.Error, pos, "unterminated raw quoted string", false));
                        return false;
                    }

                    // Carriage returns are dropped from raw strings (Go spec).
                    string val = s.Substring(pos + 1, e - pos - 1).Replace("\r", "", StringComparison.Ordinal);
                    toks.Add(new Token(Tk.String, pos, val, sp));
                    pos = e + 1;
                    continue;
                }

                case '\'':
                {
                    int e = pos + 1;
                    while (e < n && s[e] != '\'')
                    {
                        if (s[e] == '\\')
                        {
                            e++;
                        }
                        else if (s[e] == '\n')
                        {
                            break;
                        }

                        e++;
                    }

                    if (e >= n || s[e] != '\'' || !GoQuote.TryUnquoteChar(s.AsSpan(pos, e + 1 - pos), out int rune))
                    {
                        toks.Add(new Token(Tk.Error, pos, "unterminated character constant", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.CharConst, pos, rune.ToString(CultureInfo.InvariantCulture), sp));
                    pos = e + 1;
                    continue;
                }

                case '.':
                    if (pos + 1 < n && s[pos + 1] >= '0' && s[pos + 1] <= '9')
                    {
                        break; // a number such as .5
                    }

                    if (pos + 1 < n && IsAlnum(s[pos + 1]))
                    {
                        int e = pos + 1;
                        while (e < n && IsAlnum(s[e]))
                        {
                            e++;
                        }

                        if (!AtTerminator(s, e))
                        {
                            toks.Add(new Token(Tk.Error, e, $"bad character {Quote(s, e)}", false));
                            return false;
                        }

                        toks.Add(new Token(Tk.Field, pos, s.Substring(pos + 1, e - pos - 1), sp));
                        pos = e;
                        continue;
                    }

                    // a lone dot
                    if (!AtTerminator(s, pos + 1))
                    {
                        toks.Add(new Token(Tk.Error, pos + 1, $"bad character {Quote(s, pos + 1)}", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.Dot, pos, ".", sp));
                    pos++;
                    continue;
                case '$':
                {
                    int e = pos + 1;
                    while (e < n && IsAlnum(s[e]))
                    {
                        e++;
                    }

                    if (!AtTerminator(s, e))
                    {
                        toks.Add(new Token(Tk.Error, e, $"bad character {Quote(s, e)}", false));
                        return false;
                    }

                    toks.Add(new Token(Tk.Variable, pos, s.Substring(pos, e - pos), sp));
                    pos = e;
                    continue;
                }
            }

            if ((ch >= '0' && ch <= '9') || ch == '.' || ((ch == '+' || ch == '-') && pos + 1 < n && (char.IsAsciiDigit(s[pos + 1]) || (s[pos + 1] == '.' && pos + 2 < n && char.IsAsciiDigit(s[pos + 2])))))
            {
                int e = pos + 1;
                bool hex = ch == '0' && e < n && (s[e] == 'x' || s[e] == 'X');
                if ((ch == '+' || ch == '-') && e + 1 < n && s[e] == '0' && (s[e + 1] == 'x' || s[e + 1] == 'X'))
                {
                    hex = true;
                }

                while (e < n)
                {
                    char d = s[e];
                    if (IsAlnum(d) || d == '.')
                    {
                        e++;
                    }
                    else if ((d == '+' || d == '-') && (s[e - 1] == 'e' || s[e - 1] == 'E') && !hex)
                    {
                        e++;
                    }
                    else if ((d == '+' || d == '-') && (s[e - 1] == 'p' || s[e - 1] == 'P'))
                    {
                        e++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (!AtTerminator(s, e))
                {
                    toks.Add(new Token(Tk.Error, e, $"bad number syntax: \"{s.Substring(pos, e - pos + 1)}\"", false));
                    return false;
                }

                toks.Add(new Token(Tk.Number, pos, s.Substring(pos, e - pos), sp));
                pos = e;
                continue;
            }

            if (IsAlphaNumeric(ch))
            {
                int e = pos;
                while (e < n && IsAlphaNumeric(s[e]))
                {
                    e++;
                }

                if (!AtTerminator(s, e))
                {
                    toks.Add(new Token(Tk.Error, e, $"bad character {Quote(s, e)}", false));
                    return false;
                }

                string word = s.Substring(pos, e - pos);
                Tk kind = word switch
                {
                    "true" or "false" => Tk.Bool,
                    "nil" => Tk.Nil,
                    "if" => Tk.If,
                    "else" => Tk.Else,
                    "end" => Tk.End,
                    "range" => Tk.Range,
                    "with" => Tk.With,
                    "define" => Tk.Define,
                    "template" => Tk.Template,
                    "block" => Tk.Block,
                    "break" => Tk.Break,
                    "continue" => Tk.Continue,
                    _ => Tk.Ident,
                };
                toks.Add(new Token(kind, start, word, sp));
                pos = e;
                continue;
            }

            toks.Add(new Token(Tk.Error, pos, $"unexpected {Quote(s, pos)} in command", false));
            return false;
        }
    }

    private static string Quote(string s, int i) => i >= s.Length ? "EOF" : "U+" + ((int)s[i]).ToString("X4", CultureInfo.InvariantCulture);

    private static bool AtTerminator(string s, int i)
    {
        if (i >= s.Length)
        {
            return true;
        }

        char c = s[i];
        if (IsSpace(c))
        {
            return true;
        }

        return c is '.' or ',' or '|' or ':' or ')' or '(' || (c == '}' && i + 1 < s.Length && s[i + 1] == '}');
    }

    internal static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    private static bool IsAlnum(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool IsAlphaNumeric(char c) => c == '_' || char.IsLetter(c);
}

/// <summary>Go-style string and character literal decoding (escape rules from the language specification).</summary>
internal static class GoQuote
{
    public static bool TryUnquote(ReadOnlySpan<char> quoted, out string value)
    {
        value = "";
        if (quoted.Length < 2)
        {
            return false;
        }

        var sb = new StringBuilder(quoted.Length);
        int i = 1;
        int end = quoted.Length - 1;
        while (i < end)
        {
            char c = quoted[i];
            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }

            i++;
            if (i >= end)
            {
                return false;
            }

            if (!TryEscape(quoted, ref i, end, '"', sb, out _))
            {
                return false;
            }
        }

        value = sb.ToString();
        return true;
    }

    public static bool TryUnquoteChar(ReadOnlySpan<char> quoted, out int rune)
    {
        rune = 0;
        if (quoted.Length < 3)
        {
            return false;
        }

        int i = 1;
        int end = quoted.Length - 1;
        var sb = new StringBuilder();
        if (quoted[i] == '\\')
        {
            i++;
            if (!TryEscape(quoted, ref i, end, '\'', sb, out int code))
            {
                return false;
            }

            if (i != end)
            {
                return false;
            }

            rune = code >= 0 ? code : char.ConvertToUtf32(sb.ToString(), 0);
            return true;
        }

        if (char.IsHighSurrogate(quoted[i]) && i + 1 < end)
        {
            rune = char.ConvertToUtf32(quoted[i], quoted[i + 1]);
            i += 2;
        }
        else
        {
            if (quoted[i] == '\'')
            {
                return false;
            }

            rune = quoted[i];
            i++;
        }

        return i == end;
    }

    // After the backslash. Appends the decoded text; code is the numeric value for \x, octal and \u forms (else -1).
    private static bool TryEscape(ReadOnlySpan<char> s, ref int i, int end, char quote, StringBuilder sb, out int code)
    {
        code = -1;
        char c = s[i++];
        switch (c)
        {
            case 'a': sb.Append('\a'); code = 7; return true;
            case 'b': sb.Append('\b'); code = 8; return true;
            case 'f': sb.Append('\f'); code = 12; return true;
            case 'n': sb.Append('\n'); code = 10; return true;
            case 'r': sb.Append('\r'); code = 13; return true;
            case 't': sb.Append('\t'); code = 9; return true;
            case 'v': sb.Append('\v'); code = 11; return true;
            case '\\': sb.Append('\\'); code = '\\'; return true;
            case '\'' when quote == '\'': sb.Append('\''); code = '\''; return true;
            case '"' when quote == '"': sb.Append('"'); code = '"'; return true;
            case 'x':
                return Hex(s, ref i, end, 2, sb, out code, isByte: true);
            case 'u':
                return Hex(s, ref i, end, 4, sb, out code, isByte: false);
            case 'U':
                return Hex(s, ref i, end, 8, sb, out code, isByte: false);
            default:
                if (c >= '0' && c <= '7')
                {
                    if (i + 2 > end)
                    {
                        return false;
                    }

                    int v = c - '0';
                    for (int k = 0; k < 2; k++)
                    {
                        char d = s[i++];
                        if (d < '0' || d > '7')
                        {
                            return false;
                        }

                        v = (v * 8) + (d - '0');
                    }

                    if (v > 255)
                    {
                        return false;
                    }

                    code = v;
                    sb.Append((char)v); // a byte value; strings here hold text, so this maps to the Latin-1 code point
                    return true;
                }

                return false;
        }
    }

    private static bool Hex(ReadOnlySpan<char> s, ref int i, int end, int digits, StringBuilder sb, out int code, bool isByte)
    {
        code = -1;
        if (i + digits > end)
        {
            return false;
        }

        int v = 0;
        for (int k = 0; k < digits; k++)
        {
            int d = HexVal(s[i++]);
            if (d < 0)
            {
                return false;
            }

            v = (v * 16) + d;
        }

        code = v;
        if (isByte)
        {
            sb.Append((char)v);
            return true;
        }

        if (v > 0x10FFFF || (v >= 0xD800 && v <= 0xDFFF))
        {
            return false;
        }

        sb.Append(char.ConvertFromUtf32(v));
        return true;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
