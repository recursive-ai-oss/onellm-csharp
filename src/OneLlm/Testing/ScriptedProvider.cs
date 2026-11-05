using System.Runtime.CompilerServices;
using System.Text;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Testing;

/// <summary>One scripted answer: what a <see cref="ScriptedProvider"/> returns for the next chat request.</summary>
public sealed class ScriptStep
{
    /// <summary>The text of the answer, sent as one delta per chunk. A step with no chunks and no tool calls answers with an empty message.</summary>
    public IReadOnlyList<string> Chunks { get; init; } = [];

    /// <summary>Tool calls the answer makes, after its text.</summary>
    public IReadOnlyList<ScriptedToolCall> ToolCalls { get; init; } = [];

    /// <summary>The usage reported with the answer. Null reports none.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Why the answer stops. Defaults to a normal stop.</summary>
    public FinishReason Finish { get; init; } = FinishReason.Stop;

    /// <summary>When set, the request fails with this exception instead of answering.</summary>
    public Exception? Error { get; init; }

    /// <summary>Called with the request before the answer is produced; throw from it to fail a test on an unexpected request.</summary>
    public Action<ChatRequest>? Expect { get; init; }

    /// <summary>
    /// Called before each chunk is sent (index from 0) and awaited. A step can wait here for a test to cancel the call
    /// part-way through a stream.
    /// </summary>
    public Func<int, CancellationToken, Task>? BeforeChunk { get; init; }

    /// <summary>Creates a step that answers with text.</summary>
    /// <param name="chunks">The text, one delta per item.</param>
    /// <returns>The step.</returns>
    public static ScriptStep Text(params string[] chunks) => new() { Chunks = chunks };

    /// <summary>Creates a step that answers with text and a usage.</summary>
    /// <param name="text">The text, sent as one delta.</param>
    /// <param name="inputTokens">Input tokens reported.</param>
    /// <param name="outputTokens">Output tokens reported.</param>
    /// <returns>The step.</returns>
    public static ScriptStep TextWithUsage(string text, long inputTokens, long outputTokens) =>
        new() { Chunks = [text], Usage = new Usage { InputTokens = inputTokens, OutputTokens = outputTokens } };

    /// <summary>Creates a step that fails the request.</summary>
    /// <param name="error">The exception to throw.</param>
    /// <returns>The step.</returns>
    public static ScriptStep Fail(Exception error) => new() { Error = error };
}

/// <summary>A tool call in a <see cref="ScriptStep"/>.</summary>
/// <param name="Id">The call id.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="ArgumentsJson">The arguments as JSON text.</param>
public sealed record ScriptedToolCall(string Id, string Tool, string ArgumentsJson);

/// <summary>
/// A chat and embeddings provider that replays a script, for tests of anything built on providers: runners, loops, hosts. Each chat
/// request takes the next <see cref="ScriptStep"/>; requests are recorded so a test can assert on them. Streaming and non-streaming
/// calls go through the same events and the same accumulator as the real providers, so they build equal messages. Thread-safe.
/// </summary>
public sealed class ScriptedProvider : IChatProvider, IEmbeddingProvider
{
    private readonly object _gate = new();
    private readonly Queue<ScriptStep> _steps = new();
    private readonly List<ChatRequest> _requests = [];
    private readonly List<EmbeddingRequest> _embeddingRequests = [];

    /// <summary>Creates a provider.</summary>
    /// <param name="name">The provider name it reports; register it under the same name in the catalog and registry.</param>
    public ScriptedProvider(string name = "scripted")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Deployment => "direct";

    /// <summary>When set, called for each chat request that finds the script empty. Without it an empty script throws.</summary>
    public Func<ChatRequest, ScriptStep>? Fallback { get; set; }

