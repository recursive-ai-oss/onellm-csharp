using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OneLlm.Tools;

// Builds the JSON Schema 2020-12 fragment for a C# type by reflection. The generator implements the same table at compile time
// (docs/tool-schemas.md); a parity test keeps the two identical, so keep their rules and key order in step.
[RequiresUnreferencedCode("Builds schemas by reflecting over types, whose members trimming may remove.")]
[RequiresDynamicCode("Reflection over generic types may need code that is not available ahead of time.")]
internal sealed class ReflectionSchema
{
    // Models read schemas top to bottom, so the order is fixed: what it is, how it is written, what it means, bounds, children.
    internal static readonly string[] KeyOrder =
    [
        "type", "format", "description", "enum", "minimum", "maximum", "minLength", "maxLength", "pattern", "contentEncoding",
        "items", "minItems", "maxItems", "uniqueItems", "properties", "required", "additionalProperties", "default",
    ];

    internal const string TimeSpanPattern = @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$";

    private readonly NullabilityInfoContext _nullability = new();
    private readonly List<Type> _stack = [];

    internal static string SnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
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

    internal static JsonObject Ordered(Dictionary<string, JsonNode?> slots)
    {
        var result = new JsonObject();
        foreach (string key in KeyOrder)
        {
            if (slots.TryGetValue(key, out JsonNode? v))
            {
                result[key] = v;
            }
        }

        return result;
    }

    /// <summary>The object schema for a parameter list. Parameters the runtime injects must already be filtered out.</summary>
    internal JsonObject ForParameters(IEnumerable<ParameterInfo> parameters)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (ParameterInfo p in parameters)
        {
            string name = p.Name ?? throw new NotSupportedException("A parameter has no name.");
            NullabilityInfo info = _nullability.Create(p);
            JsonObject schema = ForType(p.ParameterType, info, p.GetCustomAttributes(true));
            if (p.HasDefaultValue && DefaultNode(p.DefaultValue, p.ParameterType) is { } d)
            {
                schema["default"] = d;
            }

            props[name] = schema;
            if (!p.HasDefaultValue)
            {
                required.Add(name);
            }
        }

        var slots = new Dictionary<string, JsonNode?> { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Count > 0)
        {
            slots["required"] = required;
        }

