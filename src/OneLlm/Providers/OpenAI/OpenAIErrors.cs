using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Http;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Maps OpenAI's error shape, <c>{"error": {"message", "type", "param", "code"}}</c> (Error codes guide), and the status codes it comes with onto
/// the typed errors. The compatible servers use the same shape; where one differs the status mapping still applies.
/// </summary>
internal static class OpenAIErrors
{
    private const int MaxMessage = 600;

    /// <summary>The mapper handed to <see cref="ProviderHttpClient"/>.</summary>
    public static ProviderErrorMapper Mapper => static (in ProviderErrorContext c) => FromHttp(c);

    public static ProviderException FromHttp(in ProviderErrorContext context)
    {
        ReadError(context.Body, out string? message, out string? code, out string? type);
        string text = message ?? Truncate(context.Body);
        return Create(context.Provider, (int)context.Status, text, code, type, context.RetryAfter);
    }

    /// <summary>Builds the error for an <c>error</c> or <c>response.failed</c> event in the middle of a stream.</summary>
    public static ProviderException FromStreamError(string provider, string? code, string? message, string? type)
    {
        string text = message is null ? "the stream reported an error." : Truncate(message);
        int? status = code switch
        {
            "rate_limit_exceeded" or "insufficient_quota" => 429,
            "server_error" or "internal_error" => 500,
            "overloaded" or "slow_down" or "service_unavailable" => 503,
            "invalid_api_key" or "authentication_error" => 401,
            _ => null,
        };
        return Create(provider, status, text, code, type, null);
    }

    internal static ProviderException Create(string provider, int? status, string message, string? code, string? type, TimeSpan? retryAfter)
    {
        if (code is "insufficient_quota" or "billing_hard_limit_reached")
        {
            return new RateLimitException(provider, message, retryAfter) { IsQuotaExhausted = true };
        }

        bool overflow = code == "context_length_exceeded"
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
            || (status == 400 && message.Contains("context window", StringComparison.OrdinalIgnoreCase));
        if (overflow)
        {
            ParseOverflow(message, out long? window, out long? requested);
            return new ContextOverflowException(provider, message, status ?? 400) { ContextWindow = window, RequestedTokens = requested };
        }

        if (code is "content_filter" or "content_policy_violation" or "moderation_blocked" || type is "content_filter")
        {
            return new ContentFilterException(provider, message, status);
        }

        if (status is { } s)
        {
            return ProviderErrors.FromStatus(provider, (HttpStatusCode)s, message, retryAfter);
        }

        return code is "rate_limit_exceeded"
            ? new RateLimitException(provider, message, retryAfter)
            : new ProviderException(provider, message);
    }

    private static void ReadError(string body, out string? message, out string? code, out string? type)
    {
        message = code = type = null;
        if (body.Length == 0 || body[0] != '{')
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(body) is JsonObject root)
            {
                // Some servers put the message at the top level instead of under "error".
                JsonObject e = root["error"] as JsonObject ?? root;
                message = Str(e["message"]);
                code = Str(e["code"]);
                type = Str(e["type"]);
                if (message is null && root["error"] is JsonValue plain && plain.TryGetValue(out string? s))
                {
                    message = s;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all: the caller falls back to the body text.
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : node?.ToJsonString();

    // "This model's maximum context length is 128000 tokens. However, your messages resulted in 130123 tokens."
    private static void ParseOverflow(string message, out long? window, out long? requested)
    {
        window = requested = null;
        var numbers = new List<long>(3);
        int i = 0;
        while (i < message.Length && numbers.Count < 3)
        {
            if (char.IsAsciiDigit(message[i]))
            {
                int j = i;
                while (j < message.Length && (char.IsAsciiDigit(message[j]) || message[j] == ','))
                {
                    j++;
                }

                if (long.TryParse(message.AsSpan(i, j - i).ToString().Replace(",", "", StringComparison.Ordinal), out long n))
                {
                    numbers.Add(n);
                }

                i = j;
            }
            else
            {
                i++;
            }
        }

        // Both numbers are present only in the "maximum ... resulted in" wording; otherwise nothing is guessed.
        if (numbers.Count >= 2 && message.Contains("maximum", StringComparison.OrdinalIgnoreCase))
        {
            window = numbers[0];
            requested = numbers[1];
        }
    }

    private static string Truncate(string text) => text.Length <= MaxMessage ? text : text[..MaxMessage];
}
