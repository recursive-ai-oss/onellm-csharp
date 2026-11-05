using OneLlm.Instances;
using OneLlm.Messages;

namespace OneLlm;

/// <summary>
/// A run was cancelled. The instance was saved with whatever the model had produced so far, and its state is <c>stopped</c> with
/// <c>cancelled: true</c> in its extension data. This is an <see cref="OperationCanceledException"/>, so code that already
/// catches cancellation needs no change.
/// </summary>
public sealed class RunCancelledException : OperationCanceledException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="instance">The instance as saved.</param>
    /// <param name="partial">The assistant message built from what arrived before the cancellation, or null when nothing had.</param>
    /// <param name="cancellationToken">The token that was cancelled.</param>
    public RunCancelledException(Instance instance, Message? partial, CancellationToken cancellationToken)
        : base($"The run '{instance?.InstanceId}' was cancelled.", cancellationToken)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Partial = partial;
    }

    /// <summary>The instance as saved, with the partial answer appended when there was one.</summary>
    public Instance Instance { get; }

    /// <summary>The partial assistant message, or null.</summary>
    public Message? Partial { get; }
}

/// <summary>The model's answer could not be read as the requested type.</summary>
public sealed class StructuredOutputException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="rawText">The text the model returned.</param>
    /// <param name="innerException">The parse error, when there was one.</param>
    public StructuredOutputException(string message, string rawText, Exception? innerException = null)
        : base(message, innerException)
    {
        RawText = rawText;
    }

    /// <summary>The text the model returned, as received.</summary>
    public string RawText { get; }
}
