# Changelog

All notable changes to this project are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Provider clients for Anthropic Messages, OpenAI (Responses; Chat Completions for compatible servers) and Gemini `generateContent`: chat, streaming, client and hosted tools, structured output, reasoning replay, caching, token counting, batch and embeddings; OpenAI-compatible presets (Ollama, vLLM, LM Studio, OpenRouter, Groq, DeepSeek, xAI, Mistral, Together); TypeSafe Jev. All verified on recorded fixtures.
- `RecursiveAI.OneLlm.JavaScript`: a OneEcma sandbox (fuel, allocation, wall-clock and string limits, a host allocation backstop, deterministic time and randomness on request) with a small `ctx` (log, granted file reads and writes, `fetch` through the egress policy, cost lines); JavaScript tool files with a `.tool.yaml` or an exported description; the `run_javascript` built-in; JavaScript hooks for a definition's `hooks:`; `decider.js` logic. See `docs/javascript.md`.
- Deployments: Azure OpenAI / Foundry, Amazon Bedrock (SigV4, event stream) and Vertex AI (service-account JWT) for Claude and Gemini; see `docs/deployments.md`.
- Image generation, text-to-speech, transcription and moderation clients, each with a costed built-in tool.
- One message model with a closed part union and stream events that build the same message as a whole call; unknown fields kept as extension data.
- The layered model catalog (`llm-provider.yaml`: shipped files, user files, code) with `decimal` line-item pricing, `asOf` dates and source URLs; three-tier token counting that reports its tier; typed errors; retries that honour `Retry-After`; `Secret` scrubbing.
- Prompt templates in the Go `text/template` language with a documented function set, cached parse trees and positioned errors.
- `Runtime`, the provider registry and the `Llm` happy path: ask, typed ask, streaming, embeddings, chat and scripted runs, each saved as an instance with cost, records and telemetry; a scripted provider for tests (`OneLlm.Testing`).
- Agents: the tool loop with parallel calls returned in call order, persisted approvals, steering, sub-agents, monitors, context strategies and C# hooks; `Llm.RunAgentAsync` and `Llm.StartAgentAsync`.
- Tools: the `ITool` contract, the `[Tool]` source generator and the signature-to-JSON-Schema table (`docs/tool-schemas.md`), a host-granted capability model (files, shell, network) and twelve BCL-only built-in tools.
- Deciders: `decider.yaml` definitions, three engines chosen by the catalog (TypeSafe Jev, OpenAI Decisions, any chat model), `forEach` and budget splitting, typed refusals, input-schema checks, runs as instances with replay, list and statistics, C# deciders with generated typed answers (`[Decider]`), `Llm.DecideAsync` and `ScriptedDecisionProvider` for tests.
- Instance stores (file, memory) and record sinks (JSONL, events); OpenTelemetry GenAI spans and metrics.
- `RecursiveAI.OneLlm.Web`: `fetch_webpage_markdown` on OneHtml.
- `RecursiveAI.OneLlm.EntityFrameworkCore` and `RecursiveAI.OneLlm.OneLite`: an instance store and a record sink each.
- `RecursiveAI.OneLlm.Extensions.AI`: the Microsoft.Extensions.AI bridge. OneLLM models as `IChatClient`, `IEmbeddingGenerator`, `ISpeechToTextClient`, `ITextToSpeechClient` and `IImageGenerator` (usage and cost in `AdditionalProperties`); any `IChatClient` or embedding generator as a OneLLM provider; `AIFunction` and `ITool` in both directions.
- Documentation: getting started, the "reads like" tables (`docs/reads-like.md`), per-package readmes, and `samples/DocSnippets`, which compiles the documentation's C# snippets.
- Repository skeleton: solution, core package, generator project, test, benchmark and sample projects.
