using System.Text.Json;

namespace OneLlm.Deciders;

/// <summary>
/// Marks a partial class as a decider written in C#. The generator reads the <c>decider.yaml</c> named here (add it to the project as an
/// <c>AdditionalFiles</c> item) and adds to the class: <c>Answers</c> (typed answers per question), one enum per choice question, the
/// <c>Decider</c> property and the YAML text. A static <c>Prepare(JsonElement inputs)</c> and a static
/// <c>Decide(Answers answers, JsonElement inputs)</c> on the class, both returning <see cref="JsonElement"/>, become the decider's logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DeciderAttribute : Attribute
{
    /// <summary>Marks a class as a decider.</summary>
    /// <param name="definition">The decider.yaml, as a file name or a path ending that identifies one of the project's additional files.</param>
    public DeciderAttribute(string definition) => Definition = definition;

    /// <summary>The decider.yaml's file name or path ending.</summary>
    public string Definition { get; }

    /// <summary>The decider's name; defaults to the class name.</summary>
    public string? Name { get; init; }
}

/// <summary>The typed answer to a choice question, with its options as an enum.</summary>
/// <typeparam name="TChoice">The generated enum of the question's options.</typeparam>
/// <param name="Choice">The most probable option.</param>
/// <param name="Confidence">How far the winner stands out from an even split, 0 to 1.</param>
/// <param name="Probabilities">The probability of each option by its key.</param>
public readonly record struct ChoiceAnswer<TChoice>(TChoice Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities)
    where TChoice : struct, Enum;

/// <summary>Reads choice answers.</summary>
public static class ChoiceAnswer
{
    /// <summary>Reads an answer from its JSON form.</summary>
    /// <param name="answer">The answer object.</param>
    /// <param name="parse">Maps an option key to the enum value (generated).</param>
    /// <returns>The typed answer.</returns>
    public static ChoiceAnswer<TChoice> From<TChoice>(JsonElement answer, Func<string, TChoice> parse)
        where TChoice : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(parse);
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (answer.TryGetProperty("probabilities", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty option in p.EnumerateObject())
            {
                probabilities[option.Name] = option.Value.GetDouble();
            }
        }

        return new ChoiceAnswer<TChoice>(parse(answer.GetProperty("choice").GetString() ?? ""), Number(answer, "confidence"), probabilities);
    }

    private static double Number(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}

/// <summary>The typed answer to a score question.</summary>
/// <param name="Score">The expected level, 0 for the lowest; fractional when the probability is spread.</param>
/// <param name="Confidence">How concentrated the distribution is, 0 to 1.</param>
public readonly record struct ScoreAnswer(double Score, double Confidence)
{
    /// <summary>Reads an answer from its JSON form.</summary>
    /// <param name="answer">The answer object.</param>
    /// <returns>The typed answer.</returns>
    public static ScoreAnswer From(JsonElement answer) =>
        new(answer.GetProperty("score").GetDouble(), answer.TryGetProperty("confidence", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0);
}

/// <summary>The typed answer to a yes/no question.</summary>
/// <param name="Probability">The probability that the answer is yes, 0 to 1.</param>
public readonly record struct NoulAnswer(double Probability)
{
    /// <summary>Reads an answer from its JSON form.</summary>
    /// <param name="answer">The answer object.</param>
    /// <returns>The typed answer.</returns>
    public static NoulAnswer From(JsonElement answer) => new(answer.GetProperty("noul").GetDouble());
}

/// <summary>Logic assembled from a generated class's static methods.</summary>
public sealed class DelegateDeciderLogic : IDeciderLogic
{
    private readonly Func<JsonElement, JsonElement>? _prepare;
    private readonly Func<JsonElement, JsonElement, JsonElement>? _decide;

    /// <summary>Creates the logic; either step may be null.</summary>
    /// <param name="prepare">Turns the caller's inputs into the template inputs.</param>
    /// <param name="decide">Turns the answers (and prepared inputs) into the outputs.</param>
    public DelegateDeciderLogic(Func<JsonElement, JsonElement>? prepare, Func<JsonElement, JsonElement, JsonElement>? decide)
    {
        _prepare = prepare;
        _decide = decide;
    }

    /// <inheritdoc />
    public bool HasPrepare => _prepare is not null;

    /// <inheritdoc />
    public bool HasDecide => _decide is not null;

    /// <inheritdoc />
    public ValueTask<JsonElement> PrepareAsync(JsonElement inputs, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_prepare!(inputs));

    /// <inheritdoc />
    public ValueTask<JsonElement> DecideAsync(JsonElement answers, JsonElement inputs, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_decide!(answers, inputs));
}
