using System.Net;

namespace OneLlm;

/// <summary>The base of every error OneLLM throws on purpose. Messages never contain an API key.</summary>
public class OneLlmException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OneLlmException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong. Must not contain a secret.</param>
    public OneLlmException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong. Must not contain a secret.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OneLlmException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A provider failed, was unreachable, or answered with something unusable. The subclasses name the common causes.</summary>
public class ProviderException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name, for example <c>anthropic</c>.</param>
    /// <param name="message">A description, without the API key. It is prefixed with the provider's name.</param>
    /// <param name="statusCode">The HTTP status, when there was one.</param>
    public ProviderException(string provider, string message, int? statusCode = null)
        : base($"{provider}: {message}")
    {
        Provider = provider;
        StatusCode = statusCode;
    }

    /// <summary>Creates the exception with the failure that caused it.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="innerException">The underlying failure. It is never kept when it could carry what a server echoed; pass null then.</param>
    public ProviderException(string provider, string message, Exception? innerException)
        : base($"{provider}: {message}", innerException)
    {
        Provider = provider;
    }

    /// <summary>The provider's name.</summary>
    public string Provider { get; }

    /// <summary>The HTTP status code, or null when the failure was not an HTTP answer.</summary>
    public int? StatusCode { get; }

    /// <summary>The provider's own error code or type (<c>invalid_request_error</c>, <c>RESOURCE_EXHAUSTED</c>, ...), when it gave one.</summary>
    public string? Code { get; init; }

    /// <summary>The provider's id for the request, for support tickets, when it gave one.</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// True when trying the same request again can succeed: rate limits, overload, server errors, time-outs and dropped connections.
    /// The HTTP retry policy retries exactly these.
    /// </summary>
    public virtual bool IsTransient => StatusCode is { } s && ProviderErrors.IsTransientStatus(s);
}

/// <summary>The provider rejected the credentials (HTTP 401 or 403): missing, wrong or expired key, or no access to the model.</summary>
public sealed class AuthException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status.</param>
    public AuthException(string provider, string message, int? statusCode = 401)
        : base(provider, message, statusCode)
    {
    }
}

/// <summary>The provider throttled the request (HTTP 429).</summary>
public sealed class RateLimitException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="retryAfter">How long the provider asked callers to wait, when it said.</param>
    public RateLimitException(string provider, string message, TimeSpan? retryAfter = null)
        : base(provider, message, 429)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the provider asked callers to wait (its <c>Retry-After</c> header), when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A rate limit is transient, unless the provider says the quota is exhausted: see <see cref="IsQuotaExhausted"/>.</summary>
    public override bool IsTransient => !IsQuotaExhausted;

    /// <summary>True when the account is out of quota or credit; waiting will not help.</summary>
    public bool IsQuotaExhausted { get; init; }
}

/// <summary>The provider is overloaded or temporarily unavailable (HTTP 503, 529, or an overloaded error in the body).</summary>
public sealed class OverloadedException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="retryAfter">How long the provider asked callers to wait, when it said.</param>
    public OverloadedException(string provider, string message, int? statusCode = 529, TimeSpan? retryAfter = null)
        : base(provider, message, statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the provider asked callers to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

/// <summary>The request is larger than the model's context window.</summary>
public sealed class ContextOverflowException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status, usually 400.</param>
    public ContextOverflowException(string provider, string message, int? statusCode = 400)
        : base(provider, message, statusCode)
    {
    }

    /// <summary>The model's context window in tokens, when the provider's error states it.</summary>
    public long? ContextWindow { get; init; }

    /// <summary>The number of tokens the request needed, when the provider's error states it.</summary>
    public long? RequestedTokens { get; init; }

    /// <inheritdoc />
    public override bool IsTransient => false;
}

