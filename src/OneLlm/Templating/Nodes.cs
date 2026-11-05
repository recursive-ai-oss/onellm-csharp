namespace OneLlm.Templating;

// Parse tree. Positions are offsets into the (code-region-escaped) text; ParsedTemplate maps them back for errors.

internal abstract class Node
{
    public int Pos;
}

internal sealed class ListNode : Node
{
    public readonly List<Node> Nodes = new();
}

internal sealed class TextNode : Node
{
    public string Text = "";
}

internal sealed class ActionNode : Node
{
    public PipeNode Pipe = null!;
}

internal enum BranchKind { If, Range, With }

internal sealed class BranchNode : Node
{
    public BranchKind Kind;
    public PipeNode Pipe = null!;
    public ListNode List = null!;
    public ListNode? ElseList;
}

internal sealed class TemplateCallNode : Node
{
    public string Name = "";
    public PipeNode? Pipe;
}

internal sealed class BreakNode : Node { }

internal sealed class ContinueNode : Node { }

internal sealed class PipeNode : Node
{
    public readonly List<VariableNode> Decl = new();
    public bool IsAssign;
    public readonly List<CommandNode> Cmds = new();
}

internal sealed class CommandNode : Node
{
    public readonly List<Node> Args = new();
}

internal sealed class DotNode : Node { }

internal sealed class NilNode : Node { }

internal sealed class BoolNode : Node
{
    public bool Value;
}

internal sealed class StringNode : Node
{
    public string Value = "";
}

/// <summary>A numeric constant. <see cref="AsFloat"/> says the text used float syntax (decimal point or exponent).</summary>
internal sealed class NumberNode : Node
{
    public string Text = "";
    public long Int;
    public double Float;
    public bool IsInt;
    /// <summary>An integer above the largest int that still fits an unsigned 64-bit value: it parses, but using it as an int fails.</summary>
    public bool IsUint;
    public bool AsFloat;
}

internal sealed class FieldNode : Node
{
    public string[] Idents = Array.Empty<string>();
}

/// <summary><c>$x</c> or <c>$x.A.B</c>. <see cref="Name"/> includes the dollar sign.</summary>
internal sealed class VariableNode : Node
{
    public string Name = "";
    public string[] Fields = Array.Empty<string>();
}

internal sealed class IdentifierNode : Node
{
    public string Name = "";
    public TemplateFunction Function = null!;
}

/// <summary><c>(pipeline).A.B</c> or <c>fn.A</c>.</summary>
internal sealed class ChainNode : Node
{
    public Node Inner = null!;
    public string[] Fields = Array.Empty<string>();
}

/// <summary>A parsed template text with its <c>define</c> blocks and the data needed to position errors.</summary>
internal sealed class ParsedTemplate
{
    public string Name = "template";
    public string? FileName;
    /// <summary>The text the caller supplied (before code-region escaping).</summary>
    public string Original = "";
    /// <summary>The text actually parsed (after escaping).</summary>
    public string Parsed = "";
    /// <summary>Escaped-text offsets where each <c>{{"{{"}}</c>-style replacement begins.</summary>
    public int[] Replacements = Array.Empty<int>();
    public ListNode Root = null!;
    public Dictionary<string, ListNode> Defs = new(StringComparer.Ordinal);
    public TemplateFunctions Functions = null!;

    public int ToOriginalOffset(int offset)
    {
        // Each replacement turns 2 characters into 8 (CodeRegions.ReplacementLength), so earlier ones shift later offsets by 6.
        int shift = 0;
        foreach (int start in Replacements)
        {
            if (offset < start)
            {
                break;
            }

            if (offset < start + CodeRegions.ReplacementLength)
            {
                return start - shift;
            }

            shift += CodeRegions.ReplacementLength - 2;
        }

        return offset - shift;
    }

    public (int Line, int Column, int Offset) Locate(int offset)
    {
        int o = Math.Clamp(ToOriginalOffset(offset), 0, Original.Length);
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < o; i++)
        {
            if (Original[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, o - lineStart + 1, o);
    }

    public TemplateException Error(TemplateErrorKind kind, int offset, string detail, Exception? inner = null)
    {
        var (line, col, off) = Locate(offset);
        return new TemplateException(kind, FileName ?? Name, line, col, off, detail, inner);
    }
}
