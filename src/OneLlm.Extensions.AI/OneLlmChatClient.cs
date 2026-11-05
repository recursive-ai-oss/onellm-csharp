using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using OneChatResponse = OneLlm.Providers.ChatResponse;

namespace OneLlm.Extensions.AI;

/// <summary>
/// A OneLLM chat model as a Microsoft.Extensions.AI <see cref="IChatClient"/>: requests and streaming through any OneLLM chat provider, with
/// usage in <c>UsageDetails</c> and the call's cost in <c>AdditionalProperties</c> under the keys in <see cref="OneLlmProperties"/>.
/// </summary>
/// <remarks>
/// Create one with <see cref="RuntimeExtensions.AsChatClient(Runtime, string)"/> to resolve the model through a runtime's catalog, which prices
/// every call and strips parameters the model does not take. The constructors over a bare provider send the model name as given and price nothing
/// unless a resolved catalog entry is passed. The client is thread-safe and does not own the provider: <see cref="Dispose"/> does nothing.
/// Function invocation is not done here; wrap the client with Microsoft.Extensions.AI's function-invoking middleware, or use a OneLLM agent.
/// </remarks>
public sealed class OneLlmChatClient : IChatClient
{
    private readonly IChatProvider _provider;
    private readonly ResolvedModel? _resolved;
    private readonly string _model;
    private readonly TimeProvider _time;
    private readonly Runtime? _runtime;

    /// <summary>A client over <paramref name="provider"/> that sends <paramref name="model"/> as the model name and prices nothing.</summary>
    /// <param name="provider">The chat provider. Not owned.</param>
    /// <param name="model">The model name the provider expects; <c>ChatOptions.ModelId</c> overrides it per request.</param>
    /// <param name="time">The clock used to date prices and stamp nothing else.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmChatClient(IChatProvider provider, string model, TimeProvider time)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A client over <paramref name="provider"/> for a catalog entry: requests are adjusted to the model and every call is priced.</summary>
    /// <param name="provider">The chat provider that serves <paramref name="model"/>. Not owned.</param>
    /// <param name="model">The resolved catalog entry.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmChatClient(IChatProvider provider, ResolvedModel model, TimeProvider time)
        : this(provider, model?.Name ?? throw new ArgumentNullException(nameof(model)), time) => _resolved = model;

    private OneLlmChatClient(Runtime runtime, ResolvedModel model, IChatProvider provider)
        : this(provider, model, runtime.TimeProvider) => _runtime = runtime;

