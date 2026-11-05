using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Deciders;

/// <summary>The outcome of running a decider: its answers and outputs, what the engine calls cost, and the instance that records the run.</summary>
public sealed class DecisionResult
{
    internal DecisionResult(
        JsonElement answers,
        JsonElement outputs,
        string model,
        string engine,
        Usage? usage,
        Cost cost,
        IReadOnlyList<DecisionCall> calls,
        Instance instance,
        IReadOnlyList<ResponseWarning> warnings,
        TimeSpan duration)
    {
        Answers = answers;
        Outputs = outputs;
        Model = model;
        Engine = engine;
        Usage = usage;
        Cost = cost;
        Calls = calls;
        Instance = instance;
        Warnings = warnings;
        Duration = duration;
    }

    /// <summary>The answers by question id. A choice answer is <c>{type, choice, confidence, probabilities}</c>, a score answer <c>{type, score, confidence, legend, probabilities}</c>, a noul answer <c>{type, noul}</c>; a repeated question's answer is a list of these.</summary>
    public JsonElement Answers { get; }

    /// <summary>What the decider's logic made of the answers; the answers themselves when the decider has no <c>decide</c> step.</summary>
    public JsonElement Outputs { get; }

    /// <summary>The model that answered, which can be a versioned name for the alias the decider asked for.</summary>
    public string Model { get; }

    /// <summary><c>jev</c>, <c>decisions</c> (OpenAI Decisions) or <c>llm</c> (a chat model).</summary>
    public string Engine { get; }

    /// <summary>What the engine calls used in total; null when no call was made (every question was repeated over an empty list).</summary>
    public Usage? Usage { get; }

    /// <summary>What the run cost: one line per priced counter per call.</summary>
    public Cost Cost { get; }

    /// <summary>Every engine call, in the order they finished.</summary>
    public IReadOnlyList<DecisionCall> Calls { get; }

    /// <summary>The instance that records this run, as saved: the state asked about, the answers, and the cost.</summary>
    public Instance Instance { get; }

    /// <summary>Notices: prices missing or stale, parameters dropped.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; }

    /// <summary>How long the run took by the runtime's clock, logic included.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Reads the outputs into a type through its serialization metadata (trim and AOT safe).</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="typeInfo">Metadata from a source-generated context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="JsonException">The outputs do not fit the type.</exception>
    public T GetOutputs<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Outputs.Deserialize(typeInfo) ?? throw new JsonException("The outputs are null.");
    }

    /// <summary>The answer to one declared question.</summary>
    /// <param name="questionId">The question id.</param>
    /// <returns>The answer element.</returns>
    /// <exception cref="KeyNotFoundException">There is no such question.</exception>
    public JsonElement Answer(string questionId) =>
        Answers.TryGetProperty(questionId, out JsonElement answer) ? answer : throw new KeyNotFoundException($"The decider has no question '{questionId}'.");
}

/// <summary>The outcome of running a decider, with the outputs read into a type.</summary>
/// <typeparam name="T">The outputs type.</typeparam>
public sealed class DecisionResult<T>
{
    internal DecisionResult(DecisionResult result, T value)
    {
        Result = result;
        Value = value;
    }

    /// <summary>The whole result.</summary>
    public DecisionResult Result { get; }

    /// <summary>The outputs, read into the type. Never null.</summary>
    public T Value { get; }
}

/// <summary>A decider run as recorded in its instance, for listing and inspection.</summary>
/// <param name="InstanceId">The instance id.</param>
/// <param name="Decider">The decider's name.</param>
/// <param name="State">The instance state: <c>stopped</c> for a finished run, <c>error</c> for a failed one.</param>
/// <param name="Model">The model the decider asked for.</param>
/// <param name="AnsweredBy">The model that answered, or null if none did.</param>
/// <param name="Engine">The engine: <c>jev</c>, <c>decisions</c> or <c>llm</c>.</param>
/// <param name="Origin">Where the run came from: <c>api</c> (the default), <c>replay</c>, <c>eval</c>, ... Statistics can leave some origins out.</param>
/// <param name="ReplayOf">For a replay, the instance it replays.</param>
/// <param name="Duration">How long the run took.</param>
/// <param name="Cost">What it cost in USD, summed from its messages.</param>
/// <param name="Answers">The recorded answers, or null for a run that failed before answering.</param>
/// <param name="Outputs">The recorded outputs, or null.</param>
public sealed record DecisionRun(
    string InstanceId,
    string Decider,
    string? State,
    string? Model,
    string? AnsweredBy,
    string? Engine,
    string Origin,
    string? ReplayOf,
    TimeSpan Duration,
    decimal Cost,
    JsonElement? Answers,
    JsonElement? Outputs);

/// <summary>Counts and averages over recorded runs of one decider.</summary>
/// <param name="Decider">The decider's name.</param>
/// <param name="Runs">Runs counted.</param>
/// <param name="Failed">Runs that ended in <c>error</c>.</param>
/// <param name="TotalCost">What they cost in USD.</param>
/// <param name="MeanDuration">The mean run time.</param>
/// <param name="MeanConfidence">For each question that has confidence (choice and score), the mean confidence over the runs' answers; repeated questions count every element.</param>
/// <param name="MeanProbability">For each noul question, the mean probability of yes.</param>
public sealed record DecisionStats(
    string Decider,
    int Runs,
    int Failed,
    decimal TotalCost,
    TimeSpan MeanDuration,
    IReadOnlyDictionary<string, double> MeanConfidence,
    IReadOnlyDictionary<string, double> MeanProbability);
