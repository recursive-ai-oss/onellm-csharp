using System.Globalization;
using System.Numerics;

namespace OneLlm.Templating;

/// <summary>Builds the parse tree from tokens. Grammar and parse-time checks follow the package documentation.</summary>
internal sealed class Parser
{
    private const int MaxNesting = 1000;

    private readonly List<Token> _toks;
    private readonly ParsedTemplate _tmpl;
    private readonly TemplateFunctions _funcs;
    private int _p;
    private List<string> _vars = new() { "$" };
    private int _rangeDepth;
    private int _nesting;

    private Parser(ParsedTemplate tmpl, List<Token> toks, TemplateFunctions funcs)
    {
        _tmpl = tmpl;
        _toks = toks;
        _funcs = funcs;
    }

    public static void Parse(ParsedTemplate tmpl)
    {
        var toks = Lexer.Lex(tmpl.Parsed);
        var parser = new Parser(tmpl, toks, tmpl.Functions);
        try
        {
            parser.ParseTop();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new TemplateException(TemplateErrorKind.Limit, tmpl.FileName ?? tmpl.Name, 0, 0, -1, "templates are nested too deeply");
        }
    }

    private Token Peek => _toks[_p];

    private Token Next()
    {
        Token t = _toks[_p];
        if (t.Kind != Tk.Eof)
        {
            _p++;
        }

        return t;
    }

    private Token PeekAt(int offset) => _toks[Math.Min(_p + offset, _toks.Count - 1)];

    private TemplateException Fail(int pos, string message) => _tmpl.Error(TemplateErrorKind.Parse, pos, message);

    private TemplateException Unexpected(Token t, string context)
    {
        if (t.Kind == Tk.Error)
        {
            return Fail(t.Pos, t.Val);
        }

        return Fail(t.Pos, $"unexpected {Describe(t)} in {context}");
    }

    private static string Describe(Token t) => t.Kind switch
    {
        Tk.Eof => "EOF",
        Tk.RightDelim => "\"}}\"",
        Tk.LeftDelim => "\"{{\"",
        Tk.Text => "text",
        Tk.Field => "\"." + t.Val + "\"",
        Tk.Error => t.Val,
        _ => "<" + t.Val + ">",
    };

    private void ParseTop()
    {
        var root = new ListNode { Pos = 0 };
        bool sawDefine = false;
        while (Peek.Kind != Tk.Eof)
        {
            if (Peek.Kind == Tk.LeftDelim && PeekAt(1).Kind == Tk.Define)
            {
                sawDefine = true;
                ParseDefinition();
                continue;
            }

            if (Peek.Kind == Tk.Error)
            {
                throw Unexpected(Peek, "input");
            }

            Node n = ParseTextOrAction();
            if (n is EndMarker or ElseMarker)
            {
                throw Unexpected(_toks[_p - 1], "input");
            }

            root.Nodes.Add(n);
        }

        // A define of the main template's own name replaces an empty main body; a non-empty body conflicts.
        if (_tmpl.Defs.TryGetValue(_tmpl.Name, out ListNode? existing))
        {
            if (!IsEmptyTree(root))
            {
                throw Fail(0, $"template: multiple definition of template \"{_tmpl.Name}\"");
            }

            root = existing;
        }

        _ = sawDefine;
        _tmpl.Root = root;
    }

    private sealed class EndMarker : Node { }

    private sealed class ElseMarker : Node { }

    private Node ParseTextOrAction()
    {
        Token t = Next();
        switch (t.Kind)
        {
            case Tk.Text:
                return new TextNode { Pos = t.Pos, Text = t.Val };
            case Tk.LeftDelim:
                return ParseAction(t);
            default:
                throw Unexpected(t, "input");
        }
    }

    private ListNode ParseList()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var list = new ListNode { Pos = Peek.Pos };
        if (++_nesting > MaxNesting)
        {
            throw new TemplateException(TemplateErrorKind.Limit, _tmpl.FileName ?? _tmpl.Name, 0, 0, -1, "templates are nested too deeply");
        }

