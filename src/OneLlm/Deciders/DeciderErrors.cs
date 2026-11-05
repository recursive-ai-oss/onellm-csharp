namespace OneLlm.Deciders;

/// <summary>A decider's state or a question could not be rendered with the inputs: a template failed, a name was missing (deciders always treat a missing key as an error) or a <c>forEach</c> did not give a list.</summary>
public sealed class DeciderRenderException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="path">Where in the decider: <c>state.ticket.message</c>, <c>questions.topic.instructions</c>, ...</param>
    /// <param name="innerException">The template error, when there is one.</param>
    public DeciderRenderException(string message, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>Where in the decider the failure is.</summary>
    public string Path { get; }
}

/// <summary>An engine's answers do not match the decider's questions (a missing answer, an answer of the wrong type, or text a chat model could not be made to answer in the required form).</summary>
public sealed class DeciderAnswerException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="question">The question the failure is about, or null.</param>
    public DeciderAnswerException(string message, string? question = null)
        : base(message)
    {
        Question = question;
    }

    /// <summary>The question the failure is about, or null.</summary>
    public string? Question { get; }
}
