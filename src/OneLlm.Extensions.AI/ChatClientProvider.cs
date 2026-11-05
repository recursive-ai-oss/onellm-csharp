using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;
using OneLlm.Providers;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using OneChatResponse = OneLlm.Providers.ChatResponse;

namespace OneLlm.Extensions.AI;

/// <summary>
/// Any Microsoft.Extensions.AI <see cref="IChatClient"/> (and optionally an embedding generator) as a OneLLM provider. Register it under the
/// provider name a catalog entry uses; the catalog entry supplies the model's prices and capabilities, so calls through <c>Llm</c> and the
/// <c>Runtime</c> are priced like any other provider's.
/// </summary>
/// <remarks>
/// Register with <see cref="RuntimeExtensions.AddChatClient(ProviderRegistry, string, IChatClient, IEmbeddingGenerator{string, Embedding{float}}?)"/>.
/// Usage the client reports becomes OneLLM <c>Usage</c>: a client's input count is the whole input, and its cached count is the cache-read part of it. The provider does not own the client.
/// </remarks>
public sealed class ChatClientProvider : IChatProvider, IEmbeddingProvider
{
    private readonly IChatClient _client;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    /// <summary>Wraps <paramref name="client"/> as a provider named <paramref name="name"/>.</summary>
    /// <param name="name">The provider name; it must match the provider in the catalog entry that prices the model.</param>
    /// <param name="client">The chat client. Not owned.</param>
    /// <param name="embeddings">An embedding generator to serve <see cref="IEmbeddingProvider"/> requests, or <see langword="null"/> when embeddings are not offered.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    public ChatClientProvider(string name, IChatClient client, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _embeddings = embeddings;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Deployment => "extensions-ai";

    /// <inheritdoc />
    public async Task<OneChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (List<ChatMessage> messages, ChatOptions options) = ChatMapping.ToMeai(request);
        MeaiChatResponse response = await _client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        string model = response.ModelId ?? request.Model;
        (FinishReason finish, string? raw) = ChatMapping.FromFinish(response.FinishReason);
        Message turn = ContentMapper.ToTurn(response.Messages);
        Usage? usage = response.Usage is { } details ? ChatMapping.FromUsage(details, model) : null;
        if (finish == FinishReason.Unknown && turn.ToolCalls.Count > 0)
        {
            finish = FinishReason.ToolCalls;
        }

