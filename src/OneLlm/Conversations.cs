using OneLlm.Hooks;
using OneLlm.Instances;
using OneLlm.Runners;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm;

/// <summary>
/// An answer arriving as events. Enumerate it once, to the end; then <see cref="Result"/> holds the finished answer. Cancelling
/// (the token passed to <c>WithCancellation</c>, or leaving the loop early) keeps what arrived in the instance, which ends
/// <c>stopped</c> with <c>cancelled: true</c>; <see cref="Result"/> is then cancelled.
/// </summary>
/// <remarks>A <see cref="StreamEvent"/>'s <c>Utf8</c> is valid only until the next event is requested; copy it if you keep it.</remarks>
public sealed class StreamingAnswer : IAsyncEnumerable<StreamEvent>
{
    private readonly Runtime _runtime;
    private readonly Func<CancellationToken, Task<Runtime.Turn>> _open;
    private readonly string _endState;
    private readonly Action? _release;
    private readonly TaskCompletionSource<AskResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal StreamingAnswer(Runtime runtime, Func<CancellationToken, Task<Runtime.Turn>> open, string endState, Action? release = null)
    {
        _runtime = runtime;
        _open = open;
        _endState = endState;
        _release = release;
    }

    /// <summary>The finished answer. Completes when the enumeration reaches the end; faults or is cancelled if the stream failed. Never completes if the stream is not enumerated.</summary>
    public Task<AskResult> Result => _result.Task;

