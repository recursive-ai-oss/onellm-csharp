# Errors and retries

## Errors

Every failure OneLLM raises derives from `OneLlmException`. Provider failures derive from `ProviderException` (provider name, HTTP
status). API keys are held in `Secret` and removed from every message.

| Type | When |
|---|---|
| `AuthException` | 401, 403: key missing, wrong or not allowed. |
| `RateLimitException` | 429; carries `RetryAfter`. |
| `OverloadedException` | 503, 529; carries `RetryAfter`. |
| `ContextOverflowException` | The request is larger than the model's context window. |
| `ContentFilterException` | The provider's safety filter blocked the request or response. |
| `BadRequestException` | 400, 404, 409, 413, 422: the request is wrong. Never retried. |
| `TransportException` | The connection failed or timed out. |
| `MalformedResponseException` | The response is not what the API documents. |
| `RefusedException` | The model declined to answer. |
| `BudgetExceededException` | A spend or token cap was reached (`BudgetKind`). |
| `CapabilityMissingException` | A feature needs a package or grant that is not present; the message names it. |
| `BadDefinitionException` | A definition file is invalid; carries file, line and column. |
| `ModelNotFoundException` | A model name resolves to nothing. |

Codecs map statuses with `ProviderErrors.FromStatus` and then look in the body for causes only the provider reports (a context
overflow arriving as a plain 400).

## Retries

`RetryPolicy` (defaults: 2 retries, base delay 500 ms, cap 30 s, jitter 25%, longest accepted `Retry-After` 60 s).

- **Retried:** 408, 429, 500, 502, 503, 504, 529 and transport failures.
- **Never retried:** other statuses, including every 4xx that says the request is wrong, and a 429 that reports an exhausted quota
  rather than a rate.
- **Delay:** the backoff doubles per attempt from the base delay, up to the cap, plus 0–25% random jitter; when the provider's
  `Retry-After` (seconds or an HTTP date, or `retry-after-ms`) is longer, that is used. A `Retry-After` beyond the accepted maximum is not
  waited out: the error is returned at once.
- **Timeouts** apply per attempt, up to the response headers; waits use the runtime's `TimeProvider`, so tests run on a fake clock.
- **Cancellation** ends a request or a wait immediately with `OperationCanceledException`.

## HTTP

All clients share one `SocketsHttpHandler` with `AllowAutoRedirect = false`: a redirect could carry credentials to another host, so a
redirect response is an error instead.