        while (Peek.Kind != Tk.Eof)
        {
            if (Peek.Kind == Tk.LeftDelim && (PeekAt(1).Kind == Tk.End || PeekAt(1).Kind == Tk.Else))
            {
                _nesting--;
                return list;
            }

            Node n = ParseTextOrAction();
            list.Nodes.Add(n);
        }

        throw Fail(Peek.Pos, "unexpected EOF");
    }

    private Node ParseAction(Token delim)
    {
        Token t = Peek;
        switch (t.Kind)
        {
            case Tk.Block:
                Next();
                return ParseBlock(delim);
            case Tk.Break:
                Next();
                if (_rangeDepth == 0)
                {
                    throw Fail(t.Pos, "{{break}} outside {{range}}");
                }

                ExpectRightDelim("break");
                return new BreakNode { Pos = t.Pos };
            case Tk.Continue:
                Next();
                if (_rangeDepth == 0)
                {
                    throw Fail(t.Pos, "{{continue}} outside {{range}}");
                }

                ExpectRightDelim("continue");
                return new ContinueNode { Pos = t.Pos };
            case Tk.If:
                Next();
                return ParseControl(BranchKind.If, t.Pos);
            case Tk.Range:
                Next();
                return ParseControl(BranchKind.Range, t.Pos);
            case Tk.With:
                Next();
                return ParseControl(BranchKind.With, t.Pos);
            case Tk.Template:
                Next();
                return ParseTemplateCall(t.Pos);
            case Tk.Define:
                throw Fail(t.Pos, "unexpected <define> in command");
            case Tk.End:
                Next();
                ExpectRightDelim("end");
                return new EndMarker { Pos = t.Pos };
            case Tk.Else:
                Next();
                return new ElseMarker { Pos = t.Pos };
        }

        PipeNode pipe = ParsePipeline("command", Tk.RightDelim);
        return new ActionNode { Pos = t.Pos, Pipe = pipe };
    }

    private void ExpectRightDelim(string context)
    {
        Token t = Next();
        if (t.Kind != Tk.RightDelim)
        {
            throw Unexpected(t, context);
        }
    }

    // Consumes "{{end}}" at the current position (the list parser stops before it).
    private void ConsumeEnd()
    {
        Next(); // {{
        Token e = Next();
        if (e.Kind != Tk.End)
        {
            throw Unexpected(e, "end");
        }

        ExpectRightDelim("end");
    }

    private BranchNode ParseControl(BranchKind kind, int pos)
    {
        int mark = _vars.Count;
        string context = kind switch { BranchKind.If => "if", BranchKind.Range => "range", _ => "with" };
        PipeNode pipe = ParsePipeline(context, Tk.RightDelim);
        if (kind != BranchKind.Range && pipe.Decl.Count > 1)
        {
            throw Fail(pipe.Pos, $"too many declarations in {context}");
        }

        if (kind == BranchKind.Range)
        {
            _rangeDepth++;
        }

        ListNode list = ParseList();
        if (kind == BranchKind.Range)
        {
            _rangeDepth--;
        }

        ListNode? elseList = null;
        // list stops before "{{" followed by end or else.
        if (PeekAt(1).Kind == Tk.Else)
        {
            Next(); // {{
            Next(); // else
            Token after = Peek;
            if ((kind == BranchKind.If && after.Kind == Tk.If) || (kind == BranchKind.With && after.Kind == Tk.With))
            {
                // "else if" / "else with": the nested control shares this block's single end.
                Next();
                Node nested = ParseControl(kind, after.Pos);
                elseList = new ListNode { Pos = after.Pos };
                elseList.Nodes.Add(nested);
                _vars.RemoveRange(mark, _vars.Count - mark);
                return new BranchNode { Pos = pos, Kind = kind, Pipe = pipe, List = list, ElseList = elseList };
            }

            ExpectRightDelim("else");
            elseList = ParseList();
        }

        ConsumeEnd();
        _vars.RemoveRange(mark, _vars.Count - mark);
        return new BranchNode { Pos = pos, Kind = kind, Pipe = pipe, List = list, ElseList = elseList };
    }

    private string ParseTemplateName(Token t, string context)
    {
        if (t.Kind != Tk.String)
        {
            throw Unexpected(t, context);
        }

        return t.Val;
    }

    private TemplateCallNode ParseTemplateCall(int pos)
    {
        Token nameTok = Next();
        string name = ParseTemplateName(nameTok, "template clause");
        PipeNode? pipe = null;
        if (Peek.Kind != Tk.RightDelim)
        {
            pipe = ParsePipeline("template clause", Tk.RightDelim);
        }
        else
        {
            Next();
        }

        return new TemplateCallNode { Pos = pos, Name = name, Pipe = pipe };
    }

    private TemplateCallNode ParseBlock(Token delim)
    {
        Token nameTok = Next();
        string name = ParseTemplateName(nameTok, "block clause");
        PipeNode pipe = ParsePipeline("block clause", Tk.RightDelim);
        var savedVars = _vars;
        _vars = new List<string> { "$" };
        ListNode list = ParseList();
        ConsumeEnd();
        _vars = savedVars;
        AddDefinition(name, list, nameTok.Pos);
        return new TemplateCallNode { Pos = delim.Pos, Name = name, Pipe = pipe };
    }

    private void ParseDefinition()
    {
        Next(); // {{
        Token def = Next();
        Token nameTok = Next();
        string name = ParseTemplateName(nameTok, "define clause");
        ExpectRightDelim("define clause");
        var savedVars = _vars;
        _vars = new List<string> { "$" };
        ListNode list = ParseList();
        ConsumeEnd();
        _vars = savedVars;
        AddDefinition(name, list, def.Pos);
    }

    private void AddDefinition(string name, ListNode list, int pos)
    {
        if (_tmpl.Defs.TryGetValue(name, out ListNode? old) && !IsEmptyTree(old))
        {
            if (!IsEmptyTree(list))
            {
                throw Fail(pos, $"template: multiple definition of template \"{name}\"");
            }

            return;
        }

        _tmpl.Defs[name] = list;
    }

    internal static bool IsEmptyTree(Node? n)
    {
        switch (n)
        {
            case null:
                return true;
            case TextNode t:
                return IsAllSpace(t.Text);
            case ListNode l:
                foreach (Node c in l.Nodes)
                {
                    if (!IsEmptyTree(c))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static bool IsAllSpace(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDeclEnd(Tk k) => k == Tk.Declare || k == Tk.Assign;

    private PipeNode ParsePipeline(string context, Tk end)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var pipe = new PipeNode { Pos = Peek.Pos };
        if (++_nesting > MaxNesting)
        {
            throw new TemplateException(TemplateErrorKind.Limit, _tmpl.FileName ?? _tmpl.Name, 0, 0, -1, "templates are nested too deeply");
        }

        // Optional declaration: "$x :=", "$x =", or (range only) "$i, $e :=".
        if (Peek.Kind == Tk.Variable)
        {
            Token v = Peek;
            Token after = PeekAt(1);
            if (IsDeclEnd(after.Kind))
            {
                Next();
                Token op = Next();
                pipe.IsAssign = op.Kind == Tk.Assign;
                AddDecl(pipe, v);
            }
            else if (after.Kind == Tk.Comma && PeekAt(2).Kind == Tk.Variable && IsDeclEnd(PeekAt(3).Kind))
            {
                if (context != "range")
                {
                    throw Fail(PeekAt(2).Pos, $"too many declarations in {context}");
                }

                Next();
                Next();
                Token v2 = Next();
                Token op = Next();
                pipe.IsAssign = op.Kind == Tk.Assign;
                AddDecl(pipe, v);
                AddDecl(pipe, v2);
            }
            else if (after.Kind == Tk.Comma)
            {
                if (context == "range")
                {
                    throw Fail(after.Pos, "range can only initialize variables");
                }

                throw Fail(after.Pos, $"too many declarations in {context}");
            }
        }

        while (true)
        {
            Token t = Peek;
            if (t.Kind == end)
            {
                if (pipe.Cmds.Count == 0)
                {
                    throw Fail(t.Pos, $"missing value for {context}");
                }

                for (int i = 1; i < pipe.Cmds.Count; i++)
                {
                    Node first = pipe.Cmds[i].Args[0];
                    if (first is BoolNode or DotNode or NilNode or NumberNode or StringNode)
                    {
                        throw Fail(first.Pos, $"non executable command in pipeline stage {i + 1}");
                    }
                }

                Next();
                _nesting--;
                return pipe;
            }

            switch (t.Kind)
            {
                case Tk.Ident or Tk.Bool or Tk.CharConst or Tk.Dot or Tk.Field or Tk.Nil or Tk.Number or Tk.String or Tk.Variable or Tk.LParen:
                    pipe.Cmds.Add(ParseCommand());
                    break;
                default:
                    throw Unexpected(t, context);
            }
        }
    }

    private void AddDecl(PipeNode pipe, Token v)
    {
        // A variable counts as declared from its own pipeline on, for "=" as well as ":="; assigning to one that was never
        // declared fails when the assignment runs, not when the text is parsed (observed from the reference).
        _vars.Add(v.Val);

        pipe.Decl.Add(new VariableNode { Pos = v.Pos, Name = v.Val });
    }

    private CommandNode ParseCommand()
    {
        var cmd = new CommandNode { Pos = Peek.Pos };
        while (true)
        {
            Node? operand = ParseOperand();
            if (operand is not null)
            {
                cmd.Args.Add(operand);
            }

            Token t = Peek;
            if (t.Kind is Tk.RightDelim or Tk.RParen)
            {
                break;
            }

            if (t.Kind == Tk.Pipe)
            {
                Next();
                break;
            }

            if (operand is not null && !t.SpaceBefore)
            {
                throw Unexpected(t, "operand");
            }

            if (operand is null)
            {
                throw Unexpected(t, "operand");
            }
        }

        if (cmd.Args.Count == 0)
        {
            throw Fail(cmd.Pos, "empty command");
        }

        return cmd;
    }

    private Node? ParseOperand()
    {
        Node? term = ParseTerm();
        if (term is null)
        {
            return null;
        }

        if (Peek.Kind == Tk.Field && !Peek.SpaceBefore)
        {
            var fields = new List<string>();
            while (Peek.Kind == Tk.Field && !Peek.SpaceBefore)
            {
                fields.Add(Next().Val);
            }

            switch (term)
            {
                case FieldNode f:
                    return new FieldNode { Pos = f.Pos, Idents = f.Idents.Concat(fields).ToArray() };
                case VariableNode v:
                    return new VariableNode { Pos = v.Pos, Name = v.Name, Fields = v.Fields.Concat(fields).ToArray() };
                case BoolNode or StringNode or NumberNode or NilNode or DotNode:
                    throw Fail(term.Pos, "unexpected . after term");
                default:
                    return new ChainNode { Pos = term.Pos, Inner = term, Fields = fields.ToArray() };
            }
        }

        return term;
    }

    private Node? ParseTerm()
    {
        Token t = Peek;
        switch (t.Kind)
        {
            case Tk.Ident:
                Next();
                if (_funcs.TryGet(t.Val, out TemplateFunction? f) && f is not null)
                {
                    return new IdentifierNode { Pos = t.Pos, Name = t.Val, Function = f };
                }

                if (Builtins.Table.TryGetValue(t.Val, out TemplateFunction? b))
                {
                    return new IdentifierNode { Pos = t.Pos, Name = t.Val, Function = b };
                }

                throw Fail(t.Pos, $"function \"{t.Val}\" not defined");
            case Tk.Dot:
                Next();
                return new DotNode { Pos = t.Pos };
            case Tk.Nil:
                Next();
                return new NilNode { Pos = t.Pos };
            case Tk.Variable:
                Next();
                if (!_vars.Contains(t.Val))
                {
                    throw Fail(t.Pos, $"undefined variable \"{t.Val}\"");
                }

                return new VariableNode { Pos = t.Pos, Name = t.Val };
            case Tk.Field:
                Next();
                return new FieldNode { Pos = t.Pos, Idents = new[] { t.Val } };
            case Tk.Bool:
                Next();
                return new BoolNode { Pos = t.Pos, Value = t.Val == "true" };
            case Tk.CharConst:
            case Tk.Number:
                Next();
                return ParseNumber(t);
            case Tk.LParen:
                Next();
                return ParsePipeline("parenthesized pipeline", Tk.RParen);
            case Tk.String:
                Next();
                return new StringNode { Pos = t.Pos, Value = t.Val };
            case Tk.Error:
                throw Fail(t.Pos, t.Val);
        }

        return null;
    }

    private NumberNode ParseNumber(Token t)
    {
        var n = new NumberNode { Pos = t.Pos, Text = t.Val };
        if (t.Kind == Tk.CharConst)
        {
            n.IsInt = true;
            n.Int = long.Parse(t.Val, CultureInfo.InvariantCulture);
            n.Float = n.Int;
            return n;
        }

        string text = t.Val;
        if (text.EndsWith('i'))
        {
            throw Fail(t.Pos, $"complex constants are not supported: {text}");
        }

        // Underscores may only separate digits (or follow a base prefix).
        for (int u = text.IndexOf('_', StringComparison.Ordinal); u >= 0; u = text.IndexOf('_', u + 1))
        {
            bool before = u > 0 && (char.IsAsciiLetterOrDigit(text[u - 1]));
            bool after = u + 1 < text.Length && char.IsAsciiLetterOrDigit(text[u + 1]);
            if (!before || !after)
            {
                throw Fail(t.Pos, $"illegal number syntax: \"{text}\"");
            }
        }

        string body = text.Replace("_", "", StringComparison.Ordinal);
        bool neg = false;
        string digits = body;
        if (digits.StartsWith('+') || digits.StartsWith('-'))
        {
            neg = digits[0] == '-';
            digits = digits.Substring(1);
        }

        bool hexInt = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && digits.AsSpan().IndexOfAny('p', 'P', '.') < 0;
        bool looksFloat = !hexInt && digits.AsSpan().IndexOfAny(".eEpP") >= 0;
        if (!looksFloat)
        {
            if (!TryParseInteger(digits, out BigInteger big))
            {
                throw Fail(t.Pos, $"illegal number syntax: \"{text}\"");
            }

            if (neg)
            {
                big = -big;
            }

            if (!neg && big > long.MaxValue && big <= ulong.MaxValue)
            {
                n.IsUint = true;
                n.Float = (double)big;
                return n;
            }

            if (big < long.MinValue || big > long.MaxValue)
            {
                throw Fail(t.Pos, $"integer overflow: \"{text}\"");
            }

            n.IsInt = true;
            n.Int = (long)big;
            n.Float = (double)big;
            return n;
        }

        if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsInfinity(d))
        {
            throw Fail(t.Pos, $"illegal number syntax: \"{text}\"");
        }

        n.AsFloat = true;
        n.Float = d;
        if (Math.Truncate(d) == d && Math.Abs(d) < 9.2e18)
        {
            n.IsInt = true;
            n.Int = (long)d;
        }

        return n;
    }

    private static bool TryParseInteger(string digits, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (digits.Length == 0)
        {
            return false;
        }

        int radix = 10;
        string s = digits;
        if (s.Length > 1 && s[0] == '0')
        {
            char c = s[1];
            if (c is 'x' or 'X')
            {
                radix = 16;
                s = s.Substring(2);
            }
            else if (c is 'o' or 'O')
            {
                radix = 8;
                s = s.Substring(2);
            }
            else if (c is 'b' or 'B')
            {
                radix = 2;
                s = s.Substring(2);
            }
            else
            {
                radix = 8;
                s = s.Substring(1);
            }
        }

        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 99,
            };
            if (d >= radix)
            {
                return false;
            }

            value = (value * radix) + d;
        }

        return true;
    }
}
