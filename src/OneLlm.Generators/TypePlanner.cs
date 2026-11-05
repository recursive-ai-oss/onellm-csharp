using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OneLlm.Generators;

internal sealed class PlanException : Exception
{
    public PlanException(string id, string message)
        : base(message)
    {
        Id = id;
    }

    public string Id { get; }
}

// What the generator knows about one C# type once it has been mapped through the signature table (docs/tool-schemas.md).
internal sealed class Plan
{
    public Js Slots { get; set; } = new Js();

    // A C# expression of type Func<JsonNode?, string, T> that reads the type from a JSON value.
    public string Bind { get; set; } = "";

    // The type as written in generated code, without nullable-reference annotations.
    public string TypeText { get; set; } = "";

    // A format string with {0} for a value expression, giving a JsonNode?. Null when the type has no way to become JSON.
    public string? ToNode { get; set; }

    public bool Nullable { get; set; }

    public bool IsValueType { get; set; }

    // True when the schema is written with a "default" in mind: strings, numbers, booleans and enums.
    public bool CanDefault { get; set; }

    public ITypeSymbol? EnumType { get; set; }
}

internal sealed class TypePlanner
{
    private const string B = "global::OneLlm.Tools.ToolBinder.";
    private const string R = "global::OneLlm.Tools.ToolReturn.";

    private readonly Compilation _compilation;
    private readonly List<ITypeSymbol> _stack = new List<ITypeSymbol>();
    private int _depth;

    public TypePlanner(Compilation compilation)
    {
        _compilation = compilation;
    }

    public static string Text(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public Plan Analyze(ITypeSymbol type)
    {
        ITypeSymbol t = type;
        bool nullableValue = false;
        bool nullableRef = false;
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nt)
        {
            nullableValue = true;
            t = nt.TypeArguments[0];
        }
        else if (!t.IsValueType && t.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullableRef = true;
        }

        Plan plan = AnalyzeCore(t);
        if (nullableValue || nullableRef)
        {
            plan.Slots.MakeNullable();
            string inner = plan.Bind;
            string kind = nullableValue ? "NullableValue" : "NullableRef";
            plan.Bind = $"static (n{_depth}, p{_depth}) => {B}{kind}<{plan.TypeText}>(n{_depth}, p{_depth}, {inner})";
            if (nullableValue)
            {
                plan.TypeText = Text(type);
            }

            string? toNode = plan.ToNode;
            if (toNode is not null)
            {
                plan.ToNode = nullableValue ? "({0}.HasValue ? " + toNode.Replace("{0}", "{0}.Value") + " : null)" : "({0} is null ? null : " + toNode + ")";
            }

            plan.Nullable = true;
        }

        return plan;
    }

    private Plan AnalyzeCore(ITypeSymbol t)
    {
        string text = Text(t);
        Plan Simple(string kind, Js slots, string bind, string? toNode = null) => new Plan
        {
            Slots = slots,
            Bind = bind,
            TypeText = text,
            ToNode = toNode ?? R + "Value({0})",
            IsValueType = t.IsValueType,
            CanDefault = kind == "default",
        };

        Js Int(long min, long max) => new Js().Str("type", "integer").Int("minimum", min).Int("maximum", max);

        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean:
                return Simple("default", new Js().Str("type", "boolean"), B + "ReadBoolean");
            case SpecialType.System_SByte:
                return Simple("default", Int(sbyte.MinValue, sbyte.MaxValue), B + "ReadSByte");
            case SpecialType.System_Byte:
                return Simple("default", Int(byte.MinValue, byte.MaxValue), B + "ReadByte");
            case SpecialType.System_Int16:
                return Simple("default", Int(short.MinValue, short.MaxValue), B + "ReadInt16");
            case SpecialType.System_UInt16:
                return Simple("default", Int(ushort.MinValue, ushort.MaxValue), B + "ReadUInt16");
            case SpecialType.System_Int32:
                return Simple("default", Int(int.MinValue, int.MaxValue), B + "ReadInt32");
            case SpecialType.System_UInt32:
                return Simple("default", Int(uint.MinValue, uint.MaxValue), B + "ReadUInt32");
            case SpecialType.System_Int64:
                return Simple("default", Int(long.MinValue, long.MaxValue), B + "ReadInt64");
            case SpecialType.System_UInt64:
                return Simple("default", new Js().Str("type", "integer").Int("minimum", 0).Set("maximum", ulong.MaxValue.ToString(CultureInfo.InvariantCulture)), B + "ReadUInt64");
            case SpecialType.System_Single:
                return Simple("default", new Js().Str("type", "number"), B + "ReadSingle");
            case SpecialType.System_Double:
                return Simple("default", new Js().Str("type", "number"), B + "ReadDouble");
            case SpecialType.System_Decimal:
                return Simple("default", new Js().Str("type", "number"), B + "ReadDecimal");
            case SpecialType.System_String:
                return Simple("default", new Js().Str("type", "string"), B + "ReadString", "global::System.Text.Json.Nodes.JsonValue.Create({0})");
            case SpecialType.System_Char:
                return Simple("default", new Js().Str("type", "string").Int("minLength", 1).Int("maxLength", 1), B + "ReadChar");
            case SpecialType.System_DateTime:
                return Simple("", new Js().Str("type", "string").Str("format", "date-time"), B + "ReadDateTime");
            case SpecialType.System_Object:
                throw new PlanException("OLT002", "'object' has no schema; use JsonElement or JsonNode for any JSON value, or a record for a structured one.");
        }

