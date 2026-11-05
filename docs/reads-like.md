# Reads like

If you know another .NET LLM library, these tables show where the same job lives in OneLLM. Each row names a call or concept **as its own documentation presents it**, and the OneLLM call that does the same job. They are a map, not a compatibility layer: OneLLM does not copy the other libraries' shapes, and the tables make no claim about how the libraries compare. Where a library documents a feature OneLLM does not have, the row says so.

The sources are public documentation pages only; they are listed at the end.

In the examples, `rt` is a `Runtime` (`Llm.Default` for the statics), and `model` is a catalog name or alias.

## Microsoft.Extensions.AI

Using OneLLM *as* a `Microsoft.Extensions.AI` client, or putting a client behind OneLLM, is the job of `RecursiveAI.OneLlm.Extensions.AI`; see [the bridge](extensions-ai.md). The table below is for code that would call OneLLM directly instead.

| Microsoft.Extensions.AI documents | OneLLM |
|---|---|
| `client.GetResponseAsync("What is AI?")` | `Llm.AskAsync(model, "What is AI?")` for the text; `Llm.GenerateAsync(model, "What is AI?", [])` for usage and cost |
| `GetResponseAsync(messages)` with a `List<ChatMessage>` you keep and extend (`history.AddMessages(response)`) | `Llm.Chat(model)` returns a `ChatSession` that keeps the conversation as one saved instance: `await chat.AskAsync(...)` per turn. Or build a `ChatRequest` and call `IChatProvider.SendAsync` yourself. |
| `GetStreamingResponseAsync("...")`, iterating `ChatResponseUpdate` | `Llm.StreamTextAsync(model, "...")` for text pieces; `Llm.StreamAsync(model, "...")` for all stream events, then `await stream.Result` |
| `ToChatResponse(updates)` to compose a streamed response | not needed: the stream builds the same `Message` a whole call builds, available as `stream.Result` |
| `ChatOptions.Temperature`, `MaxOutputTokens` | `AskRequest.Temperature`, `AskRequest.MaxOutputTokens` through `Runtime.AskAsync`; `temperature` and `maxTokens` in a definition |
| `ChatOptions.ModelId` | the `model` argument; `provider:model` picks the provider |
| `ChatOptions.ConversationId` (a service-held conversation) | not used: OneLLM keeps the conversation in the instance and sends the history. `Llm.Chat` gives the same continuity. |
| `ChatOptions.AdditionalProperties` / `RawRepresentationFactory` | `AskRequest.ExtraParameters` (merged into the request body) |
| `AIFunctionFactory.Create(method)` and `ChatOptions.Tools` | a `[Tool]` method on a partial class, registered with `rt.Tools.Add<T>()` and named in a definition's `references`. `Tool.Create(delegate)` is the reflection quick path (not AOT safe). |
| `UseFunctionInvocation()` (run the tools the model asks for, loop) | an agent: `Llm.RunAgentAsync(path, inputs)`. The loop runs calls in parallel unless a tool says otherwise, returns results in call order, and keeps approvals, steering and a spend cap. |
| `UseDistributedCache()` (reuse a response for an identical history) | the opt-in result cache, `Runtime.Cache`, off by default; provider prompt caching is separate and described in [pricing and counting](pricing-and-counting.md) |
| `UseOpenTelemetry()` | always on: `ActivitySource` and `Meter` named `OneLlm`, GenAI names; subscribe with `AddSource("OneLlm")`. See [telemetry](telemetry.md). |
| `UseLogging()` | record sinks (`JsonlRecordSink`, `EventRecordSink`) and opt-in scrubbed raw turn logs; see [stores](stores.md) |
| `DelegatingChatClient` middleware | hooks (`Runtime.Hooks`): before and after inference, before and after tool calls, on message, on cost; see [agents](agents.md) |
| `IEmbeddingGenerator<string, Embedding<float>>` | `Llm.EmbedAsync(model, text)` / `EmbedAsync(model, texts)`, or `IEmbeddingProvider.EmbedAsync` |
| `IImageGenerator` (experimental) | `IImageProvider.GenerateImageAsync`, the `generate_image` built-in tool; see [media](media.md) |
| `ChatResponseFormat` / structured output | `Llm.AskAsync<T>(model, prompt, typeInfo)`, native structured output on each provider |
| `UsageDetails` on a response | `AskResult.Usage` and `AskResult.Cost` (line items and a `decimal` total) |
| Chat reduction (`MessageCountingChatReducer`, `SummarizingChatReducer`, experimental) | context strategies on a definition (`clearToolResults`, `compact`, `providerNative`); see [agents](agents.md) |
| Chat routing and failover (experimental) | not provided as a type; `ProviderRegistry` lets you register any provider under a name, and an `Extensions.AI` `IChatClient` can be one |

