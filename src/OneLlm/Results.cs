using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Runners;

namespace OneLlm;

/// <summary>A request for one answer from one model, run as an instance.</summary>
public sealed record AskRequest
{
    /// <summary>The model: a catalog name or alias, or <c>provider:model</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary>The user's text. Used when <see cref="Prompt"/> and <see cref="Attachments"/> are the whole turn.</summary>
    public string? Prompt { get; init; }

    /// <summary>Assets or other parts sent after the text in the same user turn.</summary>
    public IReadOnlyList<Part> Attachments { get; init; } = [];

    /// <summary>The system prompt, sent as written (it is not rendered as a template).</summary>
    public string? System { get; init; }

    /// <summary>Earlier turns to send before the prompt, such as a conversation the caller keeps.</summary>
    public IReadOnlyList<Message> History { get; init; } = [];

    /// <summary>Sampling temperature. Dropped, with a warning, for models whose catalog entry says they take none.</summary>
    public double? Temperature { get; init; }

    /// <summary>The most output tokens to generate; null uses the provider's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Reasoning effort or budget.</summary>
    public ReasoningOptions? Reasoning { get; init; }

    /// <summary>A service tier such as <c>flex</c>, where the provider has them.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Parameters written into the provider's request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }

    /// <summary>A name for the instance, shown in listings. Defaults to <c>ask</c>.</summary>
    public string? Name { get; init; }
}

/// <summary>The answer to an <see cref="AskRequest"/>.</summary>
public class AskResult
{
    internal AskResult(string text, Message message, Instance instance, string model, Cost cost, FinishReason finish, IReadOnlyList<ResponseWarning> warnings)
    {
        Text = text;
        Message = message;
        Instance = instance;
        Model = model;
        Cost = cost;
        Finish = finish;
        Warnings = warnings;
    }

    internal AskResult(AskResult other)
        : this(other.Text, other.Message, other.Instance, other.Model, other.Cost, other.Finish, other.Warnings)
    {
    }

    /// <summary>The assistant's text: its text parts joined, without reasoning.</summary>
    public string Text { get; }

    /// <summary>The whole assistant turn, as persisted in <see cref="Instance"/>.</summary>
    public Message Message { get; }

    /// <summary>What the call consumed; null when the provider reported nothing.</summary>
    public Usage? Usage => Message.Usage;

    /// <summary>What the call cost, as line items and a total. <see cref="Messages.Cost.Zero"/> when the catalog has no price (see <see cref="Warnings"/>).</summary>
    public Cost Cost { get; }

    /// <summary>The instance this run created or advanced, as saved.</summary>
    public Instance Instance { get; }

    /// <summary>The model that was used, as <c>provider:model</c>.</summary>
    public string Model { get; }

    /// <summary>Why generation stopped.</summary>
    public FinishReason Finish { get; }

    /// <summary>Notices about the request: parameters removed for the model, prices missing or stale.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>The answer to a typed ask: the value the model's JSON was read into, plus everything in <see cref="AskResult"/>.</summary>
/// <typeparam name="T">The requested type.</typeparam>
public sealed class AskResult<T> : AskResult
{
    internal AskResult(AskResult other, T value)
        : base(other)
    {
        Value = value;
    }

    /// <summary>The value. Never null.</summary>
    public T Value { get; }
}

/// <summary>The outcome of a scripted run.</summary>
public sealed class ScriptResult
{
    internal ScriptResult(Instance instance, IReadOnlyList<Message> turns, Cost cost, Usage? usage, JsonElement? output, IReadOnlyList<string> warnings)
    {
        Instance = instance;
        Turns = turns;
        Cost = cost;
        Usage = usage;
        Output = output;
        Warnings = warnings;
    }

    /// <summary>The last assistant turn's text; empty when the script made no inference.</summary>
    public string Text => Turns.Count == 0 ? "" : Turns[^1].GetAssistantText();

    /// <summary>The last assistant turn, or null when the script made no inference.</summary>
    public Message? Message => Turns.Count == 0 ? null : Turns[^1];

    /// <summary>Every assistant turn, one per user prompt that ran, in order.</summary>
    public IReadOnlyList<Message> Turns { get; }

    /// <summary>The instance as saved, including <c>renderedSystemPrompt</c>.</summary>
    public Instance Instance { get; }

    /// <summary>The cost of every inference in the run, as line items and a total.</summary>
    public Cost Cost { get; }

    /// <summary>Token counts summed over the run; null when the script made no inference.</summary>
    public Usage? Usage { get; }

    /// <summary>The last turn's JSON when the definition declares an <c>outputSchema</c>; null otherwise.</summary>
    public JsonElement? Output { get; }

    /// <summary>Things the runner ignored or changed: tool references it does not run, a prompt skipped by <c>when</c>.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Reads <see cref="Output"/> as a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/> (source-generated, so the call is trim-safe).</param>
    /// <returns>The value.</returns>
    /// <exception cref="StructuredOutputException">The run has no output, or it does not fit the type.</exception>
    public T GetOutput<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        string text = Text;
        return StructuredOutput.Parse(text, typeInfo);
    }
}

/// <summary>Embeddings for a list of texts.</summary>
public sealed class EmbedResult
{
    internal EmbedResult(IReadOnlyList<ReadOnlyMemory<float>> embeddings, Usage? usage, Cost cost, string model)
    {
        Embeddings = embeddings;
        Usage = usage;
        Cost = cost;
        Model = model;
    }

    /// <summary>One vector per input, in input order.</summary>
    public IReadOnlyList<ReadOnlyMemory<float>> Embeddings { get; }

    /// <summary>What the call consumed; null when the provider reported nothing.</summary>
    public Usage? Usage { get; }

    /// <summary>What the call cost.</summary>
    public Cost Cost { get; }

    /// <summary>The model that was used, as <c>provider:model</c>.</summary>
    public string Model { get; }
}