        if (t.TypeKind == TypeKind.Enum)
        {
            return EnumPlan(t, text);
        }

        if (t is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                throw new PlanException("OLT002", "Multi-dimensional arrays are not supported; use an array of arrays or a list.");
            }

            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return Simple("", new Js().Str("type", "string").Str("contentEncoding", "base64"), B + "ReadBytes");
            }

            Plan e = Element(array.ElementType);
            return Collection(text, e, "List", false, ".ToArray()");
        }

        string? named = t is INamedTypeSymbol n ? FullName(n.OriginalDefinition) : FullName(t);
        switch (named)
        {
            case "System.Guid":
                return Simple("", new Js().Str("type", "string").Str("format", "uuid"), B + "ReadGuid");
            case "System.DateTimeOffset":
                return Simple("", new Js().Str("type", "string").Str("format", "date-time"), B + "ReadDateTimeOffset");
            case "System.DateOnly":
                return Simple("", new Js().Str("type", "string").Str("format", "date"), B + "ReadDateOnly");
            case "System.TimeOnly":
                return Simple("", new Js().Str("type", "string").Str("format", "time"), B + "ReadTimeOnly");
            case "System.TimeSpan":
                return Simple("", new Js().Str("type", "string").Str("pattern", @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$"), B + "ReadTimeSpan");
            case "System.Uri":
                return Simple("", new Js().Str("type", "string").Str("format", "uri"), B + "ReadUri");
            case "System.Text.Json.JsonElement":
                return Simple("", new Js(), B + "ReadElement", "System.Text.Json.Nodes.JsonNode.Parse({0}.GetRawText())");
            case "System.Text.Json.Nodes.JsonNode":
                return Simple("", new Js(), B + "ReadNode", "{0}");
            case "System.Text.Json.Nodes.JsonObject":
                return Simple("", new Js().Str("type", "object"), B + "ReadObject", "{0}");
            case "System.Text.Json.Nodes.JsonArray":
                return Simple("", new Js().Str("type", "array"), B + "ReadArray", "{0}");
            case "System.Collections.Generic.List<T>":
            case "System.Collections.Generic.IList<T>":
            case "System.Collections.Generic.ICollection<T>":
            case "System.Collections.Generic.IEnumerable<T>":
            case "System.Collections.Generic.IReadOnlyList<T>":
            case "System.Collections.Generic.IReadOnlyCollection<T>":
                return Collection(text, Element(((INamedTypeSymbol)t).TypeArguments[0]), "List", false, "");
            case "System.Collections.Generic.HashSet<T>":
            case "System.Collections.Generic.ISet<T>":
            case "System.Collections.Generic.IReadOnlySet<T>":
                return Collection(text, Element(((INamedTypeSymbol)t).TypeArguments[0]), "Set", true, "");
            case "System.Collections.Generic.Dictionary<TKey, TValue>":
            case "System.Collections.Generic.IDictionary<TKey, TValue>":
            case "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>":
                return Map(text, (INamedTypeSymbol)t);
        }

        return Complex(t, text);
    }

    private static string? FullName(ITypeSymbol t)
    {
        string ns = t.ContainingNamespace is { IsGlobalNamespace: false } c ? c.ToDisplayString() + "." : "";
        if (t is INamedTypeSymbol { IsGenericType: true } g)
        {
            return ns + t.Name + "<" + string.Join(", ", g.TypeParameters.Select(p => p.Name)) + ">";
        }

        return ns + t.Name;
    }

    private Plan Element(ITypeSymbol element)
    {
        _depth++;
        try
        {
            return Analyze(element);
        }
        finally
        {
            _depth--;
        }
    }

    private Plan Collection(string text, Plan e, string binder, bool unique, string suffix)
    {
        var slots = new Js().Str("type", "array").Set("items", e.Slots.Render());
        if (unique)
        {
            slots.Set("uniqueItems", "true");
        }

        int d = _depth;
        return new Plan
        {
            Slots = slots,
            Bind = $"static (n{d}, p{d}) => {B}{binder}<{e.TypeText}>(n{d}, p{d}, {e.Bind}){suffix}",
            TypeText = text,
            ToNode = null,
        };
    }

    private Plan Map(string text, INamedTypeSymbol t)
    {
        if (t.TypeArguments[0].SpecialType != SpecialType.System_String)
        {
            throw new PlanException("OLT002", "Dictionary keys must be strings: JSON objects have only string keys.");
        }

        Plan v = Element(t.TypeArguments[1]);
        int d = _depth;
        return new Plan
        {
            Slots = new Js().Str("type", "object").Set("additionalProperties", v.Slots.Render()),
            Bind = $"static (n{d}, p{d}) => {B}Map<{v.TypeText}>(n{d}, p{d}, {v.Bind})",
            TypeText = text,
            ToNode = null,
        };
    }

    private Plan EnumPlan(ITypeSymbol t, string text)
    {
        var names = new List<string>();
        foreach (IFieldSymbol f in t.GetMembers().OfType<IFieldSymbol>())
        {
            if (f.HasConstantValue && f.DeclaredAccessibility == Accessibility.Public)
            {
                names.Add(f.Name);
            }
        }

        string list = "[" + string.Join(",", names.Select(Js.Quote)) + "]";
        string namesArray = "new string[] { " + string.Join(", ", names.Select(n => "\"" + n + "\"")) + " }";
        string valuesArray = $"new {text}[] {{ " + string.Join(", ", names.Select(n => text + "." + n)) + " }";
        int d = _depth;
        return new Plan
        {
            Slots = new Js().Str("type", "string").Set("enum", list),
            Bind = $"static (n{d}, p{d}) => {B}ReadEnum<{text}>(n{d}, p{d}, {namesArray}, {valuesArray})",
            TypeText = text,
            ToNode = R + "Value({0})",
            IsValueType = true,
            CanDefault = true,
            EnumType = t,
        };
    }

    private Plan Complex(ITypeSymbol t, string text)
    {
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Struct) || t.IsAbstract || t is INamedTypeSymbol { IsUnboundGenericType: true })
        {
            throw new PlanException("OLT002", $"'{t.ToDisplayString()}' has no schema in the signature table.");
        }

        if (t.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable) || t.TypeKind == TypeKind.Delegate)
        {
            throw new PlanException("OLT002", $"'{t.ToDisplayString()}' is a collection type the signature table does not list; use an array, List<T>, HashSet<T> or Dictionary<string, T>.");
        }

        if (_stack.Any(s => SymbolEqualityComparer.Default.Equals(s, t)))
        {
            throw new PlanException("OLT006", $"'{t.ToDisplayString()}' refers to itself; recursive types are not supported.");
        }

        (string ctx, bool camel) = FindContext(t);
        _stack.Add(t);
        try
        {
            var props = new StringBuilder("{");
            var required = new List<string>();
            HashSet<string> ctorRequired = ConstructorRequired(t);
            bool first = true;
            foreach (ITypeSymbol level in Hierarchy(t))
            {
                foreach (IPropertySymbol p in level.GetMembers().OfType<IPropertySymbol>())
                {
                    if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public || p.GetMethod is not { DeclaredAccessibility: Accessibility.Public }
                        || IsIgnored(p))
                    {
                        continue;
                    }

                    string name = JsonName(p) ?? (camel ? CamelCase(p.Name) : p.Name);
                    Plan pp = Element(p.Type);
                    Annotations.Apply(pp.Slots, p.GetAttributes(), PropertyDoc(p), pp);
                    if (!first)
                    {
                        props.Append(',');
                    }

                    first = false;
                    props.Append(Js.Quote(name)).Append(':').Append(pp.Slots.Render());
                    if (p.IsRequired || HasAttribute(p, "System.Text.Json.Serialization.JsonRequiredAttribute") || ctorRequired.Contains(p.Name))
                    {
                        required.Add(name);
                    }
                }
            }

            props.Append('}');
            var slots = new Js().Str("type", "object").Set("properties", props.ToString()).Set("additionalProperties", "false");
            if (required.Count > 0)
            {
                slots.Set("required", "[" + string.Join(",", required.Select(Js.Quote)) + "]");
            }

            int d = _depth;
            return new Plan
            {
                Slots = slots,
                Bind = $"static (n{d}, p{d}) => {B}Complex<{text}>(n{d}, p{d}, {R}TypeInfo<{text}>({ctx}.Default))",
                TypeText = text,
                ToNode = $"{R}Serialize<{text}>({{0}}, {R}TypeInfo<{text}>({ctx}.Default))",
                IsValueType = t.IsValueType,
            };
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
    }

    // A type the schema cannot describe by itself becomes JSON through the user's serializer context; one that lists it is required.
    public (string Context, bool Camel) FindContext(ITypeSymbol t)
    {
        INamedTypeSymbol? serializable = _compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializableAttribute");
        INamedTypeSymbol? contextBase = _compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
        if (serializable is null || contextBase is null)
        {
            throw new PlanException("OLT005", $"'{t.ToDisplayString()}' needs a System.Text.Json source-generated JsonSerializerContext, but System.Text.Json is not referenced.");
        }

        foreach (INamedTypeSymbol candidate in AllTypes(_compilation.Assembly.GlobalNamespace))
        {
            if (candidate.IsAbstract || !Derives(candidate, contextBase))
            {
                continue;
            }

            foreach (AttributeData a in candidate.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, serializable) && a.ConstructorArguments.Length > 0
                    && a.ConstructorArguments[0].Value is ITypeSymbol listed && SymbolEqualityComparer.Default.Equals(listed, t))
                {
                    return (Text(candidate), NamingIsCamel(candidate));
                }
            }
        }

        throw new PlanException("OLT005", $"'{t.ToDisplayString()}' needs a JsonSerializerContext that lists it: add [JsonSerializable(typeof({t.ToDisplayString()}))] to a partial class that derives from JsonSerializerContext.");
    }

    private static bool NamingIsCamel(INamedTypeSymbol context)
    {
        foreach (AttributeData a in context.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() != "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
            {
                continue;
            }

            // JsonSerializerDefaults.Web is 1 and means camelCase; JsonKnownNamingPolicy.CamelCase is 1; Unspecified is 0.
            bool camel = a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is int d && d == 1;
            foreach (KeyValuePair<string, TypedConstant> named in a.NamedArguments)
            {
                if (named.Key == "PropertyNamingPolicy" && named.Value.Value is int policy)
                {
                    if (policy is not (0 or 1))
                    {
                        throw new PlanException("OLT011", $"{context.Name} uses a property naming policy the tool schema does not support; use CamelCase or none.");
                    }

                    camel = policy == 1;
                }
            }

            return camel;
        }

        return false;
    }

    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (INamedTypeSymbol nested in Nested(type))
            {
                yield return nested;
            }
        }

        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in AllTypes(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol n in type.GetTypeMembers())
        {
            yield return n;
            foreach (INamedTypeSymbol deeper in Nested(n))
            {
                yield return deeper;
            }
        }
    }

    private static List<ITypeSymbol> Hierarchy(ITypeSymbol t)
    {
        var chain = new List<ITypeSymbol>();
        for (ITypeSymbol? c = t; c is not null && c.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); c = c.BaseType)
        {
            chain.Add(c);
        }

        chain.Reverse();
        return chain;
    }

    private static HashSet<string> ConstructorRequired(ITypeSymbol t)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (t.IsValueType || t is not INamedTypeSymbol named)
        {
            return result;
        }

        List<IMethodSymbol> ctors = named.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        if (ctors.Count != 1 || ctors[0].Parameters.Length == 0)
        {
            return result;
        }

        foreach (IParameterSymbol p in ctors[0].Parameters)
        {
            if (!p.HasExplicitDefaultValue)
            {
                result.Add(p.Name);
            }
        }

        return result;
    }

    private static bool IsIgnored(IPropertySymbol p)
    {
        foreach (AttributeData a in p.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute")
            {
                // JsonIgnoreCondition.Always is 1 and the default when no condition is given.
                bool conditioned = a.NamedArguments.Any(na => na.Key == "Condition" && na.Value.Value is int c && c != 1);
                if (!conditioned)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAttribute(ISymbol s, string fullName) => s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static string? JsonName(IPropertySymbol p)
    {
        foreach (AttributeData a in p.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute" && a.ConstructorArguments.Length == 1)
            {
                return a.ConstructorArguments[0].Value as string;
            }
        }

        return null;
    }

    private static string? PropertyDoc(IPropertySymbol p)
    {
        foreach (SyntaxReference reference in p.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is PropertyDeclarationSyntax declaration)
            {
                string? summary = DocComments.Summary(DocComments.Read(declaration));
                if (summary is not null)
                {
                    return summary;
                }
            }
        }

        // A positional record property is documented by the <param> of the record itself.
        foreach (SyntaxReference reference in p.ContainingType.DeclaringSyntaxReferences)
        {
            string? fromParam = DocComments.Param(DocComments.Read(reference.GetSyntax()), p.Name);
            if (fromParam is not null)
            {
                return fromParam;
            }
        }

        return null;
    }

    internal static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }

        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1]))
            {
                break;
            }

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    internal static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLowerOrDigit || acronymEnd)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

