using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace OneLlm.Generators;

/// <summary>
/// Writes an AOT-safe <c>ITool</c> for every method marked <c>[Tool]</c> on a partial class: the input schema is computed at compile
/// time from the parameter list and doc comments, and arguments are read with plain code, never reflection.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ToolGenerator : IIncrementalGenerator
{
    private const string ToolAttributeName = "OneLlm.Tools.ToolAttribute";
    private const string B = "global::OneLlm.Tools.ToolBinder.";
    private const string R = "global::OneLlm.Tools.ToolReturn.";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<ToolMethodModel>> methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ToolAttributeName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => Build(ctx, ct))
            .Collect();
        context.RegisterSourceOutput(methods, static (spc, all) => Emit(spc, all));
    }

    private static ToolMethodModel Build(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        var syntax = (MethodDeclarationSyntax)ctx.TargetNode;
        AttributeData attribute = ctx.Attributes[0];
        Location where = syntax.Identifier.GetLocation();
        var model = new ToolMethodModel { Location = LocationInfo.From(where) };
        INamedTypeSymbol type = method.ContainingType;
        model.TypeKey = TypePlanner.Text(type);

        try
        {
            ct.ThrowIfCancellationRequested();
            CheckContainers(type, model);
            if (method.IsGenericMethod)
            {
                throw new PlanException("OLT004", $"'{method.Name}' is generic; tool methods must not have type parameters.");
            }

            var planner = new TypePlanner(ctx.SemanticModel.Compilation);
            BuildMethod(ctx.SemanticModel.Compilation, method, syntax, attribute, planner, model);
        }
        catch (PlanException e)
        {
            model.Failure = new DiagInfo(e.Id, e.Message, model.Location);
        }

        model.Seal();
        return model;
    }

    private static void CheckContainers(INamedTypeSymbol type, ToolMethodModel model)
    {
        var chain = new List<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.TypeKind != TypeKind.Class || t.IsRecord)
            {
                throw new PlanException("OLT001", $"'{t.Name}' must be a class to hold tool methods.");
            }

            if (t.IsGenericType)
            {
                throw new PlanException("OLT010", $"'{t.Name}' is generic; tool classes cannot have type parameters.");
            }

            bool partial = t.DeclaringSyntaxReferences.Length > 0
                && t.DeclaringSyntaxReferences.All(r => r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
            if (!partial)
            {
                throw new PlanException("OLT001", $"'{t.Name}' must be declared 'partial' so the generator can add the tools to it.");
            }

            chain.Insert(0, (t.IsStatic ? "static " : "") + "partial class " + t.Name);
        }

        model.Namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        model.Headers = chain.ToArray();
        model.TypeName = type.Name;
        model.IsStaticType = type.IsStatic;
        model.HasParameterlessConstructor = type.IsStatic || type.InstanceConstructors.Any(c => c.Parameters.Length == 0);
    }

    private static void BuildMethod(Compilation compilation, IMethodSymbol method, MethodDeclarationSyntax syntax, AttributeData attribute, TypePlanner planner, ToolMethodModel model)
    {
        string? nameArg = null;
        string? descArg = null;
        bool endsLoop = false;
        bool approval = false;
        bool parallel = true;
        int timeout = 0;
        foreach (KeyValuePair<string, TypedConstant> a in attribute.NamedArguments)
        {
            switch (a.Key)
            {
                case "Name":
                    nameArg = a.Value.Value as string;
                    break;
                case "Description":
                    descArg = a.Value.Value as string;
                    break;
                case "EndsLoop":
                    endsLoop = a.Value.Value is true;
                    break;
                case "RequiresApproval":
                    approval = a.Value.Value is true;
                    break;
                case "ParallelSafe":
                    parallel = a.Value.Value is not false;
                    break;
                case "TimeoutSeconds":
                    timeout = a.Value.Value is int s ? s : 0;
                    break;
            }
        }

        string name = nameArg ?? TypePlanner.SnakeCase(method.Name.EndsWith("Async", StringComparison.Ordinal) && method.Name.Length > 5 ? method.Name.Substring(0, method.Name.Length - 5) : method.Name);
        if (name.Length == 0 || name.Length > 64 || !name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
        {
            throw new PlanException("OLT008", $"'{name}' is not a valid tool name: use letters, digits, '_' and '-', at most 64 characters.");
        }

        model.ToolName = name;
        System.Xml.Linq.XElement? doc = DocComments.Read(syntax);
        string description = descArg ?? DescriptionOf(method) ?? DocComments.Summary(doc) ?? "";

        var props = new StringBuilder("{");
        var required = new List<string>();
        var binds = new StringBuilder();
        var args = new List<string>();
        var names = new List<string>();
        bool sawContext = false;
        bool sawToken = false;
        int index = 0;
        foreach (IParameterSymbol p in method.Parameters)
        {
            if (p.RefKind != RefKind.None)
            {
                throw new PlanException("OLT002", $"Parameter '{p.Name}' is {p.RefKind.ToString().ToLowerInvariant()}; tool parameters must be passed by value.");
            }

            string typeName = (p.Type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + p.Type.Name;
            if (typeName == "OneLlm.Tools.ToolContext")
            {
                sawContext = sawContext ? throw new PlanException("OLT009", "A tool takes at most one ToolContext parameter.") : true;
                args.Add("context");
                continue;
            }

            if (typeName == "System.Threading.CancellationToken")
            {
                sawToken = sawToken ? throw new PlanException("OLT009", "A tool takes at most one CancellationToken parameter.") : true;
                args.Add("cancellationToken");
                continue;
            }

            Plan plan;
            try
            {
                plan = planner.Analyze(p.Type);
            }
            catch (PlanException e)
            {
                throw new PlanException(e.Id, $"Parameter '{p.Name}': {e.Message}");
            }

            Annotations.Apply(plan.Slots, p.GetAttributes(), DocComments.Param(doc, p.Name), plan);
            string local = "a" + index.ToString(CultureInfo.InvariantCulture);
            index++;
            if (p.HasExplicitDefaultValue)
            {
                string? json = plan.CanDefault ? DefaultJson(p) : null;
                if (json is not null)
                {
                    plan.Slots.Set("default", json);
                }

                binds.Append($"        var {local} = {B}Optional<{plan.TypeText}>(inputs, {Lit(p.Name)}, {plan.Bind}, {DefaultExpr(p)});\n");
            }
            else
            {
                required.Add(p.Name);
                binds.Append($"        var {local} = {B}Required<{plan.TypeText}>(inputs, {Lit(p.Name)}, {plan.Bind});\n");
            }

            if (names.Count > 0)
            {
                props.Append(',');
            }

            props.Append(Js.Quote(p.Name)).Append(':').Append(plan.Slots.Render());
            names.Add(p.Name);
            args.Add(local);
        }

        props.Append('}');
        var schema = new Js().Str("type", "object").Set("properties", props.ToString()).Set("additionalProperties", "false");
        if (required.Count > 0)
        {
            schema.Set("required", "[" + string.Join(",", required.Select(Js.Quote)) + "]");
        }

        ITypeSymbol returned = method.ReturnType;
        bool isAsync = false;
        if (returned is INamedTypeSymbol named)
        {
            string? full = named.OriginalDefinition.ToDisplayString();
            if (full is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            {
                isAsync = true;
                returned = compilation.GetSpecialType(SpecialType.System_Void);
            }
            else if (full is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                isAsync = true;
                returned = named.TypeArguments[0];
            }
        }

        if (returned.SpecialType == SpecialType.System_Void && !isAsync && method.IsAsync)
        {
            throw new PlanException("OLT003", "An async void method cannot be a tool; return Task.");
        }

        string endsLiteral = endsLoop ? "true" : "false";
        (string convert, string? outputSchema) = ConvertReturn(returned, endsLiteral, planner, method);

        string call = (method.IsStatic ? TypePlanner.Text(method.ContainingType) : "_target") + "." + method.Name + "(" + string.Join(", ", args) + ")";
        model.IsStaticMethod = method.IsStatic;
        model.ClassName = method.Name + "Tool";

        var sb = new StringBuilder();
        string cls = "@@CLASS@@";
        sb.Append($"    private sealed class {cls} : global::OneLlm.Tools.ITool\n    {{\n");
        if (!method.IsStatic)
        {
            sb.Append($"        private readonly {TypePlanner.Text(method.ContainingType)} _target;\n\n");
            sb.Append($"        public {cls}({TypePlanner.Text(method.ContainingType)} target) => _target = target;\n\n");
        }

        sb.Append("        private static readonly global::OneLlm.Tools.ToolSpec s_definition = new global::OneLlm.Tools.ToolSpec(")
            .Append(Lit(name)).Append(", ").Append(Lit(description)).Append(", ").Append(R).Append("Schema(").Append(Lit(schema.Render())).Append("))\n        {\n");
        if (outputSchema is not null)
        {
            sb.Append("            OutputSchema = ").Append(R).Append("Schema(").Append(Lit(outputSchema)).Append("),\n");
        }

        sb.Append($"            EndsLoop = {endsLiteral},\n            RequiresApproval = {(approval ? "true" : "false")},\n            ParallelSafe = {(parallel ? "true" : "false")},\n");
        if (timeout > 0)
        {
            sb.Append($"            Timeout = global::System.TimeSpan.FromSeconds({timeout.ToString(CultureInfo.InvariantCulture)}),\n");
        }

        sb.Append("        };\n\n        public global::OneLlm.Tools.ToolSpec Definition => s_definition;\n\n");
        sb.Append("        public ").Append(isAsync ? "async " : "").Append("global::System.Threading.Tasks.ValueTask<global::OneLlm.Tools.ToolResult> InvokeAsync(global::System.Text.Json.Nodes.JsonObject inputs, global::OneLlm.Tools.ToolContext context, global::System.Threading.CancellationToken cancellationToken)\n        {\n");
        sb.Append($"        {B}RejectUnknown(inputs{string.Concat(names.Select(n => ", " + Lit(n)))});\n");
        sb.Append(binds);
        bool isVoid = returned.SpecialType == SpecialType.System_Void;
        string callStatement = isAsync ? "await " + call + ".ConfigureAwait(false)" : call;
        if (isVoid)
        {
            sb.Append("        ").Append(callStatement).Append(";\n");
        }
        else
        {
            sb.Append("        var result = ").Append(callStatement).Append(";\n");
        }

        string final = convert.Replace("{0}", "result");
        sb.Append(isAsync ? $"        return {final};\n" : $"        return new global::System.Threading.Tasks.ValueTask<global::OneLlm.Tools.ToolResult>({final});\n");
        sb.Append("        }\n    }\n");
        model.Code = sb.ToString();
    }

    private static string? DescriptionOf(ISymbol s)
    {
        foreach (AttributeData a in s.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute" && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is string d && !string.IsNullOrWhiteSpace(d))
            {
                return d;
            }
        }

        return null;
    }

    private static (string Convert, string? OutputSchema) ConvertReturn(ITypeSymbol t, string ends, TypePlanner planner, IMethodSymbol method)
    {
        _ = method;
        if (t.SpecialType == SpecialType.System_Void)
        {
            return ($"{R}Void({ends})", null);
        }

        string full = t.ToDisplayString();
        if (full == "OneLlm.Tools.ToolResult")
        {
            return ("{0}", null);
        }

        if (t.SpecialType == SpecialType.System_String)
        {
            return ($"{R}Text({{0}}, {ends})", null);
        }

        if (DerivesFrom(t, "OneLlm.Messages.Part"))
        {
            return ($"{R}OnePart({{0}}, {ends})", null);
        }

        if (PartSequence(t))
        {
            return ($"{R}Parts({{0}}, {ends})", null);
        }

        if (DerivesFrom(t, "System.Text.Json.Nodes.JsonNode"))
        {
            return ($"{R}Json({{0}}, {ends})", null);
        }

        Plan plan;
        try
        {
            plan = planner.Analyze(t);
        }
        catch (PlanException e)
        {
            throw new PlanException(e.Id == "OLT002" ? "OLT003" : e.Id, $"Return type '{t.ToDisplayString()}': {e.Message}");
        }

        string toNode = plan.ToNode ?? $"{R}Serialize<{plan.TypeText}>({{0}}, {R}TypeInfo<{plan.TypeText}>({planner.FindContext(Unnullable(t)).Context}.Default))";
        return ($"{R}Json({toNode}, {ends})", plan.Slots.Render());
    }

    private static ITypeSymbol Unnullable(ITypeSymbol t) =>
        t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : t;

    private static bool DerivesFrom(ITypeSymbol t, string fullName)
    {
        for (ITypeSymbol? c = t; c is not null; c = c.BaseType)
        {
            if (c.ToDisplayString() == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool PartSequence(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol arr)
        {
            return DerivesFrom(arr.ElementType, "OneLlm.Messages.Part");
        }

        if (t is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } n)
        {
            string def = n.OriginalDefinition.ToDisplayString();
            return def is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IEnumerable<T>"
                or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.ICollection<T>"
                && DerivesFrom(n.TypeArguments[0], "OneLlm.Messages.Part");
        }

        return false;
    }

    private static string Lit(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    private static string DefaultExpr(IParameterSymbol p)
    {
        object? v = p.ExplicitDefaultValue;
        if (v is null)
        {
            return "default";
        }

        ITypeSymbol t = Unnullable(p.Type);
        string literal = v switch
        {
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            ulong u => u.ToString(CultureInfo.InvariantCulture) + "UL",
            uint ui => ui.ToString(CultureInfo.InvariantCulture) + "U",
            _ => SymbolDisplay.FormatPrimitive(v, quoteStrings: true, useHexadecimalNumbers: false),
        };
        return t.TypeKind == TypeKind.Enum ? $"({TypePlanner.Text(t)}){literal}" : literal;
    }

    private static string? DefaultJson(IParameterSymbol p)
    {
        object? v = p.ExplicitDefaultValue;
        if (v is null)
        {
            return null;
        }

        ITypeSymbol t = Unnullable(p.Type);
        if (t.TypeKind == TypeKind.Enum)
        {
            string wanted = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            foreach (IFieldSymbol f in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.HasConstantValue && Convert.ToString(f.ConstantValue, CultureInfo.InvariantCulture) == wanted)
                {
                    return Js.Quote(f.Name);
                }
            }

            return null;
        }

        return v switch
        {
            string s => Js.Quote(s),
            char c => Js.Quote(c.ToString()),
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IConvertible c => c.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<ToolMethodModel> all)
    {
        foreach (IGrouping<string, ToolMethodModel> group in all.GroupBy(m => m.TypeKey))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var good = new List<ToolMethodModel>();
            foreach (ToolMethodModel m in group)
            {
                if (m.Failure is { } failure)
                {
                    spc.ReportDiagnostic(failure.ToDiagnostic());
                    continue;
                }

                if (!used.Add(m.ToolName))
                {
                    spc.ReportDiagnostic(new DiagInfo("OLT007", $"Another tool in this class is already named '{m.ToolName}'.", m.Location).ToDiagnostic());
                    continue;
                }

                good.Add(m);
            }

            if (good.Count == 0)
            {
                continue;
            }

            ToolMethodModel first = good[0];
            var sb = new StringBuilder("// <auto-generated/>\n#nullable disable\n#pragma warning disable\n\n");
            bool hasNamespace = first.Namespace.Length > 0;
            if (hasNamespace)
            {
                sb.Append("namespace ").Append(first.Namespace).Append("\n{\n");
            }

            for (int i = 0; i < first.Headers.Length; i++)
            {
                bool innermost = i == first.Headers.Length - 1;
                bool addInterface = innermost && !first.IsStaticType && (first.HasParameterlessConstructor || good.All(m => m.IsStaticMethod));
                sb.Append(first.Headers[i]);
                if (addInterface)
                {
                    sb.Append(" : global::OneLlm.Tools.IToolSource<").Append(first.TypeKey).Append('>');
                }

                sb.Append("\n{\n");
            }

            bool needsInstance = good.Any(m => !m.IsStaticMethod);
            bool canNew = first.HasParameterlessConstructor;
            string selfType = first.TypeKey;
            sb.Append("    /// <summary>Makes one tool for each [Tool] method of this class.</summary>\n");
            if (first.IsStaticType)
            {
                sb.Append("    public static global::System.Collections.Generic.IReadOnlyList<global::OneLlm.Tools.ITool> GetTools()\n    {\n");
            }
            else if (!needsInstance || canNew)
            {
                sb.Append($"    public static global::System.Collections.Generic.IReadOnlyList<global::OneLlm.Tools.ITool> GetTools({selfType} instance = null)\n    {{\n");
                if (needsInstance)
                {
                    sb.Append($"        instance ??= new {selfType}();\n");
                }
            }
            else
            {
                sb.Append($"    public static global::System.Collections.Generic.IReadOnlyList<global::OneLlm.Tools.ITool> GetTools({selfType} instance)\n    {{\n");
            }

            sb.Append("        return new global::OneLlm.Tools.ITool[]\n        {\n");
            var classNames = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ToolMethodModel m in good)
            {
                seen.TryGetValue(m.ClassName, out int n);
                seen[m.ClassName] = n + 1;
                string cls = n == 0 ? m.ClassName : m.ClassName + n.ToString(CultureInfo.InvariantCulture);
                classNames.Add(cls);
                sb.Append("            new ").Append(cls).Append(m.IsStaticMethod ? "()" : "(instance)").Append(",\n");
            }

            sb.Append("        };\n    }\n\n");
            for (int i = 0; i < good.Count; i++)
            {
                string code = good[i].Code.Replace("@@CLASS@@", classNames[i]);
                sb.Append(code).Append('\n');
            }

            for (int i = 0; i < first.Headers.Length; i++)
            {
                sb.Append("}\n");
            }

            if (hasNamespace)
            {
                sb.Append("}\n");
            }

            string hint = new string(first.TypeKey.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".Tools.g.cs";
            spc.AddSource(hint, SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }
}
