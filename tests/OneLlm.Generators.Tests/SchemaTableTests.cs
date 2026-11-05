using System.Text.Json.Nodes;

namespace OneLlm.Generators.Tests;

public class SchemaTableTests
{
    private const string Int32 = """{"type":"integer","minimum":-2147483648,"maximum":2147483647}""";

    private static string Schema(string parameterType, string extraTypes = "")
    {
        var result = Harness.Run($$"""
            {{extraTypes}}
            public static partial class T { [Tool] public static string M({{parameterType}} x) => ""; }
            """);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompileProblems.Select(d => d.ToString()));
        return result.Tools("T").Single().Definition.InputSchema.ToJsonString();
    }

    private static string Expect(string fragment, bool required = true) =>
        JsonNode.Parse($$"""{"type":"object","properties":{"x":{{fragment}}},{{(required ? "\"required\":[\"x\"]," : "")}}"additionalProperties":false}""")!.ToJsonString();

    [Theory]
    [InlineData("bool", """{"type":"boolean"}""")]
    [InlineData("sbyte", """{"type":"integer","minimum":-128,"maximum":127}""")]
    [InlineData("byte", """{"type":"integer","minimum":0,"maximum":255}""")]
    [InlineData("short", """{"type":"integer","minimum":-32768,"maximum":32767}""")]
    [InlineData("ushort", """{"type":"integer","minimum":0,"maximum":65535}""")]
    [InlineData("int", Int32)]
    [InlineData("uint", """{"type":"integer","minimum":0,"maximum":4294967295}""")]
    [InlineData("long", """{"type":"integer","minimum":-9223372036854775808,"maximum":9223372036854775807}""")]
    [InlineData("ulong", """{"type":"integer","minimum":0,"maximum":18446744073709551615}""")]
    [InlineData("float", """{"type":"number"}""")]
    [InlineData("double", """{"type":"number"}""")]
    [InlineData("decimal", """{"type":"number"}""")]
    [InlineData("string", """{"type":"string"}""")]
    [InlineData("char", """{"type":"string","minLength":1,"maxLength":1}""")]
    [InlineData("Guid", """{"type":"string","format":"uuid"}""")]
    [InlineData("DateTime", """{"type":"string","format":"date-time"}""")]
    [InlineData("DateTimeOffset", """{"type":"string","format":"date-time"}""")]
    [InlineData("DateOnly", """{"type":"string","format":"date"}""")]
    [InlineData("TimeOnly", """{"type":"string","format":"time"}""")]
    [InlineData("TimeSpan", """{"type":"string","pattern":"^-?(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?$"}""")]
    [InlineData("Uri", """{"type":"string","format":"uri"}""")]
    [InlineData("byte[]", """{"type":"string","contentEncoding":"base64"}""")]
    [InlineData("int?", """{"type":["integer","null"],"minimum":-2147483648,"maximum":2147483647}""")]
    [InlineData("string?", """{"type":["string","null"]}""")]
    [InlineData("Guid?", """{"type":["string","null"],"format":"uuid"}""")]
    [InlineData("DayOfWeek", """{"type":"string","enum":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday"]}""")]
    [InlineData("DayOfWeek?", """{"type":["string","null"],"enum":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday",null]}""")]
    [InlineData("int[]", """{"type":"array","items":{"type":"integer","minimum":-2147483648,"maximum":2147483647}}""")]
    [InlineData("List<string?>", """{"type":"array","items":{"type":["string","null"]}}""")]
    [InlineData("IReadOnlyList<bool>", """{"type":"array","items":{"type":"boolean"}}""")]
    [InlineData("IEnumerable<double>", """{"type":"array","items":{"type":"number"}}""")]
    [InlineData("HashSet<string>", """{"type":"array","items":{"type":"string"},"uniqueItems":true}""")]
    [InlineData("ISet<int>", """{"type":"array","items":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"uniqueItems":true}""")]
    [InlineData("List<List<bool>>", """{"type":"array","items":{"type":"array","items":{"type":"boolean"}}}""")]
    [InlineData("Dictionary<string, int>", """{"type":"object","additionalProperties":{"type":"integer","minimum":-2147483648,"maximum":2147483647}}""")]
    [InlineData("IReadOnlyDictionary<string, string?>", """{"type":"object","additionalProperties":{"type":["string","null"]}}""")]
    [InlineData("JsonElement", "{}")]
    [InlineData("JsonNode", "{}")]
    [InlineData("JsonObject", """{"type":"object"}""")]
    [InlineData("JsonArray", """{"type":"array"}""")]
    public void Each_row_of_the_table_gives_its_documented_fragment(string type, string fragment)
    {
        Assert.Equal(Expect(fragment), Schema(type));
    }

    private const string Context = """
        public enum Size { Small, Large }
        public sealed record Address(string Street, string? Unit = null);
        public sealed record Line { public required string Sku { get; init; } public int Qty { get; init; } public Size Size { get; init; } [JsonPropertyName("unit_price")] public decimal UnitPrice { get; init; } [JsonIgnore] public string Skipped { get; init; } = ""; public List<Address> Where { get; init; } = new(); }
        [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
        [JsonSerializable(typeof(Address))] [JsonSerializable(typeof(Line))]
        public partial class Ctx : JsonSerializerContext { public Ctx() : base(null) { } public static Ctx Default { get; } = new(); protected override JsonSerializerOptions? GeneratedSerializerOptions => null; public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type) => null; }
        """;

    [Fact]
    public void Records_become_closed_objects_with_required_members_and_constructor_parameters()
    {
        string address = """{"type":"object","properties":{"street":{"type":"string"},"unit":{"type":["string","null"]}},"required":["street"],"additionalProperties":false}""";
        Assert.Equal(Expect(address), Schema("Address", Context));
        string line = """{"type":"object","properties":{"sku":{"type":"string"},"qty":""" + Int32
            + ""","size":{"type":"string","enum":["Small","Large"]},"unit_price":{"type":"number"},"where":{"type":"array","items":""" + address
            + """}},"required":["sku"],"additionalProperties":false}""";
        Assert.Equal(Expect(line), Schema("Line", Context));
    }

    [Fact]
    public void A_context_without_a_naming_policy_keeps_the_property_names_as_written()
    {
        string plain = Context.Replace("PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ", "");
        string schema = Schema("Address", plain);
        Assert.Contains("\"Street\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_parameters_are_not_required_and_carry_their_default()
    {
        var r = Harness.Run("""
            public static partial class T { [Tool] public static string M(int a, int b = 5, string c = "hi", bool d = true, double e = 1.5, decimal f = 2.50m, DayOfWeek g = DayOfWeek.Monday, string? h = null) => ""; }
            """);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        JsonObject schema = r.Tools("T").Single().Definition.InputSchema;
        Assert.Equal("""["a"]""", schema["required"]!.ToJsonString());
        JsonObject props = schema["properties"]!.AsObject();
        Assert.Equal(5, props["b"]!["default"]!.GetValue<int>());
        Assert.Equal("hi", props["c"]!["default"]!.GetValue<string>());
        Assert.True(props["d"]!["default"]!.GetValue<bool>());
        Assert.Equal("1.5", props["e"]!["default"]!.ToJsonString());
        Assert.Equal("2.50", props["f"]!["default"]!.ToJsonString());
        Assert.Equal("Monday", props["g"]!["default"]!.GetValue<string>());
        Assert.False(props["h"]!.AsObject().ContainsKey("default"));
    }

    [Fact]
    public void Annotations_and_descriptions_map_to_keywords()
    {
        string schema = Schema(
            "[Description(\"how many\")][Range(1, 10)] int",
            "");
        Assert.Contains("\"description\":\"how many\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"minimum\":1,\"maximum\":10", schema, StringComparison.Ordinal);

        var r = Harness.Run("""
            public static partial class T { [Tool] public static string M(
                [StringLength(20, MinimumLength = 2)] string s,
                [MinLength(1)][MaxLength(5)] List<int> l,
                [RegularExpression("^a+$")] string re,
                [EmailAddress] string em,
                [Url] string url,
                [Range(0.5, 2.5)] double r) => ""; }
            """);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        JsonObject props = r.Tools("T").Single().Definition.InputSchema["properties"]!.AsObject();
        Assert.Equal("""{"type":"string","minLength":2,"maxLength":20}""", props["s"]!.ToJsonString());
        Assert.Equal("""{"type":"array","items":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"minItems":1,"maxItems":5}""", props["l"]!.ToJsonString());
        Assert.Equal("^a+$", props["re"]!["pattern"]!.GetValue<string>());
        Assert.Equal("""{"type":"string","format":"email"}""", props["em"]!.ToJsonString());
        Assert.Equal("""{"type":"string","format":"uri"}""", props["url"]!.ToJsonString());
        Assert.Equal("""{"type":"number","minimum":0.5,"maximum":2.5}""", props["r"]!.ToJsonString());
    }

    [Fact]
    public void Injected_parameters_are_not_in_the_schema()
    {
        var r = Harness.Run("""
            public static partial class T { [Tool] public static Task<string> M(ToolContext ctx, string q, CancellationToken ct) => Task.FromResult(q); }
            """);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileProblems.Select(d => d.ToString()));
        Assert.Equal("""["q"]""", r.Tools("T").Single().Definition.InputSchema["required"]!.ToJsonString());
        Assert.Single(r.Tools("T").Single().Definition.InputSchema["properties"]!.AsObject());
    }
}
