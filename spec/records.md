# Records: format v1

Records are an append-only audit trail of a run. Instance state is the truth; records show how it got there. Sinks receive
records; a sink that fails never fails a run.

## JSON lines

The JSONL sink writes one JSON object per line, to `<directory>/<instanceId>.jsonl` (`_runtime.jsonl` for records with no
instance). Every line has:

| Key | Meaning |
|---|---|
| `kind` | The record kind, below. |
| `time` | RFC 3339 UTC time, from the runtime's clock. |
| `instanceId` | The instance. Absent for runtime-level records. |
| `messageId` | The message the record belongs to, when there is one. |

and then the fields of its kind. Money is a JSON number written from a decimal, never a binary float.

| Kind | Fields |
|---|---|
| `run` | `event` (`started`, `stopped`, `resumed`, `cancelled`, `stateChanged`), `state`, `model` |
| `message` | `message`: the message, with its `source` |
| `inference` | `provider`, `operation`, `durationMs`, `finishReason`, `usage` (with `costLines`) |
| `toolCall` | `call`: the tool call with inputs, result, `cost` and `costLines`; `durationMs` |
| `taskCompletion` | `tool`, `result` |
| `approval` | `toolCallId`, `tool`, `decision` (`requested`, `approved`, `denied`), `by` |
| `compaction` | `strategy`, `at`, `replacedMessageIds`, `summaryMessageId`, `cost` |
| `hook` | `point`, `hook`, `decision` (`modify`, `block`, `requireApproval`, `respond`), `reason` |
| `error` | `errorType`, `message`, `where` |
| `rawTurn` | `request`, `response`: the raw bodies with secrets masked. Off unless the host turns it on. |

Readers must ignore kinds and fields they do not know.

## Sinks

Several sinks can be active at once. Each record goes to every sink in the order they were added. When a sink throws, the failure
is reported to a callback and the record still goes to the other sinks. Cancellation is the one exception that reaches the caller.
