# RecursiveAI.OneLlm.Cli

**LLM inference and agent runtime for .NET**: the `onellm` command-line tool. It runs OneLLM agents, scripted flows and deciders, chats with a model, lists and resumes saved instances, reports cost, and shows the model catalog with the layer each value came from. Eight example agents are built in. OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

```
dotnet tool install -g RecursiveAI.OneLlm.Cli
export ANTHROPIC_API_KEY=...
onellm run examples/agent-writer --prompt "Write ./agents/changelog.yaml: an agent that turns ./log.txt into CHANGELOG.md."
onellm run reviewer.yaml --input repo=. --allow-shell=git
onellm chat claude-sonnet
onellm instances list
onellm cost --since 7d
```

Files under the working directory are always available to the agent. Shell and network access are granted only by flags (`--allow-shell[=programs]`, `--allow-net[=hosts]`); a definition cannot grant them, and one that references a tool you did not grant does not load. Keys are read from the environment only. Exit codes and every option are in the [command-line guide](https://github.com/recursive-ai-oss/onellm-csharp/blob/main/docs/cli.md).

It hosts the core package, `RecursiveAI.OneLlm.Web` and `RecursiveAI.OneLlm.JavaScript`. Source and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
