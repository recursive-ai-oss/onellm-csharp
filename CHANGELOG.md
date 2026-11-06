# Changelog

All notable changes to this project are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Docs: the happy-path page uses catalog model names (`gemini-pro`, `embedding-small`); README and getting-started snippets name the namespaces they need; the README states which providers ran live at 1.0; package readmes link to the tagged docs.

## [1.0.0] - 2026-11-06

The first public release.

### Added

- **`RecursiveAI.OneLlm`** (core; depends on the .NET base library, OneYaml and OneImage):
  - Provider clients for Anthropic Messages, OpenAI (Responses; Chat Completions for compatible servers) and Gemini `generateContent`: chat, streaming, client and hosted tools, structured output, reasoning replay, caching, token counting, batch and embeddings. OpenAI Decisions and TypeSafe Jev for deciders. OpenAI-compatible presets for Ollama, vLLM, LM Studio, OpenRouter, Groq, DeepSeek, xAI, Mistral and Together.
  - Deployments: Azure OpenAI / Foundry, Amazon Bedrock (SigV4 and the event stream) and Vertex AI (service-account JWT) for Claude and Gemini. See `docs/deployments.md`.
  - Image generation, text-to-speech, transcription and moderation clients, each with a costed built-in tool.
  - One turn-shaped message model with a closed part union. Stream events build the same message a whole call returns, and unknown fields are kept as extension data.
  - A layered model catalog in `llm-provider.yaml` form (shipped files, then user files, then code), with `decimal` line-item pricing, `asOf` dates and source URLs.
  - Three-tier token counting that reports its tier.
  - Typed errors, retries that honour `Retry-After`, and `Secret` scrubbing.
  - Prompt templates in the Go `text/template` language, with a documented function set, cached parse trees and positioned errors. Known differences from Go are listed in `docs/templating.md`.
  - `Runtime` and the `Llm` happy path: ask, typed ask, streaming, embeddings, chat, scripted runs, agents and deciders. Every run is an instance with cost, records and telemetry.
  - A scripted provider for tests (`OneLlm.Testing`).
  - Agents:
    - the tool loop, with parallel calls returned in call order;
    - persisted approvals, steering and sub-agents;
    - monitors and context strategies;
    - C# hooks.
  - Tools: the `ITool` contract and the `[Tool]` source generator. The signature-to-JSON-Schema table is in `docs/tool-schemas.md`.
  - Built-in tools for files, shell, web, agents, judgment, media and working state. They run under a host-granted capability model (file roots, shell, network egress). A definition never grants a capability.
  - Deciders:
    - `decider.yaml` definitions, with three engines chosen through the catalog;
    - `forEach` and budget splitting, typed refusals, and input and output schema checks;
    - runs with replay, list and statistics;
    - C# deciders with generated typed answers (`[Decider]`), and `Llm.DecideAsync`.
  - Instance stores (file, memory) and record sinks (JSONL, events). OpenTelemetry GenAI spans and metrics.
- **`RecursiveAI.OneLlm.JavaScript`** (on OneEcma 1.1.1):
  - Covers JavaScript tool files, the `run_javascript` built-in, JavaScript hooks, and decider logic in `decider.js` or `decider.ts` (types erased in place, errors pointing into the `.ts` file).
  - Everything runs in a sandbox with fuel, allocation, wall-clock and string limits, per-run quotas, and a shared limiter that keeps capacity for hooks and decider logic. See `docs/javascript.md`.
- **`RecursiveAI.OneLlm.Web`:** `fetch_webpage_markdown`, on OneHtml.
- **`RecursiveAI.OneLlm.Extensions.AI`:** the Microsoft.Extensions.AI bridge, in both directions.
  - OneLLM models as `IChatClient`, `IEmbeddingGenerator`, `ISpeechToTextClient`, `ITextToSpeechClient` and `IImageGenerator`, with usage and cost in `AdditionalProperties`.
  - Any `IChatClient` or embedding generator as a OneLLM provider.
  - `AIFunction` and `ITool` in both directions.
- **`RecursiveAI.OneLlm.EntityFrameworkCore`** and **`RecursiveAI.OneLlm.OneLite`:** an instance store and a record sink each.
- **`RecursiveAI.OneLlm.Cli`:** the `onellm` tool, with `run`, `chat`, `instances`, `cost`, `catalog` and `examples`. Grants are given as explicit flags. It embeds eight example agents.
- **Documentation:** getting started; per-feature guides; the "reads like" tables (`docs/reads-like.md`); per-package readmes; and a results page (`docs/results.md`) with the speed, conformance, parity, AOT and live-run results and their method.

### Security

- The capability model and the JavaScript sandbox were each reviewed before this release, and every finding is closed or documented in `docs/capabilities.md` and `docs/javascript.md`. Those pages also list what the model does not protect against, for example hard links, and an allow-listed program that can itself run arbitrary commands.
- With `git` on the shell allow-list, either directly, behind a launcher, or through a git front end, git configuration and hook files are refused to the file tools.
