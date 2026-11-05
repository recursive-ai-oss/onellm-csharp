# OpenAI and OpenAI-compatible providers

`OpenAIProvider` talks to the OpenAI API (or an Azure OpenAI / Foundry resource through `AzureOpenAIEndpoint`, see [deployments](deployments.md)) with the **Responses** protocol by default, or **Chat Completions** with `OpenAIApi.ChatCompletions`. `OpenAICompatibleProvider` speaks Chat Completions to any server that implements it. Both implement chat, streaming, embeddings, token counting (OpenAI only) and the Batch API. Streaming and non-streaming calls build equal `Message`s.

## What the Responses codec does

- **Stateless.** `store: false` is always sent (`OpenAIOptions.Store` changes it). For reasoning models `include: reasoning.encrypted_content` is added so reasoning items can be replayed on the next turn. Reasoning items are replayed verbatim from `ReasoningPart.Data` when their provider is `openai`; other providers' reasoning is dropped.
- **Usage.** `InputTokens` is the API's `input_tokens` minus cached and cache-write tokens, so the counters never overlap. Counters: `cacheReadInputTokens`, `cacheCreationInputTokens`, `cacheCreation30mInputTokens` and `cacheCreation12hInputTokens` (subsets of the cache-write counter), `reasoningTokens` (subset of output), `toolCalls.<tool>` (`web_search`, `file_search`, `code_interpreter`, `image_generation`, `mcp`) and image-generation token counters. The catalog declares the cache-write subsets with `pricing.subsetOf`.
- **Tools.** Function tools become `function` items; free-text "custom" tools are `HostedToolDefinition("custom")` and arrive as tool calls with `{"input": text}`. Hosted tools (`web_search`, `file_search`, `code_interpreter`, `image_generation`, `mcp`) come back as `HostedToolCallPart`; an output item the codec does not know is kept as a `ProviderPart`.
- **Caching.** The cache key goes to `prompt_cache_key` (64 characters at most). A TTL goes to `prompt_cache_options.ttl` (`30m` or `12h`) on models whose catalog entry has the property `promptCacheOptions`, otherwise to `prompt_cache_retention: "24h"` for a TTL of an hour or more. Turning caching off cannot be asked of the API and is reported as a warning.
- **Refused, not sent:** stop sequences on Responses (use Chat Completions), reasoning efforts a model does not list, tools or structured output on models whose entry says no. **Stripped with a warning:** temperature/top_p on models that take none, reasoning settings on non-reasoning models, token budgets for reasoning.
- **Warnings.** `RequestAdjuster` runs once per request; its notices, plus the codec's own, are on `ChatResponse.Warnings`. Streaming and embeddings calls apply the same adjustments but have no channel for the notices yet.

## Pricing in the shipped catalog

Service tiers are `pricingMultiplier`s (flex 0.5, priority where the published ratio is clean), batch is the `batch` multiplier 0.5, and the long-context rate (above 272K input tokens) is a tier with multipliers. Web and file search calls are per-1000 items. Code-interpreter container sessions are not priced yet. Prices carry `asOf` 2026-11-05 and the pricing page as source.

## Compatible-server presets

A preset supplies the base URL, the key's environment variable and the quirks recorded as catalog `properties`. Presets accept any model name. Presets for servers on the local machine (`local: true`) are priced at zero.

| Preset | Base URL | Key variable | Quirks in the catalog |
|---|---|---|---|
| `ollama` | `http://localhost:11434/v1` | none | local |
| `vllm` | `http://localhost:8000/v1` | none | local |
| `lmstudio` | `http://localhost:1234/v1` | none | local |
| `openrouter` | `https://openrouter.ai/api/v1` | `OPENROUTER_API_KEY` | `usage.cost` is copied to `Usage.Cost` |
| `groq` | `https://api.groq.com/openai/v1` | `GROQ_API_KEY` | |
| `deepseek` | `https://api.deepseek.com` | `DEEPSEEK_API_KEY` | no JSON schema, no reasoning effort |
| `xai` | `https://api.x.ai/v1` | `XAI_API_KEY` | |
| `mistral` | `https://api.mistral.ai/v1` | `MISTRAL_API_KEY` | `required` tool choice is sent as `any`; no stream usage; no reasoning effort |
| `together` | `https://api.together.ai/v1` | `TOGETHER_API_KEY` | |

Base URLs come from each vendor's documentation. The quirks for Mistral and DeepSeek, the vLLM default port and reasoning-field names are recorded from the docs only partly and are to be checked against live servers; the recorded fixtures for the presets are written from the references, so they pin the request each preset sends and how its answers decode, not the server's behaviour.

## Registering the catalog

`OpenAICatalog.Register()` adds the files to `CatalogLoader`'s shipped list so a default catalog loads them; `OpenAICatalog.Shipped()` returns the layer directly for tests and custom catalogs.
