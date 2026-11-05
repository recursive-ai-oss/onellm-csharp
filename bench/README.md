# OneLLM benchmarks

BenchmarkDotNet suites for OneLLM, run against a local mock server so they measure client code and not the network or a vendor's
servers. Nothing here needs an API key or the internet: every client talks to the mock server on loopback.

## What is measured

| Row | Suite | Workload |
|---|---|---|
| Request build and serialization | `RequestBench` | A whole non-streamed turn with a history of about 1k, 50k and 200k tokens (alternating user and assistant turns of ~400 characters), answered with one short line. Build, serialize, send over loopback, parse the small answer. |
| SSE throughput and per-event overhead | `StreamBench` | A 2,000-event text stream (8 characters per event, flushed every 8 events), after a 1k-token request. Per-event overhead is the mean divided by 2,000. |
| Time to first event | `FirstEventBench` | A 50-event stream; the client stops at the first text delta. The server flushes the first event alone. |
| Allocations per turn | all of the above | BenchmarkDotNet's memory diagnoser, bytes per operation. Peak working set of each benchmark process is recorded too. |
| Local token counting | `CountBench` | 1k, 50k and 200k tokens of text. The reference counts exactly with an o200k tokenizer. Core ships no local tokenizer (its local tier is an adapter), so the core row is the calibrated estimate, reported for scale and not compared. |
| Agent-loop overhead | `AgentBench` | Ten model turns that each call a no-op tool, then a final turn. OneLLM runs its scripted provider; the reference runs its agent over a stub chat client. Divide by 11 for the per-turn figure. `Own_NoHook` and `Own_OneObserver` show the cost of one observer hook. |
| Rows with no managed reference | `PersistenceBench`, `TemplatingBench`, `CatalogBench` | Per-turn save of a 200k-token instance, template rendering, shipped-catalog load. Reported on their own. |

Methods named `Ours_*` are OneLLM, `Ref_*` are references, and `Own_*`/`Info_*` are reported without a comparison. For each group the
report divides OneLLM's mean by the fastest reference's mean; the bar is a geometric mean of at most 1.00 over the groups with no group
above 1.25.

## References

Restored from NuGet into this project only, at these versions, and driven only through their documented public APIs, as black boxes.
Their source and package contents are never read.

| Package | Version | Used for |
|---|---|---|
| `OpenAI` | 2.15.0 | Responses and Chat Completions rows |
| `Anthropic` | 13.5.0 | Messages rows |
| `Google.GenAI` | 1.28.0 | `generateContent` rows |
| `Microsoft.Extensions.AI` | 10.11.0 | `IChatClient` rows |
| `Microsoft.Extensions.AI.OpenAI` | 10.11.0 | `IChatClient` over the OpenAI client |
| `Microsoft.Agents.AI` | 1.27.0 | agent-loop row |
| `Microsoft.ML.Tokenizers` | 2.0.1 | counting row (with `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.1) |
| `BenchmarkDotNet` | 0.15.8 | harness |

The suite is a regression and tuning aid for OneLLM. No comparison with any of these libraries is published.

## The mock server

`tools/OneLlm.MockServer` with `--synthetic` serves deterministic answers for Anthropic Messages, OpenAI Responses and Chat Completions,
and Gemini `generateContent` (whole, and as server-sent events) from synthetic data. `POST /_mock/config?events=N&bytes=B&flushEvery=K`
sets how many text deltas an answer has and how large they are; each suite sets what it needs in its setup. `MOCK_URL` (default
`http://127.0.0.1:5311`) tells the suites where it listens. Without `--synthetic` it replays recorded fixtures as before.

## Running

```sh
dotnet run -c Release --project bench/OneLlm.Bench -- smoke   # with the mock server running: every client reads the same text
bench/run.sh --group request --mode indicative --out results/try   # starts and stops the mock server itself
bench/run.sh --group all --out results/gate
```

`bench/run.sh` builds, starts the mock server, records the machine (CPU, logical cores, ISA flags, SDK, memory) and the load before,
during and after each group, runs the groups (`request`, `stream`, `agent`, `count`, `own`; each fits in about 20 minutes), and writes
`results.md` in the output directory with, per row, OneLLM's mean, each reference's mean, the ratio, allocations and peak working set.
`--mode indicative` uses BenchmarkDotNet's short job: it shows that every row runs and which rows are far off, and its numbers are not
for publication. Timing runs need an otherwise idle machine. Run a single suite by hand with
`dotnet run -c Release --project bench/OneLlm.Bench -- --filter '*StreamBench*'`.

On the shared build worker each group is submitted through the queue's benchmark class, one job per group.
