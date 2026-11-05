using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Testing;

/// <summary>
/// A decisions engine that answers from a function and records every request, for tests of deciders and of code that runs them.
/// Without a responder it answers each question with a well-formed answer of its type (first choice, lowest score, 0.5).
/// </summary>
public sealed class ScriptedDecisionProvider : IDecisionProvider
{
    private readonly List<DecisionRequest> _requests = [];

    /// <summary>Creates the provider.</summary>
    /// <param name="name">The provider name the catalog entry uses.</param>
    public ScriptedDecisionProvider(string name = "scripted-decisions") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Deployment => "direct";

    /// <summary>Builds the response for a request; null uses the default answers.</summary>
    public Func<DecisionRequest, DecisionResponse>? Responder { get; set; }

    /// <summary>The usage reported by the default answers.</summary>
    public Usage? DefaultUsage { get; set; }

    /// <summary>The requests received, in arrival order.</summary>
    public IReadOnlyList<DecisionRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(Responder?.Invoke(request) ?? DefaultAnswers(request));
    }

    private DecisionResponse DefaultAnswers(DecisionRequest request)
    {
        var answers = new JsonObject();
        foreach (WireQuestion q in request.Questions)
        {
            JsonNode? node = JsonNode.Parse(q.Json.Span);
            answers[q.Id] = q.Type switch
            {
                "choice" => new JsonObject
                {
                    ["type"] = "choice",
                    ["choice"] = ChoiceKeys(node).FirstOrDefault() ?? "",
                    ["confidence"] = 0.5,
                    ["probabilities"] = new JsonObject(),
                },
                "score" => new JsonObject { ["type"] = "score", ["score"] = 0, ["confidence"] = 0.5, ["probabilities"] = new JsonArray() },
                _ => new JsonObject { ["type"] = "noul", ["noul"] = 0.5 },
            };
        }

        return new DecisionResponse { Answers = answers, Model = request.Model, Usage = DefaultUsage };
    }

    private static IEnumerable<string> ChoiceKeys(JsonNode? question) =>
        question?["criteria"] is JsonObject criteria ? criteria.Select(static p => p.Key) : [];
}
