using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

internal enum OpenAIProtocol
{
    Responses,
    ChatCompletions,
}

/// <summary>
/// The shared machinery under <see cref="OpenAIProvider"/> and <see cref="OpenAICompatibleProvider"/>: building and sending requests through an
/// <see cref="OpenAIEndpoint"/>, decoding whole and streamed answers with the codec of one protocol, embeddings and the Batch API.
/// </summary>
internal sealed class OpenAIWire
{
    private const int MaxBodyBytes = 256 * 1024 * 1024;

    private readonly OpenAIOptions _options;
    private readonly ModelCatalog? _catalog;

    public OpenAIWire(string provider, string catalogScope, OpenAIProtocol protocol, ChatDialect dialect, OpenAIEndpoint endpoint, OpenAIOptions options, TimeProvider time)
    {
        Provider = provider;
        CatalogScope = catalogScope;
        Protocol = protocol;
        Dialect = dialect;
        Endpoint = endpoint;
        _options = options;
        _catalog = options.Catalog;
        Http = new ProviderHttpClient(
            provider,
            time,
            options.HttpClient,
            options.Retry,
            endpoint.Secrets,
            options.Timeout,
            errorMapper: OpenAIErrors.Mapper);
    }

    public string Provider { get; }

    public string CatalogScope { get; }

    public OpenAIProtocol Protocol { get; }

    public ChatDialect Dialect { get; }

    public OpenAIEndpoint Endpoint { get; }

    public ProviderHttpClient Http { get; }

    // ---- chat ----

    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<ResponseWarning>();
        ReadOnlyMemory<byte> body = WriteChat(request, stream: false, warnings);
        using HttpResponseMessage response = await PostAsync(ChatPath, request.Model, body, stream: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        ChatResponse decoded = DecodeChat(bytes, request.Model);
        return warnings.Count == 0 ? decoded : decoded with { Warnings = warnings };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<ResponseWarning>();
        ReadOnlyMemory<byte> body = WriteChat(request, stream: true, warnings);
        // A stream has no response object to carry notices about the request, so they go out first, as events.
        foreach (ResponseWarning w in warnings)
        {
            yield return StreamEvent.Warning(w);
        }

        using HttpResponseMessage response = await PostAsync(ChatPath, request.Model, body, stream: true, cancellationToken).ConfigureAwait(false);
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var sse = new SseReader(stream, Provider);
            if (Protocol == OpenAIProtocol.Responses)
            {
                var decoder = new ResponsesDecoder(Provider);
                while (await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false))
                {
                    decoder.ProcessEvent(sse.Data);
                    foreach (StreamEvent e in decoder.Events)
                    {
                        yield return e;
                    }

                    if (decoder.Finished)
                    {
                        yield break;
                    }
                }

                throw new MalformedResponseException(Provider, "the stream ended before the response was complete.");
            }

