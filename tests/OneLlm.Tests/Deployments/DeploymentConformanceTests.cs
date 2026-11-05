using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Auth;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;
using OneLlm.Tests.Providers;

namespace OneLlm.Tests.Deployments;

/// <summary>
/// The deployment conformance rows. Each test replays one fixture written from the vendor reference (see the fixture descriptions): the
/// request the deployment sends must equal the fixture's request, byte-for-byte in the JSON body, path and recorded headers, and the answer
/// must decode to the message the direct API's equivalent fixture gives.
/// </summary>
public class DeploymentConformanceTests
{
    private static readonly Uri Local = new("http://localhost/");

    private static ReplayHandler Fixture(string relative) =>
        ReplayHandler.FromFile(Path.Combine(AppContext.BaseDirectory, "testdata", "providers", relative + ".json"));

    private static ChatRequest Hi() => AnthropicKit.Hi() with { System = "You are terse." };

    // ---- Bedrock ----

    private static (AnthropicProvider Provider, ReplayHandler Handler) Bedrock(string fixture, BedrockOptions? options = null)
    {
        ReplayHandler h = Fixture("anthropic/bedrock/" + fixture);
        var time = new ManualTimeProvider();
        var creds = new AwsCredentials("AKIDEXAMPLE", new Secret("test-secret-" + Guid.NewGuid().ToString("N")));
        var deployment = new BedrockDeployment("us-east-1", creds, time, (options ?? new BedrockOptions { InferenceProfile = "us" }) with { BaseAddress = Local });
        return (new AnthropicProvider(deployment, time, new AnthropicOptions { Http = new HttpClient(h), Retry = RetryPolicy.None }), h);
    }

