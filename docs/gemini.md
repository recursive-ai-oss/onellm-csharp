# Gemini provider notes

The Gemini API provider (`OneLlm.Providers.Gemini`) speaks `generateContent` and `streamGenerateContent?alt=sse` on `v1beta`. These notes record how its wire shapes map onto the message model, where the API reference is silent, and what the tests cover.

## Mapping to the message model

- **Turns and roles.** A user turn becomes a `user` content; an assistant turn becomes a `model` content. The results of an assistant turn's tool calls go into one following `user` content, as `functionResponse` parts in call order.
- **Call ids.** Gemini may omit `functionCall.id`. The provider then makes an id (`gemini-call-<responseId>-<n>`) so the loop can pair results; made-up ids are never sent back, and results are paired by name and position.
- **Thought signatures.** A `thoughtSignature` on a text, call or media part is kept as its own reasoning part (`Data.signature`, `Data.on` naming the part kind) placed just before the part it belongs to, and is attached to that part again on the next request. A thought summary (`thought: true`) is a reasoning part with `Display` set and `Data.on = "thought"`.
- **Hosted tools.** Code execution (`executableCode` + `codeExecutionResult`) and server-side `toolCall` + `toolResponse` become one hosted tool call each; the raw parts are kept in `Data.replay` and sent back unchanged. Google Search, Maps and file-search grounding become hosted calls with no replay data, plus `url_citation` annotations. Grounding offsets are UTF-8 byte offsets over the whole answer and are converted to character positions per text block.
- **Usage.** `promptTokenCount` includes cached tokens and, with the URL-context tool, `toolUsePromptTokenCount` is billed as input. The provider reports `InputTokens = prompt - cached + toolUsePrompt`, `CacheReadInputTokens = cached`, `OutputTokens = candidates + thoughts`. Modality counters (`inputAudioTokens`, `inputImageTokens`, ...) are subsets of the input total.
- **Extension data.** Unknown response fields, safety ratings, citation metadata and prompt feedback are kept under `Message.ExtensionData["gemini"]`.
- **Streaming.** Both paths run the same parser, so a streamed message equals the whole-response message. Function-call arguments arrive whole in one part; the reference documents no partial-argument stream.

## Request rules

- `RequestAdjuster` runs once per request against the catalog entry; its warnings, and the Gemini-specific ones below, land on `ChatResponse.Warnings`.
- Gemini-specific: no switch for `parallelToolCalls=false` and no end-user identifier on `generateContent` (both dropped and noted); effort `disabled` means budget 0, `dynamic` means -1 on budget models; levels are upper-cased on level models.
- Structured output uses `responseJsonSchema` unless the model's catalog entry sets `properties.schemaForm: responseSchema`, which converts the schema to the OpenAPI subset.
- `ServiceTier` is the top-level request field `serviceTier` (`flex`, `priority`; the reference lists it as an optional request field and an output-only usage field).
- A bad API key is answered with HTTP 400 `INVALID_ARGUMENT` (`API_KEY_INVALID`); it is mapped to an authentication error.

## Not settled by the reference

- Whether a `GenerateContentRequest` inside a batch needs its own `model` field (it is omitted; the batch names the model in the URL).
- The embeddings async batch (`asyncBatchEmbedContent`) is not implemented; `batchEmbedContents` is.
- Streamed partial function-call arguments (see above).

## Tests

`tests/OneLlm.Tests/Gemini/` holds a conformance case list (`GeminiCases*.cs`), replayed against fixtures in `testdata/providers/gemini/direct/<feature>/`. Fixtures are written from the API reference (their description says so), not recorded. To rewrite them after a deliberate request change, set `ONELLM_WRITE_FIXTURES` to that directory and run the conformance tests; read the diff. The upload fixture is written by hand, because the recorder drops the upload address header.
