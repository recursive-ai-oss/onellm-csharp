using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneLlm.Http;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// Turns Gemini's error bodies into the typed errors. A failed call answers with <c>{"error": {"code", "message", "status", "details"}}</c>
/// (the Google API error model, https://google.aip.dev/193), where <c>status</c> is the canonical name such as <c>RESOURCE_EXHAUSTED</c>.
/// The API reference for the newer interactions API writes <c>code</c> as a snake_case word instead, so both shapes are read.
/// </summary>
internal static partial class GeminiErrors
{
    private const int MaxMessage = 600;

    public static ProviderException FromContext(in ProviderErrorContext context) =>
        FromBody(context.Provider, (int)context.Status, context.Body, context.RetryAfter);

    /// <summary>Builds the error for an HTTP status and body. The body may be empty or not JSON.</summary>
    public static ProviderException FromBody(string provider, int httpStatus, string body, TimeSpan? retryAfter)
    {
        string? status = null;
        string? message = null;
        TimeSpan? retryDelay = null;
        int effective = httpStatus;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error"u8, out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                Read(error, ref status, ref message, ref retryDelay, ref effective, httpStatus);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].ValueKind == JsonValueKind.Object && doc.RootElement[0].TryGetProperty("error"u8, out JsonElement first))
            {
                // Some Google endpoints wrap the error in an array.
                Read(first, ref status, ref message, ref retryDelay, ref effective, httpStatus);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text goes into the message as it is (it has been scrubbed of the key by the caller).
        }

        string text = message ?? body;
        if (text.Length > MaxMessage)
        {
            text = text[..MaxMessage];
        }

        return Build(provider, effective, status, text.Length == 0 ? $"HTTP {effective}" : $"HTTP {effective}: {text}", body, retryAfter ?? retryDelay);
    }

    /// <summary>Builds the error for an <c>error</c> object found inside a response that had status 200 (a stream that fails midway).</summary>
    public static ProviderException FromErrorElement(string provider, in JsonElement error)
    {
        string? status = null;
        string? message = null;
        TimeSpan? retryDelay = null;
        int effective = 500;
        if (error.ValueKind == JsonValueKind.Object)
        {
            Read(error, ref status, ref message, ref retryDelay, ref effective, 500);
        }

        string text = message ?? "the stream reported an error.";
        if (text.Length > MaxMessage)
        {
            text = text[..MaxMessage];
        }

        return Build(provider, effective, status, $"the stream failed: {text}", "", retryDelay);
    }

    private static void Read(JsonElement error, ref string? status, ref string? message, ref TimeSpan? retryDelay, ref int effective, int httpStatus)
    {
        if (error.TryGetProperty("message"u8, out JsonElement m) && m.ValueKind == JsonValueKind.String)
        {
            message = m.GetString();
        }

        if (error.TryGetProperty("status"u8, out JsonElement s) && s.ValueKind == JsonValueKind.String)
        {
            status = s.GetString();
        }

        if (error.TryGetProperty("code"u8, out JsonElement c))
        {
            if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) && n is >= 400 and < 600)
            {
                effective = n;
            }
            else if (c.ValueKind == JsonValueKind.String)
            {
                // The snake_case form of the interactions API: it is the status.
                status ??= c.GetString();
            }
        }

        if (effective == httpStatus && httpStatus is < 400 or >= 600)
        {
            effective = 500;
        }

        // google.rpc.RetryInfo in details: {"@type": ".../google.rpc.RetryInfo", "retryDelay": "34s"}.
        if (error.TryGetProperty("details"u8, out JsonElement details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement d in details.EnumerateArray())
            {
                if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("retryDelay"u8, out JsonElement rd) && rd.ValueKind == JsonValueKind.String
                    && TryParseDuration(rd.GetString(), out TimeSpan span))
                {
                    retryDelay = span;
                }
            }
        }
    }

    // Protobuf Duration in JSON: decimal seconds followed by "s".
    private static bool TryParseDuration(string? text, out TimeSpan span)
    {
        span = default;
        if (text is null || !text.EndsWith('s') || !double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0 || seconds > 86400)
        {
            return false;
        }

        span = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static ProviderException Build(string provider, int http, string? status, string message, string rawBody, TimeSpan? retryAfter)
    {
        string st = (status ?? "").ToUpperInvariant();
        switch (http)
        {
            case 400 or 413 when LooksLikeContextOverflow(message):
                (long? requested, long? window) = ParseTokenCounts(message);
                return new ContextOverflowException(provider, message, http) { Code = status, RequestedTokens = requested, ContextWindow = window };
            case 429:
                bool exhausted = st == "QUOTA_EXCEEDED"
                    || rawBody.Contains("PerDay", StringComparison.Ordinal)
                    || message.Contains("billing", StringComparison.OrdinalIgnoreCase) && message.Contains("plan", StringComparison.OrdinalIgnoreCase);
                return new RateLimitException(provider, message, retryAfter) { Code = status, IsQuotaExhausted = exhausted };
            case 503 or 529:
                return new OverloadedException(provider, message, http, retryAfter) { Code = status };
            case 402:
                // Prepaid credit is used up: waiting does not help.
                return new ProviderException(provider, message, http) { Code = status };
        }

        ProviderException e = ProviderErrors.FromStatus(provider, (HttpStatusCode)http, message, retryAfter);
        return status is null ? e : CopyWithCode(e, status);
    }

    // ProviderErrors.FromStatus builds subclasses whose Code is init-only; rebuild the common ones with the code set.
    private static ProviderException CopyWithCode(ProviderException e, string code) => e switch
    {
        AuthException a => new AuthException(e.Provider, StripPrefix(a), a.StatusCode) { Code = code },
        BadRequestException b => new BadRequestException(e.Provider, StripPrefix(b), b.StatusCode) { Code = code },
        _ when e.GetType() == typeof(ProviderException) => new ProviderException(e.Provider, StripPrefix(e), e.StatusCode) { Code = code },
        _ => e,
    };

    // The exception constructors prefix the provider's name; remove it so rebuilding does not double it.
    private static string StripPrefix(ProviderException e) => e.Message[(e.Provider.Length + 2)..];

    private static bool LooksLikeContextOverflow(string message) =>
        message.Contains("token count", StringComparison.OrdinalIgnoreCase) && message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
        || message.Contains("context window", StringComparison.OrdinalIgnoreCase)
        || message.Contains("exceeds the maximum number of tokens", StringComparison.OrdinalIgnoreCase);

    // "The input token count (1100000) exceeds the maximum number of tokens allowed (1048576)."
    private static (long? Requested, long? Window) ParseTokenCounts(string message)
    {
        Match m = TokenCounts().Match(message);
        return m.Success
            ? (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (null, null);
    }

    [GeneratedRegex(@"\(([0-9]{1,15})\)[^0-9(]{0,80}\(([0-9]{1,15})\)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenCounts();
}
