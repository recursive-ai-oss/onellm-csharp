using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using static OneLlm.Tests.Providers.AnthropicKit;

namespace OneLlm.Tests.Providers;

/// <summary>
/// Feature-by-fixture conformance for the Anthropic provider. Every test replays one hand-written fixture from
/// <c>testdata/providers/anthropic/direct</c>: the request bytes our codec sends must equal the fixture's request (the replay handler
/// names the first differing JSON path), and the parsed result is checked here.
/// </summary>
public class AnthropicConformanceTests
{
    // ---- chat ----

    [Fact]
    public async Task Chat_basic()
    {
        (AnthropicProvider p, var h) = Open("chat/basic");
        ChatResponse r = await p.SendAsync(Hi() with { System = "You are terse." });
        h.AssertAllUsed();
        Assert.Equal("Hello!", r.Message.GetAssistantText());
        Assert.Equal(FinishReason.Stop, r.Finish);
        Assert.Equal("end_turn", r.FinishReasonRaw);
        Assert.Equal("msg_01Aq9w938a90dw8q", r.Id);
        Assert.Equal("claude-sonnet-5-5", r.Model);
        Assert.Equal(14, r.Usage!.InputTokens);
        Assert.Equal(4, r.Usage.OutputTokens);
        Assert.Empty(r.Warnings);
        Assert.Equal("global", r.Usage.ExtensionData!["inference_geo"]);
    }

