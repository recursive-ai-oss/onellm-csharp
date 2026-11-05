using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>Which OpenAI chat API an <see cref="OpenAIProvider"/> speaks.</summary>
public enum OpenAIApi
{
    /// <summary>The Responses API (<c>/v1/responses</c>): the primary surface, with hosted tools, reasoning items and structured output.</summary>
    Responses = 0,

    /// <summary>The Chat Completions API (<c>/v1/chat/completions</c>): function tools, JSON schema output and streamed usage, without hosted tools or reasoning items.</summary>
    ChatCompletions = 1,
}

/// <summary>
/// The OpenAI API: chat (Responses by default, Chat Completions on request), streaming, function, custom and hosted tools, structured output,
/// reasoning, prompt caching, input token counting, embeddings and the Batch API, plus the media endpoints
/// (images, speech, transcription and moderation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Statelessness.</b> Requests carry <c>store: false</c> unless <see cref="OpenAIOptions.Store"/> is set, and a reasoning model's request asks for
/// <c>reasoning.encrypted_content</c>, so the reasoning items in an answer can be sent back exactly on the next turn without OpenAI keeping the response.
/// </para>
/// <para>
/// <b>Usage.</b> <see cref="Messages.Usage.InputTokens"/> holds only uncached input: the API's <c>input_tokens</c> counts cached and cache-write tokens
/// too, so they are subtracted and reported in their own fields. <see cref="Messages.Usage.OutputTokens"/> includes reasoning tokens, which are also
/// reported, for information, in the <c>reasoningTokens</c> counter. Other counters: <c>cacheCreation30mInputTokens</c> and <c>cacheCreation12hInputTokens</c>
/// (the split of the cache-write tokens by lifetime; they are part of <see cref="Messages.Usage.CacheCreationInputTokens"/>, not additional), one
/// <c>toolCalls.&lt;tool&gt;</c> per hosted tool used (<c>web_search</c>, <c>file_search</c>, <c>code_interpreter</c>, <c>image_generation</c>, <c>mcp</c>), and
/// <c>imageGenInputTokens</c> and <c>imageGenOutputTokens</c> for the image generation tool.
/// </para>
/// <para>
/// <b>Custom tools</b> (free-text tools) are declared as a <see cref="HostedToolDefinition"/> of type <c>custom</c> whose options carry <c>name</c>,
/// <c>description</c> and optionally <c>format</c>. A call to one arrives as a <see cref="Messages.ToolCall"/> with the text in the <c>input</c> input.
/// Clients are thread-safe.
/// </para>
/// </remarks>
public sealed partial class OpenAIProvider : IChatProvider, IEmbeddingProvider, ITokenCountingProvider, IBatchProvider
{
    private readonly OpenAIWire _wire;
    private readonly OpenAIBatches _batches;
    private readonly OpenAIMedia _media;

    /// <summary>Creates a provider for the OpenAI API.</summary>
    /// <param name="options">Key, base URL, HTTP client and the other settings.</param>
    /// <param name="time">The clock used for retry delays and timeouts.</param>
    /// <param name="api">The chat API to speak. Defaults to the Responses API.</param>
    public OpenAIProvider(OpenAIOptions options, TimeProvider time, OpenAIApi api = OpenAIApi.Responses)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        OpenAIEndpoint endpoint = options.Endpoint ?? OpenAIEndpoint.Direct(options.ApiKey, options.BaseUrl);
        _wire = new OpenAIWire(
            "openai",
            "openai",
            api == OpenAIApi.Responses ? OpenAIProtocol.Responses : OpenAIProtocol.ChatCompletions,
            ChatDialect.OpenAI,
            endpoint,
            options,
            time);
        _batches = new OpenAIBatches(_wire);
        _media = new OpenAIMedia(_wire, options.Catalog, time);
    }

    /// <inheritdoc />
    public string Name => "openai";

    /// <inheritdoc />
    public string Deployment => _wire.Endpoint.Deployment;

    /// <inheritdoc />
    public Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _wire.SendAsync(request, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _wire.StreamAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
        _wire.EmbedAsync(request, cancellationToken);

    /// <summary>Counts the input tokens of a request with <c>/v1/responses/input_tokens</c>, which is exact for the model named.</summary>
    /// <param name="request">The request. Sampling, caching and output limits are not part of a count and are left out.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count, with tier <see cref="TokenCountTier.Provider"/>.</returns>
    /// <exception cref="ProviderException">The API failed.</exception>
    public async Task<TokenCount> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> body = _wire.WriteCount(request);
        using HttpResponseMessage response = await _wire.PostAsync("responses/input_tokens", request.Model, body, stream: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _wire.Http.ReadBodyAsync(response, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("input_tokens", out System.Text.Json.JsonElement n) && n.TryGetInt64(out long tokens))
            {
                return new TokenCount(tokens, TokenCountTier.Provider);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Falls through to the malformed-response error.
        }

        throw new MalformedResponseException("openai", "the token count response had no input_tokens.");
    }

    /// <inheritdoc />
    public Task<BatchJob> SubmitBatchAsync(BatchRequest request, CancellationToken cancellationToken = default) =>
        _batches.SubmitAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<BatchJob> GetBatchAsync(string batchId, CancellationToken cancellationToken = default) =>
        _batches.GetAsync(batchId, cancellationToken);

    /// <inheritdoc />
    public Task<BatchJob> CancelBatchAsync(string batchId, CancellationToken cancellationToken = default) =>
        _batches.CancelAsync(batchId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<BatchResultItem> GetBatchResultsAsync(string batchId, CancellationToken cancellationToken = default) =>
        _batches.ResultsAsync(batchId, cancellationToken);
}
