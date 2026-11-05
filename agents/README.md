# Example agents

Eight agents ship inside the `onellm` tool. Run one by name, or copy it with `onellm examples show <name>` and change it.

```
onellm run examples/<name> --prompt "<task>" [grant flags]
```

Each is a single `agent.yaml` in its own folder here. They use the default model `claude-sonnet`; pass `--model` to use another. Nothing is granted by the definition: files under the working directory are always available, and the shell is available only where the table says so. An example that needs a shell does not load without the flag, and says why.

| Example | What it does | Grants | Representative task |
|---|---|---|---|
| `cs-tool-writer` | Writes a C# `[Tool]` class, builds it, says how to register it | `--allow-shell=dotnet,git` | `--prompt "In ./src/Tools, write a tool that converts a length between metres, feet and miles; add a test."` |
| `js-tool-writer` | Writes a sandboxed JavaScript tool and its `.tool.yaml` | none | `--prompt "In ./tools, write a JS tool that reshapes {firstName, lastName} records into {name} records."` |
| `provider-writer` | Adds a provider: a catalog preset for an OpenAI-compatible server, or a provider class for a native API | `--allow-shell=dotnet,git`; `--allow-net=<docs host>` lets it read vendor documentation | `--prompt "Add a catalog preset for the OpenAI-compatible server at https://api.example.test/v1; its docs are at https://docs.example.test."` |
| `agent-writer` | Designs an agent definition from a description of the job | none | `--prompt "Write ./agents/changelog.yaml: an agent that reads the git log text in ./log.txt and writes CHANGELOG.md."` |
| `script-writer` | Writes a scripted `.flow` | none | `--prompt "Write ./commit-message.flow: summarize a diff, then tighten it to a 72-character subject plus a body."` |
| `decider-writer` | Writes a `decider.yaml`, exercises it with `run_decider` | none (the decider's model needs its key in the environment) | `--prompt "Write ./deciders/triage: classify a support ticket by area, urgency, and whether the customer is angry."` |
| `hook-writer` | Writes a C# hook with tests | `--allow-shell=dotnet,git` | `--prompt "Write a BeforeToolCall hook that blocks write_file outside ./out, with tests."` |
| `orchestrator` | Splits a job, starts the other examples (or any definition file) as sub-agents, watches them, combines the results | whatever its sub-agents need: sub-agents hold at most what the orchestrator holds | `--prompt "Write a tool, a flow and a decider for ticket triage under ./triage, each by its own agent."` with `--allow-shell=dotnet,git` |

## What counts as done

Each agent ends by calling `task_complete` with the files it wrote and what it could not verify. A run is a success when:

- the files named in the task exist where the task said,
- the agent's own checks passed (the build and tests for `cs-tool-writer`, `hook-writer` and `provider-writer`; `run_decider` results for `decider-writer`),
- the `task_complete` result names anything it did not check.

The scripted end-to-end tests in `tests/OneLlm.Cli.Tests` run every example through the real tool loop against a scripted model, so the definitions, references and grants are checked on every build. Runs against live models are a release check.
