using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Telemetry;

namespace OneLlm;

public sealed partial class Runtime
{
    /// <summary>
    /// Asks a model one question. The call runs as an instance: it is saved in <see cref="Store"/>, reported to <see cref="Sinks"/> and
    /// telemetry, priced from the catalog and checked against the spend caps.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <param name="cancellationToken">Cancels the call; the instance is then kept as <c>stopped</c> with <c>cancelled: true</c>.</param>
    /// <returns>The answer, with usage, cost and the instance.</returns>
    /// <exception cref="ModelNotFoundException">The catalog does not know the model.</exception>
    /// <exception cref="AuthException">The provider's API key is missing or refused.</exception>
    /// <exception cref="BudgetExceededException">The call could pass a spend cap; nothing was sent.</exception>
    /// <exception cref="ProviderException">The provider failed after retries.</exception>
    /// <exception cref="RunCancelledException">The token was cancelled.</exception>
    public async Task<AskResult> AskAsync(AskRequest request, CancellationToken cancellationToken = default)
    {
        (AskResult result, _) = await AskCoreAsync<object>(request, null, null, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Asks a model for JSON that fits a type, using the provider's native structured output, and reads the answer into the type. The
    /// schema comes from the type's serialization metadata.
    /// </summary>
    /// <typeparam name="T">The result type. Must serialize as a JSON object.</typeparam>
    /// <param name="request">The question.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/>, from a source-generated context (trim and AOT safe).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer with the parsed value.</returns>
    /// <exception cref="StructuredOutputException">The answer is not valid JSON for the type.</exception>
    /// <exception cref="RefusedException">The model refused to answer.</exception>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not a JSON object type.</exception>
    public async Task<AskResult<T>> AskAsync<T>(AskRequest request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ResponseFormat format = StructuredOutput.FormatFor(typeInfo);
        (AskResult result, T? value) = await AskCoreAsync<T>(
            request,
            format,
            message =>
            {
                if (message.Assistant.OfType<RefusalPart>().FirstOrDefault() is { } refusal)
                {
                    throw new RefusedException($"The model refused to answer: {refusal.Text}", refusal.Text);
                }

                return StructuredOutput.Parse(message.GetAssistantText(), typeInfo);
            },
            cancellationToken).ConfigureAwait(false);
        return new AskResult<T>(result, value!);
    }

    /// <summary>
    /// Asks a model a question and returns the answer as events as they arrive. Enumerate the stream to the end, then read
    /// <see cref="StreamingAnswer.Result"/>; it is built from the same events as a non-streaming answer, so the two are equal.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <returns>The stream. Nothing is sent until it is enumerated.</returns>
    public StreamingAnswer StreamAsync(AskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new StreamingAnswer(this, async ct =>
        {
            Turn turn = await BeginAskAsync(request, null, ct).ConfigureAwait(false);
            return turn;
        }, InstanceStates.Stopped);
    }

    /// <summary>Embeds texts. Embeddings are a stateless call: no instance is made, but the call is recorded, priced and counted against the runtime's spend cap.</summary>
    /// <param name="model">An embedding model in the catalog.</param>
    /// <param name="inputs">The texts, one vector each.</param>
    /// <param name="dimensions">A shorter vector length, for models that support it; null for the model's default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The vectors in input order, with usage and cost.</returns>
    /// <exception cref="CapabilityMissingException">The model's provider does not embed.</exception>
    /// <exception cref="BudgetExceededException">The runtime's spend cap has already been reached.</exception>
    public async Task<EmbedResult> EmbedAsync(string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ResolvedModel resolved = Resolve(model);
        IProvider provider = Providers.Get(resolved, TimeProvider);
        if (provider is not IEmbeddingProvider embedder)
        {
            throw new CapabilityMissingException($"Embeddings on provider '{provider.Name}'", $"an embedding provider for '{resolved.Id}'");
        }

        if (SpendCap is { } cap && Spent >= cap)
        {
            throw new BudgetExceededException(BudgetKind.SpendCap, $"The runtime has spent ${Spent} of its ${cap} cap.", cap, null, Spent);
        }

        using System.Diagnostics.Activity? activity = GenAiTelemetry.StartOperation("embeddings", provider.Name, resolved.Name);
        long started = TimeProvider.GetTimestamp();
        EmbeddingResponse response;
        try
        {
            response = await embedder.EmbedAsync(new EmbeddingRequest { Model = resolved.Name, Inputs = inputs, Dimensions = dimensions }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GenAiTelemetry.Fail(activity, "embeddings", provider.Name, resolved.Name, ex, TimeProvider.GetElapsedTime(started));
            throw;
        }

        DateTimeOffset now = TimeProvider.GetUtcNow();
        Usage? usage = response.Usage;
        Cost cost = Cost.Zero;
        var warnings = new List<ResponseWarning>(response.Warnings);
        if (usage is not null)
        {
            var notes = new List<string>();
            Cost? priced = CostCalculator.Price(resolved, usage, PriceContextFor(new ChatRequest()) with { Today = DateOnly.FromDateTime(now.UtcDateTime) }, notes);
            warnings.AddRange(notes.Select(static n => new ResponseWarning("pricing", n)));
            if (priced is null)
            {
                warnings.Add(new ResponseWarning("pricing", $"The catalog has no price for '{resolved.Id}', so this call's cost is not known."));
            }
            else
            {
                cost = priced;
                usage = CostCalculator.WithCost(usage, priced);
            }
        }

        RuntimeGuard.Record(cost.Total);
        TimeSpan elapsed = TimeProvider.GetElapsedTime(started);
        GenAiTelemetry.Complete(activity, "embeddings", provider.Name, resolved.Name, usage, elapsed);
        await WriteRecordAsync(
            new InferenceRecord { InstanceId = "", Time = now, Provider = provider.Name, Operation = "embeddings", Usage = usage, Duration = elapsed },
            cancellationToken).ConfigureAwait(false);
        return new EmbedResult(response.Embeddings, usage, cost, resolved.Id, warnings);
    }

    /// <summary>Starts a multi-turn chat. The chat is one instance; each turn adds to it and saves it.</summary>
    /// <param name="defaults">The model, system prompt and sampling settings every turn uses. <see cref="AskRequest.Prompt"/> is ignored.</param>
    /// <returns>The chat.</returns>
    /// <exception cref="ModelNotFoundException">The catalog does not know the model.</exception>
    public ChatSession Chat(AskRequest defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ResolvedModel model = Resolve(defaults.Model);
        return new ChatSession(this, defaults, model);
    }

    internal readonly record struct Turn(RunScope Scope, ResolvedModel Model, IChatProvider Provider, ChatRequest Request);

    internal async Task<Turn> BeginAskAsync(AskRequest request, ResponseFormat? format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Prompt) && request.Attachments.Count == 0)
        {
            throw new ArgumentException("An ask needs a prompt or an attachment.", nameof(request));
        }

        ResolvedModel model = Resolve(request.Model);
        IChatProvider provider = GetChatProvider(model);
        Instance instance = NewAskInstance(request);

        return await BeginTurnAsync(instance, request, model, provider, UserMessage(request.Prompt, request.Attachments), format, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the instance's lease, adds the user's message and builds the request for the next answer.</summary>
    internal async Task<Turn> BeginTurnAsync(Instance instance, AskRequest settings, ResolvedModel model, IChatProvider provider, Message user, ResponseFormat? format, CancellationToken cancellationToken)
    {
        RunScope scope = await RunScope.StartAsync(this, instance, cancellationToken).ConfigureAwait(false);
        try
        {
            await scope.AppendAsync(user, cancellationToken).ConfigureAwait(false);
            return new Turn(scope, model, provider, BuildChat(instance, model, settings, format));
        }
        catch (Exception ex)
        {
            Exception e = await scope.FailAsync(ex, null, cancellationToken).ConfigureAwait(false);
            await scope.DisposeAsync().ConfigureAwait(false);
            Rethrow(e, ex);
            throw;
        }
    }

    internal Instance NewAskInstance(AskRequest request)
    {
        var definition = new AgentDefinition
        {
            Name = request.Name ?? "ask",
            Model = request.Model,
            SystemPrompt = request.System ?? "",
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            ServiceTier = request.ServiceTier,
        };
        Instance instance = Instance.FromDefinition(definition, NewId("i"));
        instance.RenderedSystemPrompt = request.System;
        instance.Messages.AddRange(request.History);
        return instance;
    }

    internal Message UserMessage(string? prompt, IReadOnlyList<Part> attachments)
    {
        var parts = new List<Part>(attachments.Count + 1);
        if (!string.IsNullOrEmpty(prompt))
        {
            parts.Add(new TextPart(prompt));
        }

        parts.AddRange(attachments);
        return new Message { Id = NewId("m"), User = parts, DateTime = TimeProvider.GetUtcNow() };
    }

    internal static ChatRequest BuildChat(Instance instance, ResolvedModel model, AskRequest settings, ResponseFormat? format, IReadOnlyList<ToolDefinition>? tools = null) => new()
    {
        Model = model.Name,
        Tools = tools ?? [],
        Messages = [.. instance.Messages],
        // The rendered prompt is what the model sees; the definition keeps the template.
        System = string.IsNullOrEmpty(instance.RenderedSystemPrompt) ? null : instance.RenderedSystemPrompt,
        Temperature = settings.Temperature,
        MaxOutputTokens = settings.MaxOutputTokens,
        Reasoning = settings.Reasoning,
        ServiceTier = settings.ServiceTier,
        ExtraParameters = settings.ExtraParameters,
        ResponseFormat = format,
    };

    /// <summary>Sends one prepared turn and finishes the run. Any failure settles the instance and is rethrown.</summary>
    internal async Task<(AskResult Result, T? Value)> RunTurnAsync<T>(Turn turn, Func<Message, T>? parse, string endState, CancellationToken cancellationToken)
    {
        await using RunScope scope = turn.Scope;
        try
        {
            AskResult result = await SendTurnAsync(turn, cancellationToken).ConfigureAwait(false);
            T? value = default;
            if (parse is not null)
            {
                value = parse(result.Message);
            }

            await scope.CompleteAsync(endState, cancellationToken).ConfigureAwait(false);
            return (result, value);
        }
        catch (Exception ex)
        {
            Exception e = await scope.FailAsync(ex, null, cancellationToken).ConfigureAwait(false);
            Rethrow(e, ex);
            throw;
        }
    }

    /// <summary>Sends a prepared turn and appends the answer to the instance. Does not settle the run: the caller does.</summary>
    internal async Task<AskResult> SendTurnAsync(Turn turn, CancellationToken cancellationToken)
    {
        InferenceCall call = InferenceCall.Begin(this, turn.Scope, turn.Model, turn.Provider, turn.Request);
        ChatResponse response;
        try
        {
            response = await turn.Provider.SendAsync(call.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            call.Fail(ex);
            throw;
        }

        (Message stamped, Cost cost, FinishReason finish, IReadOnlyList<ResponseWarning> warnings) = await call
            .CompleteAsync(response.Message, response.Finish, response.FinishReasonRaw, response.Id, cancellationToken, response.Warnings).ConfigureAwait(false);
        await turn.Scope.AppendAsync(stamped, cancellationToken).ConfigureAwait(false);
        return new AskResult(stamped.GetAssistantText(), stamped, turn.Scope.Instance, turn.Model.Id, cost, finish, warnings);
    }

    private async Task<(AskResult, T?)> AskCoreAsync<T>(AskRequest request, ResponseFormat? format, Func<Message, T>? parse, CancellationToken cancellationToken)
    {
        Turn turn = await BeginAskAsync(request, format, cancellationToken).ConfigureAwait(false);
        return await RunTurnAsync(turn, parse, InstanceStates.Stopped, cancellationToken).ConfigureAwait(false);
    }

    internal static void Rethrow(Exception toThrow, Exception original)
    {
        if (ReferenceEquals(toThrow, original))
        {
            ExceptionDispatchInfo.Capture(original).Throw();
        }

        throw toThrow;
    }
}
