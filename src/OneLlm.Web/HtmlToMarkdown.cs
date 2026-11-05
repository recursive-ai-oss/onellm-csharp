using System.Text;
using RecursiveAI.OneHtml;

namespace OneLlm.Web;

/// <summary>
/// Turns an HTML page into markdown a model can read: headings, paragraphs, lists, links, emphasis, code, quotes and tables. Scripts, styles,
/// hidden elements and page furniture that carries no text (SVG, frames) are left out. The page is parsed by OneHtml, so malformed
/// markup comes out as a browser would read it.
/// </summary>
public static class HtmlToMarkdown
{
    private const int MaxDepth = 400;

    private static readonly HashSet<string> s_skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "head", "svg", "iframe", "object", "embed", "canvas", "audio", "video", "select", "button",
    };

    private static readonly HashSet<string> s_containers = new(StringComparer.Ordinal)
    {
        "html", "body", "div", "section", "article", "main", "header", "footer", "aside", "nav", "form", "figure", "figcaption", "details",
        "summary", "fieldset", "address", "hgroup", "center", "dl", "dt", "dd", "legend", "caption", "menu",
    };

    /// <summary>Converts a parsed document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="baseUri">The page's address, used to make relative links absolute. Null leaves them as written.</param>
    /// <returns>The markdown, without trailing whitespace.</returns>
    public static string Convert(Document document, Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Node root = document.DocumentElement.IsNull ? document.Root : document.DocumentElement;
        return Blocks(root, baseUri, 0).Trim();
    }

    /// <summary>Parses and converts HTML text.</summary>
    /// <param name="html">The HTML.</param>
    /// <param name="baseUri">The page's address, or null.</param>
    /// <returns>The markdown.</returns>
    public static string Convert(string html, Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Convert(Document.Parse(html), baseUri);
    }

    private static string Blocks(Node parent, Uri? baseUri, int depth)
    {
        var blocks = new List<string>();
        var inline = new StringBuilder();

        void Flush()
        {
            string text = Tidy(inline.ToString());
            if (text.Length > 0)
            {
                blocks.Add(text);
            }

            inline.Clear();
        }

        foreach (Node child in parent.ChildNodes)
        {
            if (child.Kind == NodeKind.Text)
            {
                inline.Append(Collapse(child.Data));
                continue;
            }

            if (!child.IsElement || IsHidden(child))
            {
                continue;
            }

            string tag = child.LocalName;
            if (s_skipped.Contains(tag))
            {
                continue;
            }

            if (depth >= MaxDepth)
            {
                inline.Append(Collapse(child.TextContent.AsSpan()));
                continue;
            }

            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    string heading = Tidy(Inline(child, baseUri, depth + 1));
                    if (heading.Length > 0)
                    {
                        blocks.Add(new string('#', tag[1] - '0') + " " + heading.Replace('\n', ' '));
                    }

                    break;
                case "p":
                    Flush();
                    string para = Blocks(child, baseUri, depth + 1);
                    if (para.Length > 0)
                    {
                        blocks.Add(para);
                    }

                    break;
                case "ul" or "ol":
                    Flush();
                    string list = List(child, tag == "ol", baseUri, depth + 1);
                    if (list.Length > 0)
                    {
                        blocks.Add(list);
                    }

                    break;
                case "pre":
                    Flush();
                    string code = (child.TextContent ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
                    string fence = code.Contains("```", StringComparison.Ordinal) ? "~~~" : "```";
                    blocks.Add(fence + "\n" + code + "\n" + fence);
                    break;
                case "blockquote":
                    Flush();
                    string quote = Blocks(child, baseUri, depth + 1);
                    if (quote.Length > 0)
                    {
                        blocks.Add(string.Join('\n', quote.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l)));
                    }

                    break;
                case "hr":
                    Flush();
                    blocks.Add("---");
                    break;
                case "table":
                    Flush();
                    string table = Table(child, baseUri, depth + 1);
                    if (table.Length > 0)
                    {
                        blocks.Add(table);
                    }

                    break;
                case "br":
                    inline.Append('\n');
                    break;
                default:
                    if (s_containers.Contains(tag) || tag is "li" or "tr" or "td" or "th" or "tbody" or "thead" or "tfoot")
                    {
                        Flush();
                        string inner = Blocks(child, baseUri, depth + 1);
                        if (inner.Length > 0)
                        {
                            blocks.Add(inner);
                        }
                    }
                    else
                    {
                        inline.Append(Inline(child, baseUri, depth + 1));
                    }

                    break;
            }
        }

        Flush();
        return string.Join("\n\n", blocks);
    }

    private static string Inline(Node node, Uri? baseUri, int depth)
    {
        var sb = new StringBuilder();
        foreach (Node child in node.ChildNodes)
        {
            if (child.Kind == NodeKind.Text)
            {
                sb.Append(Collapse(child.Data));
                continue;
            }

            if (!child.IsElement || IsHidden(child) || s_skipped.Contains(child.LocalName))
            {
                continue;
            }

            if (depth >= MaxDepth)
            {
                sb.Append(Collapse(child.TextContent.AsSpan()));
                continue;
            }

            switch (child.LocalName)
            {
                case "br":
                    sb.Append('\n');
                    break;
                case "a":
                    string text = Tidy(Inline(child, baseUri, depth + 1)).Replace('\n', ' ');
                    string? href = Resolve(child.GetAttribute("href"), baseUri);
                    sb.Append(href is null || text.Length == 0 ? text : $"[{text}]({href})");
                    break;
                case "strong" or "b":
                    Wrap(sb, "**", Inline(child, baseUri, depth + 1));
                    break;
                case "em" or "i":
                    Wrap(sb, "*", Inline(child, baseUri, depth + 1));
                    break;
                case "del" or "s" or "strike":
                    Wrap(sb, "~~", Inline(child, baseUri, depth + 1));
                    break;
                case "code" or "kbd" or "samp" or "tt":
                    string literal = child.TextContent ?? "";
                    if (literal.Trim().Length > 0)
                    {
                        string ticks = literal.Contains('`', StringComparison.Ordinal) ? "``" : "`";
                        sb.Append(ticks).Append(literal.Replace('\n', ' ').Trim()).Append(ticks);
                    }

                    break;
                case "img":
                    string? src = Resolve(child.GetAttribute("src"), baseUri);
                    string alt = (child.GetAttribute("alt") ?? "").Trim();
                    if (src is not null)
                    {
                        sb.Append("![").Append(alt).Append("](").Append(src).Append(')');
                    }
                    else if (alt.Length > 0)
                    {
                        sb.Append(alt);
                    }

                    break;
                default:
                    sb.Append(Inline(child, baseUri, depth + 1));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string List(Node list, bool ordered, Uri? baseUri, int depth)
    {
        var items = new List<string>();
        int number = 1;
        if (ordered && int.TryParse(list.GetAttribute("start"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int start))
        {
            number = start;
        }

        foreach (Node li in list.ChildNodes)
        {
            if (!li.IsElement || li.LocalName != "li" || IsHidden(li))
            {
                continue;
            }

            // Blocks inside an item stay together without blank lines, so nested lists read as part of the item.
            string body = Blocks(li, baseUri, depth + 1).Replace("\n\n", "\n", StringComparison.Ordinal);
            string marker = ordered ? $"{number++}. " : "- ";
            string[] lines = body.Split('\n');
            var sb = new StringBuilder(marker).Append(lines[0]);
            for (int i = 1; i < lines.Length; i++)
            {
                sb.Append('\n').Append(lines[i].Length == 0 ? "" : new string(' ', marker.Length) + lines[i]);
            }

            items.Add(sb.ToString());
        }

        return string.Join('\n', items);
    }

    private static string Table(Node table, Uri? baseUri, int depth)
    {
        var rows = new List<List<string>>();
        void Collect(Node n)
        {
            foreach (Node c in n.ChildNodes)
            {
                if (!c.IsElement)
                {
                    continue;
                }

                if (c.LocalName == "tr")
                {
                    var cells = new List<string>();
                    foreach (Node cell in c.ChildNodes)
                    {
                        if (cell.IsElement && cell.LocalName is "td" or "th")
                        {
                            cells.Add(Tidy(Inline(cell, baseUri, depth + 1)).Replace('\n', ' ').Replace("|", "\\|", StringComparison.Ordinal));
                        }
                    }

                    if (cells.Count > 0)
                    {
                        rows.Add(cells);
                    }
                }
                else if (c.LocalName is "thead" or "tbody" or "tfoot")
                {
                    Collect(c);
                }
            }
        }

        Collect(table);
        if (rows.Count == 0)
        {
            return "";
        }

        int width = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append("| ").Append(string.Join(" | ", rows[r].Concat(Enumerable.Repeat("", width - rows[r].Count)))).Append(" |\n");
            if (r == 0)
            {
                sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void Wrap(StringBuilder sb, string mark, string inner)
    {
        // Spaces move outside the markers, because "** bold**" is not bold.
        string trimmed = inner.Trim();
        if (trimmed.Length == 0)
        {
            sb.Append(inner);
            return;
        }

        if (inner.Length > 0 && char.IsWhiteSpace(inner[0]))
        {
            sb.Append(' ');
        }

        sb.Append(mark).Append(trimmed.Replace('\n', ' ')).Append(mark);
        if (char.IsWhiteSpace(inner[^1]))
        {
            sb.Append(' ');
        }
    }

    private static bool IsHidden(Node e) =>
        e.HasAttribute("hidden")
        || string.Equals(e.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)
        || (e.GetAttribute("style") is { } style && style.Replace(" ", "", StringComparison.Ordinal).Contains("display:none", StringComparison.OrdinalIgnoreCase));

    private static string? Resolve(string? href, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        string h = href.Trim();
        if (h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || h.StartsWith('#'))
        {
            return null;
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, h, out Uri? abs))
        {
            return abs.AbsoluteUri;
        }

        return h;
    }

    // Runs of whitespace become one space, as a browser renders them.
    private static string Collapse(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) && c != '\u00A0')
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c == '\u00A0' ? ' ' : c);
        }

        if (space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    // Trims each line and the whole, so spaces left next to a line break do not show.
    private static string Tidy(string text) => string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim('\n', ' ');
}
