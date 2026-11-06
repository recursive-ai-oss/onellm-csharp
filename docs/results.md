# Results

What OneLLM measured about itself for the 1.0 release, and how. Every number below is OneLLM's own.

Run date: 2026-11-06. Commit: the 1.0.0 tag. Machine for the speed rows: AMD Ryzen 9 7950X3D, 16 physical and 32 logical cores, 125 GB, Linux Mint 22.3 (kernel 7.0.0-38), .NET SDK 10.0.403, runtime 10.0.14, BenchmarkDotNet 0.15.8, on an otherwise idle machine (1-minute load average between 1.1 and 8.4 during the runs).

## Speed

Method: BenchmarkDotNet suites in `bench/` against a local mock server that replays deterministic provider responses over loopback, so the numbers are client code and not a network or a vendor. The workloads, the exact settings and the way the gate runner records the machine and its load are in [`bench/README.md`](../bench/README.md). Each row is the mean of the measured iterations; allocations are bytes per operation from the memory diagnoser.

A request row is a whole non-streamed turn: build the request from a history of the stated size, serialize it, send it over loopback and parse a one-line answer. A stream row reads a 2,000-event text stream (divide by 2,000 for the per-event cost); a first-event row stops at the first text event of a 50-event stream.

| Row | Parameters | Mean | Allocated | Peak working set (MB) |
|---|---|---:|---:|---:|
| request-anthropic | 1k tokens | 50.0 us | 31.7 KB | 146 |
| request-anthropic | 50k tokens | 1.022 ms | 714.1 KB | 91 |
| request-anthropic | 200k tokens | 5.788 ms | 2.75 MB | 106 |
| request-gemini | 1k tokens | 37.0 us | 27.1 KB | 141 |
| request-gemini | 50k tokens | 779.1 us | 839.0 KB | 88 |
| request-gemini | 200k tokens | 1.835 ms | 3.27 MB | 100 |
| request-openai-chat | 1k tokens | 35.9 us | 15.1 KB | 164 |
| request-openai-chat | 50k tokens | 652.9 us | 291.2 KB | 89 |
| request-openai-chat | 200k tokens | 1.301 ms | 1.11 MB | 91 |
| request-openai-responses | 1k tokens | 38.1 us | 21.5 KB | 163 |
| request-openai-responses | 50k tokens | 692.5 us | 297.7 KB | 86 |
| request-openai-responses | 200k tokens | 1.422 ms | 1.12 MB | 91 |
| stream-anthropic | 2,000 events | 610.7 us | 33.7 KB | 105 |
| stream-gemini | 2,000 events | 699.5 us | 155.5 KB | 131 |
| stream-openai-chat | 2,000 events | 813.7 us | 639.7 KB | 140 |
| stream-openai-responses | 2,000 events | 621.0 us | 96.3 KB | 116 |
| first-event-anthropic | first text event | 54.8 us | 31.9 KB | 144 |
| first-event-gemini | first text event | 43.4 us | 29.4 KB | 143 |
| first-event-openai-chat | first text event | 45.7 us | 13.8 KB | 163 |
| first-event-openai-responses | first text event | 52.6 us | 22.2 KB | 147 |

### The agent loop

One run is eleven model turns (ten tool-calling turns and a final one) on the scripted provider, with the default runtime, an in-memory store and no record sinks: **78.2 us and 56.8 KB per run, 7.1 us and 5.2 KB per turn**. That is above the fastest managed reference by a factor of **7.92**, because every OneLLM run is a persisted instance with a snapshot, priced line items and events for each turn.

Floors, set at this first measurement and not to rise in later releases:

| Floor | Value |
|---|---|
| Per-turn save of a 200,000-token instance (append one turn, atomic file replace) | 7.22 ms, 4.39 MB allocated |
| Run with no hook (the eleven-turn run above) | 79.2 us, 56.8 KB |