## Microsoft Agent Framework

| Agent Framework documents | OneLLM |
|---|---|
| Create an `AIAgent` from a client with a model and `instructions`; `agent.RunAsync("...")` | a definition (`model`, `systemPrompt`) run by `Llm.RunAgentAsync(path, inputs)`, which returns an `AgentResult`; or `rt.StartAgentAsync` for a live `AgentRun` |
| Agent instructions as a string | `systemPrompt`, a template rendered when the run starts (see [templating](templating.md)) |
| Function tools (`AIFunction`s given to the agent) | `[Tool]` methods; `references: - tools: [name, ...]` in the definition; MCP tool sources are not in this release |
| Multi-turn conversations with an agent session | an instance: the conversation is saved after every step; `Llm.Chat` for plain chat, and `rt.GetAgentAsync(instanceId)` to pick an agent run up again, including after a restart |
| Persisting and resuming conversations | `IInstanceStore` (file, memory, OneLite, EF Core); instance files are readable YAML; fork and duplicate with `rt.ForkAsync` / `rt.DuplicateAsync` |
| Structured output from an agent | `outputSchema` on a definition with a script run (`ScriptResult.Output`); `AskAsync<T>` for a single call |
| Middleware that intercepts agent actions | hooks; interceptors fail closed, observers fail open, and every intervention is recorded |
| Harness agent: planning and todo tracking | the `todo` built-in tool |
| Harness agent: file access and memory | the file built-ins under granted roots; the `memory` built-in |
| Harness agent: context compaction | context strategies, including a scripted `compact` definition you can override |
| Harness agent: don't-ask-again tool approval | a tool marked `RequiresApproval`, or a hook returning `RequireApproval`; approvals persist across restarts. There is no "don't ask again" memory; approve per call. |
| Harness agent: observability | OpenTelemetry spans and metrics, record sinks, per-instance and per-subtree cost |
| Multi-agent patterns | sub-agents (`run_agent_async`, `await_agent`, `message_agent`) in one `Runtime`, each child bounded by its parent's grants |
| Workflows (functional and graph-based) | not provided. Scripts (`.flow`) run prompts in sequence; graph workflows are a later item. |
| Hosted MCP tools, A2A | not in this release |

## OpenAI .NET library

The OpenAI documentation page shows a `ResponsesClient` created with a key, `CreateResponseAsync(model, input)` and `GetOutputText()`. The rest of the table follows the API guides the same documentation set links to.

| OpenAI documents | OneLLM |
|---|---|
| `new ResponsesClient(key)`; `CreateResponseAsync("gpt-...", "Say 'this is a test.'")`, then `GetOutputText()` | `await Llm.AskAsync("gpt-...", "Say 'this is a test.'")`; the key is read from `OPENAI_API_KEY` |
| Responses API with conversation state | the `OpenAIProvider` speaks Responses; the history is sent from the instance |
| Streaming responses | `Llm.StreamTextAsync` / `Llm.StreamAsync` |
| Function calling | `[Tool]` methods in an agent; or `ChatRequest.Tools` and read `ToolCalls` yourself at the low level |
| Structured output | `Llm.AskAsync<T>` |
| Reasoning models, reasoning effort | `thinking: { effort: ... }` in a definition; `AskRequest.Reasoning`; reasoning items are kept and replayed |
| Embeddings | `Llm.EmbedAsync` |
| Moderation | `IModerationProvider.ModerateAsync`, the `moderate` tool |
| Image generation | `IImageProvider.GenerateImageAsync`, the `generate_image` tool |
| Text to speech, speech to text | `ITextToSpeechProvider.SynthesizeAsync`, `ISpeechToTextProvider.TranscribeAsync` |
| Batch | `IBatchProvider.SubmitBatchAsync`, `GetBatchAsync`, `GetBatchResultsAsync` |
| Counting tokens | `rt.TokenCounter.CountAsync(request, model, provider)`; every count says which tier produced it |
| Decisions API | `Llm.DecideAsync` with a decider on `openai-decisions` models; see [deciders](deciders.md) |
| Azure OpenAI libraries | `AzureOpenAIEndpoint` on `OpenAIProvider`; see [deployments](deployments.md) |
| Prompt caching | `cache` on a message; cached tokens are priced at their own rate |

## Anthropic C# SDK

The Anthropic documentation shows `new AnthropicClient()` (key from `ANTHROPIC_API_KEY`), a `MessageCreateParams` with `MaxTokens`, `Messages` and `Model`, `client.Messages.Create(parameters)` returning content blocks, `CreateStreaming`, `WithOptions` for per-call settings, `AsIChatClient`, and separate packages for Bedrock and Vertex.

