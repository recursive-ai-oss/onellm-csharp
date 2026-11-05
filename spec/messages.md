# Message format (version 1)

The normative description of how OneLLM represents and stores conversation messages. It covers the in-memory model, the file
shape (YAML and JSON carry the same keys), the order index, unknown-field handling and how streamed events build a message.

The file shape follows the lists used by the agent definition format OneLLM reads and writes (`user`, `assistant`, `toolCalls`,
`reasoning`, ...), so existing files load, and files written here stay readable by tools that only know those lists.

## Message

One conversation turn. Keys, all optional except `id`:

| Key | Type | Meaning |
|---|---|---|
| `id` | string | Message id. |
| `user` | list of parts | Content from the user. |
| `assistant` | list of parts | Content from the model. |
| `toolCalls` | list of tool calls | Client tool calls made in this turn, with results. |
| `reasoning` | list of reasoning entries | Provider reasoning kept for replay. |
| `dateTime` | RFC 3339 timestamp | When the turn happened. |
| `source` | object | Who produced the message (`MessageSource`). |
| `cache` | bool | Cache breakpoint after this message. |
| `usage` | object | Tokens and cost of the call that produced it. |
| `when` | string | Template deciding whether the message is used (initial prompts). |
| `partOrder`, `extraParts` | | The order index, below. |

In memory, a message's assistant side is **one ordered sequence of parts** (`Message.Assistant`) plus tool calls placed in it by
`ToolCall.After` (how many assistant entries precede the call). On disk the sequence is split into the three lists above, and the order
index puts it back together.

## Parts

A part is one object whose keys say the kind. A part has exactly one kind key.

| Kind | Key(s) | Notes |
|---|---|---|
| Text | `text`, optional `annotations` | Annotations carry citations (`type`, `url`, `title`, offsets, ...). |
| Asset | `asset` (object: `storage`, `key`, `hash`, `contentType`, `size`, `name`; `data` only on input, never written) | Content lives in the asset store. |
| Reasoning | `reasoning`, `provider`, `display`, `data` | `data` is opaque provider content replayed verbatim (signatures, encrypted content). |
| HostedToolCall | `hostedToolCall` (tool name), `provider`, `id`, `status`, `inputs`, `result`, `cost`, `data` | A tool the provider ran. |
| Refusal | `refusal` | The model declined. |
| Json | `json` | A structured value. |
| FileRef | `fileRef`, `provider`, `contentType`, `name`, `uri` | A file already uploaded to a provider. |
| ProviderPart | `providerPart` (type), `raw` | Provider-specific content kept as raw JSON. |

**Legacy inline media.** A part carrying `image`, `audio`, `video` or `pdf` keys (inline content from older files) is read as an
Asset part with no storage reference; the original keys stay in its extension data so they are written back unchanged. Only
`asset` is written for new content.

## Tool calls

`id`, `tool`, `routine`, `inputs` (object, key order kept), `result` (list of parts), `cost` (number, USD), `when`, `injected`,
`injectedBy`. OneLLM adds `isError` (true when the tool failed) and `costLines` (the line items behind `cost`).

## Order index

`partOrder` is a list of tokens naming the turn's entries in order: `a<i>` = `assistant[i]`, `r<i>` = `reasoning[i]`,
`t<i>` = `toolCalls[i]`, `x<i>` = `extraParts[i]`. `extraParts` holds parts that have no counterpart in the lists (hosted tool
calls, refusals, JSON and provider parts, file references on the assistant side).

- The index is omitted when the order is the default: all reasoning, then all assistant parts, then all tool calls.
- A reader that ignores `partOrder` still sees every list unchanged. The lists are never reordered or padded by the index.
- A reader that finds a missing or stale index uses the default order.

## Usage and cost

`usage`: `model`, `serviceTier`, `inputTokens`, `outputTokens`, `cacheCreationInputTokens`, `cacheReadInputTokens`, `cost`, and
OneLLM's `counters` (map of further counters: `reasoningTokens`, `images`, `audioSeconds`, ...) and `costLines`.

**`inputTokens` excludes cached tokens** (reads and writes are separate counters). Provider codecs that receive a total including
cache subtract it. Total input = the three summed.

A cost line: `counter`, `quantity`, `per`, `unitPrice`, `multiplier`, `amount` (= quantity / per × unitPrice × multiplier), `label`, `source`, `asOf`. **All
money is exact decimal.** On disk the cost is a number; nothing is rounded except for display.

## Extension data (unknown fields)

Every object above (message, part, tool call, source, usage, cost line, annotation, reasoning entry) keeps keys it does not know and
writes them back, at the same level, unchanged in value. Values read from a file are plain: strings, booleans, integers, decimals,
lists and mappings. Absence and `null` are not distinguished for unknown keys.

## Timestamps

RFC 3339 with a `Z` or numeric offset; fractional seconds are kept to 100 ns. A YAML writer quotes timestamps that would otherwise
read as YAML timestamps.

## Stream events

A provider's stream is turned into typed events, each naming the content block by `index`:

`MessageStart`, `TextDelta`, `ReasoningDelta`, `ReasoningEnd` (provider, opaque data), `RefusalDelta`, `ToolCallStart` (id, tool),
`ToolCallArgumentsDelta`, `ToolCallEnd`, `HostedToolCallStart`/`ArgumentsDelta`/`End`, `Annotation`, `PartAdded`, `Usage`, `Done`.
Text and argument deltas are UTF-8 slices of the receive buffer, so decoding never allocates a string per event.

The **accumulator** builds a message from events, and **streaming and non-streaming calls build equal messages**:

- Deltas for one index append in arrival order; a block's position in the turn is the order its first event arrived.
- Tool call arguments are concatenated and parsed as JSON when the call ends. A text that is not a JSON object is kept as invalid
  inputs instead of being dropped.
- Usage events merge (a later non-zero counter replaces the earlier value); `Done` ends the message and records the finish reason.
- The message id and timestamp are left empty for the runtime to stamp.