One observer hook on the message point adds about 0 us and 1.8 KB per run (78.6 us, 58.6 KB). Keeping the retained instance in memory costs 79.3 us per run. The same run with the file store, which writes the instance after every turn, takes 94.5 ms and 621 KB.

### Other costs

| Row | Mean | Allocated |
|---|---:|---:|
| Serialize a 200,000-token instance with one more turn | 544 us | 3.52 MB |
| Load a 200,000-token instance | 1.98 ms | 7.61 MB |
| Save a never-written 200,000-token instance | 1.86 ms | 5.54 MB |
| Template: plain text / field substitution / range and if / embed | 47 ns / 210 ns / 4.6 us / 309 ns | |
| Template: cached parse lookup / cold parse | 55 ns / 7.6 us | |
| Build the shipped catalog / build and resolve one model | 1.47 ms / 1.49 ms | 2.26 MB |
| Token count, core estimate, 1k / 50k / 200k tokens | 2.2 us / 58 us / 388 us | |

Core ships no local tokenizer; its count comes from the provider's endpoint when one exists and otherwise from the calibrated estimate timed above.

## Conformance

Each feature of each provider, on each deployment that has one, is replayed from recorded fixtures: the request bytes OneLLM sends and the result it parses are both checked. Streaming and whole-response calls must build equal messages. The core test suite (1,304 tests: 1,239 passed, 65 skipped for platform reasons, 0 failed) includes all of these.

| Provider | Deployment | Fixture files replayed | Failed |
|---|---|---:|---:|
| Anthropic | first-party | 47 | 0 |
| Anthropic | Amazon Bedrock | 9 | 0 |
| Anthropic | Vertex AI | 6 | 0 |
| OpenAI | first-party | 67 | 0 |
| OpenAI | Azure OpenAI | 7 | 0 |
| Gemini | Gemini API | 57 | 0 |
| Gemini | Vertex AI | 6 | 0 |
| OpenAI-compatible | nine presets, two files each | 18 | 0 |

TypeSafe Jev is covered by the decider checks below. The Bedrock request signing passes the published SigV4 test suite, and the Vertex service-account token passes the published JWT vector.

## Parity and round trips

The reference corpus of definitions and instances is a private collection kept on the build machine; only counts are reported.

| Check | Result |
|---|---|
| Definition, flow, decider and instance files load and save without loss (extension data kept) | 5,473 files: 5,440 lossless, 0 lossy, 24 invalid input (a raw U+FEFF inside a block scalar), 9 removed during the run |
| Rendered prompts match the recorded rendering byte for byte | 600 sampled: 328 byte-equal, 271 not reproducible (scope not recorded, or sources changed since), 1 differing because the definition was edited after it was rendered; 0 engine differences |
| Template engine agrees with Go's `text/template` on generated templates (output and error class) | 12,858,638 templates in 1,680 s: 124 disagreements, of which 6 were fixed and 118 are documented differences (slices beyond the length but within capacity; strings cut mid-character) |
| Decider requests are byte-equal to the reference service's, and `decide` outputs agree | 336 of 336 requests byte-equal; outputs 144 of 144 for JavaScript logic and 144 of 144 for TypeScript logic; 35 of 35 TypeScript logic files load |
| Cost agrees with the reference calculator to the cent | 1,260 of 1,260 usages |

## Loop behaviour, security and AOT

| Check | Result |
|---|---|
| Loop scenarios (termination, parallel calls in call order, approvals across a restart, steering, sub-agents, monitors, compaction pairing, cache-prefix stability), on the scripted provider and a fake clock | 93 agent tests, all passed |
| Capability-model review | 28 findings (CAP-1 to CAP-28), all closed; documented residue: detached children after a timeout with an open shell, hard links, an allow-list entry is not a sandbox, the cost of the git role rule |
| Sandbox review | 16 findings (JS-1 to JS-16) closed; two availability findings (JS-17, JS-18) being fixed |
| NativeAOT publish of the sample apps (core: `Ask`, `Ask<T>`, an agent with generated tools, a file store; JavaScript; OneLite; the Microsoft.Extensions.AI bridge) | 0 warnings; all four run; the same samples also build and run in CI on Windows x64, Linux x64, Linux arm64 and macOS arm64 |
| Fuzz targets | Event-stream reader: 600 s, 47.4 million inputs, 0 findings. Template parser: 3.19 million inputs, 0 findings; the template differential above adds 12.86 million generated templates |

