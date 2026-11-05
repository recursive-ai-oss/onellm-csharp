# Deciders

A decider answers typed questions about some state: *which team should handle this ticket* (choice), *how urgent is it* (score),
*is the customer angry* (a yes/no probability). The definition is a `decider.yaml` (format: `spec/definitions.md`); optional logic
turns the caller's inputs into what the templates read and the answers into an outcome.

```csharp
DecisionResult r = await Llm.DecideAsync("deciders/triage-ticket", new Dictionary<string, object?> { ["ticket"] = ticket });
r.Answers;   // by question id
r.Outputs;   // the logic's outcome, or the answers when there is no logic
r.Cost.Total; r.Usage; r.Instance;

Routing outcome = await Llm.DecideAsync("deciders/triage-ticket", RoutingJson.Default.Routing, inputs);   // outputs read into a type (AOT safe)
```

The `Runtime` has the same calls plus the run APIs: `ReplayDecisionAsync`, `GetDecisionAsync`, `ListDecisionsAsync`, `DecisionStatsAsync`.
A tool that wants to run a decider calls `Runtime.DecideAsync(decider, JsonElement inputs, origin)`.

## Three engines, chosen by the model

`model:` resolves through the catalog. A model of kind `decision` is a decisions engine; any other model is asked as a chat model.

| Engine | Model examples | How questions are sent |
|---|---|---|
| TypeSafe Jev | `jev-latest`, `jev-1.14.0` | One request holds the state and every question; split by token budget (90% of the context window) when it would not fit, up to 8 requests in flight. Bytes match the reference service for the same decider and inputs. |
| OpenAI Decisions | `openai-decisions` | `POST /v1/decisions`: the state is `input_json` (images become `input_image`), questions are `predicate`, `choice` or `score`. At most 400 questions per request. `cache.key` becomes `prompt_cache_key`; `cache.explicit` adds an explicit breakpoint after the state. |
| Chat model | any chat model | One call per question (and per sample) over a shared system prefix that holds the instructions and the state; the model returns probabilities, and choice, score and confidence are derived from them. Off-schema replies are retried up to three times, never repaired. |

Notes that follow from the engines:

- A refusal from OpenAI Decisions is a typed `RefusedException` naming the questions.
- An image in the inputs needs a decisions engine that takes images; on Jev it is a `CapabilityMissingException`.
- Usage on OpenAI Decisions: `input_tokens` already includes cached and cache-written tokens, so they are priced once, at their own rates; output is free.
- With `llm.samples` above 1 the chat engine averages the samples' distributions (temperature defaults to 1 then).
- A prefix shared by the chat calls is cached where the provider supports it; the first call runs alone when the prefix is long enough to be cached.

## Inputs and templates

The caller's inputs go through `prepare` (if the decider has logic), then render the `state` and the questions. A missing name is an
error, not `<no value>`. A string that is exactly one action (`"{{ .inputs.customer }}"`) keeps its structure in the request; see the spec for the exact
encoding. `forEach` expands a question once per element; the answer is a list; an empty list asks nothing and makes no request.

If a `decider.schema.json` sits beside the definition and declares `inputs`, the inputs are checked against it first (`DeciderInputException`, with a path).
The checker enforces `type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, `minimum`, `maximum` and `anyOf`, and ignores
every other keyword; it is a guard against wrong inputs, not a general JSON Schema validator.

## Logic in C#

Mark a partial class and add the definition as an additional file:

```xml
<AdditionalFiles Include="triage.decider.yaml" />
```

```csharp
[Decider("triage.decider.yaml")]
public static partial class Triage
{
    // optional: JsonElement Prepare(JsonElement inputs)
    static JsonElement Decide(Answers answers, JsonElement inputs) =>
        JsonSerializer.SerializeToElement(new Routing(answers.Topic.Choice.ToString(), answers.Angry.Probability > 0.4), RoutingJson.Default.Routing);
}

DecisionResult r = await Llm.DecideAsync(Triage.Decider, inputs);
```

The generator adds `Answers` (a `ChoiceAnswer<TopicChoice>`, `ScoreAnswer` or `NoulAnswer` per question; a list for `forEach`),
one enum per choice question, the `Yaml` text and the `Decider` property. It reads the questions with a small block-YAML reader and reports
`ODG001` to `ODG004` when the class is not partial, the file is not an additional file, a choice's criteria are not a block map, or two
names collapse to one C# name. Everything it produces is trim and AOT safe; the full definition is still validated when the decider loads.

`IDeciderLogic` is the seam other packages implement (the JavaScript package runs `decider.js` through it).

## Runs

Each run is an instance (`Instance.Name` is the decider's name), saved to the runtime's store and recorded to its sinks with the cost lines of every
call. `origin` marks where it came from: `api` (default), `agent`, `playground`, `replay`, `eval`. Statistics skip the origins you pass in
`excludeOrigins`. `ReplayDecisionAsync` re-asks a recorded run's inputs, optionally on another model, as a new instance.

## Testing

`OneLlm.Testing.ScriptedDecisionProvider` answers from a function and records the requests; for chat engines use `ScriptedProvider`.
The fixtures for the Jev and OpenAI request and response shapes are written from the vendors' references, not recorded from live calls.

## Cost

Jev prices input tokens only; OpenAI Decisions prices uncached input, cache reads and cache writes and not output. Chat engines price
as their model does. All money is `decimal`; see `docs/pricing-and-counting.md`.
