using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OneLlm.Tools;

namespace OneLlm.Tests.Tools;

public enum Color
{
    Red,
    Green,
    Blue,
}

public sealed record Address(string Street, string? Unit = null);

public sealed record Item
{
    public required string Sku { get; init; }

    public int Qty { get; init; }

    [Description("Free text for the packer.")]
    public string? Note { get; init; }

    public Color Color { get; init; }

    public List<string> Tags { get; init; } = [];

    public Dictionary<string, int> Counts { get; init; } = [];

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; init; }

    [JsonIgnore]
    public string Hidden { get; init; } = "";
}

public sealed record Verdict(string Decision, int Score);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(List<Item>))]
[JsonSerializable(typeof(Verdict))]
internal sealed partial class CaseJson : JsonSerializerContext;

/// <summary>One tool method per row of the signature table. The same methods are given to Tool.Create in the parity test.</summary>
public static partial class SchemaCases
{
    [Tool, Description("Every primitive.")]
    public static string Primitives(bool flag, sbyte a, byte b, short c, ushort d, int e, uint f, long g, ulong h, float i, double j, decimal k, char l, string m)
        => "ok";

    [Tool]
    public static string Formats(Guid id, DateTime at, DateTimeOffset off, DateOnly day, TimeOnly time, TimeSpan span, Uri link, byte[] data) => "ok";

    [Tool]
    public static string Enums(Color color, Color? maybe, Color other = Color.Green) => "ok";

    [Tool]
    public static string Nullables(int? n, string? s, Guid? g, List<int>? l, Item? item) => "ok";

    [Tool]
    public static string Collections(
        int[] arr,
        List<string> list,
        IReadOnlyList<double> ro,
        HashSet<int> set,
        IEnumerable<bool> en,
        Dictionary<string, int> map,
        IReadOnlyDictionary<string, string> ro2,
        List<List<int>> nested)
        => "ok";

    [Tool]
    public static string Defaults(int n = 5, string s = "hi", bool b = true, double d = 1.5, decimal m = 2.50m, char ch = 'x', string? none = null, long? big = 7)
        => "ok";

    [Tool]
    public static string Complex(Item item, Address addr, List<Item> items) => "ok";

    [Tool]
    public static string Any(JsonElement e, JsonNode n, JsonObject o, JsonArray a) => "ok";

    [Tool]
    public static string Annotated(
        [Description("count")][Range(1, 10)] int n,
        [StringLength(20, MinimumLength = 2)] string s,
        [MinLength(1)][MaxLength(5)] List<int> l,
        [RegularExpression("^a+$")] string re,
        [EmailAddress] string em,
        [Url] string url,
        [Range(0.5, 2.5)] double r)
        => "ok";

    [Tool]
    public static async Task<string> Injected(string x, ToolContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        return x + ctx.ToolCallId;
    }

    [Tool]
    public static void NoArguments()
    {
    }
}
