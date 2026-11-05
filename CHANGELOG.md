# Changelog

All notable changes to this project are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Deciders: `decider.yaml` definitions, three engines chosen by the catalog (TypeSafe Jev, OpenAI Decisions, any chat model), `forEach` and budget splitting, typed refusals, input-schema checks, runs as instances with replay, list and statistics, C# deciders with generated typed answers (`[Decider]`), `Llm.DecideAsync` and `ScriptedDecisionProvider` for tests.
- Repository skeleton: solution, core package, generator project, test, benchmark and sample projects.
- Deployments: Azure OpenAI / Foundry, Amazon Bedrock (SigV4, event stream) and Vertex AI (service-account JWT) for Claude and Gemini; see `docs/deployments.md`.
- `Runtime`, the provider registry and the `Llm` happy path: ask, typed ask, streaming, embeddings, chat and scripted runs, each saved as an instance with cost, records and telemetry; a scripted provider for tests (`OneLlm.Testing`).
- `RecursiveAI.OneLlm.Extensions.AI`: the Microsoft.Extensions.AI bridge. OneLLM models as `IChatClient`, `IEmbeddingGenerator`, `ISpeechToTextClient`, `ITextToSpeechClient` and `IImageGenerator` (usage and cost in `AdditionalProperties`); any `IChatClient` or embedding generator as a OneLLM provider; `AIFunction` and `ITool` in both directions.