| Anthropic documents | OneLLM |
|---|---|
| `client.Messages.Create(new() { Model, MaxTokens, Messages = [...] })`, then reading text blocks | `Llm.AskAsync("claude-sonnet", "Hello, Claude")`; `max_tokens` is set from the catalog (`AskRequest.MaxOutputTokens` to change it) |
| `Messages.CreateStreaming(parameters)` | `Llm.StreamTextAsync("claude-sonnet", ...)` or `Llm.StreamAsync` |
| `AnthropicClient { ApiKey = ... }` | the key comes from `ANTHROPIC_API_KEY`; an explicit key goes to `new AnthropicDirectDeployment(new Secret(key))` inside an `AnthropicProvider`, registered with `rt.Providers.Set("anthropic", provider)` |
| `ANTHROPIC_BASE_URL`, `BaseUrl` | `AnthropicOptions.BaseAddress`, or a catalog preset |
| `MaxRetries`, `Timeout`, `WithOptions(...)` | `RetryPolicy` on the provider options (`AnthropicOptions.Retry`; honours `Retry-After`); `Runtime.AgentRetry` for the loop; a `CancellationToken` on every call. See [errors and retries](errors-and-retries.md). |
| Exceptions: bad request, unauthorized, rate limit, 5xx | typed errors: `AuthException`, `RateLimitException`, `ContextOverflowException`, `ContentFilterException`, `RefusedException`, and so on; see [errors and retries](errors-and-retries.md) |
| Response validation | unknown response fields are kept as extension data rather than failing |
| `AsIChatClient("claude-...")` | `rt.AsChatClient("claude-sonnet")` in `RecursiveAI.OneLlm.Extensions.AI` |
| Message Batches (`Messages.Batches`) | `IBatchProvider` on the Anthropic provider |
| Token counting | `ITokenCountingProvider.CountTokensAsync`, or `rt.TokenCounter` |
| Extended thinking, tool use, prompt caching | reasoning parts with their signatures are replayed on the next turn; `[Tool]` methods; `cache` on messages |
| `AnthropicBedrockClient`, `Anthropic.Vertex` packages | `BedrockDeployment` and `VertexAnthropicDeployment` inside the same package; see [deployments](deployments.md) |
| OpenTelemetry spans with `AddSource("com.anthropic.sdk.csharp*")` | `AddSource("OneLlm")`, `AddMeter("OneLlm")`, for every provider alike |

## Google GenAI (Gemini)

The Gemini documentation pages this table was written from show their client samples in Python, JavaScript, Go and Java, not .NET; for the .NET library, read its own documentation for method names. The Gemini API operations those pages document map as follows.

| Gemini API documents | OneLLM |
|---|---|
| `generateContent` | `Llm.AskAsync("gemini-flash", ...)`; the `GeminiProvider` speaks `generateContent` |
| Streaming | `Llm.StreamTextAsync` / `Llm.StreamAsync` |
| Function calling | `[Tool]` methods in an agent; thought signatures are kept and replayed |
| Structured outputs | `Llm.AskAsync<T>` |
| Thinking | `thinking:` in a definition, `AskRequest.Reasoning` |
| Embeddings (`gemini-embedding-001`, `gemini-embedding-2`) | `Llm.EmbedAsync("gemini-embedding-001", ...)` |
| Token counting | `ITokenCountingProvider.CountTokensAsync` on the Gemini provider |
| Context caching | `cache` on messages; see [Gemini](gemini.md) |
| Batch API | `IBatchProvider` on the Gemini provider |
| Image generation, text to speech | `IImageProvider`, `ITextToSpeechProvider`; the Interactions API and Live API are not implemented |
| Vertex AI | `VertexGeminiEndpoint` with `GoogleBearerAuth`; see [deployments](deployments.md) |
| OpenAI compatibility endpoint | not needed: use the Gemini provider; or an OpenAI-compatible preset can point at any compatible URL |

## Sources

Read for this page (public documentation, read for interoperability under each site's terms; no code copied):

- `https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai` and `https://learn.microsoft.com/en-us/dotnet/ai/ichatclient` — Microsoft Learn, site terms.
- `https://learn.microsoft.com/en-us/agent-framework/overview/` — Microsoft Learn, site terms.
- `https://developers.openai.com/api/docs/libraries` — OpenAI developer docs, site terms.
- `https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp` — Anthropic docs, site terms.
- `https://ai.google.dev/gemini-api/docs/libraries`, `.../text-generation`, `.../embeddings`, `.../streaming` — Google developer docs, content CC BY 4.0, code samples Apache-2.0.
