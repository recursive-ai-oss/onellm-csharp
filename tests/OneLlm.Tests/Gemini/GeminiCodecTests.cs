using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.Tests.Gemini;

/// <summary>Unit tests for the Gemini codecs that fixtures do not cover: the event reader's edge cases, stream and whole-response equality, request rules, the catalog, retries.</summary>
public class GeminiCodecTests
{
    // ---- server-sent events ----

    private sealed class ChunkStream(byte[][] chunks) : Stream
    {
        private int _chunk;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_chunk < chunks.Length && _offset >= chunks[_chunk].Length)
            {
                _chunk++;
                _offset = 0;
            }

            if (_chunk >= chunks.Length)
            {
                return 0;
            }

            int n = Math.Min(count, chunks[_chunk].Length - _offset);
            Array.Copy(chunks[_chunk], _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadEvents(byte[] bytes, int chunkSize)
    {
        byte[][] chunks = [.. bytes.Chunk(chunkSize)];
        using var reader = new GeminiSseReader(new ChunkStream(chunks), bufferSize: 16);
        var events = new List<string>();
        while (await reader.ReadAsync(default))
        {
            events.Add(Encoding.UTF8.GetString(reader.Data.Span));
        }

        return events;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public async Task Sse_events_survive_any_split(int chunkSize)
    {
        // CRLF, CR and LF line ends, a comment, a multi-line event, a field the reader ignores, and an event with only an event name.
        string text = "data: {\"a\":1}\r\n\r\n: ping\n\ndata: {\"b\":\n"
            + "data: 2}\n\nevent: x\nid: 7\ndata: three\r\r"
            + "event: lonely\n\n"
            + "data:no-space\n\n";
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes(text), chunkSize);
        Assert.Equal(["{\"a\":1}", "{\"b\":\n2}", "three", "no-space"], events);
    }

    [Fact]
    public async Task Sse_drops_a_byte_order_mark_and_a_truncated_final_event()
    {
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("data: one\n\ndata: cut off")];
        List<string> events = await ReadEvents(bytes, 5);
        Assert.Equal(["one"], events);
    }