/// <summary>The provider's content filter blocked the request or the answer.</summary>
public sealed class ContentFilterException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status, when the block came as an error.</param>
    public ContentFilterException(string provider, string message, int? statusCode = null)
        : base(provider, message, statusCode)
    {
    }

    /// <summary>
    /// What the provider's filter reported for each category it evaluated, when the error carried that (Azure OpenAI does: hate, sexual, violence,
    /// self_harm, jailbreak, protected material ...). Empty when the provider gave only a message.
    /// </summary>
    public IReadOnlyList<ContentFilterCategory> Categories { get; init; } = [];

    /// <inheritdoc />
    public override bool IsTransient => false;
}

/// <summary>One category a provider's content filter evaluated.</summary>
/// <param name="Name">The category as the provider names it, for example <c>hate</c> or <c>self_harm</c>.</param>
/// <param name="Filtered">True when this category caused the block.</param>
/// <param name="Severity">The severity level the provider reported (<c>safe</c>, <c>low</c>, <c>medium</c>, <c>high</c>), or null for the binary categories.</param>
/// <param name="Detected">For the binary categories (jailbreak, protected material), whether the content matched; null for the severity categories.</param>
public sealed record ContentFilterCategory(string Name, bool Filtered, string? Severity = null, bool? Detected = null);

/// <summary>The provider refused the request as malformed or unsupported (HTTP 400, 404, 413, 422), other than a context overflow.</summary>
public sealed class BadRequestException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status.</param>
    public BadRequestException(string provider, string message, int? statusCode = 400)
        : base(provider, message, statusCode)
    {
    }

    /// <inheritdoc />
    public override bool IsTransient => false;
}

/// <summary>The request never got an answer: connection error or time-out, after the configured retries.</summary>
public sealed class TransportException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="isTimeout">True when the failure was the request timing out.</param>
    public TransportException(string provider, string message, bool isTimeout = false)
        : base(provider, message)
    {
        IsTimeout = isTimeout;
    }

    /// <summary>True when the request timed out.</summary>
    public bool IsTimeout { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

/// <summary>The provider answered, but not in the documented format.</summary>
public sealed class MalformedResponseException : ProviderException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="message">A description, without the API key.</param>
    /// <param name="statusCode">The HTTP status of the answer.</param>
    public MalformedResponseException(string provider, string message, int? statusCode = null)
        : base(provider, message, statusCode)
    {
    }

    /// <inheritdoc />
    public override bool IsTransient => false;
}

/// <summary>The model declined to answer and the caller asked for an answer, so a refusal is an error.</summary>
public sealed class RefusedException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was refused, for example the question a decider could not answer.</param>
    /// <param name="refusal">The model's explanation, when it gave one.</param>
    public RefusedException(string message, string? refusal = null)
        : base(message)
    {
        Refusal = refusal;
    }

    /// <summary>The model's explanation, when it gave one.</summary>
    public string? Refusal { get; }
}

/// <summary>Which spending bound a call would have crossed.</summary>
public enum BudgetKind
{
    /// <summary>The estimated cost of one call is above the per-call limit.</summary>
    PerCall = 0,

    /// <summary>The spend so far plus the estimate is above the spend cap of an instance or a runtime.</summary>
    SpendCap = 1,

    /// <summary>A bound is set but the catalog has no price for the model, so the cost cannot be bounded.</summary>
    UnknownPrice = 2,
}

/// <summary>A call was refused before it was sent because it could cost more than the configured limits allow.</summary>
public sealed class BudgetExceededException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">Which bound.</param>
    /// <param name="message">A description.</param>
    /// <param name="limit">The configured bound in USD, when there is one.</param>
    /// <param name="required">The worst-case cost of the call in USD, when known.</param>
    /// <param name="spent">What has been spent so far in USD.</param>
    public BudgetExceededException(BudgetKind kind, string message, decimal? limit, decimal? required, decimal spent)
        : base(message)
    {
        Kind = kind;
        Limit = limit;
        Required = required;
        Spent = spent;
    }

    /// <summary>Which bound.</summary>
    public BudgetKind Kind { get; }

    /// <summary>The configured bound in USD, or null for an unknown price.</summary>
    public decimal? Limit { get; }

    /// <summary>The worst-case cost of the refused call in USD, or null when the price is unknown.</summary>
    public decimal? Required { get; }

    /// <summary>What had been spent when the call was refused, in USD.</summary>
    public decimal Spent { get; }
}