        return Ordered(slots);
    }

    /// <summary>The schema for a value of <paramref name="type"/> with no member annotations.</summary>
    internal JsonObject ForValue(Type type, NullabilityInfo? info) => ForType(type, info, []);

    private JsonObject ForType(Type type, NullabilityInfo? info, object[] attributes)
    {
        Dictionary<string, JsonNode?> slots = Fragment(type, info);
        bool nullable = Nullable.GetUnderlyingType(type) is not null || (!type.IsValueType && info is { ReadState: NullabilityState.Nullable });
        ApplyAnnotations(slots, attributes);
        if (nullable)
        {
            MakeNullable(slots);
        }

        return Ordered(slots);
    }

    private static void MakeNullable(Dictionary<string, JsonNode?> slots)
    {
        if (!slots.TryGetValue("type", out JsonNode? t) || t is not JsonValue)
        {
            return;
        }

        slots["type"] = new JsonArray(t.GetValue<string>(), "null");
        if (slots.TryGetValue("enum", out JsonNode? e) && e is JsonArray values)
        {
            values.Add((JsonNode?)null);
        }
    }

    private static void ApplyAnnotations(Dictionary<string, JsonNode?> slots, object[] attributes)
    {
        foreach (object a in attributes)
        {
            switch (a)
            {
                case DescriptionAttribute d when !string.IsNullOrWhiteSpace(d.Description):
                    slots["description"] = d.Description;
                    break;
                case RangeAttribute r when r.Minimum is IConvertible && r.Maximum is IConvertible:
                    slots["minimum"] = NumberNode(r.Minimum);
                    slots["maximum"] = NumberNode(r.Maximum);
                    break;
                case StringLengthAttribute s:
                    slots["maxLength"] = s.MaximumLength;
                    if (s.MinimumLength > 0)
                    {
                        slots["minLength"] = s.MinimumLength;
                    }

                    break;
                case MinLengthAttribute m:
                    slots[IsArray(slots) ? "minItems" : "minLength"] = m.Length;
                    break;
                case MaxLengthAttribute m:
                    slots[IsArray(slots) ? "maxItems" : "maxLength"] = m.Length;
                    break;
                case LengthAttribute l:
                    slots[IsArray(slots) ? "minItems" : "minLength"] = l.MinimumLength;
                    slots[IsArray(slots) ? "maxItems" : "maxLength"] = l.MaximumLength;
                    break;
                case RegularExpressionAttribute re:
                    slots["pattern"] = re.Pattern;
                    break;
                case EmailAddressAttribute:
                    slots["format"] = "email";
                    break;
                case UrlAttribute:
                    slots["format"] = "uri";
                    break;
            }
        }
    }

    private static bool IsArray(Dictionary<string, JsonNode?> slots) => slots.TryGetValue("type", out JsonNode? t) && t is JsonValue v && v.TryGetValue(out string? s) && s == "array";

    private static JsonNode NumberNode(object value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        decimal m => m,
        float f => f,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException("Unsupported range bound."),
    };

    private static JsonNode? DefaultNode(object? value, Type type)
    {
        if (value is null || value == DBNull.Value || value == Missing.Value)
        {
            return null;
        }

        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return value switch
        {
            string s => s,
            bool b => b,
            sbyte v => (int)v,
            byte v => (int)v,
            short v => (int)v,
            ushort v => (int)v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => v,
            float v => v,
            double v => v,
            decimal v => v,
            char c => c.ToString(),
            _ when t.IsEnum => value.ToString(),
            _ => null,
        };
    }

    private Dictionary<string, JsonNode?> Fragment(Type type, NullabilityInfo? info)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        NullabilityInfo? inner = info is not null && Nullable.GetUnderlyingType(type) is not null && info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : info;

        if (Primitive(t) is { } prim)
        {
            return prim;
        }

        if (t.IsEnum)
        {
            var names = new JsonArray();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                names.Add(f.Name);
            }

            return new() { ["type"] = "string", ["enum"] = names };
        }

        if (t.IsArray)
        {
            if (t.GetArrayRank() != 1)
            {
                throw new NotSupportedException("Multi-dimensional arrays are not supported.");
            }

            return ArraySchema(t.GetElementType()!, inner?.ElementType, unique: false);
        }

        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition();
            Type[] args = t.GetGenericArguments();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
            {
                return ArraySchema(args[0], inner?.GenericTypeArguments.FirstOrDefault(), unique: false);
            }

            if (def == typeof(HashSet<>) || def == typeof(ISet<>) || def == typeof(IReadOnlySet<>))
            {
                return ArraySchema(args[0], inner?.GenericTypeArguments.FirstOrDefault(), unique: true);
            }

            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                if (args[0] != typeof(string))
                {
                    throw new NotSupportedException("Dictionary keys must be strings.");
                }

                NullabilityInfo? valueInfo = inner?.GenericTypeArguments.Length == 2 ? inner.GenericTypeArguments[1] : null;
                return new() { ["type"] = "object", ["additionalProperties"] = ForType(args[1], valueInfo, []) };
            }
        }

        return ComplexSchema(t);
    }

    private static Dictionary<string, JsonNode?>? Primitive(Type t)
    {
        if (t == typeof(bool))
        {
            return new() { ["type"] = "boolean" };
        }

        if (t == typeof(sbyte))
        {
            return Integer(sbyte.MinValue, sbyte.MaxValue);
        }

        if (t == typeof(byte))
        {
            return Integer(byte.MinValue, byte.MaxValue);
        }

        if (t == typeof(short))
        {
            return Integer(short.MinValue, short.MaxValue);
        }

        if (t == typeof(ushort))
        {
            return Integer(ushort.MinValue, ushort.MaxValue);
        }

        if (t == typeof(int))
        {
            return Integer(int.MinValue, int.MaxValue);
        }

        if (t == typeof(uint))
        {
            return Integer(uint.MinValue, uint.MaxValue);
        }

        if (t == typeof(long))
        {
            return Integer(long.MinValue, long.MaxValue);
        }

        if (t == typeof(ulong))
        {
            return new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = ulong.MaxValue };
        }

        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
        {
            return new() { ["type"] = "number" };
        }

        if (t == typeof(string))
        {
            return new() { ["type"] = "string" };
        }

        if (t == typeof(char))
        {
            return new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 };
        }

        if (t == typeof(Guid))
        {
            return new() { ["type"] = "string", ["format"] = "uuid" };
        }

        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
        {
            return new() { ["type"] = "string", ["format"] = "date-time" };
        }

        if (t == typeof(DateOnly))
        {
            return new() { ["type"] = "string", ["format"] = "date" };
        }

        if (t == typeof(TimeOnly))
        {
            return new() { ["type"] = "string", ["format"] = "time" };
        }

        if (t == typeof(TimeSpan))
        {
            return new() { ["type"] = "string", ["pattern"] = TimeSpanPattern };
        }

        if (t == typeof(Uri))
        {
            return new() { ["type"] = "string", ["format"] = "uri" };
        }

        if (t == typeof(byte[]))
        {
            return new() { ["type"] = "string", ["contentEncoding"] = "base64" };
        }

        if (t == typeof(JsonElement) || t == typeof(JsonNode))
        {
            return new();
        }

        if (t == typeof(JsonObject))
        {
            return new() { ["type"] = "object" };
        }

        if (t == typeof(JsonArray))
        {
            return new() { ["type"] = "array" };
        }

        return null;
    }

    private static Dictionary<string, JsonNode?> Integer(long min, long max) =>
        new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };

    private static Dictionary<string, JsonNode?> Integer(ulong min, ulong max) =>
        new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };

    private Dictionary<string, JsonNode?> ArraySchema(Type element, NullabilityInfo? elementInfo, bool unique)
    {
        var slots = new Dictionary<string, JsonNode?> { ["type"] = "array", ["items"] = ForType(element, elementInfo, []) };
        if (unique)
        {
            slots["uniqueItems"] = true;
        }

        return slots;
    }

    private Dictionary<string, JsonNode?> ComplexSchema(Type t)
    {
        if (t.IsInterface || t.IsAbstract || t.IsGenericTypeDefinition || t.IsPointer || t.IsByRef || t == typeof(object) || typeof(Delegate).IsAssignableFrom(t)
            || typeof(IEnumerable).IsAssignableFrom(t))
        {
            throw new NotSupportedException($"{t} is not a supported tool argument type.");
        }

        if (_stack.Contains(t))
        {
            throw new NotSupportedException($"{t} refers to itself; recursive types are not supported.");
        }

        _stack.Add(t);
        try
        {
            HashSet<string> ctorRequired = ConstructorRequired(t);
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (Type level in Hierarchy(t))
            {
                foreach (PropertyInfo p in level.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.GetMethod is not { IsPublic: true } || p.GetIndexParameters().Length != 0
                        || p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                    {
                        continue;
                    }

                    string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? CamelCase(p.Name);
                    props[name] = ForType(p.PropertyType, _nullability.Create(p), p.GetCustomAttributes(true));
                    if (p.GetCustomAttribute<RequiredMemberAttribute>() is not null || p.GetCustomAttribute<JsonRequiredAttribute>() is not null
                        || ctorRequired.Contains(p.Name))
                    {
                        required.Add(name);
                    }
                }
            }

            var slots = new Dictionary<string, JsonNode?> { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
            if (required.Count > 0)
            {
                slots["required"] = required;
            }

            return slots;
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
    }

    // Base-type properties come first, so the order reads from general to specific.
    private static List<Type> Hierarchy(Type t)
    {
        var chain = new List<Type>();
        for (Type? c = t; c is not null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
        {
            chain.Add(c);
        }

        chain.Reverse();
        return chain;
    }

    // A type built through its only public constructor (a positional record) requires the parameters that have no default.
    private static HashSet<string> ConstructorRequired(Type t)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ConstructorInfo[] ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (t.IsValueType || ctors.Length != 1 || ctors[0].GetParameters().Length == 0)
        {
            return result;
        }

        foreach (ParameterInfo p in ctors[0].GetParameters())
        {
            if (!p.HasDefaultValue && p.Name is not null)
            {
                result.Add(p.Name);
            }
        }

        return result;
    }
}
