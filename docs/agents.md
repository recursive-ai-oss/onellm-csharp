# Agents: the loop, approvals, sub-agents, monitors and hooks

An agent is a definition run as an instance with tools. `Llm.RunAgentAsync(path, inputs)` runs one to rest and returns an `AgentResult`; `Llm.StartAgentAsync(...)` returns an `AgentRun` to follow, steer, approve and cancel; `Runtime` has the same calls plus `GetAgentAsync(instanceId)` to pick one up again, including after a restart.

## The loop

1. The system prompt and initial prompts are rendered like a scripted run's. Initial tool calls (`initialPrompts` entries with `toolCalls`) run first, through the same path as any call, marked `injected`. They stop for approval like any other call (a definition can be written by a model, so its author is not an approver), and an injected `ask_user` leaves the instance `waiting` for the answer; in both cases the task prompt is added behind the held turn and the remaining initial prompts do not run.
2. Each turn: the model is called with the definition's tools; the tool calls it makes run; the turn is saved with its results.
3. Calls run together when there is more than one and every one is `parallelSafe` (the default); a single `parallelSafe: false` call makes the whole turn run one after another in call order. Results are returned to the model **in call order**, not in finishing order.
4. The run ends in one of these ways:

| Ends because | State | `AgentResult.StopReason` |
|---|---|---|
| a loop-ending tool ran (`task_complete`, or any tool with `endsLoop`); the rest of that turn's calls still ran | `stopped` | `Completed` |
| the model answered without a tool call | `waiting` | `Waiting` |
| `ask_user` asked a question | `waiting` | `Waiting` |
| a call needs approval | `waiting_for_approval` | `WaitingForApproval` |
| `maxIterations` (default 200) model turns were used | `error`, message `max iterations (N) reached`, no exception | `MaxIterations` |
| a model call failed after its retries | `error`, the typed exception is thrown | |
| the token was cancelled | `stopped`, `cancelled: true`, the partial turn kept; `RunCancelledException` | |

Error rules: a tool that throws or times out gives the model an error result and the loop goes on; an unknown tool name or malformed arguments do too. A provider failure the provider calls transient is retried `Runtime.AgentRetry.MaxRetries` times more (default none: provider clients already retry), honouring `Retry-After`.

Every step is saved to the instance store and written to the record sinks; usage and cost are per turn, per tool call and per instance subtree (`AgentResult.SubtreeCost`).

## Approvals

A tool with `RequiresApproval`, or a hook that returns `RequireApproval`, holds its call. The turn is saved with that call unanswered and an entry in `pendingApprovals`; the other calls of the turn run now. `AgentRun.ApproveAsync(toolCallId?)` and `DenyAsync(toolCallId?, reason?)` record a decision in the instance file; when all are decided the run continues. The decision survives a restart: load the instance with a new `Runtime` over the same store, call `runtime.ApproveAsync(instanceId)` and await the run. A denied call reaches the model as an error result with the reason.

Shell commands use the same gate when the grant is `Capabilities.ShellWithLoopApproval`: every `execute_terminal_command` call is held with a pending approval showing the command, and runs only after `ApproveAsync`, also in another process after a restart. `Capabilities.ShellWithApproval(approver)` keeps asking its delegate inside the tool instead (the host's own prompt, as the CLI's terminal approver does). A saved instance whose grants said "approval" and that resumes in a runtime with no approver of its own uses the loop gate.

## Messages and steering

`AgentRun.MessageAsync` (or `message_agent` from another agent) delivers a message. To a running agent it is read at the next turn boundary, after the results of the turn in progress. To a `waiting` or `stopped` agent it starts a new run segment. While approvals are pending it waits until they are decided. A message from the person or host (`MessageAsync`, source `api` or `user`) sent to an agent that is waiting on `ask_user` is the answer: it becomes the result of that call. Messages from other agents or the runtime (a monitor alert) join the history behind the question and do not answer it. A message to an instance in `error` starts a new run segment, as it does for any resting instance.

## Sub-agents

`run_agent_async` starts another instance in the same `Runtime`, linked by `spawnerInstanceId`, `callerInstanceId`, `callerToolCallId` and `delegationPrompt`. The child's grants are the parent's grants intersected with what the caller asks for, so a child never holds more than its parent (`Capabilities.Intersect`). A definition file is read only from under the caller's file roots, after symlinks are followed; definitions registered on `Runtime.AgentDefinitions` need no file access. The cost of children rolls up into the parent's subtree.

