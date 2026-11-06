using System;

/// <summary>
/// Canned answers for the endpoints the live-smoke console and the example-agent runner use, so both can be rehearsed against this server
/// with no key. A request that asks for a tool gets a tool call; a request that carries a tool result gets text; a request that asks for
/// a JSON schema gets JSON. Shapes follow the vendors' published API references.
/// </summary>
internal static class SmokeResponses
{
    /// <summary>Returns the JSON answer for a non-streamed request, or null when this class does not handle the path.</summary>
    public static string? Respond(string path, string body)
    {
        if (path.EndsWith("/responses/input_tokens", StringComparison.Ordinal))
        {
            return """{"object":"response.input_tokens","input_tokens":12}""";
        }

        if (path.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            return """{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],"model":"mock-embedding","usage":{"prompt_tokens":5,"total_tokens":5}}""";
        }

        if (path.EndsWith("/decisions", StringComparison.Ordinal))
        {
            return """{"id":"decision_mock","object":"decision","model":"mock-model","answers":[{"type":"choice","choice":"billing","confidence":0.9,"probabilities":[{"value":"billing","probability":0.9},{"value":"orders","probability":0.1}]}],"usage":{"input_tokens":50,"output_tokens":0,"total_tokens":50}}""";
        }

        if (path.EndsWith(":countTokens", StringComparison.Ordinal))
        {
            return """{"totalTokens":12}""";
        }

        if (path.EndsWith(":embedContent", StringComparison.Ordinal))
        {
            return """{"embedding":{"values":[0.1,0.2,0.3]},"usageMetadata":{"promptTokenCount":5}}""";
        }

        if (path.EndsWith("/v1/responses", StringComparison.Ordinal))
        {
            string output;
            if (body.Contains("function_call_output", StringComparison.Ordinal))
            {
                output = Message("The weather in Paris is 18 C.");
            }
            else if (body.Contains("get_weather", StringComparison.Ordinal))
            {
                output = """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Paris\"}","status":"completed"}""";
            }
            else if (body.Contains("json_schema", StringComparison.Ordinal))
            {
                output = Message("{\\\"city\\\":\\\"Paris\\\",\\\"country\\\":\\\"France\\\"}");
            }
            else
            {
                return null;
            }

            return "{\"id\":\"resp_mock\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\",\"model\":\"mock-model\",\"output\":[" + output + "],\"usage\":{\"input_tokens\":30,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":20,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":50}}";
        }

        if (path.EndsWith(":generateContent", StringComparison.Ordinal))
        {
            string part;
            if (body.Contains("functionResponse", StringComparison.Ordinal))
            {
                part = """{"text":"The weather in Paris is 18 C."}""";
            }
            else if (body.Contains("functionDeclarations", StringComparison.Ordinal))
            {
                part = """{"functionCall":{"name":"get_weather","args":{"city":"Paris"}}}""";
            }
            else if (body.Contains("responseJsonSchema", StringComparison.Ordinal) || body.Contains("responseSchema", StringComparison.Ordinal))
            {
                part = "{\"text\":\"{\\\"city\\\":\\\"Paris\\\",\\\"country\\\":\\\"France\\\"}\"}";
            }
            else
            {
                return null;
            }

            return "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]},\"finishReason\":\"STOP\",\"index\":0}],\"usageMetadata\":{\"promptTokenCount\":30,\"candidatesTokenCount\":20,\"totalTokenCount\":50},\"modelVersion\":\"mock-model\"}";
        }

        return null;
    }

    private static string Message(string escapedText) =>
        "{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + escapedText + "\",\"annotations\":[]}]}";
}
