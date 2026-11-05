using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using static OneLlm.Tests.Providers.AnthropicKit;

namespace OneLlm.Tests.Providers;

/// <summary>Unit tests for the Anthropic wire codecs: the SSE reader, the response decoder and the request body writer.</summary>
public class AnthropicCodecTests
{
    // ---- SSE reader ----

    /// <summary>A stream that returns at most <c>chunk</c> bytes per read, so events are cut at every possible place.</summary>
    private sealed class SlowStream(byte[] bytes, int chunk) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - _pos);
            bytes.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadEvents(byte[] bytes, int chunk)
    {
        var events = new List<string>();
        using var reader = new SseReader(new SlowStream(bytes, chunk));
        while (await reader.ReadAsync(CancellationToken.None))
        {
            events.Add(Encoding.UTF8.GetString(reader.Data.Span));
        }

        return events;
    }

    public static TheoryData<int> Chunks => new() { 1, 2, 3, 5, 7, 4096 };

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Sse_events_survive_any_split_of_the_input(int chunk)
    {
        const string Text = "event: a\ndata: {\"n\":1}\n\nevent: b\r\ndata: {\"n\":2}\r\n\r\nevent: c\rdata: {\"n\":3}\r\r: keep-alive\n\ndata: x\ndata: y\n\ndata:z\n\ndata\n\n";
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes(Text), chunk);
        // A line with only "data" is an event with empty data; the standard dispatches it and the provider skips empty data.
        Assert.Equal(["{\"n\":1}", "{\"n\":2}", "{\"n\":3}", "x\ny", "z", ""], events);
    }

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Sse_multibyte_text_is_not_split_into_garbage(int chunk)
    {
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes("data: café 😀 日本\n\n"), chunk);
        Assert.Equal(["café 😀 日本"], events);
    }

    [Fact]
    public async Task Sse_ignores_a_leading_byte_order_mark_and_non_data_fields()
    {
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "id: 7\nretry: 100\nevent: ping\ndata: {}\n\n"u8.ToArray()];
        Assert.Equal(["{}"], await ReadEvents(bytes, 2));
    }

    [Fact]
    public async Task Sse_drops_an_event_cut_off_before_its_blank_line()
    {
        Assert.Equal(["{\"a\":1}"], await ReadEvents("data: {\"a\":1}\n\ndata: {\"b\":"u8.ToArray(), 3));
        Assert.Equal(["{\"a\":1}"], await ReadEvents("data: {\"a\":1}\n\ndata: {\"b\":2}\n"u8.ToArray(), 3));
    }

    [Fact]
    public async Task Sse_passes_the_done_marker_through_as_data()
    {
        Assert.Equal(["[DONE]"], await ReadEvents("data: [DONE]\n\n"u8.ToArray(), 4));
    }

    [Fact]
    public async Task Sse_grows_its_buffers_for_a_large_event()
    {
        string big = new('x', 200_000);
        List<string> events = await ReadEvents(Encoding.UTF8.GetBytes($"data: {big}\n\ndata: tail\n\n"), 8192);
        Assert.Equal([big, "tail"], events);
    }

    // ---- decoder, through the provider with an inline stream ----

    private sealed class SseHandler(string body, int chunk) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new SlowStream(Encoding.UTF8.GetBytes(body), chunk));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    private static AnthropicProvider Inline(HttpMessageHandler handler) =>
        AnthropicProvider.Create(Key(), TimeProvider.System, new AnthropicOptions { Http = new HttpClient(handler), BaseAddress = new Uri("http://localhost/"), Retry = RetryPolicy.None });

    private static string Ev(string name, string json) => $"event: {name}\ndata: {json}\n\n";

    private const string Start = """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-sonnet-5-5","stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}""";

    private static string Stop(string reason, string usage = """{"output_tokens":7}""", string delta = "") =>
        Ev("message_delta", $$"""{"type":"message_delta","delta":{"stop_reason":"{{reason}}","stop_sequence":null{{delta}}},"usage":{{usage}}}""") + Ev("message_stop", """{"type":"message_stop"}""");

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100000)]
    public async Task Raw_multibyte_text_in_deltas_is_decoded_once_whatever_the_chunking(int chunk)
    {
        string sse = Ev("message_start", Start)
            + Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"caf\u00e9 é"}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" 😀 日本 \"q\" \\n"}}""")
            + Ev("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + Stop("end_turn");
        (ChatResponse r, _) = await StreamAsync(Inline(new SseHandler(sse, chunk)), Hi());
        Assert.Equal("café é 😀 日本 \"q\" \\n", r.Message.GetAssistantText());
    }

    [Fact]
    public async Task Tool_arguments_are_reassembled_from_fragments_that_are_not_valid_json_alone()
    {
        string sse = Ev("message_start", Start)
            + Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"calc","input":{}}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":""}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"expr\": \"1 +"}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":" 2\", \"deep\": {\"a\": [1, 2"}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":", 3]}}"}}""")
            + Ev("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + Stop("tool_use");
        (ChatResponse r, _) = await StreamAsync(Inline(new SseHandler(sse, 5)), Hi());
        ToolCall call = Assert.Single(r.Message.ToolCalls);
        Assert.Equal("1 + 2", call.Inputs!["expr"]!.GetValue<string>());
        Assert.Equal(3, call.Inputs["deep"]!["a"]![2]!.GetValue<int>());
        Assert.Null(call.InvalidInputs);
    }

    [Fact]
    public async Task A_tool_call_with_cut_off_arguments_keeps_the_raw_text()
    {
        string sse = Ev("message_start", Start)
            + Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"calc","input":{}}}""")
            + Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"expr\": \"1"}}""")
            + Ev("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + Stop("max_tokens");
        (ChatResponse r, _) = await StreamAsync(Inline(new SseHandler(sse, 4096)), Hi());
        ToolCall call = Assert.Single(r.Message.ToolCalls);
        Assert.Null(call.Inputs);
        Assert.Equal("{\"expr\": \"1", call.InvalidInputs);
        Assert.Equal(FinishReason.Length, r.Finish);
    }

    [Fact]
    public async Task Unknown_fields_in_usage_tool_use_blocks_and_the_message_survive_streaming()
    {
        string sse = Ev("message_start", Start)
            + Ev("ping", """{"type":"ping"}""")
            + Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"shot","input":{},"toolset_name":"computer","caller":{"type":"direct"}}}""")
            + Ev("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + Ev("some_future_event", """{"type":"some_future_event","x":1}""")
            + Stop("tool_use", """{"output_tokens":7,"future_counter":5,"speed":"standard"}""", ""","container":{"id":"c_1"}""");
        (ChatResponse r, _) = await StreamAsync(Inline(new SseHandler(sse, 9)), Hi());

        ToolCall call = Assert.Single(r.Message.ToolCalls);
        Assert.Equal("computer", call.ExtensionData!["toolset_name"]);
        Assert.Equal("direct", Assert.IsType<Dictionary<string, object?>>(call.ExtensionData["caller"])["type"]);
        Assert.Equal(5L, r.Usage!.ExtensionData!["future_counter"]);
        var extras = Assert.IsType<Dictionary<string, object?>>(r.Message.ExtensionData!["anthropic"]);
        Assert.Equal("c_1", Assert.IsType<Dictionary<string, object?>>(extras["container"])["id"]);
        Assert.Equal(7, r.Usage.OutputTokens);
        Assert.Equal(10, r.Usage.InputTokens);
    }

    [Fact]
    public async Task Fast_speed_and_priority_tier_are_reported_as_the_service_tier()
    {
        string sse = Ev("message_start", Start) + Stop("end_turn", """{"output_tokens":7,"speed":"fast"}""");
        (ChatResponse fast, _) = await StreamAsync(Inline(new SseHandler(sse, 100)), Hi());
        Assert.Equal("fast", fast.Usage!.ServiceTier);

        string sse2 = Ev("message_start", Start.Replace("\"output_tokens\":1", "\"output_tokens\":1,\"service_tier\":\"priority\"", StringComparison.Ordinal)) + Stop("end_turn");
        (ChatResponse priority, _) = await StreamAsync(Inline(new SseHandler(sse2, 100)), Hi());
        Assert.Equal("priority", priority.Usage!.ServiceTier);
    }

    [Fact]
    public async Task The_done_marker_ends_a_stream_cleanly_only_after_message_stop()
    {
        string sse = Ev("message_start", Start) + Stop("end_turn") + "data: [DONE]\n\n";
        (ChatResponse r, _) = await StreamAsync(Inline(new SseHandler(sse, 100)), Hi());
        Assert.Equal(FinishReason.Stop, r.Finish);
    }

    // ---- request body writer ----

    private static JsonObject Body(ChatRequest request, out List<ResponseWarning> warnings, AnthropicOptions? options = null, BodyMode mode = BodyMode.Message, ModelCatalog? catalog = null)
    {
        ResolvedModel resolved = (catalog ?? AnthropicCatalog.Default).Resolve(request.Model);
        warnings = [];
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            new MessagesBodyWriter(request, resolved.Name, resolved.Model, mode, options ?? new AnthropicOptions(), TimeProvider.System, new Dictionary<AssetRef, ReadOnlyMemory<byte>>(), warnings).Write(w);
        }

        return (JsonObject)JsonNode.Parse(buffer.WrittenSpan)!;
    }

    private static JsonObject Body(ChatRequest request) => Body(request, out _);

    private static ChatRequest WithTool(ChatRequest r) => r with { Tools = [Weather()] };

    [Fact]
    public void Tool_choice_modes_map_to_the_api_shapes()
    {
        Assert.Equal("""{"type":"none"}""", Body(WithTool(Hi()) with { ToolChoice = ToolChoice.None })["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"any"}""", Body(WithTool(Hi()) with { ToolChoice = ToolChoice.Required })["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"tool","name":"get_weather"}""", Body(WithTool(Hi()) with { ToolChoice = ToolChoice.Tool("get_weather") })["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"tool","name":"get_weather","disable_parallel_tool_use":true}""", Body(WithTool(Hi()) with { ToolChoice = ToolChoice.Tool("get_weather"), ParallelToolCalls = false })["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"auto","disable_parallel_tool_use":true}""", Body(WithTool(Hi()) with { ParallelToolCalls = false })["tool_choice"]!.ToJsonString());
        Assert.Equal("""{"type":"none"}""", Body(WithTool(Hi()) with { ToolChoice = ToolChoice.None, ParallelToolCalls = false })["tool_choice"]!.ToJsonString());
        Assert.False(Body(WithTool(Hi()) with { ToolChoice = ToolChoice.Auto }).ContainsKey("tool_choice"));
        Assert.False(Body(WithTool(Hi())).ContainsKey("tool_choice"));
    }

    [Fact]
    public void Sampling_parameters_are_dropped_from_the_current_api_version_and_say_so()
    {
        JsonObject body = Body(Hi() with { Temperature = 0.2, TopP = 0.9 }, out List<ResponseWarning> warnings);
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("top_p"));
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.DoesNotContain("Say hello", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Sampling_parameters_are_sent_on_the_old_version_when_the_model_takes_them()
    {
        var old = new AnthropicOptions { ApiVersion = "2023-06-01" };
        JsonObject kept = Body(Hi() with { Model = "claude-sonnet-4-5", Temperature = 0.25, TopP = 0.5 }, out List<ResponseWarning> none, old);
        Assert.Equal(0.25, kept["temperature"]!.GetValue<double>());
        Assert.Equal(0.5, kept["top_p"]!.GetValue<double>());
        Assert.Empty(none);

        // A model that does not take sampling at all still drops it, whatever the version.
        JsonObject dropped = Body(Hi() with { Temperature = 0.25 }, out List<ResponseWarning> warnings, old);
        Assert.False(dropped.ContainsKey("temperature"));
        Assert.Single(warnings);
    }

    [Fact]
    public void Extra_parameters_replace_the_codecs_own_member_and_are_written_once()
    {
        JsonObject body = Body(Hi() with { ExtraParameters = Obj("""{"max_tokens":77,"top_k":5,"metadata":{"user_id":"u"}}""") });
        Assert.Equal(77, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(5, body["top_k"]!.GetValue<int>());
        Assert.Equal(1, body.Count(p => p.Key == "max_tokens"));
        Assert.Equal("model", body.First().Key);
    }

    [Theory]
    [InlineData("auto", "service_tier", "auto")]
    [InlineData("priority", "service_tier", "auto")]
    [InlineData("standard", "service_tier", "standard_only")]
    [InlineData("fast", "speed", "fast")]
    public void Service_tier_maps_onto_the_two_api_members(string tier, string member, string value)
    {
        JsonObject body = Body(Hi() with { ServiceTier = tier });
        Assert.Equal(value, body[member]!.GetValue<string>());
    }

    [Fact]
    public void An_unknown_service_tier_is_dropped_with_a_warning()
    {
        JsonObject body = Body(Hi() with { ServiceTier = "flex" }, out List<ResponseWarning> warnings);
        Assert.False(body.ContainsKey("service_tier"));
        Assert.Single(warnings);
    }

    [Fact]
    public void Adaptive_models_get_adaptive_thinking_with_effort_in_output_config()
    {
        JsonObject body = Body(Hi() with { Reasoning = new ReasoningOptions { Effort = "high", Summary = "none" } });
        Assert.Equal("""{"type":"adaptive","display":"omitted"}""", body["thinking"]!.ToJsonString());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void An_adaptive_model_drops_a_token_budget_and_says_so()
    {
        JsonObject body = Body(Hi() with { Reasoning = new ReasoningOptions { BudgetTokens = 4000 } }, out List<ResponseWarning> warnings);
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Single(warnings);
    }

    [Fact]
    public void Budget_models_get_enabled_thinking_with_the_budget_clamped_into_range()
    {
        ChatRequest r = new() { Model = "claude-sonnet-4-5", Messages = [Message.FromUser("hi")], MaxOutputTokens = 4096 };
        Assert.Equal(2048, Body(r with { Reasoning = new ReasoningOptions { BudgetTokens = 2048 } })["thinking"]!["budget_tokens"]!.GetValue<int>());

        JsonObject low = Body(r with { Reasoning = new ReasoningOptions { BudgetTokens = 100 } }, out List<ResponseWarning> w1);
        Assert.Equal(1024, low["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Single(w1);

        // The budget must stay below max_tokens.
        JsonObject high = Body(r with { Reasoning = new ReasoningOptions { BudgetTokens = 9000 }, MaxOutputTokens = 3000 }, out List<ResponseWarning> w2);
        Assert.Equal(2999, high["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Single(w2);

        // No room for the smallest budget: thinking is dropped, not sent invalid.
        JsonObject none = Body(r with { Reasoning = new ReasoningOptions { BudgetTokens = 2048 }, MaxOutputTokens = 1000 }, out List<ResponseWarning> w3);
        Assert.False(none.ContainsKey("thinking"));
        Assert.Single(w3);
    }

    [Fact]
    public void An_effort_on_a_budget_model_picks_the_catalog_budget_and_drops_the_effort_member()
    {
        ChatRequest r = new() { Model = "claude-sonnet-4-5", Messages = [Message.FromUser("hi")], MaxOutputTokens = 32000, Reasoning = new ReasoningOptions { Effort = "medium" } };
        JsonObject body = Body(r, out List<ResponseWarning> warnings);
        Assert.Equal(8192, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("output_config"));
        Assert.Single(warnings);
    }

    [Fact]
    public void Reasoning_settings_on_a_model_that_does_not_think_are_dropped()
    {
        var user = CatalogLayer.FromYaml(CatalogLayer.User, """
            name: anthropic
            models:
              - name: claude-plain-1
                contextWindow: 200000
                maxOutputTokens: 8000
            """);
        ModelCatalog catalog = ModelCatalog.Create([AnthropicCatalog.Shipped, user]);
        JsonObject body = Body(Hi() with { Model = "claude-plain-1", Reasoning = new ReasoningOptions { Effort = "high" } }, out List<ResponseWarning> warnings, catalog: catalog);
        Assert.False(body.ContainsKey("thinking"));
        Assert.False(body.ContainsKey("output_config"));
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Meaning_changing_requests_are_refused_before_anything_is_sent()
    {
        Assert.Throws<BadRequestException>(() => Body(Hi() with { ResponseFormat = ResponseFormat.AnyJson }));
        var manual = new ChatRequest
        {
            Model = "claude-sonnet-4-5",
            Messages = [Message.FromUser("hi")],
            Tools = [Weather()],
            ToolChoice = ToolChoice.Required,
            Reasoning = new ReasoningOptions { BudgetTokens = 2048 },
            MaxOutputTokens = 4096,
        };
        Assert.Throws<BadRequestException>(() => Body(manual));
        Assert.Equal("any", Body(manual with { Reasoning = null })["tool_choice"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void At_most_four_cache_breakpoints_are_sent_and_the_latest_are_kept()
    {
        Message[] messages = [.. Enumerable.Range(0, 6).Select(i => new Message { User = [new TextPart("part " + i)], Cache = true })];
        // Alternate roles so the messages are not merged into one block run.
        Message[] mixed = [.. messages.SelectMany((m, i) => new[] { m, Message.FromAssistant("reply " + i) })];
        JsonObject body = Body(new ChatRequest { Model = Model, Messages = mixed, MaxOutputTokens = 100 }, out List<ResponseWarning> warnings);
        string json = body.ToJsonString();
        Assert.Equal(4, json.Split("cache_control", StringSplitOptions.None).Length - 1);
        Assert.Single(warnings);
        Assert.DoesNotContain("part 0\",\"cache_control", json, StringComparison.Ordinal);
        Assert.Contains("part 5\",\"cache_control", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_marks_are_dropped_when_caching_is_switched_off()
    {
        JsonObject body = Body(new ChatRequest { Model = Model, Messages = [new Message { User = [new TextPart("x")], Cache = true }], Cache = CachePolicy.Off, MaxOutputTokens = 10 }, out List<ResponseWarning> warnings);
        Assert.DoesNotContain("cache_control", body.ToJsonString(), StringComparison.Ordinal);
        Assert.Single(warnings);
    }

    [Fact]
    public void Adjacent_messages_of_the_same_role_are_merged_and_tool_results_come_first()
    {
        Message call = new()
        {
            Assistant = [new TextPart("calling")],
            ToolCalls = [new ToolCall("toolu_1", "get_weather") { Inputs = Obj("""{"location":"Paris"}"""), After = 1, Result = [new TextPart("sunny")] }],
        };
        JsonObject body = Body(new ChatRequest
        {
            Model = Model,
            Messages = [Message.FromUser("one"), Message.FromUser("two"), call, Message.FromUser("three")],
            MaxOutputTokens = 100,
        });
        JsonArray messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(2, messages[0]!["content"]!.AsArray().Count);
        JsonArray last = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", last[0]!["type"]!.GetValue<string>());
        Assert.Equal("text", last[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Count_and_batch_bodies_leave_out_what_those_endpoints_do_not_take()
    {
        ChatRequest r = Hi() with { StopSequences = ["END"], Temperature = 1 };
        JsonObject count = Body(r, out _, mode: BodyMode.Count);
        Assert.False(count.ContainsKey("max_tokens"));
        Assert.False(count.ContainsKey("stop_sequences"));
        Assert.False(count.ContainsKey("stream"));
        JsonObject stream = Body(r, out _, mode: BodyMode.Stream);
        Assert.True(stream["stream"]!.GetValue<bool>());
        Assert.Equal("stream", stream.Last().Key);
        Assert.False(Body(r, out _, mode: BodyMode.BatchItem).ContainsKey("stream"));
    }

    [Fact]
    public void An_unsupported_file_type_is_refused()
    {
        var message = new Message { User = [new AssetPart(new AssetRef { ContentType = "audio/wav", Data = "AAAA" })] };
        Assert.Throws<BadRequestException>(() => Body(new ChatRequest { Model = Model, Messages = [message], MaxOutputTokens = 10 }));
    }

    [Fact]
    public void An_unresolved_asset_without_a_resolver_fails_the_request_and_names_the_asset()
    {
        var message = new Message { User = [new AssetPart(new AssetRef { ContentType = "image/png", Storage = "store", Key = "images/a.png" })] };
        var provider = Inline(new SseHandler("", 1));
        BadRequestException e = Assert.ThrowsAsync<BadRequestException>(() => provider.SendAsync(new ChatRequest { Model = Model, Messages = [message], MaxOutputTokens = 10 })).GetAwaiter().GetResult();
        Assert.Contains("images/a.png", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_asset_from_a_store_is_fetched_through_the_resolver_and_sent_as_base64()
    {
        byte[] png = [1, 2, 3, 4];
        var message = new Message { User = [new AssetPart(new AssetRef { ContentType = "image/png", Storage = "store", Key = "images/a.png" })] };
        var handler = new CaptureHandler();
        var provider = AnthropicProvider.Create(Key(), TimeProvider.System, new AnthropicOptions
        {
            Http = new HttpClient(handler),
            BaseAddress = new Uri("http://localhost/"),
            Retry = RetryPolicy.None,
            AssetResolver = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(png),
        });
        await provider.SendAsync(new ChatRequest { Model = Model, Messages = [message], MaxOutputTokens = 10 });
        JsonObject source = (JsonObject)Obj(handler.Body!)["messages"]![0]!["content"]![0]!["source"]!;
        Assert.Equal("base64", source["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(png), source["data"]!.GetValue<string>());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"m","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void Asset_routes_prefer_a_resized_copy_then_inline_data_then_a_live_public_url_then_the_store()
    {
        var time = new ManualTimeProvider();
        var original = new AssetRef { ContentType = "image/png", Data = "AAAA" };
        var resized = new ResizedImage { Asset = new AssetRef { ContentType = "image/png", Data = "BBBB" } };
        Assert.Equal("BBBB", MessagesBodyWriter.Route(new AssetPart(original) { ResizedImage = resized }, time).Target.Data);

        var stored = new AssetRef { ContentType = "image/png", Key = "k" };
        Assert.Equal(AssetRoute.Inline, MessagesBodyWriter.Route(new AssetPart(original), time).Route);
        Assert.Equal(AssetRoute.Url, MessagesBodyWriter.Route(new AssetPart(stored) { PublicUrl = "https://example.com/a.png" }, time).Route);
        Assert.Equal(AssetRoute.Url, MessagesBodyWriter.Route(new AssetPart(stored) { PublicUrl = "https://example.com/a.png", PublicUrlExpires = time.GetUtcNow().AddMinutes(1) }, time).Route);
        // An expired public URL is not sent; the bytes come from the store instead.
        Assert.Equal(AssetRoute.Resolve, MessagesBodyWriter.Route(new AssetPart(stored) { PublicUrl = "https://example.com/a.png", PublicUrlExpires = time.GetUtcNow().AddMinutes(-1) }, time).Route);
        Assert.Throws<BadRequestException>(() => MessagesBodyWriter.Route(new AssetPart(null), time));
    }

    [Fact]
    public void Citations_round_trip_through_a_text_part_with_every_field()
    {
        const string Block = """{"type":"text","text":"The sky is blue","citations":[{"type":"char_location","cited_text":"The sky is blue.","document_index":1,"document_title":"Notes","start_char_index":20,"end_char_index":36,"future_field":{"a":[1,2]}}]}""";
        var decoded = new Dictionary<string, object?>
        {
            ["document_index"] = 1L,
            ["document_title"] = "Notes",
            ["start_char_index"] = 20L,
            ["end_char_index"] = 36L,
            ["future_field"] = new Dictionary<string, object?> { ["a"] = new List<object?> { 1L, 2L } },
        };
        var text = new TextPart("The sky is blue") { Annotations = [new Annotation { Type = "char_location", CitedText = "The sky is blue.", ExtensionData = decoded }] };
        JsonObject body = Body(new ChatRequest { Model = Model, Messages = [Message.FromUser("q"), new Message { Assistant = [text] }, Message.FromUser("again")], MaxOutputTokens = 10 });
        Assert.Equal(Block, body["messages"]![1]!["content"]![0]!.ToJsonString());
    }

    [Fact]
    public void Thinking_is_replayed_with_every_field_it_came_with_and_in_the_original_order()
    {
        JsonObject data = Obj("""{"type":"thinking","signature":"sig==","future":true}""");
        var reasoning = new ReasoningPart("anthropic") { Display = "hmm", Data = data };
        JsonObject body = Body(new ChatRequest { Model = Model, Messages = [Message.FromUser("q"), new Message { Assistant = [reasoning, new TextPart("a")] }, Message.FromUser("again")], MaxOutputTokens = 10 });
        Assert.Equal("""{"type":"thinking","thinking":"hmm","signature":"sig==","future":true}""", body["messages"]![1]!["content"]![0]!.ToJsonString());
    }

    [Fact]
    public void Another_providers_reasoning_is_not_replayed_to_anthropic()
    {
        var foreign = new ReasoningPart("openai") { Display = "x", Data = Obj("""{"id":"rs_1"}""") };
        JsonObject body = Body(new ChatRequest { Model = Model, Messages = [Message.FromUser("q"), new Message { Assistant = [foreign, new TextPart("a")] }, Message.FromUser("again")], MaxOutputTokens = 10 });
        Assert.Single(body["messages"]![1]!["content"]!.AsArray());
    }

    [Fact]
    public void The_request_body_is_deterministic()
    {
        ChatRequest r = WithTool(Hi()) with { System = "s", Reasoning = new ReasoningOptions { Effort = "high" }, ToolChoice = ToolChoice.Required };
        Assert.Equal(Body(r).ToJsonString(), Body(r).ToJsonString());
    }

    [Fact]
    public void Max_tokens_defaults_to_the_models_limit_capped_and_to_a_floor_for_unknown_models()
    {
        Assert.Equal(64000, Body(new ChatRequest { Model = Model, Messages = [Message.FromUser("q")] })["max_tokens"]!.GetValue<int>());
        Assert.Equal(4096, Body(new ChatRequest { Model = "claude-not-in-the-catalog", Messages = [Message.FromUser("q")] })["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Hosted_tool_names_are_the_type_without_its_date()
    {
        Assert.Equal("web_search", MessagesBodyWriter.HostedToolName("web_search_20260209"));
        Assert.Equal("code_execution", MessagesBodyWriter.HostedToolName("code_execution_20260521"));
        Assert.Equal("str_replace_editor", MessagesBodyWriter.HostedToolName("text_editor_20250124"));
        Assert.Equal("str_replace_based_edit_tool", MessagesBodyWriter.HostedToolName("text_editor_20250728"));
        Assert.Null(MessagesBodyWriter.HostedToolName("browser_toolset_20260101"));
    }
}
