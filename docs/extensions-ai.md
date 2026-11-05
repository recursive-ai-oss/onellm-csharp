# Microsoft.Extensions.AI bridge

`RecursiveAI.OneLlm.Extensions.AI` connects OneLLM to the `Microsoft.Extensions.AI` abstractions (version 10.11 of
`Microsoft.Extensions.AI.Abstractions`) in both directions. It references the OneLLM core package and that one package.

| Direction | What | Entry point |
|---|---|---|
| OneLLM → Microsoft.Extensions.AI | A model as `IChatClient` | `runtime.AsChatClient("claude-sonnet")` |
| | A model as `IEmbeddingGenerator<string, Embedding<float>>` | `runtime.AsEmbeddingGenerator(model)` |
| | A model as `ISpeechToTextClient`, `ITextToSpeechClient`, `IImageGenerator` | `runtime.AsSpeechToTextClient(model)`, `AsTextToSpeechClient`, `AsImageGenerator` |
| | A `OneLlm` tool as an `AIFunction` | `tool.AsAIFunction(timeProvider)` |
| Microsoft.Extensions.AI → OneLLM | Any `IChatClient` (and an optional embedding generator) as a provider | `runtime.Providers.AddChatClient("name", client)` |
| | An `AIFunction` as a tool | `function.AsTool()` |

The speech, text-to-speech and image abstractions are marked experimental by Microsoft.Extensions.AI (`MEAI001`). The bridge opts in
inside one source file; code that uses `OneLlmSpeechToTextClient`, `OneLlmTextToSpeechClient` or `OneLlmImageGenerator` sees the same
diagnostic on the Microsoft.Extensions.AI types it touches.

## A OneLLM model as a client

```csharp
var rt = new Runtime();
IChatClient client = rt.AsChatClient("claude-sonnet");
ChatResponse r = await client.GetResponseAsync("Summarize this", new ChatOptions { MaxOutputTokens = 500 });
decimal dollars = (decimal)r.AdditionalProperties![OneLlmProperties.CostTotal]!;
```

The model resolves through the runtime's catalog, so requests are adjusted to what the model accepts (a parameter it rejects is dropped and
reported in `OneLlmProperties.Warnings`) and every call is priced. `ChatOptions.ModelId` names another catalog model for one request. The
client does not run functions: wrap it with Microsoft.Extensions.AI's function-invoking middleware, or use a OneLLM agent.

Over a bare provider the client sends the model name as given and prices nothing:
`new OneLlmChatClient(provider, "model-name", timeProvider)`; pass a `ResolvedModel` instead of the name to adjust and price.

### Options

| `ChatOptions` | OneLLM `ChatRequest` |
|---|---|
| `Instructions`, `System`/`Developer` messages | `System` (joined with a blank line, instructions first) |
| `MaxOutputTokens`, `Temperature`, `TopP`, `StopSequences` | the same fields |
| `Tools` (`AIFunctionDeclaration`) | `Tools`; the declaration's `strictJsonSchema` property sets `Strict` |
| `Tools` (`HostedWebSearchTool`, `HostedCodeInterpreterTool`) | `HostedTools` `web_search`, `code_interpreter` |
| `ToolMode` | `ToolChoice` (`Auto`, `None`, `Required`, or the named tool) |
| `AllowMultipleToolCalls` | `ParallelToolCalls` |
| `ResponseFormat` | `ResponseFormat`: text, any JSON, or the JSON schema with its name |
| `Reasoning.Effort` | `Reasoning.Effort` (`none`, `low`, `medium`, `high`, `xhigh`) |
| `Reasoning.Output` | `Reasoning.Summary`: `Summary` is `auto`, `Full` is `detailed`. Another spelling goes through `onellm.extraParameters`. |
| `AdditionalProperties["onellm.cache"]`, `["onellm.serviceTier"]`, `["onellm.userId"]`, `["onellm.extraParameters"]` | `Cache`, `ServiceTier`, `UserId`, `ExtraParameters` |
| `Seed`, `TopK`, `FrequencyPenalty`, `PresencePenalty`, `ConversationId` | not carried; the first four are named in a warning |

### What comes back

Text, tool calls and reasoning arrive as `TextContent`, `FunctionCallContent` and `TextReasoningContent` in the order the model produced
them, so the order of parts inside a turn is the order of the `Contents` list. Streaming sends text and reasoning as deltas; a tool call
arrives complete, once its arguments have finished streaming. A stream ends with one update that carries `FinishReason` and one
`UsageContent`, so Microsoft.Extensions.AI's `ToChatResponse()` counts usage once.

| OneLLM | Microsoft.Extensions.AI |
|---|---|
| `Usage.TotalInputTokens` (new + cache read + cache write) | `UsageDetails.InputTokenCount` |
| `Usage.CacheReadInputTokens` | `CachedInputTokenCount` |
| `Usage.CacheCreationInputTokens` | `AdditionalCounts["onellm.cacheCreationInputTokens"]` |
| `Usage.Counters["reasoningTokens"]` | `ReasoningTokenCount` |
| `Usage.OutputTokens` | `OutputTokenCount` |
| `FinishReason` `Stop`, `Length`, `ToolCalls`, `ContentFilter` | the matching `ChatFinishReason`; `Refusal` and `PauseTurn` become `refusal` and `pause_turn` |
| `TextPart` annotations | `CitationAnnotation` (title, URL, snippet, span; the original `Annotation` is `RawRepresentation`) |
| `AssetPart` with inline bytes / a public URL | `DataContent` / `UriContent`; a stored asset with neither is kept whole in `OneLlmPartContent` |
| `FileRefPart` | `HostedFileContent` |
| `JsonPart` | `TextContent` holding the JSON text |
| `RefusalPart` | `TextContent` with `onellm.refusal = true` |
| `ReasoningPart` | `TextReasoningContent`: `Text` is the display text, `ProtectedData` the provider's opaque JSON, `onellm.provider` names the provider |
| hosted tool calls, provider parts | `OneLlmPartContent` carrying the `Part` |

