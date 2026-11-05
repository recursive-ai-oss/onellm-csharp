using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneYaml;
using RecursiveAI.OneEcma;

namespace OneLlm.JavaScript;

/// <summary>Moves plain data between System.Text.Json nodes, OneEcma values and YAML nodes. Only data crosses: no object identity, no functions.</summary>
internal static class JsonBridge
{
    private const int MaxDepth = 64;

    /// <summary>Builds the script value for a JSON node. Keys such as <c>__proto__</c> become ordinary own properties.</summary>
    public static JsValue ToJs(JsRealm realm, JsonNode? node, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException("The JSON is nested too deeply.");
        }

        switch (node)
        {
            case null:
                return JsValue.Null;
            case JsonObject o:
                JsObject obj = realm.NewObject();
                foreach (KeyValuePair<string, JsonNode?> kv in o)
                {
                    realm.DefineData(obj, kv.Key, ToJs(realm, kv.Value, depth + 1));
                }

                return JsValue.FromObject(obj);
            case JsonArray a:
                var items = new List<JsValue>(a.Count);
                foreach (JsonNode? item in a)
                {
                    items.Add(ToJs(realm, item, depth + 1));
                }

                return JsValue.FromObject(realm.NewArray(items));
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        return JsValue.FromString(v.GetValue<string>());
                    case JsonValueKind.True:
                        return JsValue.FromBoolean(true);
                    case JsonValueKind.False:
                        return JsValue.FromBoolean(false);
                    case JsonValueKind.Number:
                        return JsValue.FromNumber(v.TryGetValue(out double d) ? d : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture));
                    default:
                        return JsValue.Null;
                }

            default:
                return JsValue.Null;
        }
    }

    /// <summary>Turns a script value into JSON with the engine's own <c>JSON.stringify</c> (so <c>toJSON</c> and the usual rules apply).</summary>
    /// <returns>The JSON text, or null when the value is <c>undefined</c> (or a function or symbol).</returns>
    public static string? Stringify(JsRealm realm, JsValue stringify, JsValue value, JsValue replacer = default)
    {
        JsValue text = replacer.IsUndefined ? realm.Call(stringify, JsValue.Undefined, value) : realm.Call(stringify, JsValue.Undefined, value, replacer);
        return text.IsString ? text.AsString : null;
    }

    /// <summary>A replacer for <see cref="Stringify"/> that writes <c>undefined</c> properties as JSON <c>null</c> instead of leaving them out, the form decider logic results use.</summary>
    public static JsValue NullForUndefined(JsRealm realm) =>
        JsValue.FromObject(realm.NewFunction("replacer", 2, (_, args) => args.Length > 1 && !args[1].IsUndefined ? args[1] : JsValue.Null));

    /// <summary>Reads a YAML node as JSON. Scalars keep their YAML type: numbers, booleans and null are not turned into strings.</summary>
    public static JsonNode? FromYaml(YamlNode node, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException("The YAML is nested too deeply.");
        }

        switch (node.Kind)
        {
            case YamlNodeKind.Mapping:
                var o = new JsonObject();
                foreach (KeyValuePair<YamlNode, YamlNode> e in node.Entries)
                {
                    o[e.Key.GetValue<string>()] = FromYaml(e.Value, depth + 1);
                }

                return o;
            case YamlNodeKind.Sequence:
                var a = new JsonArray();
                foreach (YamlNode item in node.Items)
                {
                    a.Add(FromYaml(item, depth + 1));
                }

                return a;
            case YamlNodeKind.Alias:
                return FromYaml(node.Target, depth + 1);
            default:
                if (node.IsNull)
                {
                    return null;
                }

                object? value = node.GetValue<object>();
                return value switch
                {
                    bool b => JsonValue.Create(b),
                    long l => JsonValue.Create(l),
                    int i => JsonValue.Create(i),
                    double d => JsonValue.Create(d),
                    decimal m => JsonValue.Create(m),
                    string s => JsonValue.Create(s),
                    _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
                };
        }
    }
}
