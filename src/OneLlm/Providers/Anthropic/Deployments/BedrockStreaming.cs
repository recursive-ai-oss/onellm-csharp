using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Http;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// Maps Amazon Bedrock errors, which come as an HTTP status with a JSON body <c>{"message": "..."}</c> and the error's shape name in the
/// <c>X-Amzn-Errortype</c> header (Smithy restJson1 protocol, "Operation error serialization"), or as an exception message inside an event
/// stream. The names and statuses are those of the InvokeModel and InvokeModelWithResponseStream references.
/// </summary>
internal static class BedrockErrors
{
    /// <summary>The mapper handed to the HTTP client for the Bedrock deployment.</summary>
    public static ProviderErrorMapper Mapper => static (in ProviderErrorContext c) => FromHttp(c);

    public static ProviderException FromHttp(in ProviderErrorContext context)
    {
        string? type = null;
        if (context.Headers.TryGetValues("x-amzn-errortype", out IEnumerable<string>? values))
        {
            type = values.FirstOrDefault();
        }

        string? message = null;
        try
        {
            if (JsonNode.Parse(context.Body) is JsonObject root)
            {
                message = Str(root["message"]) ?? Str(root["Message"]);
                type ??= Str(root["__type"]) ?? Str(root["code"]);
            }
        }
        catch (JsonException)
        {
            // A body that is not JSON (a proxy's page) falls back to the status and the start of the text.
        }

        string? requestId = context.Headers.TryGetValues("x-amzn-requestid", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null;
        message ??= context.Body.Length <= 400 ? context.Body : context.Body[..400];
        return Create(Sanitize(type), message, (int)context.Status, context.RetryAfter, requestId);
    }

    /// <summary>Builds the error for an exception message of an event stream (<c>:exception-type</c> and its JSON payload).</summary>
    public static ProviderException FromStreamException(string? exceptionType, byte[] payload, Func<string, string> scrub)
    {
        string? message = null;
        try
        {
            if (JsonNode.Parse(payload) is JsonObject root)
            {
                message = Str(root["message"]) ?? Str(root["Message"]);
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic text.
        }

        message = scrub(message ?? "the stream reported an error.");
        string type = Sanitize(exceptionType);
        // Exceptions inside a stream carry no HTTP status of their own; each type has the status its reference page documents.
        return Create(type, message, StatusFor(type), null, null);
    }

    /// <summary>The error shape name without the namespace or the legacy suffix: <c>ThrottlingException:http://...</c> is <c>ThrottlingException</c>.</summary>
    internal static string Sanitize(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return "";
        }

        int colon = type.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            type = type[..colon];
        }

        int hash = type.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? type[(hash + 1)..] : type;
    }

    // Matches the lower-camel names the stream uses (throttlingException) as well as the shape names (ThrottlingException).
    private static int StatusFor(string type) => type.ToLowerInvariant() switch
    {
        "accessdeniedexception" => 403,
        "throttlingexception" or "modelnotreadyexception" => 429,
        "validationexception" or "servicequotaexceededexception" => 400,
        "resourcenotfoundexception" => 404,
        "modeltimeoutexception" => 408,
        "modelerrorexception" or "modelstreamerrorexception" => 424,
        "serviceunavailableexception" => 503,
        _ => 500,
    };

