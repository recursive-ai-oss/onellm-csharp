# Cloud deployments

A deployment is **where a provider's requests go and how they are authenticated**. The request and response shapes are the provider's own, so the same codecs serve every deployment and each deployment is a small class. Nothing here uses a cloud SDK; signing and token exchange run on the BCL's HMAC, SHA-256 and RSA.

| Provider | Deployment | Class | Auth |
|---|---|---|---|
| OpenAI | Azure OpenAI / Foundry | `AzureOpenAIEndpoint` (an `OpenAIEndpoint`) | `api-key` header, or a Microsoft Entra bearer token from a delegate |
| Anthropic | Amazon Bedrock | `BedrockDeployment` | AWS Signature Version 4, or a Bedrock API key (bearer) |
| Anthropic | Vertex AI | `VertexAnthropicDeployment` | OAuth2 access token (service-account JWT, or a delegate) |
| Gemini | Vertex AI | `VertexGeminiEndpoint` with `GoogleBearerAuth` | the same |

`provider.Name` stays `anthropic`, `openai` or `gemini`; `provider.Deployment` is `bedrock`, `vertex` or `azure`.

Credentials are explicit values, a delegate, or (AWS only) the standard `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` and `AWS_BEARER_TOKEN_BEDROCK` variables through `FromEnvironment`. No config or credentials files, no instance metadata service and no `gcloud` state are read. Keys, session tokens and access tokens are `Secret`s and are removed from error text.

## Azure OpenAI / Foundry

```csharp
var endpoint = new AzureOpenAIEndpoint(new Uri("https://my-resource.openai.azure.com/"), new Secret(key));
var provider = new OpenAIProvider(new OpenAIOptions { Endpoint = endpoint }, TimeProvider.System);
```

- **v1 form (default):** `{resource}/openai/v1/responses` (or `chat/completions`, `embeddings`, `batches`, `files`). No `api-version`; the request's `model` is the **deployment name**.
- **Deployment-path form:** set `AzureOpenAIOptions.ApiVersion` (for example `2024-10-21`) and the chat, completions and embeddings routes become `/openai/deployments/{deployment}/…?api-version=…`. `Deployments` maps model names to deployment names.
- **Entra ID:** pass a `Func<CancellationToken, ValueTask<AccessToken>>` and a clock. Ask your credential library for the scope `https://cognitiveservices.azure.com/.default`. The token is refreshed five minutes before it expires, and one fetch serves concurrent callers.
- **Pricing:** `AzureOpenAIEndpoint.CatalogAliases` returns a catalog layer making each deployment name an alias of the model it serves, so cost and parameter rules follow the model.
- **Errors:** a 400 with `error.code` `content_filter` becomes `ContentFilterException`, with the categories and severities from `innererror.content_filter_result(s)`. A 429 honours `Retry-After`.

## Amazon Bedrock

```csharp
var bedrock = BedrockDeployment.FromEnvironment("us-east-1", TimeProvider.System, new BedrockOptions { InferenceProfile = "us" });
var claude = new AnthropicProvider(bedrock, TimeProvider.System);
```

- **Wire:** `POST /model/{modelId}/invoke` and `/invoke-with-response-stream` carrying the Messages body with `anthropic_version` (default `bedrock-2023-05-31`) in the body and no `model` or `stream` members. The model id is percent-encoded in the path (`:` becomes `%3A`) and, as SigV4 requires for non-S3 services, encoded once more in the canonical request.
- **Streaming:** the response is the AWS binary event stream (prelude, headers, payload, two CRC-32s). Each `chunk` message carries `{"bytes": base64(event)}`; the events are the Messages streaming events, so streaming builds the same `Message` as a whole call. A bad checksum, a damaged frame or a stream that ends inside a message is a `MalformedResponseException`; exception frames (`throttlingException`, `validationException`, …) map to the typed errors.
- **Model ids:** a friendly name (`claude-sonnet-4-5-20250929`) maps to `anthropic.claude-sonnet-4-5-20250929-v1:0` through a built-in table, then the rule `anthropic.<name>`. `BedrockOptions.InferenceProfile` (`us`, `eu`, `apac`, `global`) prefixes the id for cross-region inference profiles; `ModelIds` overrides any name with an exact id or ARN.
- **Auth:** SigV4 with service `bedrock`, signing `host`, `content-type`, `accept` (whole calls), every `x-amz-*` header (`x-amz-date`, and `x-amz-security-token` when there is a session token) and the body hash. A Bedrock API key is sent as `Authorization: Bearer`.
- **Refused before sending** (`BadRequestException`): message batches (use Bedrock batch inference jobs) and token counting; the counter falls back to its local estimate.
- **Sampling parameters:** the Anthropic codec reads `AnthropicOptions.ApiVersion` (default `2026-10-21`, an `anthropic-version` header value on the first-party API) to decide whether `temperature`, `top_p` and `top_k` may be sent: versions from `2026-10-21` on reject them, so the codec drops them. On Bedrock and Vertex the body carries its own `anthropic_version` and the header is not used, so to keep sampling parameters set `new AnthropicOptions { ApiVersion = "2023-06-01" }` (any earlier version string).
- **Prices** are the first-party Claude entries; regional pricing (`region: us` multiplier) is the caller's to apply from the catalog.