    internal static OneLlmChatClient Create(Runtime runtime, string model)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ResolvedModel resolved = runtime.Resolve(model);
        return new OneLlmChatClient(runtime, resolved, runtime.GetChatProvider(resolved));
    }

    /// <inheritdoc />
    public async Task<MeaiChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        (IChatProvider provider, ResolvedModel? resolved, ChatRequest request, List<string> warnings) = Prepare(messages, options);
        OneChatResponse response = await provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Billing.AddWarnings(warnings, response.Warnings);
        return ToResponse(response.Message, response.Id, response.Model ?? request.Model, response.Finish, response.FinishReasonRaw, resolved, request, warnings);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        (IChatProvider provider, ResolvedModel? resolved, ChatRequest request, List<string> warnings) = Prepare(messages, options);
        var acc = new MessageAccumulator();
        var calls = new Dictionary<int, PendingCall>();
        string? responseId = null;
        string? modelId = request.Model;

        await foreach (StreamEvent e in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            acc.Add(e);
            if (e.Kind == StreamEventKind.MessageStart)
            {
                responseId = e.Id ?? responseId;
                modelId = e.Name ?? modelId;
                continue;
            }

            if (e.Kind == StreamEventKind.Done)
            {
                continue;
            }

            AIContent? content = StreamContent(e, calls);
            if (content is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { ResponseId = responseId, ModelId = modelId };
            }
        }

        Message message = acc.ToMessage();
        (Usage? usage, Cost? cost) = Billing.Price(resolved, message.Usage, request.ServiceTier, _time, warnings);
        var final = new ChatResponseUpdate(ChatRole.Assistant, [])
        {
            ResponseId = responseId ?? acc.ResponseId,
            ModelId = modelId,
            FinishReason = ChatMapping.ToFinish(acc.Finish, acc.FinishReasonRaw),
        };
        var details = new UsageContent(usage is null ? new UsageDetails() : ChatMapping.ToUsage(usage));
        Billing.Annotate(details.AdditionalProperties ??= [], cost, warnings);
        if (usage is not null || cost is not null || warnings.Count > 0)
        {
            final.Contents.Add(details);
        }

        yield return final;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(_provider.Name, null, _model);
        }

        if (serviceType == typeof(IChatProvider))
        {
            return _provider;
        }

        if (serviceType == typeof(ResolvedModel))
        {
            return _resolved;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private (IChatProvider Provider, ResolvedModel? Resolved, ChatRequest Request, List<string> Warnings) Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        IChatProvider provider = _provider;
        ResolvedModel? resolved = _resolved;
        string model = _model;
        if (options?.ModelId is { Length: > 0 } requested && requested != _model && requested != _resolved?.Id)
        {
            if (_runtime is not null)
            {
                resolved = _runtime.Resolve(requested);
                provider = _runtime.GetChatProvider(resolved);
                model = resolved.Name;
            }
            else
            {
                resolved = null;
                model = requested;
            }
        }

        var warnings = new List<string>();
        ChatRequest request = ChatMapping.ToRequest(messages, options, model, warnings);
        if (resolved is not null)
        {
            AdjustedRequest adjusted = RequestAdjuster.Adjust(resolved, request);
            request = adjusted.Request;
            Billing.AddWarnings(warnings, adjusted.Warnings);
        }

        return (provider, resolved, request, warnings);
    }

    private MeaiChatResponse ToResponse(Message message, string? id, string model, FinishReason finish, string? raw, ResolvedModel? resolved, ChatRequest request, List<string> warnings)
    {
        (Usage? usage, Cost? cost) = Billing.Price(resolved, message.Usage, request.ServiceTier, _time, warnings);
        var chat = new List<ChatMessage>();
        ContentMapper.AppendMessages(message, chat);
        if (chat.Count == 0)
        {
            chat.Add(new ChatMessage(ChatRole.Assistant, []));
        }

        var response = new MeaiChatResponse(chat)
        {
            ResponseId = id,
            ModelId = model,
            FinishReason = ChatMapping.ToFinish(finish, raw),
            CreatedAt = message.DateTime,
            Usage = usage is null ? null : ChatMapping.ToUsage(usage),
        };
        Billing.Annotate(response.AdditionalProperties ??= [], cost, warnings);
        return response;
    }

    private sealed class PendingCall(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>The content one stream event adds to the response, or <see langword="null"/> when the event only builds up state (argument fragments, usage).</summary>
    private static AIContent? StreamContent(in StreamEvent e, Dictionary<int, PendingCall> calls)
    {
        switch (e.Kind)
        {
            case StreamEventKind.TextDelta:
                return new TextContent(e.GetText());
            case StreamEventKind.RefusalDelta:
                var refusal = new TextContent(e.GetText());
                (refusal.AdditionalProperties ??= [])[OneLlmProperties.Refusal] = true;
                return refusal;
            case StreamEventKind.ReasoningDelta:
                return new TextReasoningContent(e.GetText());
            case StreamEventKind.ReasoningEnd when e.Data is not null:
                var sealedThought = new TextReasoningContent("") { ProtectedData = e.Data.ToJsonString() };
                (sealedThought.AdditionalProperties ??= [])[OneLlmProperties.Provider] = e.Name ?? "";
                return sealedThought;
            case StreamEventKind.ToolCallStart:
                calls[e.Index] = new PendingCall(e.Id ?? "", e.Name ?? "");
                return null;
            case StreamEventKind.ToolCallArgumentsDelta:
                if (calls.TryGetValue(e.Index, out PendingCall? open))
                {
                    open.Arguments.Append(e.GetText());
                }

                return null;
            case StreamEventKind.ToolCallEnd:
                if (!calls.Remove(e.Index, out PendingCall? done))
                {
                    return null;
                }

                return FinishedCall(done);
            case StreamEventKind.HostedToolCallEnd when e.Part is { } hosted:
                return new OneLlmPartContent(hosted);
            case StreamEventKind.PartAdded when e.Part is { } part:
                return ContentMapper.ToContent(part);
            case StreamEventKind.Annotation when e.Annotation is { } annotation:
                var cited = new TextContent("");
                cited.Annotations = [new CitationAnnotation { Title = annotation.Title, Snippet = annotation.CitedText, FileId = annotation.SourceId, RawRepresentation = annotation, Url = Uri.TryCreate(annotation.Url, UriKind.Absolute, out Uri? u) ? u : null }];
                return cited;
            default:
                return null;
        }
    }

    private static FunctionCallContent FinishedCall(PendingCall call)
    {
        string text = call.Arguments.ToString();
        JsonObject? inputs = null;
        bool invalid = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                inputs = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                inputs = null;
            }

            invalid = inputs is null;
        }

        var content = new FunctionCallContent(call.Id, call.Name, invalid ? null : JsonBridge.ToArguments(inputs ?? []));
        if (invalid)
        {
            (content.AdditionalProperties ??= [])[OneLlmProperties.InvalidInputs] = text;
        }

        return content;
    }
}