    private static ProviderException Create(string type, string message, int status, TimeSpan? retryAfter, string? requestId)
    {
        string text = type.Length > 0 ? $"{type}: {message}" : message;
        string name = type.ToLowerInvariant();
        string provider = AnthropicErrors.Provider;
        if (name is "throttlingexception" or "modelnotreadyexception" or "servicequotaexceededexception" || status == 429)
        {
            return new RateLimitException(provider, text, retryAfter) { Code = type, RequestId = requestId };
        }

        if (name == "serviceunavailableexception" || status == 503)
        {
            return new OverloadedException(provider, text, 503, retryAfter) { Code = type, RequestId = requestId };
        }

        if (name is "accessdeniedexception" or "unrecognizedclientexception" or "invalidsignatureexception" or "expiredtokenexception" or "missingauthenticationtokenexception"
            || status is 401 or 403)
        {
            return new AuthException(provider, text, status is 401 or 403 ? status : 403) { Code = type, RequestId = requestId };
        }

        if (status == 400 && (message.Contains("too long", StringComparison.OrdinalIgnoreCase) || message.Contains("context window", StringComparison.OrdinalIgnoreCase)))
        {
            return new ContextOverflowException(provider, text, 400) { Code = type, RequestId = requestId };
        }

        if (name is "validationexception" or "resourcenotfoundexception" || status is 400 or 404 or 413 or 422)
        {
            return new BadRequestException(provider, text, status is 400 or 404 or 413 or 422 ? status : 400) { Code = type, RequestId = requestId };
        }

        return new ProviderException(provider, text, status) { Code = type.Length > 0 ? type : null, RequestId = requestId };
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// Presents the body of an <c>InvokeModelWithResponseStream</c> response as the server-sent event stream the Messages codec reads. Each
/// <c>chunk</c> message carries <c>{"bytes": "&lt;base64&gt;"}</c>, and the decoded bytes are one Messages stream event (the same JSON the
/// direct API sends in a <c>data:</c> line). Exception and error messages are thrown as typed errors from the read that reaches them.
/// </summary>
internal sealed class BedrockEventStream : Stream
{
    private readonly EventStreamReader _reader;
    private readonly Stream _inner;
    private readonly Func<string, string> _scrub;
    private byte[] _pending = [];
    private int _offset;
    private bool _done;

    public BedrockEventStream(Stream body, Func<string, string>? scrub = null)
    {
        _inner = body;
        _reader = new EventStreamReader(body, AnthropicErrors.Provider);
        _scrub = scrub ?? (static s => s);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _pending.Length)
        {
            if (_done)
            {
                return 0;
            }

            EventStreamMessage? message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                _done = true;
                return 0;
            }

            _pending = ToServerSentEvent(message);
            _offset = 0;
        }

        int n = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read the event stream asynchronously.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private byte[] ToServerSentEvent(EventStreamMessage message)
    {
        switch (message.Text(":message-type"))
        {
            case "event":
                // Only "chunk" carries model output. Other event types are not part of this operation's output and are skipped.
                if (message.Text(":event-type") != "chunk")
                {
                    return [];
                }

                return Wrap(DecodeChunk(message.Payload));
            case "exception":
                throw BedrockErrors.FromStreamException(message.Text(":exception-type"), message.Payload, _scrub);
            case "error":
                // Unmodeled error: :error-code and :error-message headers (specification, "Unmodeled Error Events").
                throw new ProviderException(
                    AnthropicErrors.Provider,
                    _scrub($"{message.Text(":error-code") ?? "error"}: {message.Text(":error-message") ?? "the stream reported an error."}"),
                    500) { Code = message.Text(":error-code") };
            default:
                throw new MalformedResponseException(AnthropicErrors.Provider, "an event stream message has no known message type.");
        }
    }

    private static byte[] DecodeChunk(byte[] payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("bytes", out JsonElement bytes)
                && bytes.ValueKind == JsonValueKind.String
                && bytes.GetBytesFromBase64() is { Length: > 0 } decoded)
            {
                return decoded;
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            // Reported below without echoing the payload.
        }

        throw new MalformedResponseException(AnthropicErrors.Provider, "a stream chunk has no valid bytes member.");
    }

    // "data: <json>" and a blank line. A payload with line breaks (only whitespace between tokens can have them) gets one data field per line,
    // which the event stream standard joins back with line feeds.
    private static byte[] Wrap(byte[] json)
    {
        using var ms = new MemoryStream(json.Length + 16);
        int start = 0;
        for (int i = 0; i <= json.Length; i++)
        {
            if (i == json.Length || json[i] is (byte)'\n' or (byte)'\r')
            {
                ms.Write("data: "u8);
                ms.Write(json, start, i - start);
                ms.WriteByte((byte)'\n');
                if (i < json.Length && json[i] == '\r' && i + 1 < json.Length && json[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }
}
