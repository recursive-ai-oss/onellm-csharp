# Changelog

All notable changes to this project are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - TBD

The first public release. The date is set when it is published.

### Security

- With `git` on the shell allow-list, git configuration and hook files (`config`, `config.worktree`, `commondir`, `gitdir`, `hooks/`, `info/`) are refused to the file tools in any directory; roots below a control directory are protected; extended-length (`\\?\`) roots work on Windows; `COM0`/`LPT0` are reserved names; names are folded for U+0131, U+017F and U+212A on Windows and macOS.
- JavaScript sandbox: `ctx.addCost` refuses negative, non-finite and overflowing amounts, bounds text length, and takes at most `MaxCostLines` lines per run; a negative recorded total no longer stops an instance from resuming. Hook files named by a definition are read through the run's read grants, and a refused file is never echoed. Resuming an instance whose definition has interceptor hooks in a runtime without a hook source fails with `CapabilityMissingException` instead of running without them. Host failures reach scripts, models and records as short plain errors, with no stack frames, absolute paths or .NET type names. A result nested past 64 levels is a `JsSandboxException` of kind `Result`.

- Windows: 8.3 short names, trailing dots and spaces, other drives and network shares, superscript device names, and cmd.exe/PowerShell/batch argument chaining under an allow-list are handled and tested; the example agents are found in the CLI on Windows; indented JSON written by the state tools uses LF.
- Roots that are themselves a git directory or a version-control directory are protected from the file tools; `.bzr`, `.jj`, `_darcs`, `.fslckout`, `.sl` and `CVS` join the protected names; `apply_patch` refuses a patch whose targets any protection refuses before writing anything.
- A git directory is recognised by what it holds (`HEAD`, `objects`, `refs`), so a separate git dir or a bare repository is protected from the file tools like `.git`; the runtime's state directories (`.onellm`, the file store and JSONL sink directories) are now unreadable as well as unwritable for the file tools, and are left out of listings and searches; an instance whose recorded grants are missing or damaged does not run.
- Shell: allow-listed and approval-gated commands start from a minimal environment and the model sets no variable unless the host names it (`BuiltinOptions.ShellModelEnvironmentNames`, `ShellInheritedEnvironmentNames`); file tools refuse writes into `.git`, `.hg`, `.svn` and `.gitmodules` (`Capabilities.WithRepoControlWrites()` allows them) and into the runtime's own state directories (`Capabilities.WithProtectedPaths(...)`; a `Runtime` protects its file store and JSONL sink directories). Intersecting `AnyPublicHosts` with a `localhost` or address entry no longer yields loopback. Connection pools are kept per network policy, redirects to another origin keep only plain headers and refuse to resend a body, and more IPv6 translation forms count as non-public. Glob patterns match in linear time. `list_files` no longer prints symlink targets, `run_decider` checks links in a decider directory against the read roots, and terminal approval prompts escape control characters.

### Changed

- `RecursiveAI.OneLlm.JavaScript` builds on OneEcma 1.1.0. `decider.ts` now runs: types are erased in place (annotations, interfaces, type aliases, `as`, `satisfies`, `import type`), errors point into the `.ts` file, and syntax that cannot be erased is refused at load with the construct, file, line and column. Error explanation is on again, now that OneEcma budgets the replay. The engine's allocation budget covers closures, errors, promises, collections and queued jobs, BigInt multiplication honours the time limit, and the 40 memory-bomb patterns we test all end inside the engine's limits; the host allocation backstop stays on as defence in depth. A run still ends at its time limit when the engine cannot be interrupted: the caller gets the `Time` failure, the engine thread is abandoned and counted (`JsSandbox.AbandonedRuns`, `JsSandbox.AbandonedRunsStillRunning`).

### Added

- JavaScript sandbox run lifetime and aggregate limits: a run that has ended (returned, cancelled, timed out or abandoned) can no longer write files, add cost, log or send requests, and its pending requests are cancelled; `JsRunLimiter` bounds concurrent runs and total bytes across runs and fails the excess with `JsSandboxFailure.Busy`; per-run quotas for file and response text, written bytes and files, log volume and requests; pipes and other special files are refused instead of hanging a thread; loading a tool, hook or decider file runs under the same time and memory guard as a run; the host memory backstop now measures live heap growth, not allocation churn.
- Vertex AI Gemini `countTokens` and `embedContent` (Vertex's request bodies; one embedding per call); decider outputs are checked against a declared output schema (`DeciderOutputException`); `SpendLimitReachedException` for an Anthropic account spend limit; strip and adjust notices on `EmbeddingResponse.Warnings`.

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
- Documentation: a results page (`docs/results.md`) with the speed, conformance, parity, AOT and live-run results and their method; getting started, the "reads like" tables (`docs/reads-like.md`), per-package readmes, and `samples/DocSnippets`, which compiles the documentation's C# snippets.
- Repository skeleton: solution, core package, generator project, test, benchmark and sample projects.
