using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.ClaudeCode;
using OneLlm.Providers.Codex;
using OneLlm.Providers.Subscriptions;
using OneLlm.Records;
using OneLlm.Pricing;
using OneLlm.Tests.Support;

namespace OneLlm.Tests.Providers;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private const string Access = "synthetic-access-old";
    private const string Refresh = "synthetic-refresh-old";
    private const string NewAccess = "synthetic-access-new";
    private const string NewRefresh = "synthetic-refresh-new";
    private const string Identity = "You are Claude Code, Anthropic's official CLI for Claude.";

    private static string Model(bool claude) => claude ? "claude-code/claude-sonnet-5-5" : "codex/gpt-6-astra";
    private static ChatRequest Hello(bool claude) => new() { Model = Model(claude), System = "Be concise.", Messages = [Message.FromUser("Hello")], MaxOutputTokens = 32 };
    private static ToolDefinition Tool() => new("weather", "Gets the weather", JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")!.AsObject());

    private static string WriteAuth(Scratch scratch, bool claude, bool expired = false, string access = Access, string refresh = Refresh)
    {
        JsonObject doc = claude
            ? new JsonObject { ["unknown"] = new JsonObject { ["keep"] = 7 }, ["claudeAiOauth"] = new JsonObject { ["accessToken"] = access, ["refreshToken"] = refresh, ["expiresAt"] = Now.AddHours(expired ? -1 : 1).ToUnixTimeMilliseconds(), ["scopes"] = new JsonArray("user:inference"), ["subscriptionType"] = "synthetic-plan", ["unknownTokenField"] = true } }
            : new JsonObject { ["unknown"] = new JsonObject { ["keep"] = 7 }, ["auth_mode"] = "chatgpt", ["last_refresh"] = Now.AddHours(expired ? -1 : 0).ToString("O"), ["tokens"] = new JsonObject { ["access_token"] = access, ["refresh_token"] = refresh, ["id_token"] = "synthetic-id-token", ["account_id"] = "synthetic-account", ["unknownTokenField"] = true } };
        return scratch.Write(claude ? ".credentials.json" : "auth.json", doc.ToJsonString());
    }

    private static IChatProvider Provider(bool claude, string root, StubServer server) => claude
        ? new ClaudeCodeProvider(root, new Clock(), Options(server, true))
        : new CodexProvider(root, new Clock(), Options(server, false));
    private static SubscriptionOptions Options(StubServer server, bool claude) => new() { Endpoint = new Uri(server.BaseUri, claude ? "v1/messages" : "backend-api/codex/responses"), TokenEndpoint = new Uri(server.BaseUri, "oauth/token") };
    private static void Dispose(IChatProvider provider) => ((IDisposable)provider).Dispose();

    private static StubResponse Reply(bool claude, bool stream = false, bool tool = false)
    {
        if (claude)
        {
            string content = tool ? """[{"type":"tool_use","id":"call_synthetic","name":"weather","input":{"city":"Paris"}}]""" : """[{"type":"text","text":"Hello!"}]""";
            string stop = tool ? "tool_use" : "end_turn";
            string whole = $$$$$"""{"id":"message_synthetic","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":{{{{{content}}}}},"stop_reason":"{{{{{stop}}}}}","usage":{"input_tokens":11,"output_tokens":3}}""";
            if (!stream)
            {
                return new StubResponse(200, whole, "application/json");
            }
            string block = tool ? """{"type":"tool_use","id":"call_synthetic","name":"weather","input":{}}""" : """{"type":"text","text":""}""";
            string delta = tool ? """{"type":"input_json_delta","partial_json":"{\"city\":\"Paris\"}"}""" : """{"type":"text_delta","text":"Hello!"}""";
            string sse = Event("message_start", """{"type":"message_start","message":{"id":"message_synthetic","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"usage":{"input_tokens":11,"output_tokens":0}}}""")
                + Event("content_block_start", $$$$$"""{"type":"content_block_start","index":0,"content_block":{{{{{block}}}}}}""")
                + Event("content_block_delta", $$$$$"""{"type":"content_block_delta","index":0,"delta":{{{{{delta}}}}}}""")
                + Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
                + Event("message_delta", $$$$$"""{"type":"message_delta","delta":{"stop_reason":"{{{{{stop}}}}}"},"usage":{"output_tokens":3}}""")
                + Event("message_stop", """{"type":"message_stop"}""");
            return new StubResponse(200, sse, "text/event-stream");
        }
        string output = tool ? """[{"type":"function_call","id":"item_synthetic","call_id":"call_synthetic","name":"weather","arguments":"{\"city\":\"Paris\"}"}]""" : """[{"type":"message","id":"item_synthetic","role":"assistant","content":[{"type":"output_text","text":"Hello!","annotations":[]}]}]""";
        string itemEvent = Event("response.created", """{"type":"response.created","response":{"id":"response_synthetic","model":"gpt-6-astra"}}""")
            + Event("response.output_item.done", new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = JsonNode.Parse(output)![0]!.DeepClone() }.ToJsonString());
        return new StubResponse(200, itemEvent + Event("response.completed", $$$$$"""{"type":"response.completed","response":{"id":"response_synthetic","model":"gpt-6-astra","status":"completed","output":{{{{{output}}}}},"usage":{"input_tokens":11,"output_tokens":3}}}"""), "text/event-stream");
    }
    private static string Event(string name, string body) => $"event: {name}\ndata: {body}\n\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_stream_and_tool_replay_use_subscription_wire(bool claude)
    {
        using var scratch = new Scratch();
        WriteAuth(scratch, claude);
        bool tooling = false;
        using var server = new StubServer(r => Reply(claude, JsonNode.Parse(r.Body)!["stream"]?.GetValue<bool>() == true, tooling));
        IChatProvider provider = Provider(claude, scratch.Root, server);
        try
        {
            ChatResponse whole = await provider.SendAsync(Hello(claude));
            Assert.Equal("Hello!", whole.Message.GetAssistantText());
            Assert.Equal(11, whole.Usage!.InputTokens);
            Assert.Equal(3, whole.Usage.OutputTokens);
            Assert.Equal(0m, whole.Cost!.Total);
            var accumulator = new MessageAccumulator();
            await foreach (StreamEvent e in provider.StreamAsync(Hello(claude))) { accumulator.Add(e); }
            Assert.Equal(whole.Message.GetAssistantText(), accumulator.ToMessage().GetAssistantText());
            Assert.Equal(whole.Usage.InputTokens, accumulator.ToMessage().Usage!.InputTokens);
            tooling = true;
            ChatRequest ask = Hello(claude) with { Tools = [Tool()] };
            ChatResponse called = await provider.SendAsync(ask);
            ToolCall call = Assert.Single(called.Message.ToolCalls);
            Assert.Equal("weather", call.Tool);
            Assert.Equal("Paris", call.Inputs!["city"]!.GetValue<string>());
            var toolAccumulator = new MessageAccumulator();
            await foreach (StreamEvent e in provider.StreamAsync(ask)) { toolAccumulator.Add(e); }
            Assert.Equal(call.Inputs.ToJsonString(), Assert.Single(toolAccumulator.ToMessage().ToolCalls).Inputs!.ToJsonString());
            tooling = false;
            Message result = new() { Assistant = called.Message.Assistant, ToolCalls = [new ToolCall(call.Id, call.Tool) { Inputs = call.Inputs, Result = [new TextPart("Sunny")] }] };
            await provider.SendAsync(ask with { Messages = [Message.FromUser("Weather?"), result] });
            foreach (StubRequest wire in server.Requests)
            {
                Assert.Equal("POST", wire.Method);
                Assert.Equal(claude ? "/v1/messages" : "/backend-api/codex/responses", wire.Target);
                Assert.Equal("Bearer " + Access, wire.Headers["Authorization"]);
                Assert.False(wire.Headers.ContainsKey("x-api-key"));
                JsonObject body = JsonNode.Parse(wire.Body)!.AsObject();
                Assert.Equal(Model(claude).Split('/')[1], body["model"]!.GetValue<string>());
                if (claude)
                {
                    Assert.Equal("2023-06-01", wire.Headers["anthropic-version"]);
                    Assert.Equal("claude-code-20250219,oauth-2025-04-20", wire.Headers["anthropic-beta"]);
                    Assert.Equal("claude-cli/2.1.280", wire.Headers["User-Agent"]);
                    Assert.Equal("cli", wire.Headers["x-app"]);
                    Assert.Equal(Identity, body["system"]![0]!["text"]!.GetValue<string>());
                    Assert.Equal("Be concise.", body["system"]![1]!["text"]!.GetValue<string>());
                    Assert.Equal("ephemeral", body["system"]!.AsArray()[^1]!["cache_control"]!["type"]!.GetValue<string>());
                }
                else
                {
                    Assert.Equal("synthetic-account", wire.Headers["chatgpt-account-id"]);
                    Assert.Equal("responses=experimental", wire.Headers["OpenAI-Beta"]);
                    Assert.Equal("codex_cli_go", wire.Headers["originator"]);
                    Assert.True(body["stream"]!.GetValue<bool>());
                    Assert.False(body["store"]!.GetValue<bool>());
                    Assert.Equal("reasoning.encrypted_content", body["include"]![0]!.GetValue<string>());
                    Assert.Equal("Be concise.", body["instructions"]!.GetValue<string>());
                    Assert.False(body.ContainsKey("max_output_tokens"));
                }
            }
            JsonObject replay = JsonNode.Parse(server.Requests[^1].Body)!.AsObject();
            string replayText = replay.ToJsonString();
            Assert.Contains(claude ? "tool_result" : "function_call_output", replayText, StringComparison.Ordinal);
            Assert.Contains("call_synthetic", replayText, StringComparison.Ordinal);
            Assert.Contains("Sunny", replayText, StringComparison.Ordinal);
        }
        finally { Dispose(provider); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Expiry_or_401_refreshes_once_and_preserves_cli_document(bool claude, bool rejected)
    {
        using var scratch = new Scratch();
        string path = WriteAuth(scratch, claude, expired: !rejected);
        int refreshes = 0;
        using var server = new StubServer(request =>
        {
            if (request.Target == "/oauth/token")
            {
                Interlocked.Increment(ref refreshes);
                return new StubResponse(200, $$$$$"""{"access_token":"{{{{{NewAccess}}}}}","refresh_token":"{{{{{NewRefresh}}}}}","id_token":"synthetic-new-id","expires_in":3600,"refresh_token_expires_in":7200,"scope":"user:inference user:profile"}""", "application/json");
            }
            return request.Headers["Authorization"] == "Bearer " + Access ? new StubResponse(401, "rejected " + Access) : Reply(claude);
        });
        // Two instances of the same account must not race a rotating refresh token.
        IChatProvider a = Provider(claude, scratch.Root, server);
        IChatProvider b = Provider(claude, scratch.Root, server);
        try
        {
            await Task.WhenAll(a.SendAsync(Hello(claude)), b.SendAsync(Hello(claude)));
            Assert.Equal(1, refreshes);
            StubRequest refresh = Assert.Single(server.Requests, r => r.Target == "/oauth/token");
            JsonObject refreshBody = JsonNode.Parse(refresh.Body)!.AsObject();
            Assert.Equal("refresh_token", refreshBody["grant_type"]!.GetValue<string>());
            Assert.Equal(Refresh, refreshBody["refresh_token"]!.GetValue<string>());
            Assert.Equal(claude ? "9d1c250a-e61b-44d9-88ed-5944d1962f5e" : "app_EMoamEEZ73f0CkXaXp7hrann", refreshBody["client_id"]!.GetValue<string>());
            if (claude) { Assert.Equal("user:inference", refreshBody["scope"]!.GetValue<string>()); }
            JsonObject doc = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal(7, doc["unknown"]!["keep"]!.GetValue<int>());
            JsonObject tokens = doc[claude ? "claudeAiOauth" : "tokens"]!.AsObject();
            Assert.True(tokens["unknownTokenField"]!.GetValue<bool>());
            Assert.Equal(NewAccess, tokens[claude ? "accessToken" : "access_token"]!.GetValue<string>());
            Assert.Equal(NewRefresh, tokens[claude ? "refreshToken" : "refresh_token"]!.GetValue<string>());
            if (claude)
            {
                Assert.Equal(Now.AddHours(1).ToUnixTimeMilliseconds(), tokens["expiresAt"]!.GetValue<long>());
                Assert.Equal(Now.AddHours(2).ToUnixTimeMilliseconds(), tokens["refreshTokenExpiresAt"]!.GetValue<long>());
                Assert.Equal("synthetic-plan", tokens["subscriptionType"]!.GetValue<string>());
                Assert.Equal(2, tokens["scopes"]!.AsArray().Count);
            }
            else
            {
                Assert.Equal(Now, DateTimeOffset.Parse(doc["last_refresh"]!.GetValue<string>()));
                Assert.Equal("synthetic-new-id", tokens["id_token"]!.GetValue<string>());
                Assert.Equal("chatgpt", doc["auth_mode"]!.GetValue<string>());
            }
            if (!OperatingSystem.IsWindows()) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)); }
            Assert.Empty(Directory.GetFiles(scratch.Root, "*.tmp"));
            // Loading the saved document again uses it without another refresh.
            await a.SendAsync(Hello(claude));
            Assert.Equal(1, refreshes);
        }
        finally { Dispose(a); Dispose(b); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_failure_is_typed_and_never_exposes_credentials(bool claude)
    {
        using var scratch = new Scratch();
        string path = WriteAuth(scratch, claude, expired: true);
        string before = await File.ReadAllTextAsync(path);
        using var server = new StubServer(_ => new StubResponse(400, $"{{\"error\":\"{Access} {Refresh}\"}}", "application/json"));
        IChatProvider provider = Provider(claude, scratch.Root, server);
        try
        {
            AuthException error = await Assert.ThrowsAsync<AuthException>(() => provider.SendAsync(Hello(claude)));
            Assert.Contains(scratch.Root, error.Message, StringComparison.Ordinal);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(Access, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { Dispose(provider); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Upstream_errors_and_raw_records_scrub_access_refresh_and_identity(bool claude, bool stream)
    {
        using var scratch = new Scratch();
        WriteAuth(scratch, claude);
        string secretText = Access + " " + Refresh + " synthetic-account synthetic-id-token";
        using var server = new StubServer(_ => stream
            ? new StubResponse(200, Event("error", $$$$$"""{"type":"error","error":{"type":"invalid_request_error","message":"{{{{{secretText}}}}}"}}"""), "text/event-stream")
            : new StubResponse(400, $$$$$"""{"error":{"type":"invalid_request_error","message":"{{{{{secretText}}}}}"}}""", "application/json"));
        IChatProvider provider = Provider(claude, scratch.Root, server);
        try
        {
            ProviderException error = await Assert.ThrowsAnyAsync<ProviderException>(async () =>
            {
                if (stream) { await foreach (StreamEvent _ in provider.StreamAsync(Hello(claude))) { } }
                else { await provider.SendAsync(Hello(claude)); }
            });
            Assert.DoesNotContain(Access, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, error.ToString(), StringComparison.Ordinal);
            IReadOnlyList<Secret> secrets = provider is ClaudeCodeProvider c ? c.Secrets : ((CodexProvider)provider).Secrets;
            Assert.All(secrets, s => Assert.Equal(Secret.Mask, s.ToString()));
            RawTurnRecord record = RawTurnRecord.Create("synthetic-instance", Now, secretText, error.ToString(), secrets);
            Assert.DoesNotContain(Access, record.Request + record.Response, StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, record.Request + record.Response, StringComparison.Ordinal);
            if (!claude)
            {
                Assert.DoesNotContain("synthetic-account", error.ToString() + record.Request, StringComparison.Ordinal);
                Assert.DoesNotContain("synthetic-id-token", error.ToString() + record.Request, StringComparison.Ordinal);
            }
        }
        finally { Dispose(provider); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Accounts_are_bound_to_the_explicit_directory_and_missing_credentials_do_not_fall_back(bool claude)
    {
        using var first = new Scratch();
        using var second = new Scratch();
        using var missing = new Scratch();
        WriteAuth(first, claude, access: "synthetic-person-one", refresh: "synthetic-refresh-one");
        WriteAuth(second, claude, access: "synthetic-person-two", refresh: "synthetic-refresh-two");
        using var server = new StubServer(_ => Reply(claude));
        IChatProvider a = Provider(claude, first.Root, server);
        IChatProvider b = Provider(claude, second.Root, server);
        IChatProvider absent = Provider(claude, missing.Root, server);
        try
        {
            await a.SendAsync(Hello(claude));
            await b.SendAsync(Hello(claude));
            Assert.Equal("Bearer " + "synthetic-person-one", server.Requests[0].Headers["Authorization"]);
            Assert.Equal("Bearer " + "synthetic-person-two", server.Requests[1].Headers["Authorization"]);
            AuthException error = await Assert.ThrowsAsync<AuthException>(() => absent.SendAsync(Hello(claude)));
            Assert.Contains(missing.Root, error.Message, StringComparison.Ordinal);
            Assert.Equal(2, server.Requests.Count);
        }
        finally { Dispose(a); Dispose(b); Dispose(absent); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_credentials_and_refresh_documents_are_typed_without_parser_content(bool claude)
    {
        using var scratch = new Scratch();
        string path = WriteAuth(scratch, claude, expired: true);
        using var server = new StubServer(_ => new StubResponse(200, "{ malformed " + Access + Refresh, "application/json"));
        IChatProvider provider = Provider(claude, scratch.Root, server);
        try
        {
            foreach (bool malformedFile in new[] { false, true })
            {
                if (malformedFile) { await File.WriteAllTextAsync(path, "{ malformed " + Access + Refresh); }
                AuthException error = await Assert.ThrowsAsync<AuthException>(() => provider.SendAsync(Hello(claude)));
                Assert.Contains(scratch.Root, error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(Access, error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(Refresh, error.ToString(), StringComparison.Ordinal);
                Assert.Null(error.InnerException);
            }
        }
        finally { Dispose(provider); }
    }

    [Fact]
    public async Task Codex_jwt_expiry_and_id_token_account_claim_are_used()
    {
        using var scratch = new Scratch();
        static string Jwt(JsonObject claims) => "synthetic." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
        string expired = Jwt(new JsonObject { ["exp"] = Now.AddHours(-1).ToUnixTimeSeconds() });
        string fresh = Jwt(new JsonObject { ["exp"] = Now.AddHours(1).ToUnixTimeSeconds() });
        string id = Jwt(new JsonObject { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "synthetic-jwt-account" } });
        string path = WriteAuth(scratch, false, access: expired);
        JsonObject doc = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        doc["tokens"]!.AsObject().Remove("account_id");
        doc["tokens"]!["id_token"] = id;
        await File.WriteAllTextAsync(path, doc.ToJsonString());
        using var server = new StubServer(r => r.Target == "/oauth/token"
            ? new StubResponse(200, new JsonObject { ["access_token"] = fresh, ["refresh_token"] = NewRefresh }.ToJsonString(), "application/json")
            : Reply(false));
        IChatProvider provider = Provider(false, scratch.Root, server);
        try
        {
            await provider.SendAsync(Hello(false));
            Assert.Equal("/oauth/token", server.Requests[0].Target);
            Assert.Equal("synthetic-jwt-account", server.Requests[1].Headers["chatgpt-account-id"]);
            Assert.Equal("Bearer " + fresh, server.Requests[1].Headers["Authorization"]);
        }
        finally { Dispose(provider); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Redirects_are_not_followed_with_credentials(bool claude)
    {
        using var scratch = new Scratch();
        WriteAuth(scratch, claude);
        using var receiver = new StubServer(_ => Reply(claude));
        using var server = new StubServer(_ => new StubResponse(307, Headers: new Dictionary<string, string> { ["Location"] = receiver.BaseUri.AbsoluteUri }));
        IChatProvider provider = Provider(claude, scratch.Root, server);
        try
        {
            await Assert.ThrowsAnyAsync<ProviderException>(() => provider.SendAsync(Hello(claude)));
            Assert.Empty(receiver.Requests);
        }
        finally { Dispose(provider); }
    }

    [Fact]
    public void Catalog_has_zero_prices_but_default_registry_does_not_create_subscription_providers()
    {
        SubscriptionCatalog.Register();
        ModelCatalog catalog = CatalogLoader.Load(getEnvironmentVariable: _ => null);
        ProviderRegistry registry = ProviderRegistry.CreateWithDefaults(_ => null);
        foreach (bool claude in new[] { true, false })
        {
            ResolvedModel model = catalog.Resolve(Model(claude));
            Assert.Equal(claude ? "claude-code" : "codex", model.Provider.Name);
            Assert.NotNull(model.Model);
            Assert.Equal(0m, CostCalculator.Price(model, new Usage { InputTokens = 100, OutputTokens = 50, CacheReadInputTokens = 10, CacheCreationInputTokens = 5 })!.Total);
            Assert.Throws<CapabilityMissingException>(() => registry.Get(model, new Clock()));
        }
    }
}