## Live runs

A small smoke against each provider's real API, run once at release under hard spend caps (`tools/live-smoke.sh`): chat, streaming, a client tool round trip, structured output, a reasoning model, embeddings, token counting and, for OpenAI, a Decisions call. Then each of the eight example agents ran its representative task once on an OpenAI and a Gemini model (`tools/examples-e2e.sh`). The first OpenAI smoke passed 7 of 8 (the Decisions case used a wrong model name, fixed in the tool); the second passed 8 of 8.

| Provider | Smoke | Spent in total (cap) |
|---|---|---|
| OpenAI | 8 of 8 cases passed | $0.0264 ($0.90) |
| Gemini | 7 of 7 cases passed | $0.198 ($0.45) |
| Anthropic | **Not run**: no API key was available for the release check | n/a |
| Azure OpenAI / Foundry | **Not run**: no Azure account; covered by recorded fixtures only | n/a |
| Amazon Bedrock | **Not run**: no AWS account; covered by recorded fixtures and the published SigV4 test vectors | n/a |
| Vertex AI | **Not run**: no Google Cloud account; covered by recorded fixtures and the published JWT vector | n/a |

Example agents, one run each. Outcome `task_complete` means the agent ended by calling `task_complete`; `answered` means it replied without calling it.

| Example agent | Model | Outcome | Turns | Cost | Seconds |
|---|---|---|---:|---:|---:|
| `cs-tool-writer` | gpt-5-nano | answered | 2 | $0.003912 | 77 |
| `js-tool-writer` | gpt-5-nano | task_complete | 8 | $0.003624 | 72 |
| `provider-writer` | gpt-5-nano | task_complete | 5 | $0.001828 | 42 |
| `agent-writer` | gpt-5-nano | answered | 1 | $0.001523 | 35 |
| `script-writer` | gpt-5-nano | task_complete | 1 | $0.001398 | 32 |
| `decider-writer` | gpt-5-nano | answered | 4 | $0.003123 | 64 |
| `hook-writer` | gpt-5-nano | answered | 1 | $0.002322 | 51 |
| `orchestrator` | gpt-5-nano | task_complete | 13 | $0.007200 | 130 |
| `cs-tool-writer` | gemini-3.5-flash-lite | task_complete | 26 | $0.030503 | 27 |
| `js-tool-writer` | gemini-3.5-flash-lite | task_complete | 13 | $0.016682 | 11 |
| `provider-writer` | gemini-3.5-flash-lite | task_complete | 19 | $0.022865 | 15 |
| `agent-writer` | gemini-3.5-flash-lite | task_complete | 9 | $0.011340 | 8 |
| `script-writer` | gemini-3.5-flash-lite | task_complete | 8 | $0.009396 | 6 |
| `decider-writer` | gemini-3.5-flash-lite | task_complete | 16 | $0.022134 | 17 |
| `hook-writer` | gemini-3.5-flash-lite | task_complete | 25 | $0.033729 | 31 |
| `orchestrator` | gemini-3.5-flash-lite | task_complete | 13 | $0.016196 | 11 |

The Gemini `cs-tool-writer` row is its second attempt; the first stopped at its per-run spend cap after $0.0348, which is counted in the Gemini total above. A row is a full success when the agent calls `task_complete`, the files its task names exist, and its own checks passed (see [the example agents](../agents/README.md)); the `answered` rows are runs of the small OpenAI model that stopped without finishing the protocol.
