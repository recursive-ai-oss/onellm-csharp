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
| `ctx.addCost({ counter, quantity, unitPrice, per?, multiplier?, label?, source? })` | – | Adds a cost line to the call. Pass prices as strings for exact decimals; a number is read through its shortest text. |

Each function checks twice: the tool must have **declared** the capability (`requires`), and the host must have **granted** it. File paths go through the same guard as the built-in file tools (symlinks resolved, re-checked after the open); `fetch` goes through the egress policy (hosts checked before sending, every redirect hop re-checked, loopback and private addresses refused unless granted). An undeclared capability fails with `'fileRead' in the tool's requires list`; an ungranted one fails with the usual "is not available" message. Nothing a definition says grants anything.

### Limits

All are in `JsSandboxOptions`; a limit ends the run with a `JsSandboxException` that script cannot catch.

| Limit | Default | Kind |
|---|---|---|
| `Fuel` (steps: calls, loop iterations, regexp backtracking, long built-ins) | 200 million | `Fuel` |
| `MaxAllocatedBytes` (bytes the engine allocated over the run, not live bytes) | 256 MiB | `Memory` |
| `MaxHostAllocatedBytes` (what .NET allocated on the running thread; backstop) | 1 GiB | `Memory` |
| `TimeLimit` (wall clock on the call's `TimeProvider`; a tool's `timeout` shortens it) | 10 s | `Time` |
| `MaxCallDepth` | 1,000 (catchable `RangeError`) | `Script` |
| `MaxStringLength` | 16 million UTF-16 units | `Script` (`RangeError`) |
| `MaxFileBytes`, `MaxFetchBytes` | 1 MiB | error to the script |
| `MaxResultChars` (JSON returned) | 1 million | `Result` |
| `MaxOutputChars` (console output; head and tail kept) | 10,240 | – |

**Memory is a budget, not a ceiling.** Since OneEcma 1.0.1 the engine charges growth a script controls (array storage, typed arrays and buffers, joined and stringified text, property storage, finalization registrations) to `MaxAllocatedBytes` before allocating, so the array, buffer, string, `Map` and `Set` bombs we test end in a `Memory` limit from the engine alone. The budget counts bytes requested over the run, not bytes alive, and a few things are charged little or nothing: the symbol registry (`Symbol.for` in a loop) and the promise job queue (`Promise.resolve().then(...)` in a loop) grow without being charged, and closure-heavy scripts can hold several times the budget. The host backstop counts what .NET allocates on the engine's thread and cancels the run when it passes `MaxHostAllocatedBytes`; it is what ends those patterns, and it stays on as defence in depth. It is checked on the engine's clock reads (about as often as the time limit), so one very large single allocation can still happen before it fires. One thing neither limit covers: a script that holds memory without allocating more.

**The time limit holds even when the engine does not yield.** The engine checks the clock between steps, and one long built-in operation is a single step (repeated BigInt squaring reaches multiplications that run for minutes). The host therefore races every run against the time limit plus 250 ms, and against the caller's token. When the race is lost the caller gets the `Time` failure (or the `OperationCanceledException`) at once, and the engine's thread is abandoned: its eventual result or error is discarded. The cost is real: that thread keeps using a CPU core until OneEcma can interrupt the operation. `JsSandbox.AbandonedRuns` counts runs abandoned in the process and `JsSandbox.AbandonedRunsStillRunning` those still executing, so a host can watch for it and stop accepting such scripts. A script that stops by itself within the 250 ms grace is reported as usual, with its position.

The run is started on the thread pool, so a long script never blocks the caller, and cancelling the caller's token stops it.

**Error explanation is off.** The sandbox creates every engine with `ExplainErrors = false`. With it on, OneEcma replays a failed run with full tracing to enrich the report, and that replay is not bounded by the run's limits until OneEcma 1.1.0. Reports keep the error, file, line and column and the source line; they lose the "where did this value come from" and "did you mean" additions.

### Reproducible runs

`JsSandboxOptions { Deterministic = true, FixedTime = …, RandomSeed = … }` makes `Date.now()`, `new Date()` and `Math.random()` repeat. By default the clock is the `ToolContext`'s `TimeProvider`, and randomness is unseeded.

### Errors

A failure becomes a `JsSandboxException` whose message is OneEcma's compact report: the error, `file:line:column`, the source line and the most useful hint. `Kind` says whether it was a script error, a syntax error, a limit or a bad result; `File`, `Line` and `Column` are set when known. Reports name the file as given to the loader (the file's name, never its path) and carry no .NET stack. As a tool result the message starts with `Error: `.

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

Set `Runtime.DefinitionHooks` to a `JsHookSource`. A definition's `hooks: [{ file, points? }]` entries (paths relative to the definition) load as hooks that run under the host's hooks (see `docs/agents.md` for ordering and failure rules). A hook file exports one function per point it handles: `beforeInference`, `afterInference`, `beforeToolCall`, `afterToolCall`, `beforeCompaction`, `onMessage`, `onStateChange`, `onCost`. Each gets a JSON snapshot and `ctx`.

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

`decider.ts` is **not supported yet**: OneEcma 1.0 does not strip TypeScript types. `LoadDecider` fails with a message saying so; compile the file to `decider.js` first.

## Trimming and NativeAOT

The package is annotated trim- and AOT-compatible: OneEcma is, and nothing here uses reflection, `Reflection.Emit` or runtime code generation (JSON goes through the core package's source-generated metadata and OneYaml). `samples/JavaScriptAotSmoke` loads a tool file, runs it and `run_javascript`, and hits a limit; it publishes with zero warnings and runs.