### Keys in `AdditionalProperties`

On the `ChatResponse` (and on the final `UsageContent` of a stream), and on an embedding result:

| Key | Value |
|---|---|
| `onellm.cost` | the call's `OneLlm.Messages.Cost`: line items, each with its counter, quantity, unit price and amount, and the total |
| `onellm.cost.total` | the total as a `decimal`, in US dollars |
| `onellm.warnings` | `string[]`: stripped parameters, a missing price, options the bridge could not carry |

Cost is present only when the model is priced in the catalog. Money is always `decimal`.

The lossy edges, carried so they survive a round trip through Microsoft.Extensions.AI and back:

| Key | On | Meaning |
|---|---|---|
| `onellm.when` | `ChatMessage`, `FunctionCallContent` | the entry's `when` expression |
| `onellm.injected`, `onellm.injectedBy` | `FunctionCallContent` | the call was injected by the runtime rather than asked for by the model, and by what |
| `onellm.routine` | `FunctionCallContent` | the tool source reference the call resolved to |
| `onellm.invalidInputs` | `FunctionCallContent` | the raw argument text when the model's arguments were not a JSON object (`Arguments` is then `null`) |
| `onellm.isError`, `onellm.resultParts` | `FunctionResultContent` | the tool reported an error; the original result parts, kept in process |

The speech, text-to-speech and image types carry usage on their `Usage` property; speech-to-text and text-to-speech responses also carry
the cost keys. `ImageGenerationResponse` has no property dictionary, so an image call's cost is not on it: use the runtime's result.

## A client as a provider

```csharp
var rt = new Runtime { Catalog = ModelCatalog.Create([CatalogLayer.FromFile(CatalogLayer.User, "my-models.yaml")]) };
rt.Providers.AddChatClient("acme", someChatClient, someEmbeddingGenerator);   // someEmbeddingGenerator is optional
AskResult a = await rt.AskAsync(new AskRequest { Model = "acme-large", Prompt = "hi" });   // priced from my-models.yaml
```

The catalog entry supplies what the client cannot: the model's name patterns, context window, capabilities and prices. Its `name` must be
the provider name passed to `AddChatClient`. The client's usage becomes OneLLM `Usage`: `InputTokenCount` is the whole input and
`CachedInputTokenCount` the part read from a cache, so the cached tokens are priced at the cache-read rate and not twice. Tool definitions
go out as declarations, results come back as function-result content, and a streamed update list becomes OneLLM stream events that
build the same `Message` a non-streaming call builds.

## Tools

`tool.AsAIFunction(time)` passes the tool's name, description, input schema and output schema through unchanged, so
`function.JsonSchema` equals `tool.Definition.InputSchema`. The function runs the tool in a context with **no capabilities**; pass a
`Func<string, ToolContext>` to grant the files, shell or network the tool needs. A tool that reports an error makes the function throw
`InvalidOperationException` with the tool's message, which Microsoft.Extensions.AI's function-invoking client reports to the model.
`function.AsTool()` does the reverse: the schema is read from `JsonSchema`, an `ApprovalRequiredAIFunction` becomes a tool with
`RequiresApproval`, and a result becomes text (a string), a JSON part (a JSON value) or parts (a content list). The name must be a valid
tool name (letters, digits, `_`, `-`, at most 64): a function named `has.dot` is refused with `ArgumentException`.

Tools made with `AIFunctionFactory.Create` use reflection and are not trim or AOT safe; that is a property of
Microsoft.Extensions.AI, not of the bridge. Declarations, `AsAIFunction` and `AsTool` over a hand-written or generated `ITool` are.

## Reads like

A table for readers who know the Microsoft.Extensions.AI calls. It maps calls in the public documentation to OneLLM calls and makes no
comparison.

| Microsoft.Extensions.AI | OneLLM |
|---|---|
| `chatClient.GetResponseAsync(messages, options)` | `Llm.GenerateAsync(model, prompt, attachments)`; `Runtime.AskAsync(new AskRequest { … })`; `Llm.Chat(model)` for turns |
| `chatClient.GetStreamingResponseAsync(...)` | `Llm.StreamAsync(model, prompt)`; `Llm.StreamTextAsync(model, prompt)` |
| `GetResponseAsync<T>` (structured output) | `Llm.AskAsync<T>(model, prompt, typeInfo)` |
| `ChatOptions.ModelId` | the model argument: a catalog name, alias or `provider:model` |
| `ChatOptions.Tools` with `AIFunctionFactory.Create` | `[Tool]` methods (generated, AOT safe) or `Tool.Create(delegate)`; `ITool` |
| `FunctionInvokingChatClient` | the agent runners and their tool loop |
| `UsageDetails` | `Usage` and `Cost` (line items) on every result |
| `IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync` | `Llm.EmbedAsync(model, text)` |
| `ISpeechToTextClient.GetTextAsync` | the transcription provider capability (`ISpeechToTextProvider`) |
| `ITextToSpeechClient.GetAudioAsync` | the speech provider capability (`ITextToSpeechProvider`) |
| `IImageGenerator.GenerateAsync` | the image provider capability (`IImageProvider`) |
| `ChatClientBuilder` pipelines | `runtime.AsChatClient(model)` as the inner client of any pipeline |
| `AIFunction` | `ITool` (`function.AsTool()`, `tool.AsAIFunction(time)`) |
| `OpenTelemetryChatClient` | OneLLM emits GenAI telemetry itself; see `telemetry.md` |