            var chat = new ChatCompletionsDecoder(Provider);
            while (await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false))
            {
                // Chat Completions ends the stream with a data line that is not JSON: [DONE].
                if (sse.Data.Span.SequenceEqual("[DONE]"u8))
                {
                    break;
                }

                chat.Process(sse.Data);
                foreach (StreamEvent e in chat.Events)
                {
                    yield return e;
                }
            }

            chat.Complete();
            foreach (StreamEvent e in chat.Events)
            {
                yield return e;
            }
        }
    }

    public ChatResponse DecodeChat(ReadOnlyMemory<byte> body, string requestedModel)
    {
        var acc = new MessageAccumulator();
        if (Protocol == OpenAIProtocol.Responses)
        {
            var decoder = new ResponsesDecoder(Provider);
            decoder.ProcessResponse(body);
            foreach (StreamEvent e in decoder.Events)
            {
                acc.Add(e);
            }
        }
        else
        {
            var decoder = new ChatCompletionsDecoder(Provider);
            decoder.Process(body);
            foreach (StreamEvent e in decoder.Events)
            {
                acc.Add(e);
            }

            decoder.Complete();
            foreach (StreamEvent e in decoder.Events)
            {
                acc.Add(e);
            }
        }

        return Build(acc, requestedModel);
    }

    /// <summary>Decodes a response object that is already parsed (a line of a batch output file).</summary>
    public ChatResponse DecodeChatObject(JsonObject response, string requestedModel)
    {
        var acc = new MessageAccumulator();
        if (Protocol == OpenAIProtocol.Responses)
        {
            var decoder = new ResponsesDecoder(Provider);
            decoder.ProcessResponseObject(response);
            foreach (StreamEvent e in decoder.Events)
            {
                acc.Add(e);
            }

            return Build(acc, requestedModel);
        }

        return DecodeChat(Encoding.UTF8.GetBytes(response.ToJsonString()), requestedModel);
    }

    private static ChatResponse Build(MessageAccumulator acc, string requestedModel) =>
        new()
        {
            Message = acc.ToMessage(),
            Id = acc.ResponseId,
            Model = acc.Model ?? requestedModel,
            Finish = acc.Finish,
            FinishReasonRaw = acc.FinishReasonRaw,
        };

    public ReadOnlyMemory<byte> WriteChat(ChatRequest request, bool stream, List<ResponseWarning>? warnings = null)
    {
        // One catalog pass per request: the adjuster strips what the model does not take and refuses what would change the request's meaning.
        if (FindResolved(request.Model) is { } resolved)
        {
            AdjustedRequest adjusted = RequestAdjuster.Adjust(resolved, request);
            request = adjusted.Request;
            warnings?.AddRange(adjusted.Warnings);
        }

        RequestPolicy policy = PolicyFor(request.Model, warnings);
        return Protocol == OpenAIProtocol.Responses
            ? ResponsesRequestWriter.Write(request, policy, _options.Store, stream, forCount: false)
            : ChatCompletionsRequestWriter.Write(request, policy, Dialect, stream);
    }

    public ReadOnlyMemory<byte> WriteCount(ChatRequest request) =>
        ResponsesRequestWriter.Write(request, PolicyFor(request.Model), _options.Store, stream: false, forCount: true);

    private string ChatPath => Protocol == OpenAIProtocol.Responses ? "responses" : "chat/completions";

    public string BatchEndpointPath(bool embeddings) => embeddings ? "/v1/embeddings" : Protocol == OpenAIProtocol.Responses ? "/v1/responses" : "/v1/chat/completions";

    internal RequestPolicy PolicyFor(string model, List<ResponseWarning>? warnings = null) => new(Provider, FindSpec(model), warnings);

    // The catalog may know the model by a dated snapshot's family: "gpt-5.5-2026-04-23" falls back to "gpt-5.5".
    private ModelSpec? FindSpec(string model) => FindResolved(model)?.Model;

    private ResolvedModel? FindResolved(string model)
    {
        if (_catalog is null)
        {
            return null;
        }

        string qualified = CatalogScope + ":";
        if (_catalog.TryResolve(qualified + model, out ResolvedModel? resolved) && resolved!.Model is not null)
        {
            return resolved;
        }

        string trimmed = StripSnapshot(model);
        return trimmed != model && _catalog.TryResolve(qualified + trimmed, out resolved) && resolved!.Model is not null ? resolved : null;
    }

    internal static string StripSnapshot(string model)
    {
        // A trailing -YYYY-MM-DD or -YYYYMMDD.
        if (model.Length > 11 && model[^11] == '-' && model[^6] == '-' && model[^3] == '-'
            && model.AsSpan(model.Length - 10, 4).IndexOfAnyExceptInRange('0', '9') < 0
            && model.AsSpan(model.Length - 5, 2).IndexOfAnyExceptInRange('0', '9') < 0
            && model.AsSpan(model.Length - 2, 2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return model[..^11];
        }

        if (model.Length > 9 && model[^9] == '-' && model.AsSpan(model.Length - 8).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return model[..^9];
        }

        return model;
    }

    // ---- HTTP ----

    public async Task<HttpResponseMessage> PostAsync(string path, string? model, ReadOnlyMemory<byte> body, bool stream, CancellationToken cancellationToken)
    {
        await Endpoint.PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await Http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, Endpoint.GetUri(path, model)) { Content = new ReadOnlyMemoryContent(body) };
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                if (stream)
                {
                    message.Headers.Accept.ParseAdd("text/event-stream");
                }

                Endpoint.Authenticate(message);
                return message;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        await Endpoint.PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await Http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Get, Endpoint.GetUri(path, null));
                Endpoint.Authenticate(message);
                return message;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ---- embeddings ----

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> body = WriteEmbeddings(request);
        using HttpResponseMessage response = await PostAsync("embeddings", request.Model, body, stream: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        return ParseEmbeddings(bytes, request.Model);
    }

    public ReadOnlyMemory<byte> WriteEmbeddings(EmbeddingRequest request)
    {
        if (request.Inputs.Count == 0)
        {
            throw new ArgumentException("An embeddings request needs at least one input.", nameof(request));
        }

        RequestPolicy policy = PolicyFor(request.Model);
        using var body = new BodyWriter(request.ExtraParameters);
        Utf8JsonWriter w = body.Writer;
        if (body.Field("model"))
        {
            w.WriteStringValue(request.Model);
        }

        if (body.Field("input"))
        {
            w.WriteStartArray();
            foreach (string s in request.Inputs)
            {
                w.WriteStringValue(s);
            }

            w.WriteEndArray();
        }

        if (request.Dimensions is { } dimensions && body.Field("dimensions"))
        {
            w.WriteNumberValue(dimensions);
        }

        if (request.InputType != EmbeddingInputType.Unspecified)
        {
            policy.Dropped("input type", "the embeddings API has no query/document modes");
        }

        if (body.Field("encoding_format"))
        {
            w.WriteStringValue("float");
        }

        return body.Finish();
    }

    // data[].embedding is read straight into float arrays; the order of "data" is restored by its "index" field.
    public EmbeddingResponse ParseEmbeddings(ReadOnlyMemory<byte> body, string requestedModel)
    {
        var vectors = new List<(int Index, float[] Vector)>();
        string? model = null;
        long promptTokens = 0;
        var reader = new Utf8JsonReader(body.Span, isFinalBlock: true, default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new MalformedResponseException(Provider, "the embeddings response was not a JSON object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("data"u8))
                {
                    reader.Read();
                    ReadEmbeddingData(ref reader, vectors);
                }
                else if (reader.ValueTextEquals("model"u8))
                {
                    reader.Read();
                    model = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("usage"u8))
                {
                    reader.Read();
                    if (JsonNode.Parse(ref reader) is JsonObject usage)
                    {
                        promptTokens = ResponsesDecoder.Long(usage["prompt_tokens"]);
                        if (promptTokens == 0)
                        {
                            promptTokens = ResponsesDecoder.Long(usage["total_tokens"]);
                        }
                    }
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    JsonNode? error = JsonNode.Parse(ref reader);
                    throw OpenAIErrors.FromStreamError(Provider, ResponsesDecoder.Str(error?["code"]), ResponsesDecoder.Str(error?["message"]), ResponsesDecoder.Str(error?["type"]));
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new MalformedResponseException(Provider, "the embeddings response was not valid.");
        }

        vectors.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return new EmbeddingResponse
        {
            Embeddings = [.. vectors.Select(v => (ReadOnlyMemory<float>)v.Vector)],
            Model = model ?? requestedModel,
            Usage = new Usage { Model = model ?? requestedModel, InputTokens = promptTokens },
        };
    }

    private static void ReadEmbeddingData(ref Utf8JsonReader reader, List<(int Index, float[] Vector)> vectors)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        int position = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            int index = position++;
            float[] vector = [];
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("index"u8))
                {
                    reader.Read();
                    index = reader.GetInt32();
                }
                else if (reader.ValueTextEquals("embedding"u8))
                {
                    reader.Read();
                    var values = new List<float>(1536);
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            values.Add(reader.GetSingle());
                        }
                    }
                    else
                    {
                        // Vectors are requested as float arrays; any other shape (base64, say) is not understood here.
                        throw new FormatException("an embedding was not an array of numbers.");
                    }

                    vector = [.. values];
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            vectors.Add((index, vector));
        }
    }
}
