# JavaScript: tools, hooks, decider logic and `run_javascript`

The `RecursiveAI.OneLlm.JavaScript` package runs JavaScript on [OneEcma](https://github.com/recursive-ai-oss/oneecma-csharp) in a sandbox. It adds four things to the core runtime: tool files, hook files, decider logic and the `run_javascript` built-in. It references the core package and OneEcma only.

```csharp
runtime.Tools.Add(JsTools.Load("tools/convert-units.js"));      // a tool file
runtime.Tools.Add(JsTools.RunJavaScript());                      // the run_javascript built-in
runtime.DefinitionHooks = new JsHookSource(timeProvider: runtime.TimeProvider);   // hooks: in definitions
Decider d = JsDeciderLogic.LoadDecider("deciders/triage-ticket");                // decider.yaml + decider.js
```

## The sandbox

Every call builds a fresh engine, so nothing a script leaves behind (globals, prototypes, promises) reaches the next call. The engine has no file, network, process, environment, timer or module access of its own. A script sees the ECMAScript built-ins, `console`, `input` (snippets only) and `ctx`:

| `ctx` member | Needs | Does |
|---|---|---|
| `ctx.instanceId`, `ctx.toolCallId` | – | Ids of the run and the call. |
| `ctx.log(...)` | – | Writes a line to the tool log. |
| `ctx.readFile(path)` | `fileRead` | Reads UTF-8 text (at most `MaxFileBytes`). |
| `ctx.writeFile(path, text)` | `fileWrite` | Writes UTF-8 text, creating folders. |
| `ctx.fetch(url, { method, headers, body })` | `network` | Resolves to `{ status, ok, headers, text, truncated }`. |
| `ctx.addCost({ counter, quantity, unitPrice, per?, multiplier?, label?, source? })` | – | Adds a cost line to the call. Pass prices as strings for exact decimals; a number is read through its shortest text. Quantity, unit price and multiplier must not be negative or non-finite, text fields are at most 256 characters, and one run adds at most `MaxCostLines` (100) lines; anything else throws a `TypeError`. |

Each function checks twice: the tool must have **declared** the capability (`requires`), and the host must have **granted** it. File paths go through the same guard as the built-in file tools (symlinks resolved, re-checked after the open); `fetch` goes through the egress policy (hosts checked before sending, every redirect hop re-checked, loopback and private addresses refused unless granted). An undeclared capability fails with `'fileRead' in the tool's requires list`; an ungranted one fails with the usual "is not available" message. Nothing a definition says grants anything.

### Limits

All are in `JsSandboxOptions`; a limit ends the run with a `JsSandboxException` that script cannot catch.

| Limit | Default | Kind |
|---|---|---|
| `Fuel` (steps: calls, loop iterations, regexp backtracking, long built-ins) | 200 million | `Fuel` |
| `MaxAllocatedBytes` (bytes the engine allocated over the run, not live bytes) | 256 MiB | `Memory` |
| `MaxHostAllocatedBytes` (live growth of the heap since the run started; backstop) | 1 GiB | `Memory` |
| `TimeLimit` (wall clock on the call's `TimeProvider`; a tool's `timeout` shortens it) | 10 s | `Time` |
| `MaxCallDepth` | 1,000 (catchable `RangeError`) | `Script` |
| `MaxStringLength` | 16 million UTF-16 units | `Script` (`RangeError`) |
| `MaxFileBytes`, `MaxFetchBytes` (one file or body) | 1 MiB | error to the script |
| `MaxHostResultBytes` (all file text and response bodies one run holds, two bytes a character) | 32 MiB | `RangeError` to the script |
| `MaxWriteBytesPerRun`, `MaxWritesPerRun` | 8 MiB, 256 files | `RangeError` to the script |
| `MaxFetchesPerRun` | 16 requests | `RangeError` to the script |
| `MaxLogChars` (`ctx.log`; one notice, then dropped) | 64,000 | – |
| `Limiter` (see below) | shared: 8 runs, 2 GiB | `Busy` |
| `MaxResultChars` (JSON returned) | 1 million | `Result` |
| `MaxCostLines` (`ctx.addCost` calls per run) | 100 | script `TypeError` |
| `MaxOutputChars` (console output; head and tail kept) | 10,240 | – |

**Memory is a budget, not a ceiling.** The engine charges growth a script controls to `MaxAllocatedBytes` before allocating: array storage, typed arrays and buffers, joined and stringified text, property storage, closures, errors, promises, environments, collection entries, the symbol registry and queued jobs (a queue entry is given back when it runs). The array, buffer, string, `Map`, `Set`, `Symbol.for` and promise-queue bombs we test (40 patterns) all end in a `Memory` limit, a `RangeError`, or a `Fuel` limit from the engine alone. The budget counts bytes requested over the run, not bytes alive, so live memory can pass it: in our runs the largest peak was about twice the 256 MiB budget (object property storage and `Map`/`Set` growth), and OneEcma documents about 1.1x on x64 and under 2x on macOS arm64 for its own accounting. Size the process, not just the option, for that. The host backstop reads how much larger the .NET heap is than when the run started and ends the run when that growth passes `MaxHostAllocatedBytes` (default 1 GiB, about four budgets). It looks every time the thread has allocated a sixteenth of the limit, and only when the collector has run since the last look, so a loop that allocates gigabytes of short-lived objects and keeps none (a busy-wait on `Date.now()`, say) is not counted: churn is not memory. A growth over the limit is confirmed after a full collection before the run is ended. The heap is the whole process's, so scripts that overlap see each other's growth, and so does any other work the host does while they run; that errs on the side of stopping. It is taken between steps, so one very large single allocation can still happen before it is seen, and OneEcma 1.1.1 charges what it used to miss: collation keys (96 bytes a character), host-returned strings at the boundary (about 4 bytes a character), and a .NET out-of-memory becomes an uncatchable `Memory` limit. In our probe six parallel 15-million-character `localeCompare` runs, which peaked at 12.7 GB, now stop in about 200 ms at the allocation budget with a 415 MB peak. Live heap can still pass a budget by about 1.1x on x64 and under 2x on arm64 (OneEcma documents it), and `localeCompare` on strings above about 4 million characters each is refused by the engine. A script that holds memory without allocating more is not bounded by either.

**The time limit holds even when the engine does not yield.** The engine checks the clock between steps and, since OneEcma 1.1.0, inside BigInt multiplication and exponentiation, but only between chunks of work: since OneEcma 1.1.1 a BigInt multiply stops about a second after it starts and fuel scales with the length of long-string comparisons, sorts and collation, so in our probes (eight scripts comparing 15-million-character strings in a loop, `localeCompare` loops, BigInt squaring) the engine ends every run by itself and nothing is abandoned. The host therefore races every run against the time limit plus 250 ms, and against the caller's token. When the race is lost the caller gets the `Time` failure (or the `OperationCanceledException`) at once, and the engine's thread is abandoned: its eventual result or error is discarded. The cost is real: that thread keeps using a CPU core until OneEcma can interrupt the operation (the run is closed, so the thread can no longer act and stops at its next clock check). `JsSandbox.AbandonedRuns` counts runs abandoned in the process and `JsSandbox.AbandonedRunsStillRunning` those still executing, so a host can watch for it and stop accepting such scripts. A script that stops by itself within the 250 ms grace is reported as usual, with its position.

The run is started on a thread of its own (not a thread-pool thread, so a few busy scripts cannot starve the host's other work, including hooks), so a long script never blocks the caller, and cancelling the caller's token stops it.

**Error explanation is on.** The sandbox leaves `ExplainErrors` at its default, so a failed run is replayed with tracing and the report gains where a value came from, "did you mean" suggestions and frame locals. OneEcma 1.1.0 gives that replay a deterministic work budget; past it the report says `explanation unavailable: the explanation exceeded its work budget` and keeps the plain report. (OneEcma before 1.1.0 had no such budget, which is why the sandbox once turned it off.)

### A run that has ended acts no more

Every host function (`ctx.readFile`, `writeFile`, `fetch`, `addCost`, `log`) first checks that its run is still open, and a run is closed on every way out: it returned, threw, hit a limit, was cancelled, or was abandoned because the engine did not stop when asked. A closed run's functions throw `the run has ended`, and a request still in flight is cancelled. Closing waits (up to a second) for a function that was already running, so by the time the caller has the result nothing is still acting for it: no file is written, no cost line is added to the `ToolContext`, nothing is logged and no request lands afterwards, including a `ctx.fetch` the script never awaited. A script the host gave up on also stops itself: once its run is closed the clock the engine reads for its time limit reads far past any limit, so the thread ends at its next check instead of running on.

### Many runs at once

A model can start many runs in one turn (`run_javascript` and JS tools are parallel-safe), so a host bounds them together with a `JsRunLimiter`:

```csharp
var limiter = new JsRunLimiter(maxConcurrentRuns: 4, maxTotalBytes: 1L << 30, maxWait: TimeSpan.FromSeconds(5));
var tool = JsTools.RunJavaScript(new JsSandboxOptions { Limiter = limiter });   // share one limiter across a runtime's sandboxes
```

Sandboxes that name none use `JsRunLimiter.Shared` (8 runs, 2 GiB, a 10 s wait). A run waits up to `maxWait` for a slot and then fails with `JsSandboxFailure.Busy`; it never waits for ever. A run the host abandoned keeps its slot until its thread has really ended, so stuck scripts cannot pile up threads past the limit. The byte budget counts the larger of the file text and response bodies runs hold (they live in the heap, so they are not added to the growth a second time) and the largest live growth any active run has measured; the run that passes it fails with `Busy`. OneEcma charges the same strings to each run's own `MaxAllocatedBytes`; the limiter's count is the cross-run view of them, and `MaxHostResultBytes` is the explicit per-run quota on file and response text.

One caller cannot take every slot. A caller is the root of an instance tree: sub-agents count against the instance that started them (the limiter follows `CallerInstanceId`), so starting children does not multiply the cap. Hooks and decider logic may use all of them, but tools and `run_javascript` leave `reservedForInterceptors` (default 2) free, so interceptors do not fail closed while a flood of tool runs holds the rest, and one caller (an instance id) holds at most `maxRunsPerCaller` (default 3) of the others. Abandoned runs count against their caller until the thread ends, Hook calls are capped per owner too (`maxInterceptorsPerOwner`, default 2), calls of one decider's logic share a cap per decider and decider logic as a whole may use half the slots, and one slot always stays free for hooks of other owners. Loading a file takes a slot as well, and a definition may name at most 16 hook files (`JsHookSource.MaxHooksPerDefinition`). Abandoned runs count against their owner until the thread ends, which is the strike rule: a caller whose scripts keep hanging is refused until they have stopped. `ActiveRuns` and `ChargedBytes` show the state.

Loading a tool, hook or decider file runs its top level once, and that runs under the same guard as a run: the time limit, fuel and memory limits, and the host giving up on it if it does not stop, so a file that loops at the top level fails to load instead of hanging the loader.

`ctx.readFile` and `ctx.writeFile` open only regular files. The open runs on a helper thread under a 2 s limit; a path that does not open in time (a pipe or similar waits for a peer) is refused, and the blocked open is released by opening the other end, so no thread is left behind. A special file that does open but cannot seek is refused too.

### Reproducible runs

`JsSandboxOptions { Deterministic = true, FixedTime = …, RandomSeed = … }` makes `Date.now()`, `new Date()` and `Math.random()` repeat. By default the clock is the `ToolContext`'s `TimeProvider`, and randomness is unseeded.

### Errors

A failure becomes a `JsSandboxException` whose message is OneEcma's compact report: the error, `file:line:column`, the source line and the most useful hint. `Kind` says whether it was a script error, a syntax error, a limit or a bad result; `File`, `Line` and `Column` are set when known. Reports name the file as given to the loader (the file's name, never its path) and carry no .NET stack. A message is cut at `JsSandboxException.MaxMessageChars` (10,240) with a marker saying how much was left out, so a script that throws a huge string cannot push it into model context or records. As a tool result the message starts with `Error: `.

## Tool files

A tool file is an ES module that exports `invoke(inputs, ctx)` and describes itself in one of two ways.

```js
// reshape.js: the exported `tool` object is the description.
export const tool = {
  name: "reshape_json",
  description: "Picks fields out of a list of objects.",
  inputSchema: { type: "object", properties: { items: { type: "array" } }, required: ["items"] },
  requires: [],            // fileRead, fileWrite, network
};
export function invoke({ items }) { return items.map(i => ({ id: i.id })); }
```

or a `reshape.tool.yaml` beside the file with the same keys (the YAML wins when both exist). Keys: `name` (default: the file's name), `description` (required), `inputSchema` (default: no arguments), `outputSchema`, `requires`, `endsLoop`, `requiresApproval`, `costHint` (USD; a string for an exact decimal), `parallelSafe`, `timeout` (seconds).

`invoke` may be `async`. A string result is text; `undefined` is an empty result; anything else is a JSON part (and the typed result when `endsLoop`). A throw becomes an error result. `JsTools.Load` reads the file as the host, runs its top level once under the sandbox limits, and fails with a `BadDefinitionException` (file, line, column) if it does not parse, has no `invoke`, or its description is invalid. `JsTools.LoadDirectory` loads every `.js` file in a folder. A tool reports `ICapabilityTool.Requires` from its `requires` list, so a definition that names it is refused in strict mode when the runtime has not granted that much.

Modules cannot import other files; a tool is one file. Shell commands are not available to JavaScript.

`samples/js-tools/` has a unit converter (`.tool.yaml` description) and a JSON reshaper (exported description).

## `run_javascript`

`JsTools.RunJavaScript()` is the built-in the model calls with `{ code, input? }`. The code runs as the body of an async function (top-level `await` and `return`), with `input` and `ctx` in scope and every `ctx` function available **where the caller's grants allow it**. The result is the console output followed by `=> <returned JSON>`; with neither, `(no output)`. Output is capped like the shell tool's.

## Hooks

Set `Runtime.DefinitionHooks` to a `JsHookSource`. A definition's `hooks: [{ file, points? }]` entries (paths relative to the definition) are read **through the run's read grants** (the same roots, symlink resolution and protected names as `read_file`), so a definition cannot load a hook from outside the directories the host granted; a refused file is a `CapabilityMissingException` that names the path the definition gave and never shows any of the file. They load as hooks that run under the host's hooks (see `docs/agents.md` for ordering and failure rules). A hook file exports one function per point it handles: `beforeInference`, `afterInference`, `beforeToolCall`, `afterToolCall`, `beforeCompaction`, `onMessage`, `onStateChange`, `onCost`. Each gets a JSON snapshot and `ctx`.

**Resuming without a hook source fails closed.** A saved instance whose definition lists hooks resumes only in a runtime that can run them. Without `Runtime.DefinitionHooks` it throws `CapabilityMissingException` naming the package, whether the instance is new or resumed. The one exception is a definition whose hooks all name observer points (`onMessage`, `onStateChange`, `onCost`) and a lenient load, which skips them with a warning. `JsHookSource.LoadFile`, `JsTools.Load` and `JsDeciderLogic.Load` take a path the host chose and read it as the host.

An interceptor returns nothing (continue) or `{ action, reason?, … }`:

| Point | Actions and fields |
|---|---|
| `beforeInference` | `modify` with `system`, `maxOutputTokens`, `temperature`, `messages`; `respond` with `message` or `text`; `block` |
| `afterInference` | `modify` with `message` or `text`; `block` |
| `beforeToolCall` | `modify` with `inputs`; `respond` with `result` or `text`; `requireApproval`; `block` |
| `afterToolCall` | `modify` with `result` (`{ text }` or `{ json }`, optional `isError`) or `text`; `block` |
| `beforeCompaction` | `block` |

**Interceptors fail closed:** a throw, a limit, an unknown action or a payload of the wrong shape blocks the step and is recorded. **Observers fail open:** a failure is recorded and the run goes on. Every modify, block and respond is recorded, as for hooks written in C#. An optional `export const hook = { requires: ["fileRead"] }` declares the capabilities the hook's `ctx` may use; without it a hook has none.

## Decider logic

`JsDeciderLogic.Load("decider.js")` implements `IDeciderLogic` over a module that exports `prepare(inputs)` and/or `decide({ answers, inputs })`. `LoadDecider(directory)` reads `decider.yaml` and a `decider.js` beside it. Both functions are pure (no grants, fresh engine per call), may be `async`, and return JSON. A property that is `undefined` in the result is written as `null` and `undefined` itself as `null`, which is the form compiled decider bundles have always produced. A bundle built from TypeScript works as it is, because it is JavaScript.

`decider.ts` works when it uses only **erasable** TypeScript: annotations, `interface` and `type`, `as`, `as const`, `satisfies`, `import type` (a module whose only import is `import type` loses it). OneEcma erases the types in place, replacing them with spaces and keeping line breaks, so errors and reports point at the original lines and columns of the `.ts` file. Nothing is type-checked. `LoadDecider(directory)` uses `decider.js` when it exists, otherwise `decider.ts`; `JsDeciderLogic.Load` takes either by extension. Syntax that cannot be erased is refused when the decider loads, with a `BadDefinitionException` that names the construct and gives file, line and column: `enum`, a `namespace` with values, constructor parameter properties, `import x = require` and `export =`, `<T>x` casts and parameter decorators. Each has a one-line rewrite in OneEcma's TypeScript page. A `.ts` module may only import types: the logic is one file.

## Trimming and NativeAOT

The package is annotated trim- and AOT-compatible: OneEcma is, and nothing here uses reflection, `Reflection.Emit` or runtime code generation (JSON goes through the core package's source-generated metadata and OneYaml). `samples/JavaScriptAotSmoke` loads a tool file, runs it and `run_javascript`, and hits a limit; it publishes with zero warnings and runs.

## What errors reveal

A host function that fails (a missing file, a refused request, a bad argument) reaches the script as a plain `Error` with a short message such as `ctx.readFile: no such file`: no host stack frames, no absolute paths, no .NET exception type names. The model and the records see the same text. A result nested deeper than 64 levels is a `JsSandboxException` of kind `Result`. An instance whose recorded cost total is negative resumes with the total counted as zero.
