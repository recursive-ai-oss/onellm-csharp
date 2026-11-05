using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Http;
using OneLlm.Messages;

namespace OneLlm.Providers.TypeSafe;

/// <summary>Settings for <see cref="JevProvider"/>.</summary>
public sealed class JevOptions
{
    /// <summary>The API key, sent as a bearer token.</summary>
    public Secret? ApiKey { get; init; }

    /// <summary>The API root. Defaults to <c>https://api.typesafe.ai/v1/</c>; the request goes to <c>systemone</c> below it.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>The HTTP client. Defaults to the shared one, which does not follow redirects.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Retry settings. Defaults to <see cref="RetryPolicy.Default"/>.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>The time one attempt may take. Defaults to 15 seconds: Jev answers in about 100 to 200 ms.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// TypeSafe Jev, a System One decisions engine: <c>POST /v1/systemone</c> with a state and named questions, answered with a typed answer
/// per question. The request body is <c>{"state":...,"model":...,"questions":{...}}</c> in exactly that key order, with the state and
/// questions as the decider rendered them.
/// </summary>
/// <remarks>
/// Jev answers are already in the common answer shape, so they pass through. Usage is <c>input_tokens</c> (all billable) and
/// <c>output_tokens</c> (free). A 400 with <c>max_tokens_exceeded</c> is a <see cref="ContextOverflowException"/>, which the decider
/// engine answers by splitting the questions across requests. Clients are thread-safe.
/// </remarks>
public sealed class JevProvider : IDecisionProvider
{
    private static readonly Uri DefaultRoot = new("https://api.typesafe.ai/v1/");

    private readonly ProviderHttpClient _http;
    private readonly Uri _endpoint;
    private readonly Secret? _key;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">Key, base URL and HTTP settings.</param>
    /// <param name="time">The clock for retry delays and timeouts.</param>
    public JevProvider(JevOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Uri root = options.BaseUrl ?? DefaultRoot;
        string text = root.AbsoluteUri;
        _endpoint = new Uri(text.EndsWith('/') ? root : new Uri(text + "/"), "systemone");
        _key = options.ApiKey is { IsEmpty: false } ? options.ApiKey : null;
        _http = new ProviderHttpClient(
            "typesafe",
            time,
            options.HttpClient,
            options.Retry,
            _key is null ? [] : [_key],
            options.Timeout ?? TimeSpan.FromSeconds(15),
            errorMapper: static (in ProviderErrorContext c) => MapError(c));
    }

    /// <inheritdoc />
    public string Name => "typesafe";

    /// <inheritdoc />
    public string Deployment => "direct";

    /// <summary>The bytes of the request body for a request, as they are sent.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The JSON body.</returns>
    public static ReadOnlyMemory<byte> WriteBody(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new ArrayBufferWriter<byte>(request.State.Length + 256);
        GoJson.WriteText(body, "{\"state\":");
        GoJson.WriteBytes(body, request.State.Span);
        GoJson.WriteText(body, ",\"model\":");
        GoJson.WriteString(body, request.Model);
        GoJson.WriteText(body, ",\"questions\":{");
        for (int i = 0; i < request.Questions.Count; i++)
        {
            if (i > 0)
            {
                GoJson.WriteText(body, ",");
            }

            GoJson.WriteString(body, request.Questions[i].Id);
            GoJson.WriteText(body, ":");
            GoJson.WriteBytes(body, request.Questions[i].Json.Span);
        }

        GoJson.WriteText(body, "}}");
        return body.WrittenMemory;
    }

    /// <inheritdoc />
    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images.Count > 0)
        {
            throw new CapabilityMissingException("Images in a Jev request", "an engine that takes images (OpenAI Decisions or a vision chat model); Jev images need an opt-in header and are not sent by OneLLM");
        }

        if (request.Questions.Count == 0)
        {
            throw new ArgumentException("A decision request needs at least one question.", nameof(request));
        }

        ReadOnlyMemory<byte> body = WriteBody(request);
        using HttpResponseMessage response = await _http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ReadOnlyMemoryContent(body) };
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                message.Headers.Accept.ParseAdd("application/json");
                if (_key is not null)
                {
                    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _key.Value);
                }

                return message;
            },
            cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _http.ReadBodyAsync(response, 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return Parse(bytes);
    }

    internal static DecisionResponse Parse(byte[] bytes)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new MalformedResponseException("typesafe", "the response is not JSON: " + e.Message);
        }

        if (root is not JsonObject obj || obj["answers"] is not JsonObject answers || answers.Count == 0)
        {
            throw new MalformedResponseException("typesafe", "the response has no answers.");
        }

        string model = (string?)obj["model"] ?? "";
        long input = 0, output = 0;
        if (obj["usage"] is JsonObject usage)
        {
            // Jev: input_tokens are all billable input, output_tokens are free. There is no cache split.
            input = (long?)usage["input_tokens"] ?? 0;
            output = (long?)usage["output_tokens"] ?? 0;
        }

        obj.Remove("answers");
        return new DecisionResponse
        {
            Answers = answers.DeepClone().AsObject(),
            Model = model,
            Usage = new Usage { Model = model, InputTokens = input, OutputTokens = output },
        };
    }

    private static ProviderException MapError(in ProviderErrorContext c)
    {
        string text = c.Body.Length <= 400 ? c.Body : c.Body[..400];
        if ((int)c.Status == 400 && c.Body.Contains("max_tokens_exceeded", StringComparison.Ordinal))
        {
            // The API reference: a request longer than the model's context length returns 400 with max_tokens_exceeded.
            return new ContextOverflowException(c.Provider, $"HTTP 400: {text}");
        }

        return ProviderErrors.FromStatus(c.Provider, c.Status, $"HTTP {(int)c.Status}: {text}", c.RetryAfter);
    }
}