    [Fact]
    public async Task Sse_reads_a_long_event_across_many_reads()
    {
        string big = new('x', 100_000);
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes($"data: {big}\n\ndata: end\n\n"), 4096);
        Assert.Equal([big, "end"], events);
    }

    [Fact]
    public async Task Sse_reads_utf8_split_inside_a_character()
    {
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes("data: caf\u00e9 \u65e5\u672c\n\n"), 1);
        Assert.Equal(["caf\u00e9 \u65e5\u672c"], events);
    }

    // ---- providers over an in-memory service ----

    private sealed class FuncHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _count;

        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(request, Interlocked.Increment(ref _count));
    }

    private static HttpResponseMessage Json(int status, string body, string? retryAfter = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null)
        {
            r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return r;
    }

    private static HttpResponseMessage Sse(params string[] chunks) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream([.. chunks.Select(c => Encoding.UTF8.GetBytes(c))])) { Headers = { { "Content-Type", "text/event-stream" } } } };

    private static ChatRequest Hi() => new() { Model = "gemini-3.5-flash-lite", Messages = [Message.FromUser("Hello.")] };

    [Fact]
    public async Task Streaming_and_whole_responses_build_equal_messages()
    {
        string parts1 = """{"text":"Thinking about it.","thought":true}""";
        string parts2 = """{"text":"The weather","thoughtSignature":"c2ln"},{"text":" is fine."}""";
        string parts3 = """{"functionCall":{"id":"c1","name":"get_weather","args":{"city":"Oslo"}},"thoughtSignature":"c2ln2"}""";
        string usage = """{"promptTokenCount":30,"cachedContentTokenCount":10,"candidatesTokenCount":8,"thoughtsTokenCount":5,"totalTokenCount":43}""";
        string whole = GeminiCases.Answer(parts1 + "," + parts2 + "," + parts3, usage: usage, id: "r-1");
        string[] chunks =
        [
            GeminiCases.Chunk(parts1, id: "r-1"),
            GeminiCases.Chunk(parts2, id: "r-1"),
            GeminiCases.Chunk(parts3, finish: "STOP", usage: usage, id: "r-1"),
        ];

        var request = new ChatRequest { Model = "gemini-3.5-flash-lite", Messages = [Message.FromUser("Weather?")], Tools = [GeminiCases.Weather()] };
        using var http1 = new HttpClient(new FuncHandler((_, _) => Task.FromResult(Json(200, whole))));
        ChatResponse a = await GeminiHarness.NewProvider(http1).SendAsync(request);

        using var http2 = new HttpClient(new FuncHandler((_, _) => Task.FromResult(Sse(chunks))));
        var acc = new MessageAccumulator();
        await GeminiCases.Collect(GeminiHarness.NewProvider(http2), request, acc);
        Message b = acc.ToMessage();

        Assert.Equal(GeminiCases.Describe(a.Message), GeminiCases.Describe(b));
        Assert.Equal(a.Finish, acc.Finish);
        Assert.Equal(a.Message.Usage!.InputTokens, b.Usage!.InputTokens);
        Assert.Equal(a.Message.Usage.CacheReadInputTokens, b.Usage.CacheReadInputTokens);
        Assert.Equal(a.Message.Usage.OutputTokens, b.Usage.OutputTokens);
        Assert.Equal(20, b.Usage.InputTokens);
        Assert.Equal(10, b.Usage.CacheReadInputTokens);
        Assert.Equal(13, b.Usage.OutputTokens);
    }

    [Fact]
    public async Task Unknown_response_fields_are_kept_on_the_message()
    {
        string body = """{"candidates":[{"content":{"role":"model","parts":[{"text":"x"}]},"finishReason":"STOP","index":0,"brandNew":[1,2]}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2,"newCounter":9},"top":{"deep":true}}""";
        using var http = new HttpClient(new FuncHandler((_, _) => Task.FromResult(Json(200, body))));
        ChatResponse r = await GeminiHarness.NewProvider(http).SendAsync(Hi());
        var gemini = (Dictionary<string, object?>)r.Message.ExtensionData!["gemini"]!;
        var unknown = (Dictionary<string, object?>)gemini["unknown"]!;
        Assert.True(unknown.ContainsKey("top"));
        // The message survives a JSON round trip with the extension data in place.
        string json = System.Text.Json.JsonSerializer.Serialize(r.Message, OneLlmJsonContext.Default.Message);
        Message back = System.Text.Json.JsonSerializer.Deserialize(json, OneLlmJsonContext.Default.Message)!;
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(back, OneLlmJsonContext.Default.Message));
    }

    [Fact]
    public async Task A_request_carries_the_key_in_a_header_and_never_in_the_address()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new FuncHandler((req, _) =>
        {
            seen = req;
            return Task.FromResult(Json(200, GeminiCases.Answer("""{"text":"x"}""")));
        }));
        await GeminiHarness.NewProvider(http).SendAsync(Hi());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent", seen!.RequestUri!.AbsoluteUri);
        Assert.True(seen.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("key=", seen.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_is_scrubbed_from_error_messages()
    {
        using var http = new HttpClient(new FuncHandler((_, _) => Task.FromResult(Json(500, "boom " + GeminiHarness.FakeKey + " boom"))));
        var e = await Assert.ThrowsAnyAsync<ProviderException>(() => GeminiHarness.NewProvider(http).SendAsync(Hi()));
        Assert.DoesNotContain(GeminiHarness.FakeKey, e.Message, StringComparison.Ordinal);
    }

    // ---- request rules ----

    private static async Task<(JsonNode Body, List<string> Warnings)> SendCapture(ChatRequest request, ModelCatalog? catalog = null)
    {
        byte[]? body = null;
        using var http = new HttpClient(new FuncHandler(async (req, _) =>
        {
            body = await req.Content!.ReadAsByteArrayAsync();
            return Json(200, GeminiCases.Answer("""{"text":"x"}""", model: request.Model));
        }));
        var dropped = new List<string>();
        ChatResponse r = await GeminiHarness.NewProvider(http, dropped: dropped.Add, catalog: catalog).SendAsync(request);
        Assert.Equal(dropped, r.Warnings.Select(w => w.Message));
        return (JsonNode.Parse(body!)!, dropped);
    }

    [Fact]
    public async Task Parameters_a_model_ignores_are_stripped_and_reported()
    {
        // The catalog says gemini-3.6-flash ignores sampling parameters.
        var (body, warnings) = await SendCapture(new ChatRequest { Model = "gemini-3.6-flash", Messages = [Message.FromUser("Hi.")], Tools = [GeminiCases.Weather()], Temperature = 0.5, TopP = 0.9, ParallelToolCalls = false, UserId = "u-1" });
        Assert.Null(body["generationConfig"]?["temperature"]);
        Assert.Null(body["generationConfig"]?["topP"]);
        Assert.Contains(warnings, w => w.Contains("temperature", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("parallelToolCalls", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("user id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sampling_parameters_are_sent_to_models_that_take_them()
    {
        var (body, warnings) = await SendCapture(new ChatRequest { Model = "gemini-2.5-flash", Messages = [Message.FromUser("Hi.")], Temperature = 0.5, TopP = 0.9, MaxOutputTokens = 100, StopSequences = ["END"] });
        Assert.Equal(0.5, body["generationConfig"]!["temperature"]!.GetValue<double>());
        Assert.Equal(100, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal("END", body["generationConfig"]!["stopSequences"]![0]!.GetValue<string>());
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Service_tier_is_written_when_the_model_has_it_and_refused_when_not()
    {
        var (body, _) = await SendCapture(new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("Hi.")], ServiceTier = "flex" });
        Assert.Equal("flex", body["serviceTier"]!.GetValue<string>());
        // A tier the model does not have changes what was asked (price and latency), so the request is refused and nothing is sent.
        await Assert.ThrowsAsync<UnsupportedParameterException>(() => SendCapture(new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("Hi.")], ServiceTier = "platinum" }));
    }

    [Fact]
    public async Task Extra_parameters_replace_what_the_codec_writes()
    {
        var (body, _) = await SendCapture(new ChatRequest
        {
            Model = "gemini-2.5-flash",
            Messages = [Message.FromUser("Hi.")],
            ExtraParameters = (JsonObject)JsonNode.Parse("""{"safetySettings":[{"category":"HARM_CATEGORY_HARASSMENT","threshold":"BLOCK_ONLY_HIGH"}]}""")!,
        });
        Assert.Equal("BLOCK_ONLY_HIGH", body["safetySettings"]![0]!["threshold"]!.GetValue<string>());
    }

    [Fact]
    public async Task Forced_tool_choice_is_written_as_an_allowed_function()
    {
        var (body, _) = await SendCapture(new ChatRequest { Model = "gemini-2.5-flash", Messages = [Message.FromUser("Hi.")], Tools = [GeminiCases.Weather()], ToolChoice = ToolChoice.Tool("get_weather") });
        JsonNode cfg = body["toolConfig"]!["functionCallingConfig"]!;
        Assert.Equal("ANY", cfg["mode"]!.GetValue<string>());
        Assert.Equal("get_weather", cfg["allowedFunctionNames"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Forced_tool_choice_with_no_such_tool_is_refused()
    {
        using var http = new HttpClient(new FuncHandler((_, _) => throw new InvalidOperationException("must not be sent")));
        await Assert.ThrowsAnyAsync<Exception>(() => GeminiHarness.NewProvider(http).SendAsync(new ChatRequest { Model = "gemini-2.5-flash", Messages = [Message.FromUser("Hi.")], ToolChoice = ToolChoice.Tool("missing") }));
    }

    [Fact]
    public async Task Signatures_come_back_exactly_on_the_parts_they_were_on()
    {
        JsonNode? seenBody = null;
        int n = 0;
        using var http = new HttpClient(new FuncHandler(async (req, _) =>
        {
            n++;
            if (n == 2)
            {
                seenBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync());
            }

            return Json(200, n == 1
                ? GeminiCases.Answer("""{"text":"Plan","thought":true},{"functionCall":{"id":"c1","name":"get_weather","args":{"city":"Oslo"}},"thoughtSignature":"U0lHLUE="}""", model: "gemini-3.8-flash")
                : GeminiCases.Answer("""{"text":"done"}""", model: "gemini-3.8-flash"));
        }));
        GeminiProvider p = GeminiHarness.NewProvider(http);
        var first = await p.SendAsync(new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("W?")], Tools = [GeminiCases.Weather()] });
        Message turn = GeminiCases.AssistantWithResults(first.Message, ("c1", [new TextPart("sunny")]));
        await p.SendAsync(new ChatRequest { Model = "gemini-3.8-flash", Messages = [Message.FromUser("W?"), turn], Tools = [GeminiCases.Weather()] });

        JsonArray contents = seenBody!["contents"]!.AsArray();
        JsonNode model = contents[1]!;
        Assert.Equal("model", model["role"]!.GetValue<string>());
        JsonNode call = model["parts"]!.AsArray().Single(x => x!["functionCall"] is not null)!;
        Assert.Equal("U0lHLUE=", call["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("user", contents[2]!["role"]!.GetValue<string>());
        Assert.Equal("get_weather", contents[2]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
    }

    // ---- uploads ----

    [Fact]
    public async Task Upload_follows_the_resumable_protocol_and_returns_a_file_reference()
    {
        ReplayHandler handler = ReplayHandler.FromFile(Path.Combine(GeminiHarness.FixtureDir, "upload", "resumable.json"));
        using var http = new HttpClient(handler);
        FileRefPart file = await GeminiHarness.NewProvider(http).UploadFileAsync(new byte[] { 1, 2, 3 }, "application/pdf", "doc");
        handler.AssertAllUsed();
        Assert.Equal("files/abc-123", file.FileId);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("resumable", handler.Requests[0].Headers["X-Goog-Upload-Protocol"]);
        Assert.Equal("start", handler.Requests[0].Headers["X-Goog-Upload-Command"]);
        Assert.Equal("3", handler.Requests[0].Headers["X-Goog-Upload-Header-Content-Length"]);
        Assert.Equal("application/pdf", handler.Requests[0].Headers["X-Goog-Upload-Header-Content-Type"]);
        Assert.Equal("upload, finalize", handler.Requests[1].Headers["X-Goog-Upload-Command"]);
        Assert.Equal("0", handler.Requests[1].Headers["X-Goog-Upload-Offset"]);
    }

    [Fact]
    public async Task Upload_to_a_foreign_host_is_refused_before_the_key_leaves()
    {
        int sent = 0;
        using var http = new HttpClient(new FuncHandler((req, n) =>
        {
            sent++;
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            r.Headers.TryAddWithoutValidation("x-goog-upload-url", "https://evil.example.com/upload?id=1");
            return Task.FromResult(r);
        }));
        await Assert.ThrowsAsync<MalformedResponseException>(() => GeminiHarness.NewProvider(http).UploadFileAsync(new byte[] { 1 }, "text/plain"));
        Assert.Equal(1, sent);
    }

    // ---- retries ----

    [Fact]
    public async Task A_429_waits_for_the_server_given_delay_then_succeeds()
    {
        var time = new FastTimeProvider();
        using var http = new HttpClient(new FuncHandler((_, n) => Task.FromResult(n == 1
            ? Json(429, GeminiCases_Error("RESOURCE_EXHAUSTED", "Too many requests."), retryAfter: "7")
            : Json(200, GeminiCases.Answer("""{"text":"ok"}""")))));
        ChatResponse r = await GeminiHarness.NewProvider(http, time).SendAsync(Hi());
        Assert.Equal("ok", r.Message.GetAssistantText());
        Assert.Contains(TimeSpan.FromSeconds(7), time.Delays);
    }

    [Fact]
    public async Task A_daily_quota_429_is_not_retried()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json(429, GeminiCases_Error("RESOURCE_EXHAUSTED", "Quota exceeded: GenerateRequestsPerDayPerProjectPerModel"))));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<RateLimitException>(() => GeminiHarness.NewProvider(http).SendAsync(Hi()));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task A_bad_request_is_not_retried()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json(400, GeminiCases_Error("INVALID_ARGUMENT", "bad"))));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<BadRequestException>(() => GeminiHarness.NewProvider(http).SendAsync(Hi()));
        Assert.Equal(1, handler.Count);
    }

    private static string GeminiCases_Error(string status, string message) =>
        "{\"error\":{\"code\":0,\"message\":\"" + message + "\",\"status\":\"" + status + "\"}}";

    [Fact]
    public async Task Cancellation_stops_a_stream()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new FuncHandler((_, _) => Task.FromResult(Sse(GeminiCases.Chunk("""{"text":"a"}"""), GeminiCases.Chunk("""{"text":"b"}""")))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (StreamEvent e in GeminiHarness.NewProvider(http).StreamAsync(Hi(), cts.Token))
            {
                cts.Cancel();
            }
        });
    }

    // ---- the shipped catalog ----

    [Fact]
    public void Every_model_resolves_by_name_and_alias_and_every_price_has_a_date_and_source()
    {
        CatalogLayer layer = GeminiProvider.GetCatalogLayer();
        ModelCatalog catalog = ModelCatalog.Create([layer]);
        // The layer holds the chat file and the media file; the catalog merges them into one provider.
        Assert.All(layer.Providers, p => Assert.Equal("gemini", p.Name));
        ProviderSpec provider = catalog.FindProvider("gemini")!;
        Assert.NotEmpty(provider.Models!);
        foreach (ModelSpec m in provider.Models!)
        {
            Assert.True(catalog.TryResolve("gemini:" + m.Name, out ResolvedModel? byName), m.Name);
            Assert.Equal(m.Name, byName!.Model!.Name);
            Assert.True(catalog.TryResolve(m.Name, out _), m.Name);
            if (m.Kind is null or "chat")
            {
                Assert.NotNull(m.ContextWindow);
            }

            Assert.NotNull(m.Pricing);
            Assert.NotNull(m.Pricing!.AsOf);
            Assert.False(string.IsNullOrWhiteSpace(m.Pricing.Source), m.Name);
        }

        foreach ((string alias, string target) in provider.Aliases!)
        {
            Assert.True(catalog.TryResolve(alias, out ResolvedModel? viaAlias), alias);
            Assert.Equal(target, viaAlias!.Model!.Name);
        }
    }

    [Fact]
    public void The_shipped_catalog_is_registered_for_the_default_catalog()
    {
        _ = GeminiProvider.GetCatalogLayer();
        _ = new GeminiOptions { Time = TimeProvider.System, Auth = new GeminiApiKeyAuth(new Secret(GeminiHarness.FakeKey)) };
        using var http = new HttpClient();
        _ = new GeminiProvider(new GeminiOptions { Time = TimeProvider.System, Auth = new GeminiApiKeyAuth(new Secret(GeminiHarness.FakeKey)), Http = http });
        Assert.Contains(CatalogLoader.ShippedLayers(), l => l.Providers.Any(p => p.Name == "gemini"));
    }

    [Fact]
    public void A_user_layer_can_change_a_shipped_price()
    {
        var user = new CatalogLayer(CatalogLayer.User, [new ProviderSpec { Name = "gemini", Models = [new ModelSpec { Name = "gemini-2.5-flash", ContextWindow = 123_456 }] }]);
        ModelCatalog catalog = ModelCatalog.Create([GeminiProvider.GetCatalogLayer(), user]);
        Assert.Equal(123_456, catalog.Resolve("gemini:gemini-2.5-flash").Model!.ContextWindow);
        Assert.NotNull(catalog.Resolve("gemini:gemini-2.5-flash").Model!.Pricing);
    }

    // ---- fixtures ----

    [Fact]
    public void Committed_fixtures_hold_no_secrets_and_say_they_were_written_from_the_reference()
    {
        string root = GeminiHarness.FixtureDir;
        string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            RecordedFixture f = RecordedFixture.Load(file);
            // The upload start answer carries the service's address for the second step; this one is a made-up placeholder.
            string[] violations = [.. FixtureScrubber.FindViolations(f).Where(v => !v.Contains("x-goog-upload-url", StringComparison.Ordinal))];
            Assert.True(violations.Length == 0, file + ": " + string.Join("; ", violations));
            Assert.Contains("Written from the API reference", f.Description, StringComparison.Ordinal);
        }
    }
}