    /// <summary>Chat requests received so far, in order.</summary>
    public IReadOnlyList<ChatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Embedding requests received so far, in order.</summary>
    public IReadOnlyList<EmbeddingRequest> EmbeddingRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _embeddingRequests];
            }
        }
    }

    /// <summary>Adds steps to the end of the script.</summary>
    /// <param name="steps">The steps.</param>
    /// <returns>This provider.</returns>
    public ScriptedProvider Enqueue(params ScriptStep[] steps)
    {
        lock (_gate)
        {
            foreach (ScriptStep step in steps)
            {
                _steps.Enqueue(step);
            }
        }

        return this;
    }

    /// <summary>Adds a text answer to the end of the script.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This provider.</returns>
    public ScriptedProvider EnqueueText(string text) => Enqueue(ScriptStep.Text(text));

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ScriptStep step = Take(request);
        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in Play(step, request, cancellationToken).ConfigureAwait(false))
        {
            acc.Add(e);
        }

        return new ChatResponse
        {
            Message = acc.ToMessage(),
            Id = acc.ResponseId,
            Model = acc.Model,
            Finish = acc.Finish,
            FinishReasonRaw = acc.FinishReasonRaw,
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Play(Take(request), request, cancellationToken);

    /// <inheritdoc />
    public Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _embeddingRequests.Add(request);
        }

        // A vector the test can predict: the length of the text and its position in the request.
        var vectors = new List<ReadOnlyMemory<float>>(request.Inputs.Count);
        for (int i = 0; i < request.Inputs.Count; i++)
        {
            vectors.Add(new float[] { request.Inputs[i].Length, i });
        }

        long tokens = request.Inputs.Sum(static s => (long)s.Length);
        return Task.FromResult(new EmbeddingResponse
        {
            Embeddings = vectors,
            Model = request.Model,
            Usage = new Usage { Model = request.Model, InputTokens = tokens },
        });
    }

    private ScriptStep Take(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _requests.Add(request);
            if (_steps.Count > 0)
            {
                return _steps.Dequeue();
            }
        }

        return Fallback?.Invoke(request)
            ?? throw new InvalidOperationException($"The scripted provider '{Name}' received request {_requests.Count} but its script is empty.");
    }

    private static async IAsyncEnumerable<StreamEvent> Play(ScriptStep step, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        step.Expect?.Invoke(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (step.Error is { } error)
        {
            throw error;
        }

        yield return StreamEvent.MessageStart("scripted-" + request.Model, request.Model);
        for (int i = 0; i < step.Chunks.Count; i++)
        {
            if (step.BeforeChunk is { } wait)
            {
                await wait(i, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return StreamEvent.TextDelta(0, Encoding.UTF8.GetBytes(step.Chunks[i]));
        }

        int index = 1;
        foreach (ScriptedToolCall call in step.ToolCalls)
        {
            yield return StreamEvent.ToolCallStart(index, call.Id, call.Tool);
            yield return StreamEvent.ToolCallArgumentsDelta(index, Encoding.UTF8.GetBytes(call.ArgumentsJson));
            yield return StreamEvent.ToolCallEnd(index);
            index++;
        }

        if (step.Usage is { } usage)
        {
            yield return StreamEvent.UsageReported(usage.Model.Length == 0 ? CopyWithModel(usage, request.Model) : usage);
        }

        yield return StreamEvent.Done(step.ToolCalls.Count > 0 && step.Finish == FinishReason.Stop ? FinishReason.ToolCalls : step.Finish);
    }

    private static Usage CopyWithModel(Usage u, string model) => new()
    {
        Model = model,
        ServiceTier = u.ServiceTier,
        InputTokens = u.InputTokens,
        OutputTokens = u.OutputTokens,
        CacheCreationInputTokens = u.CacheCreationInputTokens,
        CacheReadInputTokens = u.CacheReadInputTokens,
        Cost = u.Cost,
        Counters = u.Counters,
        CostLines = u.CostLines,
        ExtensionData = u.ExtensionData,
    };
}
