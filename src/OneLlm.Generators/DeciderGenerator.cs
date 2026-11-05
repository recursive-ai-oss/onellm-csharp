using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OneLlm.Generators;

/// <summary>
/// Writes the typed side of a C# decider: for a partial class marked <c>[Decider("decider.yaml")]</c> it reads the project's copy of that
/// file and adds <c>Answers</c>, one enum per choice question, the YAML text and the <c>Decider</c> property. The questions are read with a
/// small block-YAML reader (no reflection, no runtime parsing); a definition it cannot read is reported as an error, and the full
/// definition is validated when the decider loads.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DeciderGenerator : IIncrementalGenerator
{
    private const string AttributeName = "OneLlm.Deciders.DeciderAttribute";
    private const string D = "global::OneLlm.Deciders.";
    private static readonly char[] Bar = { '|' };
    private const string J = "global::System.Text.Json.JsonElement";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<DeciderTarget> targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => Describe(ctx, ct));
        IncrementalValueProvider<ImmutableArray<(string Path, string Text)>> files = context.AdditionalTextsProvider
            .Select(static (t, ct) => (t.Path, t.GetText(ct)?.ToString() ?? ""))
            .Collect();
        context.RegisterSourceOutput(targets.Combine(files), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    private sealed class DeciderTarget
    {
        public string Definition = "";
        public string Name = "";
        public string Namespace = "";
        public string TypeName = "";
        public string[] Headers = Array.Empty<string>();
        public bool HasPrepare;
        public bool HasDecide;
        public string? Error;
        public Location Where = Location.None;
    }

    private sealed class Question
    {
        public string Id = "";
        public string Type = "";
        public bool Repeated;
        public List<string> Options = new List<string>();
        public bool BlockCriteria;
    }

    private static DeciderTarget Describe(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        AttributeData attribute = ctx.Attributes[0];
        var target = new DeciderTarget
        {
            Definition = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "",
            Name = type.Name,
            Where = ((ClassDeclarationSyntax)ctx.TargetNode).Identifier.GetLocation(),
            Namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            TypeName = type.Name,
        };
        foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string n && n.Length > 0)
            {
                target.Name = n;
            }
        }

        var headers = new List<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            bool partial = t.DeclaringSyntaxReferences.Length > 0
                && t.DeclaringSyntaxReferences.All(r => r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(m => m.Text == "partial"));
            if (!partial || t.TypeKind != TypeKind.Class || t.IsGenericType)
            {
                target.Error = "ODG001|'" + t.Name + "' must be a non-generic partial class so the generator can add the typed answers to it.";
            }

            headers.Insert(0, (t.IsStatic ? "static " : "") + "partial class " + t.Name);
        }

        target.Headers = headers.ToArray();
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is IMethodSymbol { IsStatic: true } m)
            {
                target.HasPrepare |= m.Name == "Prepare";
                target.HasDecide |= m.Name == "Decide";
            }
        }

        return target;
    }

    private static void Emit(SourceProductionContext spc, DeciderTarget target, ImmutableArray<(string Path, string Text)> files)
    {
        if (target.Error is not null)
        {
            string[] parts = target.Error.Split(Bar, 2);
            Report(spc, parts[0], parts[1], target.Where);
            return;
        }

        string norm = target.Definition.Replace('\\', '/');
        (string Path, string Text)[] match = files.Where(f => f.Path.Replace('\\', '/').EndsWith(norm, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (match.Length != 1)
        {
            Report(spc, "ODG002", match.Length == 0
                ? "The decider definition '" + target.Definition + "' is not among the project's AdditionalFiles; add <AdditionalFiles Include=\"" + target.Definition + "\" />."
                : "More than one additional file ends with '" + target.Definition + "'; give a longer path.", target.Where);
            return;
        }

        string yaml = match[0].Text;
        List<Question>? questions = ReadQuestions(yaml, out string? problem);
        if (questions is null)
        {
            Report(spc, "ODG003", problem ?? "The questions could not be read.", target.Where);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Question q in questions)
        {
            if (!seen.Add(Pascal(q.Id)))
            {
                Report(spc, "ODG004", "Questions '" + q.Id + "' and another become the same C# name '" + Pascal(q.Id) + "'.", target.Where);
                return;
            }

            if (q.Type == "choice")
            {
                var options = new HashSet<string>(StringComparer.Ordinal);
                foreach (string o in q.Options)
                {
                    if (!options.Add(Pascal(o)))
                    {
                        Report(spc, "ODG004", "Options of '" + q.Id + "' become the same C# name '" + Pascal(o) + "'.", target.Where);
                        return;
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n#nullable enable\n");
        int depth = 0;
        if (target.Namespace.Length > 0)
        {
            sb.Append("namespace ").Append(target.Namespace).Append("\n{\n");
            depth++;
        }

        foreach (string header in target.Headers)
        {
            sb.Append(' ', depth * 4).Append(header).Append('\n').Append(' ', depth * 4).Append("{\n");
            depth++;
        }

        string pad = new string(' ', depth * 4);
        sb.Append(pad).Append("/// <summary>The decider definition this class was generated from.</summary>\n");
        sb.Append(pad).Append("public const string Yaml = ").Append(Literal(yaml)).Append(";\n\n");
        foreach (Question q in questions.Where(static q => q.Type == "choice"))
        {
            sb.Append(pad).Append("/// <summary>The options of question '").Append(Doc(q.Id)).Append("'.</summary>\n");
            sb.Append(pad).Append("public enum ").Append(Pascal(q.Id)).Append("Choice\n").Append(pad).Append("{\n");
            foreach (string o in q.Options)
            {
                sb.Append(pad).Append("    /// <summary>Option '").Append(Doc(o)).Append("'.</summary>\n");
                sb.Append(pad).Append("    ").Append(Pascal(o)).Append(",\n");
            }

            sb.Append(pad).Append("}\n\n");
        }

        sb.Append(pad).Append("/// <summary>The answers to every question, typed.</summary>\n");
        sb.Append(pad).Append("public sealed class Answers\n").Append(pad).Append("{\n");
        foreach (Question q in questions)
        {
            string t = TypeOf(q);
            sb.Append(pad).Append("    /// <summary>The answer to question '").Append(Doc(q.Id)).Append("'.</summary>\n");
            sb.Append(pad).Append("    public ").Append(q.Repeated ? "global::System.Collections.Generic.IReadOnlyList<" + t + ">" : t).Append(' ').Append(Pascal(q.Id)).Append(" { get; private init; } = default!;\n\n");
        }

        sb.Append(pad).Append("    /// <summary>Reads the answers object the runtime produced.</summary>\n");
        sb.Append(pad).Append("    /// <param name=\"answers\">The answers by question id.</param>\n");
        sb.Append(pad).Append("    /// <returns>The typed answers.</returns>\n");
        sb.Append(pad).Append("    public static Answers From(").Append(J).Append(" answers) => new Answers\n").Append(pad).Append("    {\n");
        foreach (Question q in questions)
        {
            string one = Read(q, "e", "e");
            if (q.Repeated)
            {
                sb.Append(pad).Append("        ").Append(Pascal(q.Id)).Append(" = ReadList(answers.GetProperty(").Append(Literal(q.Id)).Append("), static e => ").Append(one).Append("),\n");
            }
            else
            {
                sb.Append(pad).Append("        ").Append(Pascal(q.Id)).Append(" = ").Append(Read(q, "answers.GetProperty(" + Literal(q.Id) + ")", "")).Append(",\n");
            }

        }

        sb.Append(pad).Append("    };\n\n");
        sb.Append(pad).Append("    private static global::System.Collections.Generic.IReadOnlyList<T> ReadList<T>(").Append(J).Append(" list, global::System.Func<").Append(J).Append(", T> read)\n");
        sb.Append(pad).Append("    {\n").Append(pad).Append("        var items = new global::System.Collections.Generic.List<T>(list.GetArrayLength());\n");
        sb.Append(pad).Append("        foreach (").Append(J).Append(" e in list.EnumerateArray())\n").Append(pad).Append("        {\n").Append(pad).Append("            items.Add(read(e));\n").Append(pad).Append("        }\n\n");
        sb.Append(pad).Append("        return items;\n").Append(pad).Append("    }\n");
        foreach (Question q in questions.Where(static q => q.Type == "choice"))
        {
            string en = Pascal(q.Id) + "Choice";
            sb.Append('\n').Append(pad).Append("    private static ").Append(en).Append(" Parse").Append(en).Append("(string key) => key switch\n").Append(pad).Append("    {\n");
            foreach (string o in q.Options)
            {
                sb.Append(pad).Append("        ").Append(Literal(o)).Append(" => ").Append(en).Append('.').Append(Pascal(o)).Append(",\n");
            }

            sb.Append(pad).Append("        _ => throw new global::System.FormatException(\"'\" + key + \"' is not an option of question '").Append(Doc(q.Id)).Append("'.\"),\n").Append(pad).Append("    };\n");
        }

        sb.Append(pad).Append("}\n\n");
        sb.Append(pad).Append("/// <summary>The decider: the definition and this class's logic.</summary>\n");
        sb.Append(pad).Append("public static ").Append(D).Append("Decider Decider { get; } = ").Append(D).Append("Decider.FromYaml(Yaml, ").Append(Literal(target.Name)).Append(", new ").Append(D).Append("DelegateDeciderLogic(")
            .Append(target.HasPrepare ? "static i => Prepare(i)" : "null").Append(", ")
            .Append(target.HasDecide ? "static (a, i) => Decide(Answers.From(a), i)" : "null").Append("));\n");
        for (int i = target.Headers.Length - 1; i >= 0; i--)
        {
            depth--;
            sb.Append(' ', depth * 4).Append("}\n");
        }

        if (target.Namespace.Length > 0)
        {
            sb.Append("}\n");
        }

        spc.AddSource(target.TypeName + ".Decider.g.cs", sb.ToString());
    }

    private static string TypeOf(Question q) => q.Type switch
    {
        "choice" => D + "ChoiceAnswer<" + Pascal(q.Id) + "Choice>",
        "score" => D + "ScoreAnswer",
        _ => D + "NoulAnswer",
    };

    // How one answer is read from the JSON element expression 'source'. 'e' is the lambda parameter when a list is read.
    private static string Read(Question q, string source, string _) => q.Type switch
    {
        "choice" => D + "ChoiceAnswer.From(" + source + ", Parse" + Pascal(q.Id) + "Choice)",
        "score" => D + "ScoreAnswer.From(" + source + ")",
        _ => D + "NoulAnswer.From(" + source + ")",
    };

    private static void Report(SourceProductionContext spc, string id, string message, Location where) =>
        spc.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(id, "Decider definition", "{0}", "OneLlm.Deciders", DiagnosticSeverity.Error, true), where, message));

    private static string Doc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", " ").Replace("\n", " ");

    private static string Literal(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    internal static string Pascal(string key)
    {
        var sb = new StringBuilder();
        bool upper = true;
        foreach (char c in key)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    // ---- a small reader for the questions block of a decider.yaml ----

    private static List<Question>? ReadQuestions(string yaml, out string? problem)
    {
        problem = null;
        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, static l => l.TrimEnd() == "questions:");
        if (start < 0)
        {
            problem = "The definition has no 'questions:' block at the top level.";
            return null;
        }

        var result = new List<Question>();
        int idIndent = -1, childIndent = -1;
        Question? current = null;
        int optionIndent = -1;
        bool inCriteria = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                break;
            }

            if (idIndent < 0)
            {
                idIndent = indent;
            }

            if (indent == idIndent)
            {
                string id = KeyOf(trimmed);
                if (id.Length == 0)
                {
                    problem = "Line " + (i + 1) + ": a question id was expected.";
                    return null;
                }

                current = new Question { Id = id };
                result.Add(current);
                childIndent = -1;
                inCriteria = false;
                optionIndent = -1;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent == childIndent)
            {
                inCriteria = false;
                string key = KeyOf(trimmed);
                string value = ValueOf(trimmed);
                if (key == "type")
                {
                    current.Type = value.Trim('"', '\'');
                }
                else if (key == "forEach")
                {
                    current.Repeated = true;
                }
                else if (key == "criteria")
                {
                    inCriteria = true;
                    optionIndent = -1;
                    if (value.Length > 0 && !value.StartsWith("#", StringComparison.Ordinal))
                    {
                        current.BlockCriteria = false;
                        current.Options.Add("\u0000flow");
                    }
                }
            }
            else if (inCriteria && indent > childIndent)
            {
                if (optionIndent < 0)
                {
                    optionIndent = indent;
                }

                if (indent == optionIndent && !trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    current.Options.Add(KeyOf(trimmed));
                }
            }
        }

        foreach (Question q in result)
        {
            if (q.Type != "choice" && q.Type != "score" && q.Type != "noul")
            {
                problem = "Question '" + q.Id + "' has type '" + q.Type + "'; it must be choice, score or noul.";
                return null;
            }

            if (q.Type == "choice" && (q.Options.Count < 2 || q.Options.Contains("\u0000flow")))
            {
                problem = "Question '" + q.Id + "': write the choice criteria as a block map with at least two options so the generator can name them.";
                return null;
            }
        }

        return result;
    }

    private static string KeyOf(string trimmed)
    {
        if (trimmed.Length > 0 && (trimmed[0] == '"' || trimmed[0] == '\''))
        {
            int close = trimmed.IndexOf(trimmed[0], 1);
            return close > 0 ? trimmed.Substring(1, close - 1) : "";
        }

        int colon = trimmed.IndexOf(':');
        while (colon >= 0 && colon + 1 < trimmed.Length && trimmed[colon + 1] != ' ')
        {
            colon = trimmed.IndexOf(':', colon + 1);
        }

        return colon < 0 ? "" : trimmed.Substring(0, colon).Trim();
    }

    private static string ValueOf(string trimmed)
    {
        int colon = trimmed.IndexOf(':');
        return colon < 0 ? "" : trimmed.Substring(colon + 1).Trim();
    }
}
