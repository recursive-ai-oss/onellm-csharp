using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

/// <summary>Maps Gemini's <c>finishReason</c> words (https://ai.google.dev/api/generate-content, "Candidate") to the neutral reasons.</summary>
internal static class GeminiFinish
{
    public static FinishReason Map(string? word) => word switch
    {
        null or "" or "FINISH_REASON_UNSPECIFIED" => FinishReason.Unknown,
        "STOP" => FinishReason.Stop,
        "MAX_TOKENS" => FinishReason.Length,
        // Blocked by a filter of some kind.
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT"
            or "IMAGE_RECITATION" or "PUP_LIMITED_DISABLED" or "ESCALATION" => FinishReason.ContentFilter,
        // The answer was cut at the per-request limit and can be continued with the returned continuation token.
        "CONTINUATION" => FinishReason.PauseTurn,
        // LANGUAGE, OTHER, MALFORMED_FUNCTION_CALL, UNEXPECTED_TOOL_CALL, TOO_MANY_TOOL_CALLS, MISSING_THOUGHT_SIGNATURE, MALFORMED_RESPONSE, NO_IMAGE, IMAGE_OTHER and new words.
        _ => FinishReason.Other,
    };
}

/// <summary>Turns parsed JSON into the plain values extension data holds: string, bool, long, double, list and dictionary.</summary>
internal static class PlainJson
{
    public static object? From(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                var d = new Dictionary<string, object?>(o.Count, StringComparer.Ordinal);
                foreach ((string k, JsonNode? v) in o)
                {
                    d[k] = From(v);
                }

                return d;
            case JsonArray a:
                var l = new List<object?>(a.Count);
                foreach (JsonNode? item in a)
                {
                    l.Add(From(item));
                }

                return l;
            default:
                JsonElement e = node.GetValue<JsonElement>();
                return e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => e.TryGetInt64(out long n) ? n : e.GetDouble(),
                    _ => null,
                };
        }
    }
}
