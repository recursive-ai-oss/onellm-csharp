# Subscription providers

`ClaudeCodeProvider` (`claude-code/*`) and `CodexProvider` (`codex/*`) use the
OAuth credentials written by the official `claude` and `codex` CLIs. They call
undocumented consumer endpoints which may change or stop working. Users are
responsible for complying with their provider's terms.

Neither provider is registered by default. Each instance takes an explicit
configuration directory and an injected clock; it never searches the environment,
the home directory, or another account. Log in with the official CLI first, using
`CLAUDE_CONFIG_DIR` or `CODEX_HOME` set to that same directory. Claude credentials
must be file-backed (`.credentials.json`); macOS Keychain credentials are not read.
Codex uses `auth.json`.

```csharp
using OneLlm;
using OneLlm.Providers.ClaudeCode;
using OneLlm.Providers.Codex;
using OneLlm.Providers.Subscriptions;

SubscriptionCatalog.Register(); // before constructing a runtime/catalog
using var claude = new ClaudeCodeProvider(claudeDirectory, clock);
using var codex = new CodexProvider(codexDirectory, clock);
var runtime = new Runtime();
runtime.Providers.Set("claude-code", claude);
runtime.Providers.Set("codex", codex);
var answer = await runtime.AskAsync(new AskRequest
{
    Model = "codex/gpt-6-astra",
    Prompt = "Say hello."
});
```

Use separate runtimes/registries for accounts which must not share a provider.
The host owns the provider lifetime. The optional `SubscriptionOptions` supplies
HTTP/endpoint overrides for tests; a supplied client must disable redirects and
must not log authentication headers. The default shared client never follows
redirects. Endpoint overrides require HTTPS, except HTTP on loopback.

Both providers support chat, streaming and client tools, reuse the Messages or
Responses codec, and preserve usage counters. Codex's backend is stream-only;
`SendAsync` accumulates that stream into a whole turn. Codex sends `store: false`
and requests encrypted reasoning for stateless replay. Claude prepends the
required CLI identity system block. No CLI process is spawned for inference.
Batch, token counting and media endpoints are not exposed by these providers.

The catalog lists current subscription model names and zero per-token prices.
Quota, model access and rate limits still depend on the account's plan. Zero here
means no per-token API charge, not a free subscription.

## Credential refresh and logging

Expiry is checked with a two-minute margin. Claude uses `expiresAt` (milliseconds);
Codex uses the access JWT's `exp`, falling back to `last_refresh` plus 25 minutes.
A 401 triggers one refresh and one replay; a second 401 fails. A refresh failure is
an `AuthException` naming the configuration directory, without upstream response
text, tokens or an inner exception.

Refreshes are serialized per full credential-file path in one process. Cooperating
OneLLM processes also hold an exclusive `.onellm-oauth.lock` file handle while
reloading and refreshing; a waiting process rereads credentials and adopts an
already refreshed token. Lock acquisition is cancellable and times out after
30 seconds. Path aliases/symlinks should not be used for the same account.
The official CLIs do not participate in this lock: **do not run CLI login or token
refresh concurrently with OneLLM for the same directory**. No coordination with
non-cooperating credential writers is claimed.

Refreshed credentials are written to an owner-readable/writable temporary file
in the same directory, then atomically renamed over the original. Unknown fields
in the CLI document and token object are retained; JSON whitespace is not.
Cancellation or refresh failure leaves the old document in place. Protect the
configuration directory as private account data (including on Windows, where its
ACL controls access).

Tokens are held as `Secret` values and scrubbed from HTTP and stream errors.
Each provider exposes a live `Secrets` list for hosts using
`RawTurnRecord.Create(..., provider.Secrets)` or their own raw logger. Never log
credential documents, authorization headers, or OAuth request/response bodies.
