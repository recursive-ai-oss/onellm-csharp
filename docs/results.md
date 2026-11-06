# Results

What OneLLM measured about itself for the 1.0 release, and how. Every number below is OneLLM's own. **TBD** marks a number that is filled in from the release-check run and is not a claim until it is.

Run date: TBD. Commit: TBD. Machine: TBD (CPU, cores, memory, operating system, .NET SDK and runtime).

## Speed

Method: BenchmarkDotNet suites in `bench/` against a local mock server that replays deterministic provider responses over loopback, so the numbers are client code and not a network or a vendor. The workloads, the exact settings and the way the gate runner records the machine and its load are in [`bench/README.md`](../bench/README.md). Each row is the mean of the measured iterations; allocations are bytes per operation from the memory diagnoser.

<!-- speed-rows:begin -->
| Row | Parameters | Mean | Allocated | Peak working set (MB) |
|---|---|---:|---:|---:|
| TBD | TBD | TBD | TBD | TBD |
<!-- speed-rows:end -->

### The agent loop

<!-- agent-loop:begin -->
OneLLM's agent loop costs **TBD** per turn (floor: **TBD**). That is above the fastest managed reference by a factor of **TBD**, because every OneLLM run is a persisted instance with a snapshot, priced line items and events for each turn.
<!-- agent-loop:end -->

The two numbers that are floors, the per-turn save of a 200,000-token instance and the cost of a run with no hook, are recorded at the first measurement and must not rise in later releases.

## Conformance

Each feature of each provider, on each deployment that has one, is replayed from recorded fixtures: the request bytes OneLLM sends and the result it parses are both checked. Streaming and whole-response calls must build equal messages.

| Provider | Deployment | Fixture rows passed | Failed |
|---|---|---:|---:|
| Anthropic | first-party | TBD | TBD |
| Anthropic | Bedrock | TBD | TBD |
| Anthropic | Vertex | TBD | TBD |
| OpenAI | first-party | TBD | TBD |
| OpenAI | Azure OpenAI | TBD | TBD |
| Gemini | Gemini API | TBD | TBD |
| Gemini | Vertex | TBD | TBD |
| OpenAI-compatible presets | each preset | TBD | TBD |
| TypeSafe Jev | first-party | TBD | TBD |

## Parity and round trips

| Check | Result |
|---|---|
| Definition, flow, decider and instance files load and save without loss (extension data kept) | TBD files, TBD lossy |
| Rendered prompts match the recorded rendering byte for byte | TBD matched, TBD differing |
| Template engine agrees with Go's `text/template` on generated templates (output and error class) | TBD templates, TBD disagreements |
| Decider requests are byte-equal to the reference service's, and `decide` outputs agree | TBD cases, TBD differing |
| Cost agrees with the reference calculator to the cent | TBD usages, TBD differing |

## Loop behaviour, security and AOT

| Check | Result |
|---|---|
| Loop scenarios (termination, parallel calls in call order, approvals across a restart, steering, sub-agents, monitors, compaction pairing, cache-prefix stability) | TBD scenarios passed |
| Capability-model and sandbox reviews | TBD findings, TBD open |
| NativeAOT publish of the sample app (`Ask`, `Ask<T>`, an agent with generated tools, a file store) | TBD warnings; runs: TBD |
| Fuzz targets (SSE, wire readers, template parser, catalog loader) | TBD executions, TBD crashes |

## Live runs

A small smoke against each provider's real API, run once at release under hard spend caps (`tools/live-smoke.sh`): chat, streaming, a client tool round trip, structured output, a reasoning model, embeddings, token counting and, for OpenAI, a Decisions call. Then each of the eight example agents runs its representative task once on an OpenAI and a Gemini model (`tools/examples-e2e.sh`).

| Provider | Smoke | Spent (cap) |
|---|---|---|
| OpenAI | TBD of TBD cases passed | TBD ($0.90) |
| Gemini | TBD of TBD cases passed | TBD ($0.45) |
| Anthropic | **Not run**: no API key was available for the release check | n/a |
| Azure OpenAI / Foundry | **Not run**: no Azure account; covered by recorded fixtures only | n/a |
| Amazon Bedrock | **Not run**: no AWS account; covered by recorded fixtures and the published SigV4 test vectors | n/a |
| Vertex AI | **Not run**: no Google Cloud account; covered by recorded fixtures and the published JWT vector | n/a |

| Example agent | Model | Outcome | Turns | Cost | Seconds |
|---|---|---|---:|---:|---:|
| `cs-tool-writer` | TBD | TBD | TBD | TBD | TBD |
| `js-tool-writer` | TBD | TBD | TBD | TBD | TBD |
| `provider-writer` | TBD | TBD | TBD | TBD | TBD |
| `agent-writer` | TBD | TBD | TBD | TBD | TBD |
| `script-writer` | TBD | TBD | TBD | TBD | TBD |
| `decider-writer` | TBD | TBD | TBD | TBD | TBD |
| `hook-writer` | TBD | TBD | TBD | TBD | TBD |
| `orchestrator` | TBD | TBD | TBD | TBD | TBD |

A row is a success when the agent ends by calling `task_complete`, the files its task names exist, and its own checks passed (see [the example agents](../agents/README.md)).