    [Fact]
    public async Task Settings_the_model_cannot_take_are_stripped_once_and_reported_on_the_response()
    {
        (AnthropicProvider p, var h) = Open("chat/basic");
        ChatResponse r = await p.SendAsync(Hi() with { System = "You are terse.", Temperature = 0.3, TopP = 0.9 });
        h.AssertAllUsed();
        ResponseWarning w = Assert.Single(r.Warnings);
        Assert.Equal("parameter_stripped", w.Code);
        Assert.Contains("temperature", w.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_setting_that_would_change_the_meaning_is_refused_before_anything_is_sent()
    {
        (AnthropicProvider p, var h) = Open("chat/basic");
        await Assert.ThrowsAsync<OneLlm.Catalog.UnsupportedParameterException>(() => p.SendAsync(Hi() with { ServiceTier = "no-such-tier" }));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Chat_stop_sequence_and_max_tokens()
    {
        (AnthropicProvider p, var h) = Open("chat/stop-sequence-max-tokens");
        ChatResponse first = await p.SendAsync(Hi() with { StopSequences = ["END"] });
        Assert.Equal(FinishReason.Stop, first.Finish);
        Assert.Equal("stop_sequence", first.FinishReasonRaw);
        var extras = Assert.IsType<Dictionary<string, object?>>(first.Message.ExtensionData!["anthropic"]);
        Assert.Equal("END", extras["stop_sequence"]);

        ChatResponse second = await p.SendAsync(Hi() with { MaxOutputTokens = 5 });
        Assert.Equal(FinishReason.Length, second.Finish);
        Assert.Equal("max_tokens", second.FinishReasonRaw);
        h.AssertAllUsed();
    }

    [Fact]
    public async Task Chat_context_window_exceeded_stop_is_a_length_finish()
    {
        (AnthropicProvider p, var h) = Open("chat/context-window-exceeded");
        ChatResponse r = await p.SendAsync(Hi());
        h.AssertAllUsed();
        Assert.Equal(FinishReason.Length, r.Finish);
        Assert.Equal("model_context_window_exceeded", r.FinishReasonRaw);
    }

    // ---- streaming ----

    [Fact]
    public async Task Stream_text_builds_the_message_the_whole_call_builds()
    {
        (AnthropicProvider ps, var hs) = Open("chat/stream-text");
        (ChatResponse streamed, List<StreamEventKind> kinds) = await StreamAsync(ps, Hi() with { System = "You are terse." });
        hs.AssertAllUsed();
        (AnthropicProvider pw, var hw) = Open("chat/stream-text-whole");
        ChatResponse whole = await pw.SendAsync(Hi() with { System = "You are terse." });
        hw.AssertAllUsed();

        Assert.Equal("Hello, café 😀\n\"quoted\" \\ done.", streamed.Message.GetAssistantText());
        Assert.Equal(Json(whole.Message), Json(streamed.Message));
        Assert.Equal(whole.Id, streamed.Id);
        Assert.Equal(whole.Finish, streamed.Finish);
        Assert.Equal(StreamEventKind.MessageStart, kinds[0]);
        Assert.Equal(StreamEventKind.Done, kinds[^1]);
        Assert.Contains(StreamEventKind.TextDelta, kinds);
    }

    // ---- client tools ----

    [Fact]
    public async Task Client_tools_two_parallel_calls_and_results_in_call_order()
    {
        (AnthropicProvider p, var h) = Open("tools/client-tools-roundtrip");
        Message question = Message.FromUser("What is the weather in San Francisco and in Paris?");
        ChatRequest first = new() { Model = Model, Messages = [question], Tools = [Weather()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);

        Assert.Equal(FinishReason.ToolCalls, r.Finish);
        Assert.Equal("I'll check both cities.", r.Message.GetAssistantText());
        Assert.Equal(2, r.Message.ToolCalls.Count);
        Assert.Equal("toolu_01A09q90qw90lq917835lq9", r.Message.ToolCalls[0].Id);
        Assert.Equal("San Francisco, CA", r.Message.ToolCalls[0].Inputs!["location"]!.GetValue<string>());
        Assert.Equal("Paris, France", r.Message.ToolCalls[1].Inputs!["location"]!.GetValue<string>());
        Assert.Equal(1, r.Message.ToolCalls[0].After); // after the text part

        Message withResults = WithResults(
            r.Message,
            ("toolu_01A09q90qw90lq917835lq9", [new TextPart("15 degrees")], false),
            ("toolu_01B18r01rx01mr028946mr0", [new TextPart("raining"), new TextPart("9 degrees")], true));
        ChatResponse second = await p.SendAsync(first with { Messages = [question, withResults] });
        h.AssertAllUsed();
        Assert.Equal("San Francisco is 15 degrees. The Paris lookup failed.", second.Message.GetAssistantText());
        Assert.Equal(FinishReason.Stop, second.Finish);
    }

    [Fact]
    public async Task Client_tools_stream_equals_the_whole_response()
    {
        ChatRequest request = new()
        {
            Model = Model,
            Messages = [Message.FromUser("What is the weather in San Francisco and in Paris?")],
            Tools = [Weather()],
            MaxOutputTokens = 1024,
        };
        (AnthropicProvider ps, var hs) = Open("tools/client-tools-stream");
        (ChatResponse streamed, List<StreamEventKind> kinds) = await StreamAsync(ps, request);
        hs.AssertAllUsed();
        (AnthropicProvider pw, var hw) = Open("tools/client-tools-roundtrip");
        ChatResponse whole = await pw.SendAsync(request);

        Assert.Equal(Json(whole.Message), Json(streamed.Message));
        Assert.Contains(StreamEventKind.ToolCallArgumentsDelta, kinds);
        Assert.Equal("San Francisco, CA", streamed.Message.ToolCalls[0].Inputs!["location"]!.GetValue<string>());
        _ = hw;
    }

    [Fact]
    public async Task Tool_choice_specific_with_parallel_calls_off_and_a_strict_tool()
    {
        (AnthropicProvider p, var h) = Open("tools/choice-and-strict");
        ChatResponse r = await p.SendAsync(new ChatRequest
        {
            Model = Model,
            Messages = [Message.FromUser("What is the weather in San Francisco and in Paris?")],
            Tools = [Weather() with { Strict = true }],
            ToolChoice = ToolChoice.Tool("get_weather"),
            ParallelToolCalls = false,
            MaxOutputTokens = 1024,
        });
        h.AssertAllUsed();
        Assert.Single(r.Message.ToolCalls);
    }

    [Fact]
    public async Task Tool_use_extra_fields_come_back_with_the_block_and_the_result_echoes_the_toolset()
    {
        (AnthropicProvider p, var h) = Open("tools/tool-use-extras");
        Message question = Message.FromUser("What is the weather in San Francisco and in Paris?");
        ChatRequest first = new() { Model = Model, Messages = [question], Tools = [Weather()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        ToolCall call = Assert.Single(r.Message.ToolCalls);
        Assert.Equal("computer", call.ExtensionData!["toolset_name"]);
        Assert.IsType<Dictionary<string, object?>>(call.ExtensionData["caller"]);

        ChatResponse second = await p.SendAsync(first with { Messages = [question, WithResults(r.Message, ("toolu_01C", [new TextPart("ok")], false))] });
        h.AssertAllUsed();
        Assert.Equal("Done.", second.Message.GetAssistantText());
    }

    // ---- hosted tools ----

    private static HostedToolDefinition Search() =>
        new("web_search_20260209") { Options = Obj("""{"max_uses":3}""") };

    private static Message SearchQuestion() => Message.FromUser("Who won the 2025 men's final?");

    [Fact]
    public async Task Web_search_call_result_and_citations_replay_unchanged()
    {
        (AnthropicProvider p, var h) = Open("hosted/web-search");
        ChatRequest first = new() { Model = Model, Messages = [SearchQuestion()], HostedTools = [Search()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);

        HostedToolCallPart call = Assert.Single(r.Message.Assistant.OfType<HostedToolCallPart>());
        Assert.Equal("web_search", call.Tool);
        Assert.Equal("srvtoolu_01WYG3ziw53XMcoyKL4XcZmE", call.Id);
        Assert.Equal("2025 men's final winner", call.Inputs!["query"]!.GetValue<string>());
        Assert.NotNull(call.Result);
        TextPart answer = Assert.IsType<TextPart>(r.Message.Assistant[^1]);
        Annotation citation = Assert.Single(answer.Annotations!);
        Assert.Equal("web_search_result_location", citation.Type);
        Assert.Equal("https://example.com/final", citation.ExtensionData!["url"]);
        Assert.Equal(1, r.Usage!.GetCounter("toolCalls.web_search"));

        ChatResponse second = await p.SendAsync(first with
        {
            Messages = [SearchQuestion(), r.Message, Message.FromUser("Thanks. And the women's final?")],
        });
        h.AssertAllUsed();
        Assert.Equal("Not asked yet.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Web_search_stream_equals_the_whole_response()
    {
        ChatRequest request = new() { Model = Model, Messages = [SearchQuestion()], HostedTools = [Search()], MaxOutputTokens = 1024 };
        (AnthropicProvider ps, var hs) = Open("hosted/web-search-stream");
        (ChatResponse streamed, List<StreamEventKind> kinds) = await StreamAsync(ps, request);
        hs.AssertAllUsed();
        (AnthropicProvider pw, var hw) = Open("hosted/web-search-stream-whole");
        ChatResponse whole = await pw.SendAsync(request);
        hw.AssertAllUsed();

        Assert.Equal(Json(whole.Message), Json(streamed.Message));
        Assert.Contains(StreamEventKind.HostedToolCallStart, kinds);
        Assert.Contains(StreamEventKind.HostedToolCallEnd, kinds);
        Assert.Contains(StreamEventKind.Annotation, kinds);
    }

    [Fact]
    public async Task Web_fetch_document_result_replays()
    {
        (AnthropicProvider p, var h) = Open("hosted/web-fetch");
        var fetch = new HostedToolDefinition("web_fetch_20260318");
        Message q = Message.FromUser("Summarize https://example.com/article");
        ChatRequest first = new() { Model = Model, Messages = [q], HostedTools = [fetch], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        HostedToolCallPart call = Assert.Single(r.Message.Assistant.OfType<HostedToolCallPart>());
        Assert.Equal("web_fetch", call.Tool);
        Assert.Equal(1, r.Usage!.GetCounter("toolCalls.web_fetch"));
        ChatResponse second = await p.SendAsync(first with { Messages = [q, r.Message, Message.FromUser("Thanks.")] });
        h.AssertAllUsed();
        Assert.Equal("You are welcome.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Code_execution_result_and_container_replay()
    {
        (AnthropicProvider p, var h) = Open("hosted/code-execution");
        var tool = new HostedToolDefinition("code_execution_20260521");
        Message q = Message.FromUser("What is 6 times 7? Use code.");
        ChatRequest first = new() { Model = Model, Messages = [q], HostedTools = [tool], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        HostedToolCallPart call = Assert.Single(r.Message.Assistant.OfType<HostedToolCallPart>());
        Assert.Equal("code_execution", call.Tool);
        Assert.Equal("print(6 * 7)", call.Inputs!["code"]!.GetValue<string>());
        var extras = Assert.IsType<Dictionary<string, object?>>(r.Message.ExtensionData!["anthropic"]);
        var container = Assert.IsType<Dictionary<string, object?>>(extras["container"]);
        Assert.Equal("container_011CZ", container["id"]);

        ChatResponse second = await p.SendAsync(first with { Messages = [q, r.Message, Message.FromUser("Again.")] });
        h.AssertAllUsed();
        Assert.Equal("42 again.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Pause_turn_is_reported_and_the_paused_content_continues_the_turn()
    {
        (AnthropicProvider p, var h) = Open("hosted/pause-turn");
        ChatRequest first = new() { Model = Model, Messages = [SearchQuestion()], HostedTools = [Search()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        Assert.Equal(FinishReason.PauseTurn, r.Finish);
        ChatResponse second = await p.SendAsync(first with { Messages = [SearchQuestion(), r.Message] });
        h.AssertAllUsed();
        Assert.Equal("Continued.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task An_unknown_block_type_is_kept_whole_and_sent_back()
    {
        (AnthropicProvider p, var h) = Open("hosted/unknown-block");
        ChatRequest first = new() { Model = Model, Messages = [SearchQuestion()], MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        ProviderPart raw = Assert.Single(r.Message.Assistant.OfType<ProviderPart>());
        Assert.Equal("container_upload", raw.Type);
        Assert.Equal("file_011CZABC", raw.Raw["file_id"]!.GetValue<string>());
        ChatResponse second = await p.SendAsync(first with { Messages = [SearchQuestion(), r.Message, Message.FromUser("ok")] });
        h.AssertAllUsed();
        Assert.Equal("Fine.", second.Message.GetAssistantText());
    }

    // ---- structured output and refusal ----

    [Fact]
    public async Task Structured_output_sends_the_schema_in_output_config()
    {
        (AnthropicProvider p, var h) = Open("structured/json-schema");
        JsonObject schema = Obj("""{"type":"object","properties":{"name":{"type":"string"},"email":{"type":"string"},"demo_requested":{"type":"boolean"}},"required":["name","email","demo_requested"],"additionalProperties":false}""");
        ChatResponse r = await p.SendAsync(new ChatRequest
        {
            Model = Model,
            Messages = [Message.FromUser("Extract: John Smith (john@example.com) wants a demo.")],
            ResponseFormat = ResponseFormat.FromSchema("contact", schema),
            MaxOutputTokens = 1024,
        });
        h.AssertAllUsed();
        JsonObject parsed = Obj(r.Message.GetAssistantText());
        Assert.Equal("John Smith", parsed["name"]!.GetValue<string>());
        Assert.True(parsed["demo_requested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_refusal_stop_becomes_a_refusal_part_with_its_category()
    {
        (AnthropicProvider p, var h) = Open("structured/refusal");
        ChatResponse r = await p.SendAsync(Ask("Do something harmful."));
        h.AssertAllUsed();
        Assert.Equal(FinishReason.Refusal, r.Finish);
        RefusalPart refusal = Assert.Single(r.Message.Assistant.OfType<RefusalPart>());
        Assert.Equal("I cannot help with that request.", refusal.Text);
        Assert.Equal("general_harms", refusal.ExtensionData!["category"]);
    }

    // ---- thinking ----

    private static ChatRequest Paris(ReasoningOptions? reasoning, params Message[] more) => new()
    {
        Model = Model,
        Messages = [Message.FromUser("What is the weather in Paris?"), .. more],
        Tools = [Weather()],
        Reasoning = reasoning,
        MaxOutputTokens = 1024,
    };

    private static readonly ReasoningOptions s_high = new() { Effort = "high", Summary = "summarized" };

    [Fact]
    public async Task Adaptive_thinking_goes_back_with_its_signature_after_the_tool_result()
    {
        (AnthropicProvider p, var h) = Open("thinking/adaptive-replay");
        ChatResponse r = await p.SendAsync(Paris(s_high));
        ReasoningPart thinking = Assert.Single(r.Message.Assistant.OfType<ReasoningPart>());
        Assert.Equal("anthropic", thinking.Provider);
        Assert.Equal("The user wants Paris weather; I should call the tool.", thinking.Display);
        Assert.Equal("EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds", thinking.Data!["signature"]!.GetValue<string>());
        Assert.Equal(90, r.Usage!.GetCounter("reasoningTokens"));

        ChatResponse second = await p.SendAsync(Paris(s_high, WithResults(r.Message, ("toolu_01T", [new TextPart("12 degrees")], false))));
        h.AssertAllUsed();
        Assert.Equal("It is 12 degrees in Paris.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Redacted_and_omitted_thinking_blocks_replay_unchanged()
    {
        (AnthropicProvider p, var h) = Open("thinking/redacted-and-omitted");
        ChatRequest first = new() { Model = Model, Messages = [Message.FromUser("What is the weather in Paris?")], Reasoning = new ReasoningOptions(), MaxOutputTokens = 1024 };
        ChatResponse r = await p.SendAsync(first);
        List<ReasoningPart> blocks = [.. r.Message.Assistant.OfType<ReasoningPart>()];
        Assert.Equal(2, blocks.Count);
        Assert.Equal("redacted_thinking", blocks[0].Data!["type"]!.GetValue<string>());
        Assert.Equal("Wa7zkypQ2mUEVM36O2Txu", blocks[1].Data!["signature"]!.GetValue<string>());
        Assert.True(string.IsNullOrEmpty(blocks[1].Display));

        ChatResponse second = await p.SendAsync(first with { Messages = [first.Messages[0], r.Message, Message.FromUser("Go on.")] });
        h.AssertAllUsed();
        Assert.Equal("Going on.", second.Message.GetAssistantText());
    }

    [Fact]
    public async Task Thinking_stream_equals_the_whole_response_and_keeps_the_signature()
    {
        (AnthropicProvider ps, var hs) = Open("thinking/stream");
        (ChatResponse streamed, List<StreamEventKind> kinds) = await StreamAsync(ps, Paris(s_high));
        hs.AssertAllUsed();
        (AnthropicProvider pw, var hw) = Open("thinking/stream-whole");
        ChatResponse whole = await pw.SendAsync(Paris(s_high));
        hw.AssertAllUsed();

        Assert.Equal(Json(whole.Message), Json(streamed.Message));
        ReasoningPart thinking = Assert.Single(streamed.Message.Assistant.OfType<ReasoningPart>());
        Assert.Equal("EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds", thinking.Data!["signature"]!.GetValue<string>());
        Assert.Contains(StreamEventKind.ReasoningDelta, kinds);
        Assert.Contains(StreamEventKind.ReasoningEnd, kinds);
    }

    [Fact]
    public async Task A_manual_budget_model_gets_enabled_thinking_with_the_budget()
    {
        (AnthropicProvider p, var h) = Open("thinking/manual-budget");
        ChatResponse r = await p.SendAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-5",
            Messages = [Message.FromUser("What is the weather in Paris?")],
            Reasoning = new ReasoningOptions { BudgetTokens = 2048 },
            MaxOutputTokens = 4096,
        });
        h.AssertAllUsed();
        Assert.Equal("Probably mild.", r.Message.GetAssistantText());
        Assert.Equal("sigmanual", Assert.Single(r.Message.Assistant.OfType<ReasoningPart>()).Data!["signature"]!.GetValue<string>());
    }

    // ---- caching ----

    private static Message TwoPartDocument(bool cache) => new()
    {
        User = [new TextPart("<document one>"), new TextPart("Summarize the document.")],
        Cache = cache,
    };

    [Fact]
    public async Task Explicit_cache_breakpoint_5_minutes_and_usage_split()
    {
        (AnthropicProvider p, var h) = Open("caching/explicit-5m");
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = Model, Messages = [TwoPartDocument(true)], MaxOutputTokens = 1024 });
        h.AssertAllUsed();
        Assert.Equal(20, r.Usage!.InputTokens);
        Assert.Equal(2048, r.Usage.CacheCreationInputTokens);
        Assert.Equal(0, r.Usage.CacheReadInputTokens);
        Assert.Equal(0, r.Usage.GetCounter("cacheCreation1hInputTokens"));
    }

    [Fact]
    public async Task Explicit_cache_breakpoint_1_hour_reports_the_1h_part_of_the_write()
    {
        (AnthropicProvider p, var h) = Open("caching/ttl-1h-mixed");
        ChatResponse r = await p.SendAsync(new ChatRequest
        {
            Model = Model,
            Messages = [TwoPartDocument(true)],
            Cache = new CachePolicy { Ttl = TimeSpan.FromHours(1) },
            MaxOutputTokens = 1024,
        });
        h.AssertAllUsed();
        Assert.Equal(3000, r.Usage!.CacheCreationInputTokens); // the API total, 5 minute and 1 hour writes together
        Assert.Equal(2500, r.Usage.GetCounter("cacheCreation1hInputTokens")); // the part of it written for an hour
        Assert.Equal(1000, r.Usage.CacheReadInputTokens);
    }

    [Fact]
    public async Task Automatic_caching_is_one_top_level_cache_control()
    {
        (AnthropicProvider p, var h) = Open("caching/automatic");
        ChatResponse r = await p.SendAsync(Hi() with { Cache = new CachePolicy() });
        h.AssertAllUsed();
        Assert.Equal(1500, r.Usage!.CacheCreationInputTokens);
    }

    // ---- images, PDFs, citations ----

    [Fact]
    public async Task Images_inline_by_url_and_by_file_id()
    {
        const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";
        (AnthropicProvider p, var h) = Open("images/base64-url-and-file");
        var message = new Message
        {
            User =
            [
                new AssetPart(new AssetRef { ContentType = "image/png", Data = Png }),
                new AssetPart(new AssetRef { ContentType = "image/jpeg" }) { PublicUrl = "https://example.com/cat.jpg" },
                new FileRefPart("anthropic", "file_011CNha8iCJcU1wXNR6q4V8w") { ContentType = "image/png" },
                new TextPart("Describe these."),
            ],
        };
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = Model, Messages = [message], MaxOutputTokens = 1024 });
        h.AssertAllUsed();
        Assert.Equal("Three small images.", r.Message.GetAssistantText());
    }

    [Fact]
    public async Task Pdf_and_text_documents_with_citations_become_annotations()
    {
        (AnthropicProvider p, var h) = Open("pdf/base64-citations");
        var message = new Message
        {
            User =
            [
                new AssetPart(new AssetRef { ContentType = "application/pdf", Data = "JVBERi0xLjQKJcfsj6IKJSVFT0YK" })
                {
                    ExtensionData = new Dictionary<string, object?> { ["title"] = "Report", ["citations"] = true },
                },
                new AssetPart(new AssetRef { ContentType = "text/plain", Data = Convert.ToBase64String("The grass is green. The sky is blue."u8.ToArray()) })
                {
                    ExtensionData = new Dictionary<string, object?> { ["title"] = "Notes", ["context"] = "Field notes", ["citations"] = true },
                },
                new TextPart("What colour is the sky, and what does the report conclude?"),
            ],
        };
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = Model, Messages = [message], MaxOutputTokens = 1024 });
        h.AssertAllUsed();

        List<TextPart> texts = [.. r.Message.Assistant.OfType<TextPart>()];
        Assert.Equal(2, texts.Count);
        Annotation chars = Assert.Single(texts[0].Annotations!);
        Assert.Equal("char_location", chars.Type);
        Assert.Equal("The sky is blue.", chars.CitedText);
        Assert.Equal(20L, chars.ExtensionData!["start_char_index"]);
        Assert.Equal(36L, chars.ExtensionData["end_char_index"]);
        Annotation pages = Assert.Single(texts[1].Annotations!);
        Assert.Equal("page_location", pages.Type);
        Assert.Equal(3L, pages.ExtensionData!["start_page_number"]);
    }

    // ---- count tokens ----

    [Fact]
    public async Task Count_tokens_reports_the_provider_tier()
    {
        (AnthropicProvider p, var h) = Open("count-tokens/count");
        TokenCount count = await p.CountTokensAsync(Hi() with { System = "You are terse.", Tools = [Weather()], MaxOutputTokens = null });
        h.AssertAllUsed();
        Assert.Equal(2095, count.InputTokens);
        Assert.Equal(TokenCountTier.Provider, count.Tier);
    }

    // ---- batches ----

    [Fact]
    public async Task Batch_submit_poll_results_and_cancel()
    {
        (AnthropicProvider p, var h) = Open("batch/lifecycle");
        BatchJob job = await p.SubmitBatchAsync(new BatchRequest
        {
            Items =
            [
                new BatchItem { CustomId = "first", Chat = Hi() },
                new BatchItem { CustomId = "second", Chat = Ask("Say goodbye.", 64) },
            ],
        });
        Assert.Equal("msgbatch_013Zva2CMHLNnXjNJJKqJ2EF", job.Id);
        Assert.Equal(BatchStatus.InProgress, job.Status);
        Assert.Equal(3, job.Total);

        Assert.Equal(BatchStatus.InProgress, (await p.GetBatchAsync(job.Id)).Status);
        BatchJob ended = await p.GetBatchAsync(job.Id);
        Assert.Equal(BatchStatus.Completed, ended.Status);
        Assert.Equal(1, ended.Succeeded);
        Assert.Equal(1, ended.Failed);

        var results = new List<BatchResultItem>();
        await foreach (BatchResultItem item in p.GetBatchResultsAsync(job.Id))
        {
            results.Add(item);
        }

        Assert.Equal(["first", "second", "third", "fourth"], results.Select(x => x.CustomId));
        Assert.Equal("Hello!", results[0].Chat!.Message.GetAssistantText());
        Assert.Equal("batch", results[0].Chat!.Usage!.ServiceTier);
        var bad = Assert.IsType<BadRequestException>(results[1].Error);
        Assert.Equal("[scrubbed]", bad.RequestId);
        Assert.Equal("expired", results[2].Error!.Code);
        Assert.Equal("canceled", results[3].Error!.Code);

        Assert.Equal(BatchStatus.Cancelling, (await p.CancelBatchAsync(job.Id)).Status);
        h.AssertAllUsed();
    }

    [Fact]
    public async Task Batch_ids_are_checked_before_anything_is_sent()
    {
        (AnthropicProvider p, var h) = Open("batch/lifecycle");
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest { Items = [new BatchItem { CustomId = "has space", Chat = Hi() }] }));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest { Items = [new BatchItem { CustomId = "a", Chat = Hi() }, new BatchItem { CustomId = "a", Chat = Hi() }] }));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest { Items = [] }));
        Assert.Empty(h.Requests);
    }
}
