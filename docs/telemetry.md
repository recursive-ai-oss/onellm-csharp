# Telemetry

OneLLM emits OpenTelemetry traces and metrics for model calls through `System.Diagnostics`. Subscribe to the activity source and
the meter named `OneLlm`; with an OpenTelemetry SDK that is `AddSource("OneLlm")` and `AddMeter("OneLlm")`. When nothing is
subscribed, a call costs one check and allocates nothing.

Names follow the OpenTelemetry GenAI semantic conventions (attribute registry:
`https://opentelemetry.io/docs/specs/semconv/registry/attributes/gen-ai/`). The conventions are still evolving; the names below
are the ones OneLLM emits.

## Spans

One client span per model call, named `{gen_ai.operation.name} {gen_ai.request.model}`, kind `Client`.

| Attribute | Value |
|---|---|
| `gen_ai.operation.name` | `chat`, `embeddings`, `text_completion`, `execute_tool`, `invoke_agent`, ... |
| `gen_ai.provider.name` | `anthropic`, `openai`, `gcp.gemini`, ... |
| `gen_ai.request.model` | The model that was asked for. |
| `gen_ai.response.model` | The model that answered. |
| `gen_ai.response.id` | The provider's response id, when it gave one. |
| `gen_ai.response.finish_reasons` | One-element array. |
| `gen_ai.usage.input_tokens` | All input tokens, including cache reads and writes. |
| `gen_ai.usage.output_tokens` | Output tokens, including reasoning. |
| `gen_ai.usage.cache_read.input_tokens` | When not zero. |
| `gen_ai.usage.cache_creation.input_tokens` | When not zero. |
| `gen_ai.usage.reasoning.output_tokens` | When not zero. |
| `error.type` | The exception type, on failure; the span status is `Error`. |

Prompts, completions and tool arguments are not recorded on spans.

## Metrics

| Instrument | Kind | Unit | Tags |
|---|---|---|---|
| `gen_ai.client.token.usage` | histogram of long | `{token}` | operation, provider, request model, `gen_ai.token.type` = `input` or `output` |
| `gen_ai.client.operation.duration` | histogram of double | `s` | operation, provider, request model, `error.type` on failure |
