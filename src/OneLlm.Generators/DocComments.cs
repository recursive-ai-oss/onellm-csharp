using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace OneLlm.Generators;

// Reads XML doc comments from the syntax tree. The compilation may not be set to parse documentation (no GenerateDocumentationFile),
// in which case Roslyn keeps '///' lines as plain comments, so the lines are taken from the trivia text and parsed here.
internal static class DocComments
{
    public static XElement? Read(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (SyntaxTrivia trivia in node.GetLeadingTrivia())
        {
            string text = trivia.ToFullString();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("///", StringComparison.Ordinal))
                {
                    lines.Add(line.Substring(3));
                }
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        try
        {
            return XElement.Parse("<root>" + string.Join("\n", lines) + "</root>");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static string? Summary(XElement? doc) => Clean(doc?.Element("summary"));

    public static string? Param(XElement? doc, string name) =>
        Clean(doc?.Elements("param").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.Ordinal)));

    private static string? Clean(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        Append(element, sb);
        string text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static void Append(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText t:
                sb.Append(t.Value);
                break;
            case XElement e when e.Name.LocalName is "see" or "seealso":
                string? cref = (string?)e.Attribute("cref");
                string? lang = (string?)e.Attribute("langword");
                if (lang is not null)
                {
                    sb.Append(lang);
                }
                else if (cref is not null)
                {
                    // "T:Ns.Type", "M:Ns.Type.Method(System.Int32)" -> "Method"
                    string name = cref.Substring(cref.IndexOf(':') + 1);
                    int paren = name.IndexOf('(');
                    if (paren >= 0)
                    {
                        name = name.Substring(0, paren);
                    }

                    sb.Append(name.Substring(name.LastIndexOf('.') + 1));
                }
                else
                {
                    sb.Append(e.Value);
                }

                break;
            case XElement e when e.Name.LocalName is "paramref" or "typeparamref":
                sb.Append((string?)e.Attribute("name"));
                break;
            case XElement e:
                foreach (XNode child in e.Nodes())
                {
                    Append(child, sb);
                }

                if (e.Name.LocalName == "para")
                {
                    sb.Append(' ');
                }

                break;
        }
    }
}
