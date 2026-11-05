# OneLlm.Conformance: recorded fixtures and the replay harness

Provider tests run against recorded conversations, not live APIs. This project holds the fixture format, the handler that replays
fixtures, the handler that records them, and the scrubber that keeps secrets out of both.

## Fixture files

A fixture is one JSON file under `testdata/providers/<provider>/<deployment>/<feature>/` (hand-written samples used by the harness's own
tests live in `testdata/fixtures/`).

```json
{
  "format": 1,
  "description": "What the fixture shows, in a sentence.",
  "exchanges": [
    {
      "request": {
        "method": "POST",
        "path": "/v1/messages?beta=true",
        "headers": { "anthropic-version": "2023-06-01", "content-type": "application/json" },
        "body": { "model": "m", "max_tokens": 64, "messages": [] }
      },
      "response": {
        "status": 200,
        "headers": { "content-type": "application/json" },
        "body": { "id": "msg_1" }
      }
    }
  ]
}
```

- **`exchanges`** are in the order the calls happened. A fixture can hold a whole multi-call scenario (a tool loop, a batch poll).
- **Request.** `method`, `path` (with query), `headers` (only those that change what the provider does: version and beta headers,
  content type; authentication is never recorded) and a body.
- **Response.** `status`, `headers` (content type and the headers clients act on: `retry-after`, rate-limit headers) and a body, or for a
  stream `sse`.
- **Bodies** are stored in the most readable form that is exact: `body` holds JSON as a JSON value, `bodyText` holds UTF-8 text,
  `bodyBase64` holds anything else. JSON bodies are compared structurally (key order and whitespace do not matter; numbers
  compare by value; everything else is exact).
- **`sse`** is a list of strings, the chunks the stream delivered. Each chunk is returned by one read of the response stream, and a chunk
  may end in the middle of an event, so parsers' buffering is exercised.

## Replay

`ReplayHandler` is an `HttpMessageHandler`. Give it to the client under test (`new HttpClient(handler)`).

- Each request must match an exchange that is **not yet used**: method, path and query, every header recorded on the request, and the body
  exactly. Headers not recorded are ignored.
- The matching exchange's response is returned and the exchange is used up. Replay is exact and ordered by availability; the same
  request twice needs two exchanges.
- A request that matches nothing throws `FixtureMismatchException` naming the first difference from the closest exchange (a JSON path for
  bodies). Values of secret-looking headers are never printed.
- `AssertAllUsed()` throws when an exchange was never requested, so a fixture cannot go stale silently.
- `Requests` exposes what the client sent, for assertions on request bytes.

## Recording

`RecordingHandler` wraps a real handler, forwards calls, and writes the exchanges to a fixture file on `Save()`. Everything goes through the
scrubber first. Recordings are made with prompts that are synthetic, and only where a key is available and the owner has allowed the call.

## Scrubbing

`FixtureScrubber` runs on every recording, and `FindViolations` is used by a test over every committed fixture, so a fixture that would leak
cannot be committed unnoticed.

- **Request headers** are kept only from an allow list (`content-type`, `accept`, `anthropic-version`, `anthropic-beta`, `openai-beta`,
  `x-goog-api-client`, `api-version`); anything that looks like authentication is dropped.
- **Response headers** are kept only for content type, `retry-after` and rate-limit families; request ids and account headers are dropped.
- **Values** (in paths, JSON strings at any depth, SSE chunks, descriptions) are scanned for API keys of the major vendors' shapes, bearer
  tokens, JSON web tokens, AWS access key IDs, organization, project and request IDs, email addresses (other than `example.com/org/net`)
  and private-key headers. Matches become `[scrubbed]`.
- **Identifying JSON keys** (`organization`, `project_id`, `account_id`, `request_id`, `user`, `email`, `api_key`, token and secret fields)
  have their string values replaced.
- **Known secrets:** the caller passes the literal key used for the recording and every occurrence is replaced.

A fixture is public text: prompts are synthetic and nothing from a real conversation belongs in one.

## The mock server

`tools/OneLlm.MockServer` serves the same files over loopback HTTP for benchmarks:

```
onellm-mockserver --fixtures testdata/fixtures --port 5099
```

A request is answered by the first exchange with the same method and path (including query); bodies and headers are not compared, since
benchmark requests differ in size. Exchanges are reusable and streamed responses are written chunk by chunk with a flush after each.
