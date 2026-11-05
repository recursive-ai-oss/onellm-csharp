# The happy path and the runtime beneath it

OneLLM has two ways in. The statics on `Llm` are verbs that take a model name and what to ask. Underneath is `Runtime`, which the
statics use through `Llm.Default`; build your own when you want to choose where instances are kept, add record sinks, set spend
caps or register a provider.

```csharp
string s      = await Llm.AskAsync("claude-sonnet", "Summarize this", Asset.File("brief.pdf"));
Invoice inv   = await Llm.AskAsync("gpt-5.5", "Extract the invoice", Asset.File("scan.png"), AppJson.Default.Invoice);
await foreach (string piece in Llm.StreamTextAsync("gemini-3-pro", "Write a haiku")) Console.Write(piece);
float[] v     = await Llm.EmbedAsync("text-embedding-4", "reset my password");
using var chat = Llm.Chat("claude-sonnet");  await chat.AskAsync("…");  await chat.AskAsync("and then?");
ScriptResult r = await Llm.RunScriptAsync("prompts/commit-message.flow", new Dictionary<string, object?> { ["diff"] = diff });
```

## Models

A model is a catalog name or alias (`claude-sonnet`), or `provider:model` (`openai:gpt-5.5`, or a preset such as `ollama:llama3`) to
say which provider. An unknown name throws `ModelNotFoundException`; a name that two providers claim throws it too, with the
candidates listed. The catalog is the shipped files, then the files named by `ONELLM_CATALOG`, then code overrides.

## The statics

| Call | Returns | Notes |
|---|---|---|
| `AskAsync(model, prompt)` / `AskAsync(model, prompt, asset)` | `string` | The assistant's text. |
| `AskAsync<T>(model, prompt, typeInfo)` | `T` | Native structured output; `typeInfo` from a source-generated `JsonSerializerContext`. Without it, `AskAsync<T>(model, prompt)` finds the type by reflection and is not trim or AOT safe. `T` must be a JSON object type. |
| `GenerateAsync(model, prompt, attachments)` / `GenerateAsync<T>(…, typeInfo)` | `AskResult` / `AskResult<T>` | Text or value, plus `Usage`, `Cost` (line items and total), `Warnings`, the saved `Instance`, `Finish`. |
| `StreamAsync(model, prompt)` | `StreamingAnswer` | Events as they arrive; after the loop, `await stream.Result`. |
| `StreamTextAsync(model, prompt)` | `IAsyncEnumerable<string>` | Text pieces only. |
| `EmbedAsync(model, text)` / `EmbedAsync(model, texts)` | `float[]` / `float[][]` | |
| `Chat(model, system)` | `ChatSession` | `AskAsync`, `SendAsync`, `StreamAsync`; one instance for the whole conversation. |
| `RunScriptAsync(path, inputs)` | `ScriptResult` | `.flow` or agent definition run as a script. |

Every call takes a `CancellationToken`. Assets come from `Asset.File(path)` and `Asset.FromBytes(bytes, contentType)`.

Inputs to a script are a dictionary (or a value with a `JsonTypeInfo`) on the AOT-safe path. Passing an anonymous object works by
reflection and is marked `RequiresUnreferencedCode`.

## Every call is an instance

A call creates an instance, runs it, and saves it in the runtime's store (in memory unless `ONELLM_INSTANCES` names a directory,
or you set `Runtime.Store`). The instance holds the messages with their usage and cost, so a chat can be listed, forked and
resumed. Records go to the runtime's sinks (`ONELLM_RECORDS` adds a JSONL directory) and each model call is a trace span and
metrics (see [telemetry](telemetry.md)). Embeddings are stateless: they make no instance, but are recorded and priced.

The instance states a run ends in: `stopped` (an ask or a script finished), `waiting` (a chat between turns), `error`. A cancelled
run ends `stopped` with `cancelled: true` in its extension data, keeps what had arrived (a streamed partial answer is saved as the
last assistant message), and throws `RunCancelledException`, an `OperationCanceledException` that carries the instance.

## Cost and spend caps

Usage is priced from the catalog (see [pricing](pricing-and-counting.md)); an unpriced model gives `Cost.Zero` and a `pricing`
warning. `Runtime.SpendCap` (whole runtime), `Runtime.InstanceSpendCap` (one instance, counting its earlier turns) and
`Runtime.MaxCostPerCall` are checked before a call against its worst case (local token estimate plus the full output allowance)
and after against the real cost. A call that could pass a cap throws `BudgetExceededException` before anything is sent.

## Scripted runs

`RunScriptAsync` renders the definition's `systemPrompt` and each of its `initialPrompts` as Go-template text (see
[templating](templating.md)) and sends the rendered system prompt with each call. Each user prompt gets one model call, in order,
with everything before it as history; no tools are offered, and tool references and tool calls are ignored with a warning.
Assistant prompts are added to the history without a call.

The template scope:

| Name | Holds |
|---|---|
| `.inputs` | The definition's `inputs` defaults, overridden by the caller's values. |
| `.system` | `os`, `arch`, `hostname`, `username`, `homeDir`, `tempDir`, `workingDir`. |
| `.time` | `now` (RFC 3339), `today`, `timezone`, from the runtime's clock. |
| `.env` | Empty, except for the names in `Runtime.AllowedEnvironment`. A prompt cannot read a credential the host did not list. |
| `.git` | Empty fields; the host fills it through `Runtime.ScopeValues`. |
| `.swarm`, `.caller`, `.invocationMode` | Extension data, the calling instance, and the definition's mode (default `autonomous`). |
| `.turns` | The earlier answers: `.turns[0].text`, and `.turns[0].json` when the text is JSON. |

`missingKey: error` in the definition makes a missing key a render error; otherwise it prints `<no value>`. `embed` reads files beside
the definition (never outside its directory) and then `Runtime.EmbedSources`. A prompt's `when` is a template: the prompt runs unless
it renders to empty, `false`, `0`, `no` or `off` (a `when` that fails to render counts as true). The rendered system prompt is stored
as `metadata.renderedSystemPrompt` in the instance. A definition with an `outputSchema` (JSON Schema, an extra field of the
definition) asks for that shape on its last user prompt; `ScriptResult.Output` holds the JSON.

## Providers

`Runtime.Providers` maps a catalog provider name (or preset name, or preset protocol) to a provider, created on first use from
the environment variable the catalog names (`apiKeyEnvVar`). A missing variable throws `AuthException` naming it; no registered
provider throws `CapabilityMissingException` naming what to register. `ProviderRegistry.Set(name, provider)` plugs in a ready provider.

## Testing code that uses OneLLM

`OneLlm.Testing.ScriptedProvider` replays a script of answers (text, tool calls, usage, failures, pauses) and records the requests it
received. Streaming and whole answers go through the same events, so they build equal messages.

```csharp
var provider = new ScriptedProvider().EnqueueText("hello");
var runtime = new Runtime { Catalog = myCatalog, Providers = new ProviderRegistry() };
runtime.Providers.Set("scripted", provider);
Llm.Default = runtime;            // for the statics; tests that do this must not run in parallel with others that use them
```