internal static class Annotations
{
    // Applies [Description] (or the doc comment), data annotations and the like. Runs before the type is made nullable, as the reflection path does.
    public static void Apply(Js slots, ImmutableArray<AttributeData> attributes, string? doc, Plan plan)
    {
        string? description = doc;
        foreach (AttributeData a in attributes)
        {
            string? name = a.AttributeClass?.ToDisplayString();
            switch (name)
            {
                case "System.ComponentModel.DescriptionAttribute":
                    if (a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string d && !string.IsNullOrWhiteSpace(d))
                    {
                        description = d;
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.RangeAttribute":
                    if (a.ConstructorArguments.Length == 2 && a.ConstructorArguments[0].Value is IConvertible lo && a.ConstructorArguments[1].Value is IConvertible hi
                        && a.ConstructorArguments[0].Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Double)
                    {
                        slots.Set("minimum", Number(lo));
                        slots.Set("maximum", Number(hi));
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.StringLengthAttribute":
                    if (a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int max)
                    {
                        slots.Int("maxLength", max);
                        foreach (KeyValuePair<string, TypedConstant> na in a.NamedArguments)
                        {
                            if (na.Key == "MinimumLength" && na.Value.Value is int min && min > 0)
                            {
                                slots.Int("minLength", min);
                            }
                        }
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.MinLengthAttribute":
                    if (a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int minLen)
                    {
                        slots.Int(IsArray(slots) ? "minItems" : "minLength", minLen);
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.MaxLengthAttribute":
                    if (a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int maxLen)
                    {
                        slots.Int(IsArray(slots) ? "maxItems" : "maxLength", maxLen);
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.LengthAttribute":
                    if (a.ConstructorArguments.Length == 2 && a.ConstructorArguments[0].Value is int l0 && a.ConstructorArguments[1].Value is int l1)
                    {
                        slots.Int(IsArray(slots) ? "minItems" : "minLength", l0);
                        slots.Int(IsArray(slots) ? "maxItems" : "maxLength", l1);
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.RegularExpressionAttribute":
                    if (a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string pattern)
                    {
                        slots.Str("pattern", pattern);
                    }

                    break;
                case "System.ComponentModel.DataAnnotations.EmailAddressAttribute":
                    slots.Str("format", "email");
                    break;
                case "System.ComponentModel.DataAnnotations.UrlAttribute":
                    slots.Str("format", "uri");
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            slots.Str("description", description!);
        }

        _ = plan;
    }

    // Nullable is applied by TypePlanner.Analyze before the annotations, so the array type may already read ["array","null"].
    private static bool IsArray(Js slots)
    {
        string? type = slots.Get("type");
        return type is "\"array\"" or "[\"array\",\"null\"]";
    }

    private static string Number(IConvertible value) =>
        value is int i ? i.ToString(CultureInfo.InvariantCulture) : value.ToDouble(CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
}
