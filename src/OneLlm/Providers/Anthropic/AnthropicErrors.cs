using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Http;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// Turns Anthropic error responses into the typed errors. Error bodies have the shape
/// <c>{"type":"error","error":{"type":"...","message":"..."},"request_id":"..."}</c> (API reference, Errors); the same object arrives
/// as a stream event and as a batch result, so one mapper serves all three.
/// </summary>
internal static class AnthropicErrors
{
    public const string Provider = "anthropic";

    /// <summary>Maps a failed HTTP response. Used as the <see cref="ProviderErrorMapper"/> of the provider's HTTP client.</summary>
    public static ProviderException FromResponse(in ProviderErrorContext context)
    {
        JsonObject? error = null;
        string? requestId = null;
        try
        {
            if (JsonNode.Parse(context.Body) is JsonObject root)
            {
                error = root["error"] as JsonObject;
                requestId = AnthropicJson.GetString(root, "request_id");
            }
        }
        catch (JsonException)
        {
            // A body that is not JSON (a proxy's error page) falls back to the status alone.
        }

        if (requestId is null && context.Headers.TryGetValues("request-id", out IEnumerable<string>? ids))
        {
            requestId = ids.FirstOrDefault();
        }

        string type = error is null ? "" : AnthropicJson.GetString(error, "type") ?? "";
        string message = error is null ? Truncate(context.Body) : AnthropicJson.GetString(error, "message") ?? Truncate(context.Body);
        return Build(type, message, (int)context.Status, context.RetryAfter, requestId, error);
    }

    /// <summary>Maps an error object found in a stream event or a batch result, where no HTTP status of its own exists.</summary>
    public static ProviderException FromErrorObject(JsonObject error, string? requestId, Func<string, string> scrub)
    {
        string type = AnthropicJson.GetString(error, "type") ?? "";
        string message = scrub(AnthropicJson.GetString(error, "message") ?? "the API reported an error");
        return Build(type, message, StatusForType(type), null, requestId, error);
    }

    // The status each documented error type goes with (API reference, Errors).
    private static int StatusForType(string type) => type switch
    {
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "billing_error" => 402,
        "permission_error" => 403,
        "not_found_error" => 404,
        "conflict_error" => 409,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        "api_error" => 500,
        "timeout_error" => 504,
        "overloaded_error" => 529,
        _ => 500,
    };

    private static ProviderException Build(string type, string message, int status, TimeSpan? retryAfter, string? requestId, JsonObject? error)
    {
        string? errorCode = error?["details"] is JsonObject details ? AnthropicJson.GetString(details, "error_code") : null;
        string text = $"{(type.Length > 0 ? type + ": " : "")}{message}";

        // Overload can come as a stream event or inside a batch, with any status; it is always transient.
        if (type == "overloaded_error" || status == 529)
        {
            return new OverloadedException(Provider, text, 529, retryAfter) { Code = type, RequestId = requestId };
        }

        if (type == "rate_limit_error" || status == 429)
        {
            // A spend-cap 429 has no retry-after and keeps failing until access resumes (API reference, Rate limits).
            bool exhausted = errorCode == "enforced_spend_limit_reached";
            return new RateLimitException(Provider, text, retryAfter) { Code = type.Length > 0 ? type : "rate_limit_error", RequestId = requestId, IsQuotaExhausted = exhausted };
        }

        if (type == "authentication_error" || type == "permission_error" || status is 401 or 403)
        {
            return new AuthException(Provider, text, status is 401 or 403 ? status : StatusForType(type)) { Code = type, RequestId = requestId };
        }

        if (status == 400 && IsContextOverflow(message))
        {
            (long? requested, long? window) = ParseOverflowNumbers(message);
            return new ContextOverflowException(Provider, text, 400) { Code = type, RequestId = requestId, RequestedTokens = requested, ContextWindow = window };
        }

        if (status is 400 or 404 or 409 or 413 or 422)
        {
            return new BadRequestException(Provider, text, status) { Code = type, RequestId = requestId };
        }

        // 402 billing_error, 500 api_error, 504 timeout_error and anything new: a plain provider error. Transience follows the status.
        return new ProviderException(Provider, text, status) { Code = type.Length > 0 ? type : null, RequestId = requestId };
    }

    // The API answers an input that alone exceeds the window with 400 invalid_request_error "prompt is too long" (API reference, Context windows).
    private static bool IsContextOverflow(string message) =>
        message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
        || message.Contains("exceed the context window", StringComparison.OrdinalIgnoreCase);

    // The message usually reads "prompt is too long: 210000 tokens > 200000 maximum"; the numbers are read when that is the shape.
    private static (long? Requested, long? Window) ParseOverflowNumbers(string message)
    {
        ReadOnlySpan<char> s = message.AsSpan();
        int gt = s.IndexOf('>');
        if (gt < 0)
        {
            return (null, null);
        }

        return (LastNumberBefore(s[..gt]), FirstNumberAfter(s[(gt + 1)..]));
    }

    private static long? LastNumberBefore(ReadOnlySpan<char> s)
    {
        int end = s.Length;
        while (end > 0 && !char.IsAsciiDigit(s[end - 1]))
        {
            end--;
        }

        int start = end;
        while (start > 0 && char.IsAsciiDigit(s[start - 1]))
        {
            start--;
        }

        return start < end && long.TryParse(s[start..end], out long v) ? v : null;
    }

    private static long? FirstNumberAfter(ReadOnlySpan<char> s)
    {
        int start = 0;
        while (start < s.Length && !char.IsAsciiDigit(s[start]))
        {
            start++;
        }

        int end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
        {
            end++;
        }

        return start < end && long.TryParse(s[start..end], out long v) ? v : null;
    }

    private static string Truncate(string body) => body.Length <= 400 ? body : body[..400];
}