        return new OneChatResponse
        {
            Message = new Message { Id = turn.Id, Assistant = turn.Assistant, ToolCalls = turn.ToolCalls, DateTime = response.CreatedAt ?? turn.DateTime, Usage = usage ?? new Usage { Model = model } },
            Id = response.ResponseId,
            Model = model,
            Finish = finish,
            FinishReasonRaw = raw,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (List<ChatMessage> messages, ChatOptions options) = ChatMapping.ToMeai(request);
        var state = new StreamState();
        string model = request.Model;
        bool started = false;
        ChatFinishReason? finish = null;
        UsageDetails? usage = null;
        bool sawCalls = false;

        await foreach (ChatResponseUpdate update in _client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            model = update.ModelId ?? model;
            if (!started)
            {
                started = true;
                yield return StreamEvent.MessageStart(update.ResponseId, model);
            }

            finish = update.FinishReason ?? finish;
            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent u)
                {
                    usage = usage is null ? u.Details : Merge(usage, u.Details);
                    continue;
                }

                foreach (StreamEvent e in state.Events(content))
                {
                    sawCalls |= e.Kind == StreamEventKind.ToolCallStart;
                    yield return e;
                }
            }
        }

        if (!started)
        {
            yield return StreamEvent.MessageStart(null, model);
        }

        if (usage is not null)
        {
            yield return StreamEvent.UsageReported(ChatMapping.FromUsage(usage, model));
        }

        (FinishReason reason, string? raw) = ChatMapping.FromFinish(finish);
        yield return StreamEvent.Done(reason == FinishReason.Unknown && sawCalls ? FinishReason.ToolCalls : reason, raw);
    }

    /// <inheritdoc />
    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_embeddings is null)
        {
            throw new CapabilityMissingException($"Embeddings on provider '{Name}'", "an IEmbeddingGenerator<string, Embedding<float>> passed to AddChatClient");
        }

        GeneratedEmbeddings<Embedding<float>> result = await _embeddings.GenerateAsync(
            request.Inputs,
            new EmbeddingGenerationOptions { ModelId = request.Model.Length > 0 ? request.Model : null, Dimensions = request.Dimensions },
            cancellationToken).ConfigureAwait(false);
        Usage? usage = result.Usage is { } details ? ChatMapping.FromUsage(details, request.Model) : null;
        return new EmbeddingResponse
        {
            Embeddings = [.. result.Select(e => e.Vector)],
            Model = result.FirstOrDefault()?.ModelId ?? request.Model,
            Usage = usage,
        };
    }

    private static UsageDetails Merge(UsageDetails first, UsageDetails later)
    {
        // Updates may repeat running totals or report parts; a later non-empty count replaces an earlier one, which is the rule the SSE readers use.
        return new UsageDetails
        {
            InputTokenCount = later.InputTokenCount ?? first.InputTokenCount,
            OutputTokenCount = later.OutputTokenCount ?? first.OutputTokenCount,
            TotalTokenCount = later.TotalTokenCount ?? first.TotalTokenCount,
            CachedInputTokenCount = later.CachedInputTokenCount ?? first.CachedInputTokenCount,
            ReasoningTokenCount = later.ReasoningTokenCount ?? first.ReasoningTokenCount,
            AdditionalCounts = later.AdditionalCounts ?? first.AdditionalCounts,
        };
    }

    /// <summary>Turns streamed content into OneLLM stream events with consistent block indexes: consecutive text joins one block, anything else starts the next.</summary>
    private sealed class StreamState
    {
        private int _next;
        private int _textBlock = -1;
        private int _reasoningBlock = -1;

        public IEnumerable<StreamEvent> Events(AIContent content)
        {
            switch (content)
            {
                case TextContent text when text.AdditionalProperties?.TryGetValue(OneLlmProperties.Refusal, out object? flag) == true && flag is true:
                    _textBlock = -1;
                    _reasoningBlock = -1;
                    yield return StreamEvent.RefusalDelta(_next++, Encoding.UTF8.GetBytes(text.Text ?? ""));
                    break;
                case TextContent text:
                    if (text.Annotations is { Count: > 0 } && (text.Text is null || text.Text.Length == 0))
                    {
                        if (_textBlock < 0)
                        {
                            _textBlock = _next++;
                        }

                        foreach (OneLlm.Messages.Annotation a in ((ContentMapper.ToPart(text) as TextPart)?.Annotations ?? []))
                        {
                            yield return StreamEvent.AnnotationAdded(_textBlock, a);
                        }

                        break;
                    }

                    _reasoningBlock = -1;
                    if (_textBlock < 0)
                    {
                        _textBlock = _next++;
                    }

                    if (!string.IsNullOrEmpty(text.Text))
                    {
                        yield return StreamEvent.TextDelta(_textBlock, Encoding.UTF8.GetBytes(text.Text));
                    }

                    if (text.Annotations is { Count: > 0 })
                    {
                        foreach (OneLlm.Messages.Annotation a in ((ContentMapper.ToPart(text) as TextPart)?.Annotations ?? []))
                        {
                            yield return StreamEvent.AnnotationAdded(_textBlock, a);
                        }
                    }

                    break;
                case TextReasoningContent thought:
                    _textBlock = -1;
                    if (_reasoningBlock < 0)
                    {
                        _reasoningBlock = _next++;
                    }

                    if (!string.IsNullOrEmpty(thought.Text))
                    {
                        yield return StreamEvent.ReasoningDelta(_reasoningBlock, Encoding.UTF8.GetBytes(thought.Text));
                    }

                    if (thought.ProtectedData is { Length: > 0 } || thought.AdditionalProperties?.ContainsKey(OneLlmProperties.Provider) == true)
                    {
                        ReasoningPart sealedPart = (ReasoningPart)ContentMapper.ToPart(thought);
                        yield return StreamEvent.ReasoningEnd(_reasoningBlock, sealedPart.Provider, sealedPart.Data);
                        _reasoningBlock = -1;
                    }

                    break;
                case FunctionCallContent call:
                    _textBlock = -1;
                    _reasoningBlock = -1;
                    int index = _next++;
                    yield return StreamEvent.ToolCallStart(index, call.CallId, call.Name);
                    string arguments = call.AdditionalProperties?.TryGetValue(OneLlmProperties.InvalidInputs, out object? raw) == true && raw is string bad
                        ? bad
                        : JsonBridge.FromArguments(call.Arguments).ToJsonString();
                    yield return StreamEvent.ToolCallArgumentsDelta(index, Encoding.UTF8.GetBytes(arguments));
                    yield return StreamEvent.ToolCallEnd(index);
                    break;
                case FunctionResultContent:
                    // A result in a response comes from a client that ran the function itself; the OneLLM stream has no event for it.
                    break;
                default:
                    _textBlock = -1;
                    _reasoningBlock = -1;
                    yield return StreamEvent.PartAdded(_next++, ContentMapper.ToPart(content));
                    break;
            }
        }
    }
}