/// <summary>
/// A feature needs a package that is not installed or a grant the runtime has not given. The message names which, for example
/// <c>Add the RecursiveAI.OneLlm.JavaScript package</c> or <c>Grant shell access to the runtime</c>.
/// </summary>
public sealed class CapabilityMissingException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="capability">What was asked for, for example <c>run_javascript</c> or <c>embeddings</c>.</param>
    /// <param name="remedy">What to add or grant, for example <c>the RecursiveAI.OneLlm.Web package</c>.</param>
    /// <param name="isPackage">True when the remedy is a package; false when it is a runtime grant or a provider feature.</param>
    public CapabilityMissingException(string capability, string remedy, bool isPackage = false)
        : base($"{capability} is not available. {(isPackage ? "Add" : "Grant")} {remedy}.")
    {
        Capability = capability;
        Remedy = remedy;
        IsPackage = isPackage;
    }

    /// <summary>What was asked for.</summary>
    public string Capability { get; }

    /// <summary>What to add or grant.</summary>
    public string Remedy { get; }

    /// <summary>True when <see cref="Remedy"/> is a package to install.</summary>
    public bool IsPackage { get; }
}

/// <summary>A definition, flow, decider, catalog file or template is invalid. It says where: file, line and column.</summary>
public sealed class BadDefinitionException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="file">The file or source name, when known.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    /// <param name="innerException">The parser error behind it, when there is one.</param>
    public BadDefinitionException(string message, string? file = null, int line = 0, int column = 0, Exception? innerException = null)
        : base(Format(message, file, line, column), innerException)
    {
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>The file or source name, when known.</summary>
    public string? File { get; }

    /// <summary>The 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>The 1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    private static string Format(string message, string? file, int line, int column)
    {
        if (file is null)
        {
            return message;
        }

        string where = line > 0 ? $"{file}:{line}:{column}" : file;
        return $"{where}: {message}";
    }
}

/// <summary>A model name did not resolve to exactly one model in the catalog.</summary>
public sealed class ModelNotFoundException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="requested">The name the caller used.</param>
    /// <param name="message">What went wrong, including the candidates when the name was ambiguous.</param>
    public ModelNotFoundException(string requested, string message)
        : base(message)
    {
        Requested = requested;
    }

    /// <summary>The name the caller used.</summary>
    public string Requested { get; }
}

/// <summary>Maps HTTP statuses to the typed errors; used by the shared HTTP layer and by provider codecs that refine the result.</summary>
public static class ProviderErrors
{
    /// <summary>True for the statuses the retry policy retries: 408, 429, 500, 502, 503, 504 and 529.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <returns>Whether a retry can help.</returns>
    public static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504 or 529;

    /// <summary>
    /// Builds the typed error for an HTTP failure from its status alone. Provider codecs call this and then look inside the body for
    /// causes only they know, such as a context overflow reported as a plain 400.
    /// </summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="status">The HTTP status.</param>
    /// <param name="message">A description with the API key already removed.</param>
    /// <param name="retryAfter">The parsed <c>Retry-After</c>, when the response had one.</param>
    /// <returns>The error to throw.</returns>
    public static ProviderException FromStatus(string provider, HttpStatusCode status, string message, TimeSpan? retryAfter = null)
    {
        int code = (int)status;
        return code switch
        {
            401 or 403 => new AuthException(provider, message, code),
            429 => new RateLimitException(provider, message, retryAfter),
            503 or 529 => new OverloadedException(provider, message, code, retryAfter),
            400 or 404 or 409 or 413 or 422 => new BadRequestException(provider, message, code),
            _ => new ProviderException(provider, message, code),
        };
    }
}
