# Pricing, cost and token counting

Normative notes for the catalog's price fields, how a `Usage` becomes a `Cost`, and how input tokens are counted. Money is `decimal`
everywhere; nothing is rounded except when a cost is displayed (`Cost.Display`, half away from zero, 6 places by default).

## Cost lines

A cost is a list of lines plus their sum. A line is one usage counter:
`amount = quantity / per * unitPrice * multiplier`, with the price's `asOf` date and source URL on the line.

| Counter | Price field |
|---|---|
| `inputTokens` (not cache reads or writes) | `inputPerMillion` |
| `outputTokens` (reasoning included) | `outputPerMillion` |
| `cacheCreationInputTokens` | `cacheCreationPerMillion` |
| `cacheReadInputTokens` | `cacheReadPerMillion` |
| any other counter (`images`, `audioSeconds`, `characters`, `toolCalls.web_search`, `containerHours`, `inputAudioTokens`, ...) | an entry in `items` |

The media counters and which models use them are listed in `media.md`.

An item gives `price` for `per` units (default 1,000,000 for counters ending in `Tokens`, otherwise 1). `when` narrows it to a case (for example
`{size: 1024x1024, quality: high}`): it applies when every key matches the call's qualifiers; of several items for one counter the one naming the most keys
wins. An item for one of the four main counters beats the flat field.

A counter with a quantity and no price is left out of the cost and reported as a warning. A model with no price at all has an unknown cost, except on a
loopback server (`localhost`, `127.0.0.0/8`, `::1`), where it is free unless the catalog says otherwise.

## Subset counters

A counter that counts tokens already inside another is a subset: `reasoningTokens` and the output audio/image counters inside `outputTokens`;
`inputAudioTokens`, `inputImageTokens`, `inputVideoTokens` inside `inputTokens`; `cacheCreation1hInputTokens` inside `cacheCreationInputTokens`.
If the model prices the subset, its tokens are removed from the parent's quantity and charged at the subset's price, so nothing is charged twice.
If it does not, they stay in the parent. A model can declare more relations with `pricing.subsetOf` (`child: parent`).

## Long context, tiers and multipliers

- **Long context.** When uncached input plus cache writes plus cache reads is strictly above `longContextThreshold` (200,000 when unset) and
  `longContextInputPerMillion` is set, the whole request is priced at the long-context rates: input and output take their long-context prices (output
  keeps its normal price when no long-context output price is given), and cache prices are multiplied by long-context input price over normal input price.
- **Tiers.** A tier applies when its basis (`totalInputTokens` unless `basis` names a counter) is strictly above `above`. Its `multipliers` factor the prices
  of the named counters, or every counter under `*`. Of several tiers that apply, the highest `above` wins; they do not stack.
- **Factors on every line:** the service tier's `pricingMultiplier` (`standard`, `default` and `auto` carry none; an unknown tier prices at the standard rate and warns),
  `multipliers.batch` for batch calls, `multipliers.region:<name>` for a region, and any other named multiplier the caller asks for. A missing name warns and is not applied.

## Staleness

Prices older than 180 days past `asOf` are stale: a priced call warns when the caller supplies today's date, and `PriceFreshness.FindStale` lists them.

## Request adjustment

`RequestAdjuster` reconciles a chat request with the catalog entry. It strips what is harmless to drop (temperature and top-p when `supportsTemperature` is
false, reasoning settings for a model with no `thinking`, a thinking budget the model does not take, caching for a model without it) and clamps values
(thinking budget to `minBudget`/`maxBudget`, `maxOutputTokens` to the model's maximum), returning a warning with a stable code (`parameter_stripped`,
`parameter_clamped`) for each. It refuses with `UnsupportedParameterException` what would change the request's meaning: tools or a forced tool for a model without
tool support, a JSON schema for a model without structured output, an image, PDF, audio or video the model cannot read, a reasoning effort or service tier the
model lacks, and chat for a decisions-only provider. A field the catalog leaves out means unknown and never strips or refuses.

## Token counting

Three tiers, tried in order; every count says which one answered.

1. **Provider** – the provider's own count endpoint, where the provider has one.
2. **Tokenizer** – a local tokenizer built from the vendor's published vocabulary data, supplied through `ILocalTokenizer`. Text is counted exactly; message,
   tool and file allowances are the estimate's.
3. **Estimate** – characters per token by family (4.0 for OpenAI-style and Gemini models, 3.5 otherwise; overridable per model with the property `charsPerToken`),
   two-byte characters at double weight, CJK and other characters at one token each; 4 tokens per message and 3 for the reply; 8 per tool, and 346 once for
   Anthropic when any tool is present; images by the vendor's documented formula (Anthropic width × height ÷ 750 capped at 1,600; OpenAI 85 + 170 per 512 px tile
   after scaling to fit 2048 px and the short side to 768 px; Gemini 258 up to 384 × 384 and 258 per 768 px tile above); PDFs by page estimate; audio and video by
   duration estimate. The estimate errs high.

Context usage is the last turn's total input tokens (uncached, cache writes and cache reads) divided by the model's `contextWindow`.

## Estimates and spend limits

`CostEstimator` gives the worst case before sending: every input token at the dearer of the input and cache-write prices (when the request may write the cache) plus the full
output allowance (the request's limit, otherwise the model's maximum), with long-context rates when the input is that large. `SpendGuard` enforces a per-call limit and
a spend cap on those estimates; it holds reservations for calls in flight, so concurrent calls cannot pass the cap together, and refuses (rather than ignores) a limit it
cannot check because the price or the output limit is unknown.

## Result cache

`ResultCache` is an opt-in in-memory cache of chat answers keyed by a SHA-256 over everything that can change the answer. A hit costs nothing and sends nothing.
