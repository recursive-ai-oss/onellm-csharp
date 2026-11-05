using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAIResponsesTests
{
    private const string Model = "gpt-6.1-terra";
    private const string Dir = "openai/direct/responses/";

    private static ChatRequest Hello(string model = Model) => new()
    {
        Model = model,
        System = "You are terse.",
        Messages = [Message.FromUser("Say hello.")],
        MaxOutputTokens = 64,
    };

    private static ToolDefinition Weather() => new("get_weather", "Gets the weather for a city.", OpenAIFx.Obj("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}"""));

    private static ToolDefinition Time() => new("get_time", "Gets the time in a zone.", OpenAIFx.Obj("""{"type":"object","properties":{"zone":{"type":"string"}},"required":["zone"],"additionalProperties":false}""")) { Strict = true };

    [Fact]
    public async Task Chat_gives_text_usage_finish_and_ids()
    {
        using var fx = OpenAIFx.Open(Dir + "chat.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(Hello());
        Assert.Equal("Hello! How can I help?", r.Message.GetAssistantText());
        Assert.Equal(FinishReason.Stop, r.Finish);
        Assert.Equal("completed", r.FinishReasonRaw);
        Assert.Equal("resp_chat_1", r.Id);
        Assert.Equal(24, r.Usage!.InputTokens);
        Assert.Equal(9, r.Usage.OutputTokens);
        Assert.Equal("default", r.Usage.ServiceTier);
        Assert.Equal(Model, r.Usage.Model);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_stream_builds_the_message_the_whole_call_builds()
    {
        using var whole = OpenAIFx.Open(Dir + "chat.json");
        using var streamed = OpenAIFx.Open(Dir + "stream.json");
        ChatResponse a = await OpenAIFx.OpenAI(whole).SendAsync(Hello());
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed), Hello());
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
        Assert.Equal(a.Finish, b.Finish);
        Assert.Equal(a.Id, b.Id);
        streamed.AssertAllUsed();
    }

    [Fact]
    public async Task Parallel_function_calls_come_back_in_call_order_and_their_results_go_out_on_the_next_turn()
    {
        using var fx = OpenAIFx.Open(Dir + "function-tools.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        Message ask = Message.FromUser("Weather and time in Paris?");
        ChatRequest first = new() { Model = Model, Messages = [ask], Tools = [Weather(), Time()] };
        ChatResponse r = await p.SendAsync(first);

        Assert.Equal(FinishReason.ToolCalls, r.Finish);
        Assert.Equal(["call_a1", "call_b2"], r.Message.ToolCalls.Select(c => c.Id));
        Assert.Equal(["get_weather", "get_time"], r.Message.ToolCalls.Select(c => c.Tool));
        Assert.Equal("Paris", r.Message.ToolCalls[0].Inputs!["city"]!.GetValue<string>());
        Assert.Equal("Let me check.", r.Message.GetAssistantText());
        Assert.All(r.Message.ToolCalls, c => Assert.Equal(1, c.After));

        ToolCall[] done =
        [
            new(r.Message.ToolCalls[0].Id, "get_weather") { Inputs = r.Message.ToolCalls[0].Inputs, After = 1, Result = [new TextPart("Sunny, 21C")] },
            new(r.Message.ToolCalls[1].Id, "get_time") { Inputs = r.Message.ToolCalls[1].Inputs, After = 1, Result = [new TextPart("14:05")] },
        ];
        var turn = new Message { Assistant = r.Message.Assistant, ToolCalls = done };
        ChatResponse second = await p.SendAsync(new ChatRequest { Model = Model, Messages = [ask, turn], Tools = [Weather(), Time()] });
        Assert.Equal("It is sunny and 14:05 in Paris.", second.Message.GetAssistantText());
        Assert.Equal(64, second.Usage!.CacheReadInputTokens);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Streamed_function_calls_equal_the_whole_ones()
    {
        using var whole = OpenAIFx.Open(Dir + "function-tools.json");
        using var streamed = OpenAIFx.Open(Dir + "function-tools-stream.json");
        ChatRequest req = new() { Model = Model, Messages = [Message.FromUser("Weather and time in Paris?")], Tools = [Weather(), Time()] };
        ChatResponse a = await OpenAIFx.OpenAI(whole).SendAsync(req);
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed), req);
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
    }

    [Fact]
    public async Task A_custom_tool_call_carries_its_text_and_replays_as_a_custom_call()
    {
        using var fx = OpenAIFx.Open(Dir + "custom-tool.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        HostedToolDefinition custom = new("custom") { Options = OpenAIFx.Obj("""{"name":"run_sql","description":"Runs SQL.","format":{"type":"text"}}""") };
        Message ask = Message.FromUser("Run a query.");
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = Model, Messages = [ask], HostedTools = [custom] });
        ToolCall call = Assert.Single(r.Message.ToolCalls);
        Assert.Equal("run_sql", call.Tool);
        Assert.Equal("SELECT 1;\nSELECT \"two\";", call.Inputs!["input"]!.GetValue<string>());

        var turn = new Message { Assistant = r.Message.Assistant, ToolCalls = [new ToolCall(call.Id, call.Tool) { Inputs = call.Inputs, After = call.After, Result = [new TextPart("1")] }] };
        ChatResponse next = await p.SendAsync(new ChatRequest { Model = Model, Messages = [ask, turn], HostedTools = [custom] });
        Assert.Equal("The query returned 1.", next.Message.GetAssistantText());
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Web_search_is_a_hosted_tool_call_with_citations_and_a_counter()
    {
        using var fx = OpenAIFx.Open(Dir + "hosted-web-search.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Search.")], HostedTools = [new HostedToolDefinition("web_search")] });
        var hosted = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
        Assert.Equal("web_search", hosted.Tool);
        Assert.Equal("completed", hosted.Status);
        Assert.Equal("example topic", hosted.Inputs!["action"]!["query"]!.GetValue<string>());
        Assert.Equal("web_search_call", hosted.Data!["type"]!.GetValue<string>());
        var text = Assert.IsType<TextPart>(r.Message.Assistant[1]);
        Annotation a = Assert.Single(text.Annotations!);
        Assert.Equal("url_citation", a.Type);
        Assert.Equal("https://example.com/a", a.Url);
        Assert.Equal(0, a.Start);
        Assert.Equal(17, a.End);
        Assert.Equal(1, r.Usage!.Counters!["toolCalls.web_search"]);
    }

    [Fact]
    public async Task A_web_search_stream_equals_the_whole_response()
    {
        using var whole = OpenAIFx.Open(Dir + "hosted-web-search.json");
        using var streamed = OpenAIFx.Open(Dir + "hosted-web-search-stream.json");
        ChatRequest req = new() { Model = Model, Messages = [Message.FromUser("Search.")], HostedTools = [new HostedToolDefinition("web_search")] };
        ChatResponse a = await OpenAIFx.OpenAI(whole).SendAsync(req);
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed), req);
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
    }

    [Fact]
    public async Task File_search_keeps_queries_results_and_the_file_citation()
    {
        using var fx = OpenAIFx.Open(Dir + "hosted-file-search.json");
        HostedToolDefinition tool = new("file_search") { Options = OpenAIFx.Obj("""{"vector_store_ids":["vs_synthetic"]}""") };
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("What does the policy say?")], HostedTools = [tool] });
        var hosted = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
        Assert.Equal("file_search", hosted.Tool);
        Assert.Equal("policy summary", hosted.Inputs!["queries"]![0]!.GetValue<string>());
        Assert.Equal("file_abc", hosted.Result![0]!["file_id"]!.GetValue<string>());
        Annotation a = Assert.Single(((TextPart)r.Message.Assistant[1]).Annotations!);
        Assert.Equal("file_citation", a.Type);
        Assert.Equal("file_abc", a.SourceId);
        Assert.Equal("policy.txt", a.Title);
        Assert.Equal(21, a.Start);
        Assert.Equal(1, r.Usage!.Counters!["toolCalls.file_search"]);
    }

    [Fact]
    public async Task Code_interpreter_gets_an_automatic_container_and_returns_its_outputs()
    {
        using var fx = OpenAIFx.Open(Dir + "hosted-code-interpreter.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("What is 2+2?")], HostedTools = [new HostedToolDefinition("code_interpreter")] });
        var hosted = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
        Assert.Equal("code_interpreter", hosted.Tool);
        Assert.Equal("print(2 + 2)", hosted.Inputs!["code"]!.GetValue<string>());
        Assert.Equal("4\n", hosted.Result![0]!["logs"]!.GetValue<string>());
        Assert.Equal(1, r.Usage!.Counters!["toolCalls.code_interpreter"]);
    }

    [Fact]
    public async Task Image_generation_keeps_its_item_and_reports_image_tokens()
    {
        using var fx = OpenAIFx.Open(Dir + "hosted-image-generation.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Draw a lighthouse.")], HostedTools = [new HostedToolDefinition("image_generation") { Options = OpenAIFx.Obj("""{"quality":"low"}""") }] });
        var hosted = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
        Assert.Equal("image_generation", hosted.Tool);
        Assert.Equal("aGVsbG8=", hosted.Data!["result"]!.GetValue<string>());
        Assert.Equal(50, r.Usage!.Counters!["imageGenInputTokens"]);
        Assert.Equal(272, r.Usage.Counters["imageGenOutputTokens"]);
        Assert.Equal(1, r.Usage.Counters["toolCalls.image_generation"]);
    }

    [Fact]
    public async Task Remote_mcp_lists_tools_and_calls_one()
    {
        using var fx = OpenAIFx.Open(Dir + "hosted-mcp.json");
        HostedToolDefinition mcp = new("mcp") { Options = OpenAIFx.Obj("""{"server_label":"docs","server_url":"https://mcp.example.com/sse","require_approval":"never"}""") };
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Search the docs.")], HostedTools = [mcp] });
        var list = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[0]);
        var call = Assert.IsType<HostedToolCallPart>(r.Message.Assistant[1]);
        Assert.Equal("mcp_list_tools", list.Tool);
        Assert.Equal("mcp", call.Tool);
        Assert.Equal("Limits are documented.", call.Result!.GetValue<string>());
        Assert.Equal(1, r.Usage!.Counters!["toolCalls.mcp"]);
    }

    [Fact]
    public async Task An_output_item_of_an_unknown_kind_is_kept_whole_and_sent_back_unchanged()
    {
        using var fx = OpenAIFx.Open(Dir + "unknown-item.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Hi.")] });
        var raw = Assert.IsType<ProviderPart>(r.Message.Assistant[0]);
        Assert.Equal("future_item", raw.Type);
        Assert.Equal(3, raw.Raw["payload"]!["x"]!.AsArray().Count);
        Assert.Equal(2, r.Message.Assistant.Count);
    }

    [Fact]
    public async Task Structured_output_sends_a_strict_schema_and_returns_the_json_text()
    {
        using var fx = OpenAIFx.Open(Dir + "structured-output.json");
        JsonObject schema = OpenAIFx.Obj("""{"type":"object","properties":{"city":{"type":"string"},"temp_c":{"type":"integer"}},"required":["city","temp_c"],"additionalProperties":false}""");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Weather in Paris as JSON.")], ResponseFormat = ResponseFormat.FromSchema("weather", schema) });
        Assert.Equal("{\"city\":\"Paris\",\"temp_c\":14}", r.Message.GetAssistantText());
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Reasoning_is_kept_with_its_summary_and_encrypted_content_and_replayed_exactly()
    {
        using var fx = OpenAIFx.Open(Dir + "reasoning.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        Message ask = Message.FromUser("What is the answer?");
        ChatRequest first = new() { Model = Model, Messages = [ask], Reasoning = new ReasoningOptions { Effort = "medium", Summary = "auto" } };
        ChatResponse r = await p.SendAsync(first);
        var reasoning = Assert.IsType<ReasoningPart>(r.Message.Assistant[0]);
        Assert.Equal("openai", reasoning.Provider);
        Assert.Equal("Considering the question.\n\nChoosing a short answer.", reasoning.Display);
        Assert.Equal("gAAAAA_synthetic_encrypted_content_1", reasoning.Data!["encrypted_content"]!.GetValue<string>());
        Assert.Equal(120, r.Usage!.Counters!["reasoningTokens"]);

        Message turn = new() { Assistant = r.Message.Assistant };
        ChatResponse next = await p.SendAsync(new ChatRequest { Model = Model, Messages = [ask, turn, Message.FromUser("Why?")], Reasoning = first.Reasoning });
        Assert.Equal("Because it is 6 times 7.", next.Message.GetAssistantText());
        Assert.Equal(128, next.Usage!.CacheReadInputTokens);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_reasoning_stream_equals_the_whole_response()
    {
        using var whole = OpenAIFx.Open(Dir + "reasoning.json");
        using var streamed = OpenAIFx.Open(Dir + "reasoning-stream.json");
        ChatRequest req = new() { Model = Model, Messages = [Message.FromUser("What is the answer?")], Reasoning = new ReasoningOptions { Effort = "medium", Summary = "auto" } };
        ChatResponse a = await OpenAIFx.OpenAI(whole).SendAsync(req);
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed), req);
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
    }

    [Fact]
    public async Task Cache_usage_is_split_into_uncached_read_and_written_tokens_and_the_key_and_lifetime_are_sent()
    {
        using var fx = OpenAIFx.Open(Dir + "caching.json");
        ChatRequest req = new() { Model = "gpt-6.1-sol", Messages = [Message.FromUser("Question.")], Cache = new CachePolicy { Key = "docs-v1", Ttl = TimeSpan.FromHours(12) } };
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(req);
        Usage u = r.Usage!;
        // input_tokens (2006) counts cached (1536) and written (256) tokens; OneLLM keeps the three apart.
        Assert.Equal(214, u.InputTokens);
        Assert.Equal(1536, u.CacheReadInputTokens);
        Assert.Equal(256, u.CacheCreationInputTokens);
        Assert.Equal(256, u.Counters!["cacheCreation12hInputTokens"]);
        Assert.Equal(2006, u.TotalInputTokens);
    }

    [Fact]
    public async Task Older_models_get_the_retention_switch_instead_of_cache_options()
    {
        using var fx = OpenAIFx.Open(Dir + "caching-retention.json");
        ChatRequest req = new() { Model = "gpt-5.4", Messages = [Message.FromUser("Question.")], Cache = new CachePolicy { Key = "k", Ttl = TimeSpan.FromHours(24) } };
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(req);
        Assert.Equal(1024, r.Usage!.CacheReadInputTokens);
        Assert.Equal(476, r.Usage.InputTokens);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task The_service_tier_asked_for_is_sent_and_the_one_used_is_reported()
    {
        using var fx = OpenAIFx.Open(Dir + "service-tier.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Hi.")], ServiceTier = "flex" });
        Assert.Equal("flex", r.Usage!.ServiceTier);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Counting_input_tokens_uses_the_count_endpoint_and_reports_the_provider_tier()
    {
        using var fx = OpenAIFx.Open(Dir + "count-tokens.json");
        TokenCount n = await OpenAIFx.OpenAI(fx).CountTokensAsync(new ChatRequest
        {
            Model = Model,
            System = "You are terse.",
            Messages = [Message.FromUser("Count me.")],
            Tools = [Weather()],
            MaxOutputTokens = 50,
            Temperature = 0.2,
            Cache = new CachePolicy { Key = "ignored" },
        });
        Assert.Equal(37, n.InputTokens);
        Assert.Equal(TokenCountTier.Provider, n.Tier);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_response_cut_off_by_the_output_limit_finishes_with_length()
    {
        using var fx = OpenAIFx.Open(Dir + "incomplete.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Story.")], MaxOutputTokens = 16 });
        Assert.Equal(FinishReason.Length, r.Finish);
        Assert.Equal("max_output_tokens", r.FinishReasonRaw);
        Assert.Equal("Once upon a", r.Message.GetAssistantText());
    }

    [Fact]
    public async Task A_refusal_is_a_refusal_part_and_the_finish_says_so()
    {
        using var fx = OpenAIFx.Open(Dir + "refusal.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [Message.FromUser("Do something bad.")] });
        Assert.Equal("I can't help with that.", Assert.IsType<RefusalPart>(Assert.Single(r.Message.Assistant)).Text);
        Assert.Equal(FinishReason.Refusal, r.Finish);
    }

    [Fact]
    public async Task Images_in_the_user_turn_and_in_tool_results_are_sent_as_input_images()
    {
        using var fx = OpenAIFx.Open(Dir + "vision-and-tool-output.json");
        var png = new AssetPart(new AssetRef { Storage = "memory", Key = "a", ContentType = "image/png", Data = "iVBORw0KGgo=" });
        var pdf = new AssetPart(new AssetRef { Storage = "memory", Key = "b", ContentType = "application/pdf", Name = "doc.pdf", Data = "JVBERi0=" });
        Message ask = new() { User = [new TextPart("What is this?"), png, pdf] };
        var call = new ToolCall("call_v1", "screenshot") { Inputs = [], After = 0, Result = [new TextPart("Taken."), png] };
        Message turn = new() { ToolCalls = [call] };
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(new ChatRequest { Model = Model, Messages = [ask, turn, Message.FromUser("And now?")] });
        Assert.Equal("A red square.", r.Message.GetAssistantText());
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Parameters_the_model_does_not_take_come_back_as_warnings_on_the_response()
    {
        using var fx = OpenAIFx.Open(Dir + "chat.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx).SendAsync(Hello() with { Temperature = 0.5 });
        Assert.Contains(r.Warnings, w => w.Parameter!.Contains("temperature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_stops_a_call()
    {
        using var fx = OpenAIFx.Open(Dir + "chat.json");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAIFx.OpenAI(fx).SendAsync(Hello(), cts.Token));
    }
}