    /// <summary>The text deltas only, as strings. Enumerating this enumerates the stream.</summary>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The pieces of the answer's text, in order.</returns>
    public async IAsyncEnumerable<string> TextAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (StreamEvent e in this.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (e.Kind == StreamEventKind.TextDelta)
            {
                yield return e.GetText();
            }
        }
    }

    /// <summary>Starts the stream. It can be enumerated once.</summary>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The enumerator.</returns>
    /// <exception cref="InvalidOperationException">The stream was already enumerated.</exception>
    public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("An StreamingAnswer can be enumerated only once.");
        }

        return Iterate(cancellationToken);
    }

    private async IAsyncEnumerator<StreamEvent> Iterate(CancellationToken cancellationToken)
    {
        Runtime.Turn turn;
        try
        {
            turn = await _open(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The opener releases anything it took when it fails.
            _result.TrySetException(ex);
            throw;
        }

        RunScope scope = turn.Scope;
        var accumulator = new MessageAccumulator();
        InferenceCall? call = null;
        IAsyncEnumerator<StreamEvent>? events = null;
        Exception? failure = null;
        Message? given = null;
        HookContext? streamContext = null;
        bool finished = false;
        try
        {
            try
            {
                (ChatRequest request, given) = await scope.BeforeInferenceAsync(turn.Request, cancellationToken).ConfigureAwait(false);
                if (given is null)
                {
                    call = InferenceCall.Begin(_runtime, scope, turn.Model, turn.Provider, request);
                    events = turn.Provider.StreamAsync(call.Request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    streamContext = scope.Hooks.Has(HookPoints.StreamEvent) ? new HookContext(HookPoints.StreamEvent, scope.Instance, scope.Iteration) : null;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            while (failure is null && given is null)
            {
                bool more;
                try
                {
                    more = await events!.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!more)
                {
                    break;
                }

                accumulator.Add(events.Current);
                if (streamContext is not null)
                {
                    scope.Hooks.EmitStreamEvent(streamContext, events.Current);
                }

                yield return events.Current;
            }

            if (failure is null)
            {
                try
                {
                    AskResult result = given is not null
                        ? await RespondAsync(given, scope, turn.Model, cancellationToken).ConfigureAwait(false)
                        : await FinishAsync(call!, scope, turn.Model, accumulator, cancellationToken).ConfigureAwait(false);
                    finished = true;
                    _result.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (failure is not null)
            {
                finished = true;
                call?.Fail(failure);
                Message? partial = HasContent(accumulator) ? accumulator.ToMessage() : null;
                Exception toThrow = await scope.FailAsync(failure, partial, cancellationToken).ConfigureAwait(false);
                if (toThrow is OperationCanceledException)
                {
                    _result.TrySetCanceled(cancellationToken);
                }
                else
                {
                    _result.TrySetException(toThrow);
                }

                Runtime.Rethrow(toThrow, failure);
            }
        }
        finally
        {
            if (!finished)
            {
                // The caller stopped enumerating before the end. Treat it as a cancellation: keep what arrived.
                call?.Fail(new OperationCanceledException());
                Message? partial = HasContent(accumulator) ? accumulator.ToMessage() : null;
                await scope.FailAsync(new OperationCanceledException(), partial, new CancellationToken(true)).ConfigureAwait(false);
                _result.TrySetCanceled(CancellationToken.None);
            }

            if (events is not null)
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }

            await scope.DisposeAsync().ConfigureAwait(false);
            _release?.Invoke();
        }
    }

    private async Task<AskResult> FinishAsync(InferenceCall call, RunScope scope, Catalog.ResolvedModel model, MessageAccumulator accumulator, CancellationToken cancellationToken)
    {
        (Message stamped, Cost cost, FinishReason finish, IReadOnlyList<ResponseWarning> warnings) = await call
            .CompleteAsync(accumulator.ToMessage(), accumulator.Finish, accumulator.FinishReasonRaw, accumulator.ResponseId, cancellationToken, accumulator.Warnings).ConfigureAwait(false);
        stamped = await scope.AfterInferenceAsync(call.Request, stamped, cost, cancellationToken).ConfigureAwait(false);
        await scope.AppendAsync(stamped, cancellationToken).ConfigureAwait(false);
        await scope.CompleteAsync(_endState, cancellationToken).ConfigureAwait(false);
        return new AskResult(stamped.GetAssistantText(), stamped, scope.Instance, model.Id, cost, finish, warnings);
    }

    // A hook answered for the model: no events arrive, the instance gets the hook's message, and nothing is spent.
    private async Task<AskResult> RespondAsync(Message given, RunScope scope, Catalog.ResolvedModel model, CancellationToken cancellationToken)
    {
        await scope.AppendAsync(given, cancellationToken).ConfigureAwait(false);
        await scope.CompleteAsync(_endState, cancellationToken).ConfigureAwait(false);
        return new AskResult(given.GetAssistantText(), given, scope.Instance, model.Id, Cost.Zero, FinishReason.Stop, []);
    }

    private static bool HasContent(MessageAccumulator accumulator)
    {
        Message m = accumulator.ToMessage();
        return m.Assistant.Count > 0 || m.ToolCalls.Count > 0;
    }
}

/// <summary>
/// A multi-turn conversation with one model. It is one instance: each turn adds the user's message and the answer and saves the
/// instance, so the conversation can be listed, forked and resumed from the store. Turns run one at a time.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly Runtime _runtime;
    private readonly AskRequest _settings;
    private readonly Catalog.ResolvedModel _model;
    private readonly SemaphoreSlim _turns = new(1, 1);

    internal ChatSession(Runtime runtime, AskRequest settings, Catalog.ResolvedModel model)
    {
        _runtime = runtime;
        _settings = settings;
        _model = model;
        Instance = runtime.NewAskInstance(settings with { Name = settings.Name ?? "chat" });
    }

    /// <summary>Releases the chat turn gate. Optional: the chat holds no unmanaged resources, and its instance stays in the store.</summary>
    public void Dispose() => _turns.Dispose();

    /// <summary>The conversation's instance. Its messages grow with each turn.</summary>
    public Instance Instance { get; }

    /// <summary>The instance id, for finding the conversation in the store.</summary>
    public string InstanceId => Instance.InstanceId;

    /// <summary>Sends a message and returns the answer's text.</summary>
    /// <param name="prompt">The user's message.</param>
    /// <param name="cancellationToken">Cancels the turn; the partial answer is kept.</param>
    /// <returns>The assistant's text.</returns>
    /// <exception cref="ProviderException">The provider failed. The unanswered message is removed, so the next turn can retry.</exception>
    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default) =>
        (await SendAsync(prompt, null, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Sends a message with attachments and returns the full result.</summary>
    /// <param name="prompt">The user's message; may be null when attachments are given.</param>
    /// <param name="attachments">Assets sent after the text, or null.</param>
    /// <param name="cancellationToken">Cancels the turn; the partial answer is kept.</param>
    /// <returns>The answer with usage and cost.</returns>
    public async Task<AskResult> SendAsync(string? prompt, IReadOnlyList<Part>? attachments = null, CancellationToken cancellationToken = default)
    {
        await _turns.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Runtime.Turn turn = await BeginAsync(prompt, attachments, cancellationToken).ConfigureAwait(false);
            return (await _runtime.RunTurnAsync<object>(turn, null, InstanceStates.Waiting, cancellationToken).ConfigureAwait(false)).Result;
        }
        finally
        {
            _turns.Release();
        }
    }

    /// <summary>Sends a message and returns the answer as events. Turns do not overlap: the next turn waits for this stream to end.</summary>
    /// <param name="prompt">The user's message.</param>
    /// <param name="attachments">Assets sent after the text, or null.</param>
    /// <returns>The stream.</returns>
    public StreamingAnswer StreamAsync(string? prompt, IReadOnlyList<Part>? attachments = null) =>
        new(_runtime, async ct =>
        {
            await _turns.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await BeginAsync(prompt, attachments, ct).ConfigureAwait(false);
            }
            catch
            {
                _turns.Release();
                throw;
            }
        }, InstanceStates.Waiting, () => _turns.Release());

    private Task<Runtime.Turn> BeginAsync(string? prompt, IReadOnlyList<Part>? attachments, CancellationToken cancellationToken)
    {
        IReadOnlyList<Part> parts = attachments ?? [];
        if (string.IsNullOrEmpty(prompt) && parts.Count == 0)
        {
            throw new ArgumentException("A chat message needs text or an attachment.", nameof(prompt));
        }

        IChatProvider provider = _runtime.GetChatProvider(_model);
        return BeginCoreAsync(provider, prompt, parts, cancellationToken);
    }

    private async Task<Runtime.Turn> BeginCoreAsync(IChatProvider provider, string? prompt, IReadOnlyList<Part> parts, CancellationToken cancellationToken)
    {
        Runtime.Turn turn = await _runtime.BeginTurnAsync(Instance, _settings, _model, provider, _runtime.UserMessage(prompt, parts), null, cancellationToken).ConfigureAwait(false);
        // A failed turn would leave a user message with no answer, and providers reject two user messages in a row.
        turn.Scope.DropUnansweredUser = true;
        return turn;
    }
}
