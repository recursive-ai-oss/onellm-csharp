namespace OneLlm.Tools;

public sealed partial class ToolContext
{
    private List<Secret>? _secrets;

    /// <summary>True after a secret was registered on this call. Hosts can avoid redaction work for calls that never resolve secrets.</summary>
    public bool HasRegisteredSecrets
    {
        get
        {
            lock (_gate)
            {
                return _secrets is { Count: > 0 };
            }
        }
    }

    /// <summary>Registers a resolved secret for redaction in this call's log and progress lines. Thread-safe; values stay wrapped.</summary>
    /// <param name="secret">The secret. Null is refused.</param>
    public void RegisterSecret(Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        lock (_gate)
        {
            (_secrets ??= []).Add(secret);
        }
    }

    /// <summary>Scrubs registered secrets without truncating text. With no secrets registered, returns the original string.</summary>
    /// <param name="text">The text to scrub.</param>
    /// <returns>Text with resolved secrets replaced by the existing Secret mask.</returns>
    public string ScrubSecrets(string text)
    {
        lock (_gate)
        {
            return _secrets is null ? text : Secret.ScrubAll(text, _secrets);
        }
    }
}