    [Fact]
    public async Task Bedrock_chat_is_signed_and_sent_to_invoke_with_the_version_in_the_body()
    {
        (AnthropicProvider p, ReplayHandler h) = Bedrock("chat/basic");
        ChatResponse r = await p.SendAsync(Hi());
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal("anthropic", p.Name);
        Assert.Equal("bedrock", p.Deployment);
        string auth = h.Requests[0].Headers["authorization"];
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260101/us-east-1/bedrock/aws4_request, SignedHeaders=accept;content-type;host;x-amz-date, Signature=", auth, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bedrock_stream_decodes_the_event_stream_and_builds_the_whole_call_message()
    {
        (AnthropicProvider ps, ReplayHandler hs) = Bedrock("chat/stream");
        (ChatResponse streamed, List<StreamEventKind> kinds) = await AnthropicKit.StreamAsync(ps, Hi());
        hs.AssertAllUsed();
        (AnthropicProvider pw, ReplayHandler hw) = Bedrock("chat/stream-whole");
        ChatResponse whole = await pw.SendAsync(Hi());
        hw.AssertAllUsed();
        Assert.Equal("Hello, café 😀\n\"quoted\" \\ done.", streamed.Message.GetAssistantText());
        Assert.Equal(AnthropicKit.Json(whole.Message), AnthropicKit.Json(streamed.Message));
        Assert.Equal(StreamEventKind.MessageStart, kinds[0]);
        Assert.Equal(StreamEventKind.Done, kinds[^1]);
    }

    [Fact]
    public async Task Bedrock_client_tools_come_back_in_call_order_and_results_go_out_next_turn()
    {
        (AnthropicProvider p, ReplayHandler h) = Bedrock("tools/client-tools-roundtrip");
        Message question = Message.FromUser("What is the weather in San Francisco and in Paris?");
        ChatRequest first = new() { Model = AnthropicKit.Model, Messages = [question], Tools = [AnthropicKit.Weather()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        Assert.Equal(FinishReason.ToolCalls, r.Finish);
        Assert.Equal(2, r.Message.ToolCalls.Count);
        Message withResults = AnthropicKit.WithResults(
            r.Message,
            ("toolu_01A09q90qw90lq917835lq9", [new TextPart("15 degrees")], false),
            ("toolu_01B18r01rx01mr028946mr0", [new TextPart("raining"), new TextPart("9 degrees")], true));
        ChatResponse second = await p.SendAsync(first with { Messages = [question, withResults] });
        h.AssertAllUsed();
        Assert.Equal("San Francisco is 15 degrees. The Paris lookup failed.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Bedrock_model_id_with_a_version_suffix_is_percent_encoded_in_the_path()
    {
        (AnthropicProvider p, ReplayHandler h) = Bedrock("chat/model-id-with-colon", new BedrockOptions { ModelIds = new Dictionary<string, string> { ["claude-sonnet"] = "us.anthropic.claude-sonnet-4-5-20250929-v1:0" } });
        await p.SendAsync(Hi());
        h.AssertAllUsed();
        Assert.Contains("v1%3A0", h.Requests[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bedrock_throttling_is_a_rate_limit_error_with_its_retry_after()
    {
        (AnthropicProvider p, ReplayHandler h) = Bedrock("errors/throttling");
        var e = await Assert.ThrowsAsync<RateLimitException>(() => p.SendAsync(Hi()));
        h.AssertAllUsed();
        Assert.Equal(TimeSpan.FromSeconds(3), e.RetryAfter);
        Assert.Equal("ThrottlingException", e.Code);
        Assert.True(e.IsTransient);
    }

    [Fact]
    public async Task Bedrock_too_long_input_is_a_context_overflow_and_access_denied_is_an_auth_error()
    {
        (AnthropicProvider p, _) = Bedrock("errors/validation-too-long");
        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(() => p.SendAsync(Hi()));
        Assert.Equal(400, overflow.StatusCode);

        (AnthropicProvider q, _) = Bedrock("errors/access-denied");
        var denied = await Assert.ThrowsAsync<AuthException>(() => q.SendAsync(Hi()));
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal("AccessDeniedException", denied.Code);
    }

    [Fact]
    public async Task Bedrock_stream_exception_arrives_after_the_events_before_it()
    {
        (AnthropicProvider p, _) = Bedrock("errors/stream-exception");
        var seen = new List<StreamEventKind>();
        ProviderException e = await Assert.ThrowsAnyAsync<ProviderException>(async () =>
        {
            await foreach (StreamEvent ev in p.StreamAsync(Hi()))
            {
                seen.Add(ev.Kind);
            }
        });
        Assert.Contains(StreamEventKind.MessageStart, seen);
        Assert.Equal("modelStreamErrorException", e.Code);
        Assert.Equal(424, e.StatusCode);
    }

    [Fact]
    public async Task Bedrock_refuses_batches_and_token_counts_before_sending()
    {
        (AnthropicProvider p, ReplayHandler h) = Bedrock("chat/basic");
        await Assert.ThrowsAsync<BadRequestException>(() => p.CountTokensAsync(Hi()));
        await Assert.ThrowsAsync<BadRequestException>(() => p.GetBatchAsync("batch_1"));
        Assert.Empty(h.Requests);
    }

    // ---- Vertex AI: Claude ----

    private sealed class Router(HttpMessageHandler api, Func<int, string> token) : HttpMessageHandler
    {
        public int TokenCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                TokenCalls++;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"access_token":"{{token(TokenCalls)}}","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json"),
                };
            }

            return await new HttpMessageInvoker(api).SendAsync(request, cancellationToken);
        }
    }

    private static (AnthropicProvider Provider, ReplayHandler Handler, VertexAnthropicDeployment Deployment) VertexClaude(string fixture)
    {
        ReplayHandler h = Fixture("anthropic/vertex/" + fixture);
        var time = new ManualTimeProvider();
        int n = 0;
        var deployment = new VertexAnthropicDeployment(
            "my-project",
            "us-east5",
            _ => ValueTask.FromResult(new AccessToken(new Secret("ya29.fixture-token-" + ++n), time.GetUtcNow().AddHours(1))),
            time,
            new VertexAnthropicOptions { BaseAddress = Local });
        return (new AnthropicProvider(deployment, time, new AnthropicOptions { Http = new HttpClient(h), Retry = RetryPolicy.None }), h, deployment);
    }

    [Fact]
    public async Task Vertex_claude_chat_uses_rawPredict_with_a_bearer_token()
    {
        (AnthropicProvider p, ReplayHandler h, _) = VertexClaude("chat/basic");
        ChatResponse r = await p.SendAsync(Hi());
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal("vertex", p.Deployment);
        Assert.StartsWith("Bearer ya29.fixture-token-", h.Requests[0].Headers["authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vertex_claude_stream_is_server_sent_events_and_builds_the_whole_call_message()
    {
        (AnthropicProvider ps, ReplayHandler hs, _) = VertexClaude("chat/stream");
        (ChatResponse streamed, _) = await AnthropicKit.StreamAsync(ps, Hi());
        hs.AssertAllUsed();
        (AnthropicProvider pw, ReplayHandler hw, _) = VertexClaude("chat/stream-whole");
        ChatResponse whole = await pw.SendAsync(Hi());
        hw.AssertAllUsed();
        Assert.Equal(AnthropicKit.Json(whole.Message), AnthropicKit.Json(streamed.Message));
    }

    [Fact]
    public async Task Vertex_claude_client_tools_round_trip()
    {
        (AnthropicProvider p, ReplayHandler h, _) = VertexClaude("tools/client-tools-roundtrip");
        Message question = Message.FromUser("What is the weather in San Francisco and in Paris?");
        ChatRequest first = new() { Model = AnthropicKit.Model, Messages = [question], Tools = [AnthropicKit.Weather()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        Message withResults = AnthropicKit.WithResults(
            r.Message,
            ("toolu_01A09q90qw90lq917835lq9", [new TextPart("15 degrees")], false),
            ("toolu_01B18r01rx01mr028946mr0", [new TextPart("raining"), new TextPart("9 degrees")], true));
        ChatResponse second = await p.SendAsync(first with { Messages = [question, withResults] });
        h.AssertAllUsed();
        Assert.Equal(FinishReason.Stop, second.Finish);
    }

    [Fact]
    public async Task Vertex_claude_errors_use_the_google_error_shape()
    {
        (AnthropicProvider p, _, _) = VertexClaude("errors/quota");
        var limited = await Assert.ThrowsAsync<RateLimitException>(() => p.SendAsync(Hi()));
        Assert.Equal(429, limited.StatusCode);
        (AnthropicProvider q, _, _) = VertexClaude("errors/unauthenticated");
        var auth = await Assert.ThrowsAsync<AuthException>(() => q.SendAsync(Hi()));
        Assert.Equal(401, auth.StatusCode);
    }

    [Fact]
    public async Task Vertex_claude_refuses_batches_and_counts_and_names_dated_models_with_an_at_sign()
    {
        (AnthropicProvider p, ReplayHandler h, _) = VertexClaude("chat/basic");
        await Assert.ThrowsAsync<BadRequestException>(() => p.CountTokensAsync(Hi()));
        Assert.Empty(h.Requests);

        var d = new VertexAnthropicDeployment("proj", "global", _ => ValueTask.FromResult(new AccessToken(new Secret("t-t-t-t-t-t"), DateTimeOffset.MaxValue)), new ManualTimeProvider());
        await d.PrepareAsync(new AnthropicRequestInfo(AnthropicOperation.Messages, "claude-sonnet-4-5-20250929", false, null), default);
        HttpRequestMessage r = d.CreateRequest(new AnthropicRequestInfo(AnthropicOperation.Messages, "claude-sonnet-4-5-20250929", false, null), """{"model":"x","max_tokens":1}"""u8.ToArray());
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/proj/locations/global/publishers/anthropic/models/claude-sonnet-4-5@20250929:rawPredict", r.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Vertex_claude_with_a_service_account_fetches_its_token_through_the_jwt_exchange()
    {
        using RSA rsa = RSA.Create(2048);
        string keyJson = new JsonObject
        {
            ["client_email"] = "ci-bot@example-project.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["project_id"] = "my-project",
            ["token_uri"] = "http://localhost/token",
        }.ToJsonString();
        ReplayHandler api = Fixture("anthropic/vertex/tools/client-tools-roundtrip");
        var router = new Router(api, n => "ya29.exchanged-" + n);
        var time = new ManualTimeProvider();
        var deployment = new VertexAnthropicDeployment(GoogleServiceAccount.FromJson(keyJson), "us-east5", time, options: new VertexAnthropicOptions { BaseAddress = Local }, tokenHttp: new HttpClient(router));
        var p = new AnthropicProvider(deployment, time, new AnthropicOptions { Http = new HttpClient(router), Retry = RetryPolicy.None });

        Message question = Message.FromUser("What is the weather in San Francisco and in Paris?");
        ChatRequest first = new() { Model = AnthropicKit.Model, Messages = [question], Tools = [AnthropicKit.Weather()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        Message withResults = AnthropicKit.WithResults(
            r.Message,
            ("toolu_01A09q90qw90lq917835lq9", [new TextPart("15 degrees")], false),
            ("toolu_01B18r01rx01mr028946mr0", [new TextPart("raining"), new TextPart("9 degrees")], true));
        await p.SendAsync(first with { Messages = [question, withResults] });

        api.AssertAllUsed();
        Assert.Equal(1, router.TokenCalls); // one exchange served both calls
        Assert.All(api.Requests, q => Assert.Equal("Bearer ya29.exchanged-1", q.Headers["authorization"]));
        Assert.Contains(deployment.Secrets, s => s.Value == "ya29.exchanged-1");
    }

    // ---- Vertex AI: Gemini ----

    private static (GeminiProvider Provider, ReplayHandler Handler) VertexGeminiProvider(string fixture)
    {
        ReplayHandler h = Fixture("gemini/vertex/" + fixture);
        var time = new ManualTimeProvider();
        var cache = new AccessTokenCache(_ => ValueTask.FromResult(new AccessToken(new Secret("ya29.gemini-token"), time.GetUtcNow().AddHours(1))), time);
        var p = new GeminiProvider(new GeminiOptions
        {
            Time = time,
            Auth = new GoogleBearerAuth(cache),
            Endpoint = new VertexGeminiEndpoint("my-project", "us-central1", Local),
            Deployment = "vertex",
            Http = new HttpClient(h),
            Retry = RetryPolicy.None,
            Catalog = Gemini.GeminiHarness.DefaultCatalog,
        });
        return (p, h);
    }

    [Fact]
    public async Task Vertex_gemini_chat_uses_the_publisher_model_path_and_a_bearer_token()
    {
        (GeminiProvider p, ReplayHandler h) = VertexGeminiProvider("chat/basic");
        ChatResponse r = await p.SendAsync(new ChatRequest
        {
            Model = "gemini-3.5-flash-lite",
            System = "Be brief.",
            Messages = [Message.FromUser("Say hello in one word.")],
            MaxOutputTokens = 64,
            Temperature = 0.2,
            TopP = 0.9,
            StopSequences = ["END"],
        });
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal("vertex", p.Deployment);
        Assert.Equal("Bearer ya29.gemini-token", h.Requests[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Vertex_gemini_stream_uses_streamGenerateContent_with_sse()
    {
        (GeminiProvider p, ReplayHandler h) = VertexGeminiProvider("stream/text");
        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in p.StreamAsync(new ChatRequest { Model = "gemini-3.5-flash-lite", Messages = [Message.FromUser("Greet the world.")] }))
        {
            acc.Add(e);
        }

        h.AssertAllUsed();
        Assert.Equal("Hello, world!", acc.ToMessage().GetAssistantText());
    }

    [Fact]
    public async Task Vertex_gemini_function_call_and_result()
    {
        (GeminiProvider p, ReplayHandler h) = VertexGeminiProvider("functions/call-and-result");
        ToolDefinition weather = new("get_weather", "Gets the weather for a city.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")!);
        var request = new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("Weather in Paris?")], Tools = [weather], ToolChoice = ToolChoice.Auto };
        ChatResponse first = await p.SendAsync(request);
        ToolCall call = Assert.Single(first.Message.ToolCalls);
        Message turn = new()
        {
            Assistant = first.Message.Assistant,
            ToolCalls = [new ToolCall(call.Id, call.Tool) { Inputs = call.Inputs, After = call.After, Result = [new JsonPart(JsonNode.Parse("""{"temp_c":18}""")!)] }],
        };
        ChatResponse second = await p.SendAsync(new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("Weather in Paris?"), turn], Tools = [weather] });
        h.AssertAllUsed();
        Assert.Equal("It is 18 degrees in Paris.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Vertex_gemini_permission_denied_is_an_auth_error_and_unsupported_calls_are_refused()
    {
        (GeminiProvider p, ReplayHandler h) = VertexGeminiProvider("errors/permission-denied");
        var e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(new ChatRequest { Model = "gemini-3.5-flash-lite", Messages = [Message.FromUser("Hello.")] }));
        Assert.Equal(403, e.StatusCode);
        h.AssertAllUsed();

        await Assert.ThrowsAsync<BadRequestException>(() => p.EmbedAsync(new EmbeddingRequest { Model = "gemini-embedding-001", Inputs = ["x"] }));
        await Assert.ThrowsAsync<BadRequestException>(() => p.CountTokensAsync(new ChatRequest { Model = "gemini-3.5-flash-lite", Messages = [Message.FromUser("x")] }));
    }

    // ---- Azure OpenAI ----

    private static OpenAIProvider Azure(OpenAIFx fx, AzureOpenAIEndpoint endpoint, OpenAIApi api = OpenAIApi.Responses) =>
        new(new OpenAIOptions { Endpoint = endpoint, HttpClient = fx.Client, Catalog = OpenAIFx.Catalog, Retry = RetryPolicy.None }, new ManualTimeProvider(), api);

    private static AzureOpenAIEndpoint KeyEndpoint(AzureOpenAIOptions? options = null) => new(Local, new Secret("azure-key-0000"), options);

    private static ChatRequest Hello(string model = "gpt-6.1-terra") =>
        new() { Model = model, System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64 };

    [Fact]
    public async Task Azure_v1_chat_goes_to_openai_v1_responses_with_an_api_key_header()
    {
        using var fx = OpenAIFx.Open("openai/azure/chat/responses-v1.json");
        OpenAIProvider p = Azure(fx, KeyEndpoint());
        ChatResponse r = await p.SendAsync(Hello());
        fx.AssertAllUsed();
        Assert.Equal("Hello! How can I help?", r.Message.GetAssistantText());
        Assert.Equal("azure", p.Deployment);
        Assert.Equal("azure-key-0000", fx.Requests[0].Headers["api-key"]);
        Assert.False(fx.Requests[0].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Azure_v1_stream_builds_the_whole_call_message()
    {
        using var whole = OpenAIFx.Open("openai/azure/chat/responses-v1.json");
        using var streamed = OpenAIFx.Open("openai/azure/chat/responses-v1-stream.json");
        ChatResponse a = await Azure(whole, KeyEndpoint()).SendAsync(Hello());
        ChatResponse b = await OpenAIFx.CollectAsync(Azure(streamed, KeyEndpoint()), Hello());
        streamed.AssertAllUsed();
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
    }

    [Fact]
    public async Task Azure_v1_function_tools_round_trip()
    {
        using var fx = OpenAIFx.Open("openai/azure/tools/responses-v1-function-tools.json");
        OpenAIProvider p = Azure(fx, KeyEndpoint());
        ToolDefinition weather = new("get_weather", "Gets the weather for a city.", OpenAIFx.Obj("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}"""));
        ToolDefinition time = new("get_time", "Gets the time in a zone.", OpenAIFx.Obj("""{"type":"object","properties":{"zone":{"type":"string"}},"required":["zone"],"additionalProperties":false}""")) { Strict = true };
        Message ask = Message.FromUser("Weather and time in Paris?");
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = "gpt-6.1-terra", Messages = [ask], Tools = [weather, time] });
        Assert.Equal(["call_a1", "call_b2"], r.Message.ToolCalls.Select(c => c.Id));
        ToolCall[] done =
        [
            new(r.Message.ToolCalls[0].Id, "get_weather") { Inputs = r.Message.ToolCalls[0].Inputs, After = 1, Result = [new TextPart("Sunny, 21C")] },
            new(r.Message.ToolCalls[1].Id, "get_time") { Inputs = r.Message.ToolCalls[1].Inputs, After = 1, Result = [new TextPart("14:05")] },
        ];
        ChatResponse second = await p.SendAsync(new ChatRequest { Model = "gpt-6.1-terra", Messages = [ask, new Message { Assistant = r.Message.Assistant, ToolCalls = done }], Tools = [weather, time] });
        fx.AssertAllUsed();
        Assert.Equal("It is sunny and 14:05 in Paris.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Azure_deployment_path_form_names_the_deployment_and_the_api_version()
    {
        using var fx = OpenAIFx.Open("openai/azure/chat/chat-completions-deployment-path.json");
        var endpoint = KeyEndpoint(new AzureOpenAIOptions { ApiVersion = "2024-10-21", Deployments = new Dictionary<string, string> { ["gpt-4.1"] = "my-gpt-deployment" } });
        ChatResponse r = await Azure(fx, endpoint, OpenAIApi.ChatCompletions).SendAsync(new ChatRequest { Model = "gpt-4.1", System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64, Temperature = 0.2 });
        fx.AssertAllUsed();
        Assert.Equal("Hello! How can I help?", r.Message.GetAssistantText());
    }

    [Fact]
    public async Task Azure_deployment_path_form_embeddings()
    {
        using var fx = OpenAIFx.Open("openai/azure/embeddings/deployment-path.json");
        var endpoint = KeyEndpoint(new AzureOpenAIOptions { ApiVersion = "2024-10-21", Deployments = new Dictionary<string, string> { ["text-embedding-3-small"] = "my-embedding-deployment" } });
        EmbeddingResponse r = await Azure(fx, endpoint).EmbedAsync(new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["first", "second"], Dimensions = 3 });
        fx.AssertAllUsed();
        Assert.Equal(2, r.Embeddings.Count);
    }

    [Fact]
    public async Task Azure_with_an_entra_token_sends_a_bearer_and_fetches_it_once()
    {
        using var fx = OpenAIFx.Open("openai/azure/chat/responses-v1.json");
        var time = new ManualTimeProvider();
        int calls = 0;
        var endpoint = new AzureOpenAIEndpoint(Local, _ => ValueTask.FromResult(new AccessToken(new Secret("entra-token-" + ++calls), time.GetUtcNow().AddHours(1))), time);
        await Azure(fx, endpoint).SendAsync(Hello());
        fx.AssertAllUsed();
        Assert.Equal("Bearer entra-token-1", fx.Requests[0].Headers["authorization"]);
        Assert.False(fx.Requests[0].Headers.ContainsKey("api-key"));
        Assert.Equal(1, calls);
        Assert.Contains(endpoint.Secrets, s => s.Value == "entra-token-1");
    }

    [Fact]
    public async Task Azure_content_filter_block_is_a_content_filter_error_with_the_categories()
    {
        using var fx = OpenAIFx.Open("openai/azure/errors/content-filter-400.json");
        var e = await Assert.ThrowsAsync<ContentFilterException>(() => Azure(fx, KeyEndpoint()).SendAsync(new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Hi.")] }));
        fx.AssertAllUsed();
        Assert.Equal(400, e.StatusCode);
        Assert.Equal(5, e.Categories.Count);
        ContentFilterCategory violence = Assert.Single(e.Categories, c => c.Name == "violence");
        Assert.True(violence.Filtered);
        Assert.Equal("medium", violence.Severity);
        ContentFilterCategory jailbreak = Assert.Single(e.Categories, c => c.Name == "jailbreak");
        Assert.False(jailbreak.Filtered);
        Assert.False(jailbreak.Detected);
        Assert.Null(jailbreak.Severity);
        Assert.DoesNotContain(e.Categories, c => c.Name != "violence" && c.Filtered);
    }

    [Fact]
    public async Task Azure_rate_limit_carries_retry_after()
    {
        using var fx = OpenAIFx.Open("openai/azure/errors/rate-limit-429.json");
        var e = await Assert.ThrowsAsync<RateLimitException>(() => Azure(fx, KeyEndpoint()).SendAsync(new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Hi.")] }));
        Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
    }

    [Fact]
    public void Azure_addresses_in_both_forms_and_a_deployment_alias_layer()
    {
        AzureOpenAIEndpoint v1 = new(new Uri("https://myres.openai.azure.com/"), new Secret("k-k-k-k-k-k"));
        Assert.Equal("https://myres.openai.azure.com/openai/v1/responses", v1.GetUri("responses", "dep").AbsoluteUri);
        Assert.Equal("https://myres.openai.azure.com/openai/v1/files", v1.GetUri("files", null).AbsoluteUri);

        AzureOpenAIEndpoint legacy = new(new Uri("https://myres.services.ai.azure.com/some/path"), new Secret("k-k-k-k-k-k"), new AzureOpenAIOptions { ApiVersion = "2024-10-21" });
        Assert.Equal("https://myres.services.ai.azure.com/openai/deployments/dep/chat/completions?api-version=2024-10-21", legacy.GetUri("chat/completions", "dep").AbsoluteUri);
        Assert.Equal("https://myres.services.ai.azure.com/openai/responses?api-version=2024-10-21", legacy.GetUri("responses", "dep").AbsoluteUri);
        Assert.Equal("https://myres.services.ai.azure.com/openai/files?api-version=2024-10-21", legacy.GetUri("files", null).AbsoluteUri);
        Assert.Equal("https://myres.services.ai.azure.com/openai/batches/b1?x=1&api-version=2024-10-21", legacy.GetUri("batches/b1?x=1", null).AbsoluteUri);

        var layer = AzureOpenAIEndpoint.CatalogAliases(new Dictionary<string, string> { ["prod-gpt"] = "gpt-4.1" });
        var catalog = OneLlm.Catalog.ModelCatalog.Create([OpenAICatalog.Shipped(), layer]);
        Assert.True(catalog.TryResolve("prod-gpt", out OneLlm.Catalog.ResolvedModel? resolved));
        Assert.Equal("gpt-4.1", resolved!.Name);
    }

    [Fact]
    public void An_azure_address_must_be_https_unless_it_is_this_machine()
    {
        Assert.Throws<ArgumentException>(() => new AzureOpenAIEndpoint(new Uri("http://myres.openai.azure.com"), new Secret("k-k-k-k-k-k")));
        Assert.Throws<ArgumentException>(() => new AzureOpenAIEndpoint(Local, new Secret("")));
    }
}
