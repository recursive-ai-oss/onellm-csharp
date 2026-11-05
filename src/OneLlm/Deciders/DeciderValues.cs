using System.Text.Json;

namespace OneLlm.Deciders;

/// <summary>Reads JSON into the plain values a decider's templates see. Numbers are always doubles, as in the format's reference behaviour, so printing a number gives the same text.</summary>
internal static class DeciderValues
{
    public static object? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    map[p.Name] = FromElement(p.Value);
                }

                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }

                return list;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                return e.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}
