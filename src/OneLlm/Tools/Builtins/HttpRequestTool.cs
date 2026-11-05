using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Access;

namespace OneLlm.Tools.Builtins;

/// <summary>http_request: sends an http(s) request to a granted host and returns the status, key headers and body.</summary>
internal sealed class HttpRequestTool : BuiltinTool
{
    internal const string Description = "Send an HTTP request and return the status, the main response headers and the body as text. Only hosts the runtime has granted can be reached; redirects are followed (up to 5) only to granted hosts, and credentials you put in headers are not sent to a different host. Bodies are cut at a size limit, and binary bodies are described rather than returned.";

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The http or https URL to request." },
            "method": { "type": "string", "enum": ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], "description": "The HTTP method. Defaults to GET.", "default": "GET" },
            "headers": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Request headers, as name to value." },
            "body": { "type": "string", "description": "The request body, sent as UTF-8 text. Set a Content-Type header to say what it is." },
            "timeout": { "type": "integer", "description": "Timeout in seconds (default: 30, max: 120).", "default": 30 }
          },
          "required": ["url"]
        }
        """;

    private static readonly string[] s_shownHeaders = ["Content-Type", "Content-Length", "Location", "Retry-After", "ETag", "Last-Modified"];

    private readonly BuiltinOptions _options;

    public HttpRequestTool(BuiltinOptions options)
        : base(Spec("http_request", Description, Schema, TimeSpan.FromSeconds(130)), ToolCapability.Network)
    {
        _options = options;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string url = Args.RequiredString(inputs, "url");
        string method = (Args.OptionalString(inputs, "method") ?? "GET").ToUpperInvariant();
        string? body = Args.OptionalString(inputs, "body");
        int timeout = Math.Clamp(Args.OptionalInt(inputs, "timeout") ?? 30, 1, 120);
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"))
        {
            throw new ToolInputException("method", "must be GET, HEAD, POST, PUT, PATCH, DELETE or OPTIONS");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return Fail($"'{url}' is not a valid absolute URL.");
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (inputs.TryGetPropertyValue("headers", out JsonNode? h) && h is not null)
        {
            if (h is not JsonObject ho)
            {
                throw new ToolInputException("headers", "must be an object of strings");
            }

            foreach ((string name, JsonNode? value) in ho)
            {
                headers.Add(new(name, value is JsonValue v && v.TryGetValue(out string? s) ? s : throw new ToolInputException($"headers.{name}", "must be a string")));
            }
        }

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout), context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            using HttpResponseMessage response = await EgressClient.SendAsync(context.Capabilities, new HttpMethod(method), uri, headers, body, linked.Token).ConfigureAwait(false);
            (byte[] bytes, bool truncated) = await EgressClient.ReadBodyAsync(response, _options.HttpMaxBodyBytes, linked.Token).ConfigureAwait(false);
            return ToolResult.FromText(Format(response, uri, bytes, truncated));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"The request timed out after {timeout} seconds.");
        }
    }

    private static string Format(HttpResponseMessage response, Uri requested, byte[] bytes, bool truncated)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n");
        Uri? final = response.RequestMessage?.RequestUri;
        if (final is not null && final != requested)
        {
            sb.Append("Final URL: ").Append(final.GetLeftPart(UriPartial.Path)).Append('\n');
        }

        foreach (string name in s_shownHeaders)
        {
            string? value = response.Headers.TryGetValues(name, out IEnumerable<string>? hv) ? string.Join(", ", hv)
                : response.Content.Headers.TryGetValues(name, out IEnumerable<string>? cv) ? string.Join(", ", cv) : null;
            if (value is not null)
            {
                sb.Append(name).Append(": ").Append(value).Append('\n');
            }
        }

        sb.Append('\n');
        MediaTypeHeaderValue? type = response.Content.Headers.ContentType;
        if (bytes.Length == 0)
        {
            sb.Append("(empty body)");
        }
        else if (IsText(type?.MediaType) && TextFile.TryDecode(bytes, out string text, out _, out _))
        {
            sb.Append(text);
            if (truncated)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\n[Body truncated: showing the first {bytes.Length} bytes.]");
            }
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"[Binary or non-text body: {(truncated ? "at least " : "")}{bytes.Length} bytes, {type?.MediaType ?? "no content type"}]");
        }

        return sb.ToString();
    }

    private static bool IsText(string? mediaType) =>
        mediaType is null
        || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/yaml", StringComparison.OrdinalIgnoreCase);
}
