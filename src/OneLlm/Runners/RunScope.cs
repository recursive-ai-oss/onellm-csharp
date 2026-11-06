using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Records;

namespace OneLlm.Runners;

/// <summary>
/// One live run of an instance: holds the store lease, keeps the instance saved as messages are added, writes the run's records and
/// settles how the run ended (finished, failed or cancelled). Disposing releases the lease.
/// </summary>
internal sealed class RunScope : IAsyncDisposable
{
    private readonly IInstanceLease _lease;

    private RunScope(Runtime runtime, Instance instance, IInstanceLease lease, SpendGuard guard)
    {
        Runtime = runtime;
        Instance = instance;
        _lease = lease;
        Guard = guard;
    }

    public Runtime Runtime { get; }

    public Instance Instance { get; }

    /// <summary>The instance's spend limits, starting from what its earlier turns cost.</summary>
    public SpendGuard Guard { get; }

    /// <summary>When set, a failed or cancelled run that produced nothing removes the user message it was answering (chats).</summary>
    public bool DropUnansweredUser { get; set; }

    public static async Task<RunScope> StartAsync(Runtime runtime, Instance instance, CancellationToken cancellationToken)
    {
        IInstanceLease lease = await runtime.Store.AcquireAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);
        try
        {
            // A negative total (a bad tool cost written into an older instance) counts as zero: the instance must still load.
            decimal spent = Math.Max(0m, instance.Messages.Sum(static m => m.Usage?.Cost ?? 0m));
            var scope = new RunScope(runtime, instance, lease, new SpendGuard(runtime.MaxCostPerCall, runtime.InstanceSpendCap, spent));
            DateTimeOffset now = runtime.TimeProvider.GetUtcNow();
            instance.State = InstanceStates.Running;
            instance.RunStartedAt = Rfc3339.Format(now);
            instance.ExtensionData?.Remove("cancelled");
            await runtime.Store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            await runtime.WriteRecordAsync(
                new RunRecord { InstanceId = instance.InstanceId, Time = now, Event = "started", State = instance.State, Model = instance.Model },
                cancellationToken).ConfigureAwait(false);
            return scope;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Adds a message to the instance, saves it and records it.</summary>
    public async Task AppendAsync(Message message, CancellationToken cancellationToken)
    {
        Instance.Messages.Add(message);
        await Runtime.Store.SaveAsync(Instance, cancellationToken).ConfigureAwait(false);
        if (!Runtime.Sinks.IsEmpty)
        {
            await Runtime.WriteRecordAsync(
                new MessageRecord { InstanceId = Instance.InstanceId, Time = message.DateTime ?? Runtime.TimeProvider.GetUtcNow(), MessageId = message.Id, Message = message },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the run in a state (<c>stopped</c> or <c>waiting</c>) and saves.</summary>
    public async Task CompleteAsync(string state, CancellationToken cancellationToken)
    {
        Instance.State = state;
        await Runtime.Store.SaveAsync(Instance, cancellationToken).ConfigureAwait(false);
        await Runtime.WriteRecordAsync(
            new RunRecord { InstanceId = Instance.InstanceId, Time = Runtime.TimeProvider.GetUtcNow(), Event = "finished", State = state, Model = Instance.Model },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles a run that did not finish and returns the exception to throw. Cancellation keeps the partial answer and leaves the
    /// instance <c>stopped</c> with <c>cancelled: true</c>; any other failure puts the instance in <c>error</c>. Saves ignore the
    /// cancelled token so the record of the stop is not itself cancelled.
    /// </summary>
    public async Task<Exception> FailAsync(Exception error, Message? partial, CancellationToken token)
    {
        DateTimeOffset now = Runtime.TimeProvider.GetUtcNow();
        bool cancelled = error is OperationCanceledException && token.IsCancellationRequested;
        if (partial is null && DropUnansweredUser && Instance.Messages is [.., { Assistant.Count: 0, ToolCalls.Count: 0, User.Count: > 0 }])
        {
            Instance.Messages.RemoveAt(Instance.Messages.Count - 1);
        }

        if (partial is not null)
        {
            Instance.Messages.Add(new Message
            {
                Id = Runtime.NewId("m"),
                User = partial.User,
                Assistant = partial.Assistant,
                ToolCalls = partial.ToolCalls,
                DateTime = now,
                Usage = partial.Usage,
                ExtensionData = partial.ExtensionData,
            });
        }

        if (cancelled)
        {
            Instance.State = InstanceStates.Stopped;
            (Instance.ExtensionData ??= [])["cancelled"] = true;
        }
        else
        {
            Instance.State = InstanceStates.Error;
        }

        await Runtime.Store.SaveAsync(Instance, CancellationToken.None).ConfigureAwait(false);
        await Runtime.WriteRecordAsync(
            new RunRecord { InstanceId = Instance.InstanceId, Time = now, Event = cancelled ? "cancelled" : "failed", State = Instance.State, Model = Instance.Model },
            CancellationToken.None).ConfigureAwait(false);
        if (!cancelled)
        {
            await Runtime.WriteRecordAsync(
                new ErrorRecord { InstanceId = Instance.InstanceId, Time = now, ErrorType = error.GetType().Name, Message = RecordText.Bound(error.Message), Where = "run" },
                CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        return new RunCancelledException(Instance, partial, token);
    }

    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}

internal static class RecordText
{
    /// <summary>Provider errors are scrubbed of secrets before they reach here; this only bounds the length kept in a record.</summary>
    public static string Bound(string text) => text.Length <= 2000 ? text : text[..2000];
}
