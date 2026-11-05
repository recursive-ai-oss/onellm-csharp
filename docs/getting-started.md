# Getting started

Every C# snippet on this page and in the README is compiled by `samples/DocSnippets`, so the calls named here exist.

## Install and keys

```
dotnet add package RecursiveAI.OneLlm
```

OneLLM reads a provider's key from the environment variable the catalog names for it, when the first call to that provider is made:

| Provider | Variable |
|---|---|
| Anthropic | `ANTHROPIC_API_KEY` |
| OpenAI | `OPENAI_API_KEY` |
| Gemini | `GEMINI_API_KEY` |
| TypeSafe Jev | `TYPESAFE_API_KEY` |

A missing variable throws `AuthException` naming it. Keys are held in a `Secret` and removed from error text and logs. Cloud deployments (Azure OpenAI, Bedrock, Vertex) take explicit credentials; see [deployments](deployments.md).

## Model names

A model is a catalog name or alias, such as `claude-sonnet`, `gpt-6-luna` or `gemini-flash`. `provider:model` says which provider serves it (`openai:gpt-6-luna`, `ollama:llama3`). An unknown or ambiguous name throws `ModelNotFoundException` listing the candidates. The catalog is the shipped files, then files named by `ONELLM_CATALOG`, then overrides in code; see [pricing and counting](pricing-and-counting.md) for its layers and for adding a model or a price.

## A first call

```csharp
string answer = await Llm.AskAsync("claude-sonnet", "In one sentence, what is a monad?");
```

`GenerateAsync` returns the same call with everything the run knows:

```csharp
AskResult r = await Llm.GenerateAsync("gpt-6-luna", "Name three prime numbers.", []);
Console.WriteLine($"{r.Text} [{r.Usage?.InputTokens} in, {r.Usage?.OutputTokens} out, {r.Cost.Total}]");
```

`Cost` is a list of line items (each names the usage counter, quantity, unit price and amount) plus a `decimal` total. A model with no price in the catalog gives zero cost and a warning, never a guess.

## Structured output

```csharp
public sealed record Invoice(string Customer, decimal Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Invoice))]
public sealed partial class AppJson : JsonSerializerContext;

Invoice invoice = await Llm.AskAsync("gpt-6-luna", "ACME Ltd billed 120.50 for consulting.", AppJson.Default.Invoice);
```

The schema comes from the type and is sent through the provider's native structured output. Passing the source-generated type info keeps the call trim and NativeAOT safe; without it, `AskAsync<T>` finds the type by reflection and is marked `RequiresUnreferencedCode`.

## Streaming

```csharp
StreamingAnswer stream = Llm.StreamAsync("claude-haiku", "Write a haiku about build servers.");
await foreach (string piece in stream.TextAsync()) Console.Write(piece);
AskResult done = await stream.Result;     // the same result a non-streaming call builds
```

## A script

A `.flow` file is a definition run as a script: its prompts are templates, rendered in order, one model call per user prompt, no tools.

```yaml
# prompts/commit-message.flow
model: gpt-6-luna
systemPrompt: You write git commit messages.
initialPrompts:
  - user:
      - text: "Summarize this change in one sentence: {{ .inputs.diff }}"
  - user:
      - text: "Now write the commit message from your summary: {{ (index .turns 0).text }}"
```

```csharp
ScriptResult script = await Llm.RunScriptAsync("prompts/commit-message.flow", new Dictionary<string, object?> { ["diff"] = diff });
```

See [templating](templating.md) for the language.

## An agent with a C# tool

A tool is a method marked `[Tool]` on a partial class. The generator writes the JSON schema from the XML comments and an invoker that uses no reflection.

```csharp
public sealed partial class DocketTools
{
    /// <summary>Looks up a docket entry.</summary>
    /// <param name="caseNo">Case number, for example 24-1234.</param>
    [Tool]
    public static string GetDocket(string caseNo) => "Docket " + caseNo + ": open";
}
```

```yaml
# agents/clerk.yaml
name: clerk
model: claude-sonnet
systemPrompt: You look up court dockets. Call task_complete with your answer.
inputs:
  caseNo: ""
initialPrompts:
  - user:
      - text: "Find docket {{ .inputs.caseNo }} and report its status."
references:
  - tools: [get_docket, task_complete]
```

```csharp
Llm.Default.Tools.Add<DocketTools>();
AgentResult agent = await Llm.RunAgentAsync("agents/clerk.yaml", new Dictionary<string, object?> { ["caseNo"] = "24-1234" });
Console.WriteLine($"{agent.StopReason}: {agent.Text} ({agent.Cost.Total})");
```

The loop ends when `task_complete` runs, the model answers with no tool call (`Waiting`), a call needs approval (`WaitingForApproval`) or `maxIterations` is reached. To follow or steer a run, use `StartAgentAsync`:

```csharp
AgentRun run = await Llm.StartAgentAsync("agents/clerk.yaml");
await run.ApproveAsync();            // a tool marked [Tool(RequiresApproval = true)] holds its call until then
AgentResult end = await run.AwaitAsync();
```

Approvals are saved in the instance: load it later with a new `Runtime` over the same store and approve there. See [agents](agents.md).

## A decider

A decider asks typed questions of a model and returns the answers (and, if it has logic, an outcome computed from them). The engine is chosen from the model's catalog capabilities: TypeSafe Jev, OpenAI Decisions or a chat model.

```yaml
# deciders/triage-ticket/decider.yaml
model: jev-latest
state:
  title: "{{ .inputs.title }}"
questions:
  topic:
    type: choice
    instructions: Which team should handle the ticket?
    criteria:
      billing: Charges and refunds
      orders: Delivery and returns
  angry:
    type: noul
    instructions: Is the customer angry?
```

```csharp
DecisionResult verdict = await Llm.DecideAsync("deciders/triage-ticket", new Dictionary<string, object?> { ["title"] = "Refund please" });
```

`verdict.Answers` holds one answer per question; `verdict.Outputs` holds what the logic returned. See [deciders](deciders.md) for the engines, `forEach`, refusals, and C# deciders with generated typed answers.

## Grants

A definition names tools; the host decides what they may touch. The default runtime allows files under the working directory, with the shell and the network off. To grant more, build a `Runtime` and set it as the default:

```csharp
var runtime = new Runtime
{
    Capabilities = Capabilities.Files("work")
                 + Capabilities.ReadOnlyFiles("reference")
                 + Capabilities.ShellCommands("git status")
                 + Capabilities.Network("api.example.com"),
    Store = new FileInstanceStore("runs"),
    SpendCap = 5m,             // dollars, for the whole runtime
    InstanceSpendCap = 1m,
};
runtime.Tools.Add<DocketTools>();
runtime.Tools.Add(new FetchWebpageMarkdownTool());     // from RecursiveAI.OneLlm.Web; needs the Network grant
Llm.Default = runtime;
```

A reference to a tool the runtime has not granted fails to load (the default) or is dropped with a warning (lenient). Sub-agents get at most their parent's grants. See [capabilities](capabilities.md).

## Where things go

Every call is an instance. By default instances stay in memory; set `ONELLM_INSTANCES` to a directory to keep them as readable files, and `ONELLM_RECORDS` to add a JSONL audit trail. Database stores are in [stores](stores.md). Traces and metrics are in [telemetry](telemetry.md).

## Using it from Microsoft.Extensions.AI

```csharp
using OneLlm.Extensions.AI;      // package RecursiveAI.OneLlm.Extensions.AI

IChatClient client = new Runtime().AsChatClient("claude-sonnet");
```

See [the bridge](extensions-ai.md), and [reads like](reads-like.md) if you are coming from another library.