- **Tools.** A child started from a definition *file* holds at most the tools its parent holds (plus `task_complete`): a reference to any other tool fails the start with a message naming the tool. A model can write such a file, so the file cannot widen the set. A definition the host registered on `Runtime.AgentDefinitions` chooses its own tools, whatever its parent holds; its grants are still the parent's at most. Registering a definition therefore lets any agent that can name it use the tools it lists, including a shell tool the caller lacks (the child still runs under the caller's grants).
- **Reach.** The agent tools (`query_agent`, `message_agent`, `run_agent_async` with `agentId`, `await_agent`, `monitor_agent`, `list_agent_instances`) work on the calling agent and the agents it started, found by following `spawnerInstanceId` (live or saved), because another instance may hold wider grants than the caller. A monitor belongs to the agent that armed it. Set `Runtime.AgentsMayReachAnyInstance` to let agents reach every instance in the store; calls on the `Runtime` itself are never restricted.
- **Grants survive a restart.** The grants an instance runs with are saved in its file (the `capabilities` key, see the instance format). When any runtime resumes it (a message, an approval, a lazy load), it gets those grants intersected with that runtime's, so a read-only start stays read-only and a runtime never widens a saved instance. The key is written with a marker (`capabilitiesRecorded`). An instance file with neither (written before grants were recorded) gets the resuming runtime's grants. A file with the marker but no valid `capabilities` entry (missing, or not a well-formed summary) fails closed: it gets no grants and refuses to run, with an error naming the instance. The marker is not a signature; the instance store must be out of the model's reach, which the runtime arranges for its file store directory. Likewise `spawnerInstanceId` in a saved file is trusted as written.

## Agent tools

| Tool | What it does |
|---|---|
| `task_complete` | ends the loop with `result` and `summary` (and an optional `title`) |
| `run_agent_async` | starts an agent (`agentSwarmUri`, `prompt`) or messages one (`agentId`) and returns its instance id |
| `await_agent` | waits for one or all of `ids`; reports result, cost and context use of each; any message for the caller ends the wait |
| `message_agent` | delivers `prompt` to the instance `agentId`; with no arguments it points at `run_agent_async` |
| `monitor_agent` | arms a monitor on an agent (below), or just reports its status |
| `await_monitor` | waits for monitors to fire |
| `list_agent_instances` | lists saved instances, filtered by name, state or swarm |
| `query_agent` | searches an instance's messages |

Their names and input properties are the ones existing agent definitions use, so those definitions run unchanged. They sit in `Runtime.Tools` beside the built-ins (`docs/capabilities.md`); a reference such as `/agent-tools/os/read-file` or `/runtime/agents/await-agent` finds its tool by the last path segment. A host or a package adds more with `Runtime.Tools.Add` (for example a decider tool).

## Monitors

`monitor_agent` takes up to four conditions, combined with OR: `contextAt` (a token count such as `400k`, or a percentage of the window), `turns` (more turns from now), `onFinish` (the agent rests) and `elapsed` (`30m`, `2h`). The first to hold fires the monitor, once, and disarms it; arming again replaces it. A fired monitor sends the watcher a runtime-sourced message, unless the watcher is blocked in `await_monitor`, which returns the alert instead. `elapsed` only reports: it never stops the agent. `Runtime.AgentEvents` raises a `MonitorFired` event.

## Context strategies

See `contextStrategy` in `spec/definitions.md`: `clearToolResults`, `compact`, `providerNative`. Each run is recorded on the instance with what it replaced and what it cost.

## Hooks

`Runtime.Hooks` takes `IHook` implementations or lambdas (`BeforeInference`, `AfterInference`, `BeforeToolCall`, `AfterToolCall`, `BeforeCompaction`, `OnMessage`, `OnStateChange`, `OnCost`); an `IHook` can also observe `StreamEvent`s.

| Point | Decisions it can return |
|---|---|
| `BeforeInference` | Continue, `ModifyRequest`, `Block` (the run fails with `HookBlockedException`), `Respond` (answer without calling the model) |
| `AfterInference` | Continue, `ModifyMessage`, `Block` |
| `BeforeToolCall` | Continue, `ModifyInputs`, `Block` (the model sees an error result with the reason), `RequireApproval`, `Respond` (a result without running the tool) |
| `AfterToolCall` | Continue, `ModifyResult`, `Block` |
| `BeforeCompaction` | Continue, `Block` (skip this compaction) |
| `OnMessage`, `OnStateChange`, `OnCost`, `StreamEvent` | observe only |

Hooks run in registration order; host hooks wrap hooks a definition names (those come with the `.JavaScript` package). **Interceptors fail closed:** one that throws blocks the step. **Observers fail open:** one that throws is recorded and the run goes on. Every Modify, Block, Respond and RequireApproval is written as a `hook` record. With no hooks registered a run builds no hook context and allocates nothing for them.
