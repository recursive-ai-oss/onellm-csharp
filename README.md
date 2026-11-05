# OneLLM

**LLM inference and agent runtime for .NET.** One message model and one set of calls over Anthropic, OpenAI and Gemini (first-party and on Azure, Bedrock and Vertex) and OpenAI-compatible servers. A layered model catalog prices every call in `decimal` line items. Agents, scripts and deciders are files you can read, run, persist, fork and resume. The core depends on the base class library, OneYaml and OneImage only, and is trim and NativeAOT safe on its main paths.

NuGet: `RecursiveAI.OneLlm` (namespace `OneLlm`). Requires .NET 10.

> OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

This repository is under construction: the API can still change before the first release.

## What it is

- **Provider clients** for each provider's inference surface: chat, streaming, client and hosted tools, structured output, reasoning replay, caching, token counting, batch, embeddings, plus image generation, speech and moderation where the provider has them.
- **One runtime, three ways to run:** simple calls (`Ask`, `Stream`, `Chat`), scripts (`.flow` files: prompts in order, one call each) and agents (the tool loop with approvals, steering, sub-agents, monitors and context strategies). Every run is an instance saved in a store you choose.
- **Deciders:** typed choice, score and yes/no judgments on TypeSafe Jev, OpenAI Decisions or any chat model.
- **Cost on everything:** pricing is metadata (catalog files you can override), and every result, tool call and instance subtree carries a line-item `Cost`.
- **Safe by default:** loading a definition never grants anything. The host grants file roots, shell and network; the default is files under the working directory only.
- **Records and telemetry:** instance stores (file, memory, OneLite, Entity Framework Core), record sinks (JSONL, events, OneLite, EF) and OpenTelemetry GenAI spans and metrics.

## Install

```
dotnet add package RecursiveAI.OneLlm
```

Set the key of the provider you use: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `GEMINI_API_KEY`. See [getting started](docs/getting-started.md).

## The happy path

```csharp
using OneLlm;

string s      = await Llm.AskAsync("claude-sonnet", "Summarize this", Asset.File("brief.pdf"));
Invoice inv   = await Llm.AskAsync("gpt-6-luna", "Extract the invoice", Asset.File("scan.png"), AppJson.Default.Invoice);
await foreach (string piece in Llm.StreamTextAsync("gemini-flash", "Write a haiku")) Console.Write(piece);
float[] v     = await Llm.EmbedAsync("embedding-small", "reset my password");

using ChatSession chat = Llm.Chat("claude-sonnet");
await chat.AskAsync("Name a prime.");
await chat.AskAsync("And the next one?");

AskResult r = await Llm.GenerateAsync("gpt-6-luna", "Name three prime numbers.", []);
Console.WriteLine($"{r.Text} [{r.Usage?.InputTokens} in, {r.Usage?.OutputTokens} out, {r.Cost.Total}]");
```

`Invoice` is your record and `AppJson` a `JsonSerializerContext` that lists it, which keeps structured output trim and NativeAOT safe. Models are catalog names or aliases; `provider:model` says which provider. Agents, scripts and deciders:

```csharp
ScriptResult script = await Llm.RunScriptAsync("prompts/commit-message.flow", new Dictionary<string, object?> { ["diff"] = diff });
AgentResult  agent  = await Llm.RunAgentAsync("agents/clerk.yaml", new Dictionary<string, object?> { ["caseNo"] = "24-1234" });
DecisionResult d    = await Llm.DecideAsync("deciders/triage-ticket", new Dictionary<string, object?> { ["title"] = "Refund please" });
```

Under the statics is `Runtime`, which owns the catalog, providers, stores, sinks, tools, hooks and grants. See [the happy path and the runtime](docs/happy-path.md).

## Providers and deployments

Status is what the repository tests today: **recorded fixtures** replay request bytes and parsed results for every row; no row is verified against a live account yet.

| Provider | Deployments | Covered |
|---|---|---|
| Anthropic (Messages) | first-party, Amazon Bedrock, Vertex AI | chat, streaming, tools, hosted tools, structured output, thinking replay, caching, token counting, message batches |
| OpenAI (Responses; Chat Completions for compatible servers) | first-party, Azure OpenAI / Foundry | chat, streaming, tools, hosted tools, structured output, reasoning items, caching, batch, embeddings, images, speech, moderation, Decisions |
| Gemini (`generateContent`) | Gemini API, Vertex AI | chat, streaming, tools, hosted tools, structured output, thought signatures, caching, token counting, batch, embeddings, images, speech |
| OpenAI-compatible | any base URL; presets for Ollama, vLLM, LM Studio, OpenRouter, Groq, DeepSeek, xAI, Mistral, Together | what the server implements |
| TypeSafe Jev | first-party | decisions |

See [deployments](docs/deployments.md), [OpenAI](docs/providers-openai.md), [Gemini](docs/gemini.md) and [media](docs/media.md).

## Packages

All packages share one version.

| Package | Adds | Depends on |
|---|---|---|
| `RecursiveAI.OneLlm` | message model, catalog and pricing, providers and deployments, templating, runtime, agents, tools and generators, deciders, file and memory stores, JSONL and event sinks, OpenTelemetry | BCL, OneYaml, OneImage |
| `RecursiveAI.OneLlm.Web` | `fetch_webpage_markdown` | core, OneHtml |
| `RecursiveAI.OneLlm.Extensions.AI` | OneLLM models as `IChatClient` and friends; any `IChatClient` as a provider; `AIFunction` and `ITool` both ways | core, Microsoft.Extensions.AI.Abstractions |
| `RecursiveAI.OneLlm.EntityFrameworkCore` | instance store and record sink on your `DbContext` | core, EF Core |
| `RecursiveAI.OneLlm.OneLite` | instance store and record sink on a OneLite file | core, OneLite |

The source generators for `[Tool]` and `[Decider]` ship inside the core package.

## Documentation

| | |
|---|---|
| [Getting started](docs/getting-started.md) | keys, model names, first call, structured output, an agent with a C# tool, a decider, grants |
| [The happy path and the runtime](docs/happy-path.md) | the statics, instances, spend caps, scripted runs |
| [Agents](docs/agents.md) | the loop, approvals, steering, sub-agents, monitors, hooks |
| [Capabilities and built-in tools](docs/capabilities.md) | grants and the built-in tools |
| [Deciders](docs/deciders.md) | the three engines and the run APIs |
| [Pricing and counting](docs/pricing-and-counting.md) | catalog layers, line-item cost, token counts |
| [Templating](docs/templating.md) | the prompt template language |
| [Stores and sinks](docs/stores.md) | where instances and records go |
| [Telemetry](docs/telemetry.md) | OpenTelemetry names |
| [Errors and retries](docs/errors-and-retries.md) | typed errors, `Retry-After` |
| [Microsoft.Extensions.AI bridge](docs/extensions-ai.md) | interop both ways |
| [Reads like](docs/reads-like.md) | call-for-call tables from Microsoft.Extensions.AI, Microsoft Agent Framework and the vendor SDKs |
| [JavaScript tools and hooks](docs/javascript.md) | arrives with the `.JavaScript` package |
| [The `onellm` command line](docs/cli.md) | arrives with the `onellm` tool |
| [`spec/`](spec/) | the file formats: definitions, instances, catalog, messages, records |

## Build

```
dotnet build OneLlm.slnx
dotnet test --solution OneLlm.slnx
```

Build output goes to the directory in `ONELLM_ARTIFACTS`, or to `artifacts/` in the repository.

## Licence

`MIT OR Apache-2.0`, Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`.