## Vertex AI

```csharp
var account = GoogleServiceAccount.FromFile("service-account.json");
var claude = new AnthropicProvider(new VertexAnthropicDeployment(account, "us-east5", TimeProvider.System), TimeProvider.System);
var gemini = new GeminiProvider(new GeminiOptions
{
    Time = TimeProvider.System,
    Auth = new GoogleBearerAuth(account.CreateTokenCache(TimeProvider.System)),
    Endpoint = new VertexGeminiEndpoint(account.ProjectId!, "us-central1"),
    Deployment = "vertex",
});
```

- **Service account:** an RS256-signed JWT (`iss`, `scope`, `aud`, `iat`, `exp` one hour out, optional `sub`) is exchanged at the token URI with the `urn:ietf:params:oauth:grant-type:jwt-bearer` grant (RFC 7523). Tokens are cached and refreshed five minutes before expiry; 5xx and 429 on the token endpoint are retried. A token delegate can replace the exchange.
- **Claude:** `…/projects/{p}/locations/{l}/publishers/anthropic/models/{model}:rawPredict` or `:streamRawPredict`, `anthropic_version: vertex-2023-10-16` in the body, no `model` member, `stream` kept. The `@date` form of model ids is handled. Location `global` uses `aiplatform.googleapis.com`; others `{location}-aiplatform.googleapis.com`.
- **Gemini:** `…/publishers/google/models/{model}:generateContent` (and `:streamGenerateContent?alt=sse`) over the same codec as the Gemini API.
- **Refused before sending** (`BadRequestException`): Claude batches and token counting; Gemini `countTokens`, embeddings, batch and files (their Vertex request shapes differ from the Gemini API's and are not mapped yet).
- Project, region and location strings are validated as plain names, so they cannot redirect a request to another host; base addresses must be https (loopback excepted, for tests).

## Conformance

Fixtures are written from the vendors' reference pages and say so in their descriptions; none was recorded from a live call. Each replay checks the request path, headers and body as well as the decoded answer. Live rows need real accounts and stay open.

| Deployment | chat | stream | tools | error fixtures |
|---|---|---|---|---|
| Bedrock Claude | `chat/basic`, `chat/model-id-with-colon` | `chat/stream` (event stream), `chat/stream-whole` | `tools/client-tools-roundtrip` | throttling, access denied, validation (too long), mid-stream exception |
| Vertex Claude | `chat/basic` | `chat/stream`, `chat/stream-whole` | `tools/client-tools-roundtrip` | quota, unauthenticated |
| Vertex Gemini | `chat/basic` | `stream/text` | `functions/call-and-result` | permission denied |
| Azure OpenAI | `responses-v1`, `chat-completions-deployment-path` | `responses-v1-stream` | `responses-v1-function-tools` | content filter 400, rate limit 429 (+ embeddings, deployment path) |

Signing is checked against the published AWS Signature Version 4 test suite (`testdata/aws-sigv4`): every case's canonical request, string to sign and Authorization header must match, except `get-header-value-multiline` (obsolete header line folding cannot be put on an HTTP request message) and `post-x-www-form-urlencoded-parameters` (the suite's own artifacts disagree with one another); for `post-x-www-form-urlencoded` the canonical request is not compared because the suite's file lists a header its own string to sign omits. The JWT path is checked against the RFC 7515 appendix A.2 vector and by verifying signatures with the public key.
