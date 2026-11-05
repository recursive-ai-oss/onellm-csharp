# Catalog format (version 1)

The catalog says which models exist, what they can do and what they cost. It is a set of `llm-provider.yaml` files merged in layers.

## Provider entry

One YAML document per provider (a file may hold several, or a `providers:` list):

| Key | Meaning |
|---|---|
| `name` | Provider id, unique across layers. |
| `displayName` | For people. |
| `modelPatterns` | Globs (`*`, `?`) of model names the provider serves. |
| `aliases` | Short name → model name. |
| `apiKeyEnvVar` | Environment variable holding the key. |
| `capabilities` | What the provider offers (chat, streaming, tools, embeddings, ...). |
| `presets` | Named variants of the entry for OpenAI-compatible servers (own base URL, patterns, models). |
| `models[]` | `name`, `contextWindow`, `maxOutputTokens`, `supports*` flags, `thinking`, `serviceTiers`, image limits, `pricing`. |
| `properties` | Free-form map for anything else. |

Keys not listed are kept as extension data and written back.

## Layers

Layers are stacked lowest first: **shipped** (embedded in the package) < **user** (files or a directory named by code or an
environment variable) < **code** (overrides set programmatically). Providers match by `name`. Merging a provider across layers:

- A scalar set in a higher layer replaces the lower value.
- Mappings (`aliases`, `properties`, `presets`, `serviceTiers`, `pricing`, ...) merge key by key.
- `models` merge **by model name**: a layer can add a model or change single fields of an existing one.
- Any other list (`modelPatterns`, `efforts`, price `items`) is replaced as a whole.
- A field a layer omits is unchanged.

Every effective value records which layer set it (`SourceOf(provider, "models[name].pricing.inputPerMillion")`).

## Resolving a model name

1. An optional `provider:model` or `preset:model` prefix limits the search. A colon belongs to the model name unless the text before it
   names a provider or preset (`llama3:8b` is one name).
2. Alias.
3. Exact model name.
4. Patterns; the pattern with the most literal characters wins.

Names compare ignoring case. A name two providers match at the same step is ambiguous and is an error naming the candidates. An
unknown name is a `ModelNotFoundException`.

## Pricing

All amounts are USD, exact decimals.

- **Per-million token prices:** `inputPerMillion` (tokens that are not cache reads or writes), `outputPerMillion`,
  `cacheCreationPerMillion`, `cacheReadPerMillion`, and the long-context pair with `longContextThreshold` (applies to the whole request when its
  total input is above the threshold).
- **`items`:** a price for any other usage counter: `counter`, `price`, `per` (default 1,000,000 for counters ending in `Tokens`, else
  1), optional `when` qualifiers (`size`, `quality`), `label`, `asOf`, `source`. Counters include `cacheCreation1hInputTokens`,
  `inputAudioTokens`, `inputImageTokens`, `images`, `audioSeconds`, `characters`, `toolCalls.<name>`, `containerHours`.
- **`tiers`:** from a size upwards (`above` of `basis`, default total input tokens) multiply named counters' prices.
- **`multipliers`:** named factors (`batch`, `region:eu`) a caller applies.
- **`asOf` and `source`:** the date each price was checked and the page it came from, on the model or on a single item. A catalog
  whose prices are older than 180 days warns.

The calculation turns a usage report into cost lines (`counter × price × multipliers`), one line per counter, plus a total.
