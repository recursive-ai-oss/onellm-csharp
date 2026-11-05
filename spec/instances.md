# Instances: format v1

An instance is one run of a definition and everything it has said and done. It is a YAML file named `<instanceId>.yaml`. The
rules for unknown keys, null values, empty lists and byte-for-byte saving in `spec/definitions.md` apply here too.

## Layout

An instance file is an agent definition (every key of `spec/definitions.md`, as they were when the run started) plus these keys:

| Key | Type | Meaning |
|---|---|---|
| `instanceId` | string | 1 to 128 characters of letters, digits, `-` and `_`. It is the file name. |
| `agentDefinition` | string | Path or URI of the definition the run started from. |
| `requestedModel` | string | The model as first requested, before alias resolution. `model` holds the resolved name. |
| `maxImageSize` | integer | The longest image edge in pixels the instance sends. Only ever tightened. |
| `state` | string | The lifecycle state, below. |
| `callerInstanceId` | string | The instance that last messaged this one. |
| `spawnerInstanceId` | string | The instance that started this one. Set once. |
| `callerToolCallId` | string | The tool call of the caller that started this instance. |
| `delegationPrompt` | string | The prompt a calling agent gave when it started this instance. |
| `runStartedAt` | string | RFC 3339 time the current run started. |
| `metadata.renderedSystemPrompt` | string | The system prompt as rendered for the latest turn: what the model saw. |
| `pendingApprovals` | list | Tool calls waiting for a decision, below. |
| `compactions` | list | Records of context compaction, below. |
| `messages` | list | The conversation, in the message format (`spec/messages.md`). |

OneLLM writes the keys in this order, with `messages` last, so that a long conversation can be appended to without disturbing
the header. A reader must accept any order.

Keys other tools put in an instance file (a bound subscription, a wait record, context watchers, ...) are unknown keys and are
kept. `metadata` keys other than `renderedSystemPrompt` are kept.

## States

`state` is a string. The values OneLLM uses:

| Value | Meaning |
|---|---|
| `running` | A run is in progress. |
| `waiting` | The agent finished its turn and waits for input. |
| `waiting_for_approval` | A tool call needs a person's decision; see `pendingApprovals`. |
| `stopped` | The run ended. A new message continues the instance. |
| `error` | The run failed. |

Any other string loads and saves unchanged. A runner that finds a state it does not know treats the instance as stopped.

## `pendingApprovals`

Each entry: `toolCallId`, `tool`, `messageId` (the message that holds the call, when it has been written), `inputs`, `reason`
and `requestedAt` (RFC 3339). Approvals are saved with the instance, so an approval or denial still works after a restart.
Unknown keys are kept.

## `compactions`

Each entry records one context compaction: `strategy` (`clearToolResults`, `compact` or `providerNative`), `at`,
`replacedMessageIds`, `summaryMessageId` and `cost` (a decimal in USD, when the compaction made a model call).

## Fork and duplicate

A **fork** copies an instance under a new id, optionally only up to and including one message. It starts stopped with no pending
approvals and no `runStartedAt`, and records where it came from in an unknown key `forkedFrom` (`instanceId`, `messageId`).
A **duplicate** is a fork of the whole conversation.

## Assets

Assets of an instance live beside it, in a directory `<instanceId>.assets/`, in a file store. Messages refer to them by `asset`
(storage, key, hash, content type, size, name).

## Persistence layout and cost

A save writes a temporary file in the same directory, flushes it to disk and renames it over the instance, so a reader sees the
old file or the new one. A lock file `<instanceId>.lock`, held open without sharing for the life of a run, keeps a second runner
out, across processes; the operating system drops it if the holder dies.

Messages are immutable once appended. A save re-serializes the header and any message it has not written before, and reuses the
text of the others, so adding a turn to a long instance costs the new turn plus the write of the file.
