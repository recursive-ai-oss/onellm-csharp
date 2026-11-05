# The `onellm` command line

`onellm` runs OneLLM definitions from a terminal. It is a .NET tool (`RecursiveAI.OneLlm.Cli`, command `onellm`) built on the same `Runtime` the library exposes, and it is line-oriented: no full-screen interface, results on standard output, progress on standard error, so it composes in pipes and scripts.

```
dotnet tool install -g RecursiveAI.OneLlm.Cli
onellm --help
```

API keys are read from the environment only (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, `BRAVE_API_KEY` for web search, and the others the catalog names). There is no option to pass a key.

## Commands

| Command | What it does |
|---|---|
| `onellm run <definition> [options]` | Runs a definition. The file decides the mode (below). |
| `onellm chat <model> [--system text]` | Talks to a model: reads a line from standard input, streams the answer, repeats. `/exit` or end of input stops. The chat is saved as an instance. |
| `onellm instances list [--state-filter s] [--name text] [--limit n]` | Lists saved instances, tab-separated: id, state, modified (UTC), name, model. |
| `onellm instances show <id>` | Prints an instance: header, cost, and each message with its tool calls and their results (clipped). |
| `onellm instances resume <id> [--prompt text]` | Continues an instance that is waiting for a message or for approvals. Takes the same grant flags as `run`. |
| `onellm instances fork <id> [--at messageId]` | Copies an instance (up to a message) into a new one and prints its id. |
| `onellm cost [id]` | With an id: cost by model, for the instance and for it with its sub-agents. Without: everything in the state directory. `--since 24h`, `7d`, `2w` or a date limits it. |
| `onellm catalog [model]` | Without a model: every catalog model and its context window. With one: each value and, in brackets, the layer that set it (`shipped`, `user`, `code`). |
| `onellm examples [show <name>]` | Lists the example agents built into the tool, or prints one. |

### Modes of `run`

| Target | Mode |
|---|---|
| `examples/<name>` | The embedded example agent of that name |
| `*.yaml` (and a directory holding `agent.yaml`) | Agent: the tool loop |
| `*.flow` | Scripted: the prompts in order, one model call each, no tools |
| a directory holding `decider.yaml`, or that file | Decider: asks its questions, prints its outputs as JSON |

Options of `run`:

- `--input k=v` (repeatable) sets an input; `k=@file` reads the value from a file. `--input-json '{...}'` (or `@file`) sets several, with types.
- `--prompt text` is an agent's first user message (agents only). `--model name` replaces the definition's model.
- `--max-cost usd` stops a run that would spend more.
- `--yes` approves every held tool call without asking. `--quiet` hides the tool-call lines on standard error.
- Common to every command: `--root dir` (default: the current directory) and `--state dir` (below).

The result is printed to standard output: an agent's `task_complete` result (its summary goes to standard error), the typed output when the definition declares one, otherwise the last answer. The last line on standard error says the instance id, its state, turns and cost (US dollars, six places, halves away from zero).

## Grants

Definitions never grant anything. The tool grants only what its flags say:

| Flag | Grants |
|---|---|
| (none) | Reading and writing files under `--root`; nothing else. No shell. No network for tools. |
| `--allow-shell` | Any shell command |
| `--allow-shell=git,dotnet` | Only these programs, run without a shell (arguments are never interpreted) |
| `--allow-shell=ask` | Any command, each one confirmed at the terminal |
| `--allow-net` | Public hosts (loopback, private and link-local addresses stay refused) |
| `--allow-net=a.example.com,*.b.org` | Only these hosts |

A definition that references a tool the grants do not cover (a shell tool without `--allow-shell`, `http_request` without `--allow-net`) does not load: the run stops with exit code 3 naming the tool and the missing grant, before any model call. Sub-agents hold at most what their parent holds.

When a tool call needs approval (a tool marked `requiresApproval`, or a hook asking), the terminal asks `Approve <tool> <inputs>? [y/N]`. The default is no, and so is end of input. The held call is saved with the instance, so a run can also be left and answered later with `onellm instances resume <id>`.

## Where things are kept

Under `<root>/.onellm/` unless `--state` says otherwise:

- `instances/`: one YAML file per instance, written atomically. Agents can read it (it is under the root), which lets them look at other runs; point `--state` outside the root to prevent that.
- `records/`: the JSON-lines audit records (runs, messages, inference calls, tool calls, errors).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. Also: the agent answered and waits for your next message (the last line says how to resume). |
| 1 | The run failed: a provider error after retries, a spend cap, a hook block, an instance that ended in `error`, an unknown instance id. |
| 2 | The command line is wrong. |
| 3 | A file, definition, grant, model or key problem: a missing file, an invalid definition (with file, line and column), an ungranted reference, an unknown model, a missing API key. |
| 4 | The agent used all its iterations without finishing. |
| 130 | Cancelled with Ctrl-C. The partial run is kept as a stopped instance. |

## The example agents

`onellm examples` lists the eight embedded agents; `agents/README.md` in the repository describes each with its grants and a representative task. Sub-agents of the orchestrator may start any of them by name (`examples/<name>`).

## Notes

- The tool is a managed .NET tool, not published as NativeAOT; the library it hosts is trim- and AOT-safe.
- `onellm catalog` shows the effective catalog: set `ONELLM_CATALOG` to a file or directory of `llm-provider.yaml` files to add a user layer.
- Web search (`search_web`) is available to agents when `BRAVE_API_KEY` is set; fetching pages (`fetch_webpage_markdown`) needs `--allow-net`.
