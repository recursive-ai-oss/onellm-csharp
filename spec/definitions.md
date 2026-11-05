# Definitions, flows and deciders: format v1

This is the file format OneLLM reads and writes for agent definitions, scripted flows and deciders. It is YAML 1.2. The format is
versioned as a whole; this is version 1. OneLLM reads every key described here and keeps every key it does not know.

## Rules that apply to every file

- **Unknown keys are kept.** A key this document does not describe is loaded into the type's `ExtensionData` and written back
  with the same value. This holds at every level: the definition, a tool reference, a hook entry, a message, a message part, a
  tool call, a decider question.
- **A key with a null value and an absent key mean the same.** OneLLM does not write keys whose value is null.
- **A list that was written as an empty list stays an empty list** when the file is saved again; an absent list stays absent.
- **Saving something that was not changed returns the loaded text byte for byte**, comments included. Once a value is changed
  the file is written again from the model: the same keys and values, with unknown keys kept, but not the comments or the
  original layout.
- **Loading never grants a capability.** `references` name tool sources the agent would like; the runtime's grants decide.
- **Errors carry a position.** A malformed file, a value of the wrong type, or a refused reference fails with the file name and
  the 1-based line and column of the node (`BadDefinitionException`).
- **Strict is the default.** Strict loading fails on an ungranted reference, an invalid `missingKey` or a hook without a `file`.
  Lenient loading drops the offending entry and records the reason in the definition's `Warnings`.

## Agent definition (`agent.yaml`, `*.flow`)

A mapping. Every key is optional.

| Key | Type | Meaning |
|---|---|---|
| `name` | string | Display name. |
| `description` | string | One line. |
| `model` | string | A catalog model name or alias, or `provider:model`. |
| `serviceTier` | string | Service tier to request (`flex`, `priority`, ...). |
| `thinking` | mapping | `effort` (string) and/or `budget` (integer): request-side reasoning controls. |
| `temperature` | number | Sampling temperature. |
| `maxIterations` | integer | The most model turns one run may take. |
| `maxTokens` | integer | The most output tokens per turn. |
| `systemPrompt` | string | A template. Rendered when a run starts and again each turn. |
| `references` | list | Tool sources, below. |
| `workingDirectory` | string | A template. |
| `initialPrompts` | list | Messages that open every run, below. |
| `inputs` | mapping | Default input values. Run inputs are merged over them. |
| `headers` | mapping of string to string | Extra HTTP headers for HTTP tool sources; values are templates. |
| `invocationMode` | string | `interactive`, `delegated` or `autonomous`. |
| `llmTurnLogs` | boolean | Keep raw request and response logs for each turn. |
| `missingKey` | string | `default` or `error`. What a template does with a key it cannot find. `default` renders `<no value>`. Agents and scripted runs default to `default`; deciders are always `error`. |
| `hooks` | list | Hook files, below. |

### `references`

Each entry is a mapping with one of:

- `routine`: a tool source path or URI, with optional `tools` (list of tool names; absent means all of them, an empty list means
  none) and `required` (boolean);
- `swarm`: a group of sources to take every tool from.

Other keys are kept. An entry that names a source the runtime has not granted is refused (strict) or dropped (lenient).

### `initialPrompts`

A list of messages in the message format (`spec/messages.md`): `user` and `assistant` lists of parts, and `toolCalls`. Two keys
matter only here:

- `when` (on a message or on a tool call): a template. The message or call runs unless the rendered text, trimmed and lower-cased,
  is empty, `false`, `0`, `no` or `off`.
- tool calls without a `result` are run before the first turn; ones with a `result` are injected as they are.

A `user` part may be `from: <path>` to expand a list from the template data (for example `inputs.images`) into several parts.
Messages in `initialPrompts` usually have no `id`; the runtime gives them one when they enter an instance.

### `contextStrategy`

Optional. Keeps a long run inside the model's window. `contextStrategy: {type, triggerTokens | triggerPercent, keepLast}` or just the
type name. It runs at a turn boundary, when the previous model call's context (input plus output tokens) reached the trigger
(`triggerTokens`, or `triggerPercent` of the model's window, default 70):

- `clearToolResults`: the results of all but the latest `keepLast` (default 3) tool-calling turns are replaced by the note
  `[tool result cleared to save context]`. Every call keeps a result, so no call is orphaned, and messages before the first
  change are untouched, so a provider's cached prefix stays valid up to there. Doing it again changes nothing.
- `compact`: messages before a user message at least `keepLast` (default 6) messages from the end are summarized by a model call
  and replaced by one user message that starts with `[Summary of the earlier conversation]`. The summarizer is the shipped
  `compact` flow, replaceable on the `Runtime`; it runs as an instance of its own below this one, so its cost is in the subtree.
- `providerNative`: the provider compacts the conversation, when it can. A provider that cannot is skipped with a warning.

Each run adds an entry to `compactions` (`spec/instances.md`). A host hook can veto one.

### `hooks`

A list of mappings: `file` (required, relative to the definition) and `points` (list of hook point names; absent means every
point the file handles). Host hooks registered on the runtime wrap these.

## Flows (`*.flow`, `flow.yaml`)

A `.flow` file has the shape of an agent definition and is run in the scripted mode by default: the `initialPrompts` run in
order, one model call per user prompt, no tools. `references` in a flow are ignored with a warning.

`flow.yaml` beside it is metadata: `name`, `description`, `version` and any other keys (version history, input examples, rubrics).
OneLLM keeps the whole file and does not interpret the other keys; they are available as `FlowMetadata.Other`.

## Decider (`decider.yaml`)

A mapping with:

| Key | Type | Meaning |
|---|---|---|
| `model` | string | Required. The engine or model to ask. |
| `state` | any | A template tree rendered with the run's inputs. |
| `questions` | mapping | Required, at least one. Question id to question. |

Other top-level keys are kept (`samples`, `cache`, ...).

A question is a mapping with:

- `type`: `choice`, `score` or `noul` (a yes/no judgment with a probability);
- `instructions`: required; a template string, or a mapping of named parts, each a template;
- `criteria`: for `choice`, a mapping from value to description with at least 2 entries; for `score`, the levels from lowest to
  highest as a list (or a mapping from label to description) with 2 to 10 entries; for `noul`, optional conditions;
- `forEach`: a template naming the items to ask the question about, once each.

Loading a decider checks this shape and nothing more; the logic that uses the answers is separate.
