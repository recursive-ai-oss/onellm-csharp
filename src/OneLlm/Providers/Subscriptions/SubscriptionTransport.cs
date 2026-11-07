using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OneLlm.Http;

namespace OneLlm.Providers.Subscriptions;

internal sealed class SubscriptionTransport(SubscriptionCredentials credentials, bool claude, SubscriptionOptions options) : HttpMessageHandler
{
    private readonly HttpClient _backend = options.HttpClient ?? SharedHttpClient.Instance;
    private readonly Uri _endpoint = SubscriptionCredentials.Validate(options.Endpoint ?? new Uri(claude ? "https://api.anthropic.com/v1/messages" : "https://chatgpt.com/backend-api/codex/responses"));

    private async Task<HttpResponseMessage> SendBackendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _backend.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new TransportException(claude ? "claude-code" : "codex", Secret.ScrubAll("the subscription request failed: " + e.Message, credentials.Secrets));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonObject body = JsonNode.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
        string model = body["model"]!.GetValue<string>();
        string prefix = claude ? "claude-code/" : "codex/";
        if (model.StartsWith(prefix, StringComparison.Ordinal))
        {
            body["model"] = model[prefix.Length..];
        }
        if (claude)
        {
            var system = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "You are Claude Code, Anthropic's official CLI for Claude." });
            if (body["system"] is JsonArray blocks)
            {
                foreach (JsonNode? block in blocks)
                {
                    system.Add(block?.DeepClone());
                }
            }
            else if (body["system"] is JsonValue value)
            {
                system.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = value.GetValue<string>() });
            }
            system[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            body["system"] = system;
        }
        else
        {
            // The consumer endpoint is stream-only, stateless, and rejects the public API's output limit.
            body["stream"] = true;
            body["store"] = false;
            body.Remove("max_output_tokens");
            body["instructions"] ??= "";
            body["include"] = new JsonArray("reasoning.encrypted_content");
        }
        SubscriptionCredentials.Credential auth = await credentials.GetAsync(null, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = SubscriptionCredentials.JsonContent(body) };
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Access.Value);
            if (claude)
            {
                outgoing.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                outgoing.Headers.TryAddWithoutValidation("anthropic-beta", "claude-code-20250219,oauth-2025-04-20");
                outgoing.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.280");
                outgoing.Headers.TryAddWithoutValidation("x-app", "cli");
            }
            else
            {
                outgoing.Headers.TryAddWithoutValidation("chatgpt-account-id", auth.Account);
                outgoing.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
                outgoing.Headers.TryAddWithoutValidation("originator", "codex_cli_go");
                outgoing.Headers.TryAddWithoutValidation("User-Agent", "onellm-codex-provider/1.0");
            }
            outgoing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(body["stream"]?.GetValue<bool>() == true ? "text/event-stream" : "application/json"));
            HttpResponseMessage response = await SendBackendAsync(outgoing, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }
            response.Dispose();
            if (attempt != 0)
            {
                throw credentials.Failure();
            }
            auth = await credentials.GetAsync(auth.Access.Value, cancellationToken).ConfigureAwait(false);
        }
    }
}
