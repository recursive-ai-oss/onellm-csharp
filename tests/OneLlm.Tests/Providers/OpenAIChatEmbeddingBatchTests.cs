using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAIChatCompletionsTests
{
    private const string Dir = "openai/direct/chat-completions/";

    private static ChatRequest Hello() => new() { Model = "gpt-4.1", System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64, Temperature = 0.2 };

    private static ToolDefinition Weather() => new("get_weather", "Gets the weather for a city.", OpenAIFx.Obj("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}"""));

    private static ToolDefinition Time() => new("get_time", "Gets the time in a zone.", OpenAIFx.Obj("""{"type":"object","properties":{"zone":{"type":"string"}},"required":["zone"]}"""));

    [Fact]
    public async Task Chat_gives_text_and_usage_with_cached_tokens_taken_out_of_the_input()
    {
        using var fx = OpenAIFx.Open(Dir + "chat.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions).SendAsync(Hello());
        Assert.Equal("Hello! How can I help?", r.Message.GetAssistantText());
        Assert.Equal(FinishReason.Stop, r.Finish);
        Assert.Equal("stop", r.FinishReasonRaw);
        Assert.Equal(24, r.Usage!.InputTokens);
        Assert.Equal(9, r.Usage.OutputTokens);
        Assert.Equal("default", r.Usage.ServiceTier);
        Assert.Equal("gpt-4.1-2025-04-14", r.Model);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_stream_with_usage_builds_the_whole_call_message()
    {
        using var whole = OpenAIFx.Open(Dir + "chat.json");
        using var streamed = OpenAIFx.Open(Dir + "stream.json");
        ChatResponse a = await OpenAIFx.OpenAI(whole, OpenAIApi.ChatCompletions).SendAsync(Hello());
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed, OpenAIApi.ChatCompletions), Hello());
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
        Assert.Equal(a.Finish, b.Finish);
        streamed.AssertAllUsed();
    }

    [Fact]
    public async Task Tool_calls_keep_their_order_and_their_results_go_out_as_tool_messages()
    {
        using var fx = OpenAIFx.Open(Dir + "tools.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions);
        Message ask = Message.FromUser("Weather and time in Paris?");
        ChatResponse r = await p.SendAsync(new ChatRequest { Model = "gpt-4.1", Messages = [ask], Tools = [Weather(), Time()], ToolChoice = ToolChoice.Required });
        Assert.Equal(FinishReason.ToolCalls, r.Finish);
        Assert.Equal(["call_a1", "call_b2"], r.Message.ToolCalls.Select(c => c.Id));
        ToolCall[] done =
        [
            new(r.Message.ToolCalls[0].Id, "get_weather") { Inputs = r.Message.ToolCalls[0].Inputs, Result = [new TextPart("Sunny, 21C")] },
            new(r.Message.ToolCalls[1].Id, "get_time") { Inputs = r.Message.ToolCalls[1].Inputs, Result = [new TextPart("14:05")] },
        ];
        ChatResponse second = await p.SendAsync(new ChatRequest { Model = "gpt-4.1", Messages = [ask, new Message { Assistant = r.Message.Assistant, ToolCalls = done }], Tools = [Weather(), Time()] });
        Assert.Equal("It is sunny and 14:05 in Paris.", second.Message.GetAssistantText());
        Assert.Equal(64, second.Usage!.CacheReadInputTokens);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Streamed_tool_call_fragments_are_reassembled_per_index()
    {
        using var whole = OpenAIFx.Open(Dir + "tools.json");
        using var streamed = OpenAIFx.Open(Dir + "tools-stream.json");
        ChatRequest req = new() { Model = "gpt-4.1", Messages = [Message.FromUser("Weather and time in Paris?")], Tools = [Weather(), Time()], ToolChoice = ToolChoice.Required };
        ChatResponse a = await OpenAIFx.OpenAI(whole, OpenAIApi.ChatCompletions).SendAsync(req);
        ChatResponse b = await OpenAIFx.CollectAsync(OpenAIFx.OpenAI(streamed, OpenAIApi.ChatCompletions), req);
        Assert.Equal(OpenAIFx.Json(a.Message), OpenAIFx.Json(b.Message));
    }

    [Fact]
    public async Task A_json_schema_response_format_is_sent_in_its_chat_completions_form()
    {
        using var fx = OpenAIFx.Open(Dir + "response-format.json");
        JsonObject schema = OpenAIFx.Obj("""{"type":"object","properties":{"city":{"type":"string"},"temp_c":{"type":"integer"}},"required":["city","temp_c"],"additionalProperties":false}""");
        ChatResponse r = await OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions).SendAsync(new ChatRequest { Model = "gpt-4.1", Messages = [Message.FromUser("Weather as JSON.")], ResponseFormat = ResponseFormat.FromSchema("weather", schema) });
        Assert.Equal("{\"city\":\"Paris\",\"temp_c\":14}", r.Message.GetAssistantText());
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_length_finish_is_reported()
    {
        using var fx = OpenAIFx.Open(Dir + "length.json");
        ChatResponse r = await OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions).SendAsync(new ChatRequest { Model = "gpt-4.1", Messages = [Message.FromUser("Story.")], MaxOutputTokens = 16 });
        Assert.Equal(FinishReason.Length, r.Finish);
    }

    [Fact]
    public async Task Hosted_tools_and_function_calling_without_them_are_refused_up_front()
    {
        using var fx = OpenAIFx.Open(Dir + "chat.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx, OpenAIApi.ChatCompletions);
        await Assert.ThrowsAsync<BadRequestException>(() => p.SendAsync(new ChatRequest { Model = "gpt-4.1", Messages = [Message.FromUser("x")], HostedTools = [new HostedToolDefinition("web_search")] }));
        Assert.Empty(fx.Requests);
    }
}

public class OpenAIEmbeddingTests
{
    [Fact]
    public async Task Vectors_come_back_in_input_order_with_usage()
    {
        using var fx = OpenAIFx.Open("openai/direct/embeddings/basic.json");
        EmbeddingResponse r = await OpenAIFx.OpenAI(fx).EmbedAsync(new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["first", "second"], Dimensions = 3 });
        Assert.Equal(2, r.Embeddings.Count);
        Assert.Equal([0.0625f, 0.25f, -0.5f], r.Embeddings[0].ToArray());
        Assert.Equal([0.5f, -0.25f, 0.125f], r.Embeddings[1].ToArray());
        Assert.Equal(9, r.Usage!.InputTokens);
        Assert.Equal("text-embedding-3-small", r.Model);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task An_empty_input_list_is_refused()
    {
        using var fx = OpenAIFx.Open("openai/direct/embeddings/basic.json");
        await Assert.ThrowsAsync<ArgumentException>(() => OpenAIFx.OpenAI(fx).EmbedAsync(new EmbeddingRequest { Model = "text-embedding-3-small" }));
    }
}

public class OpenAIBatchTests
{
    private const string Dir = "openai/direct/batch/";

    [Fact]
    public async Task A_batch_goes_from_upload_to_results_with_errors_mapped_back_to_items()
    {
        using var fx = OpenAIFx.Open(Dir + "lifecycle.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        BatchJob job = await p.SubmitBatchAsync(new BatchRequest
        {
            Items =
            [
                new BatchItem { CustomId = "req-1", Chat = new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 32 } },
                new BatchItem { CustomId = "req-2", Chat = new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Say goodbye.")], MaxOutputTokens = 32 } },
            ],
        });
        Assert.Equal("batch_1", job.Id);
        Assert.Equal(BatchStatus.Validating, job.Status);

        BatchJob running = await p.GetBatchAsync("batch_1");
        Assert.Equal(BatchStatus.InProgress, running.Status);
        Assert.Equal((3, 1, 0), (running.Total, running.Succeeded, running.Failed));
        BatchJob finished = await p.GetBatchAsync("batch_1");
        Assert.Equal(BatchStatus.Completed, finished.Status);
        Assert.Equal(1741476777, finished.CreatedAt!.Value.ToUnixTimeSeconds());

        var results = new List<BatchResultItem>();
        await foreach (BatchResultItem item in p.GetBatchResultsAsync("batch_1"))
        {
            results.Add(item);
        }

        Assert.Equal(["req-1", "req-2", "req-3"], results.Select(r => r.CustomId));
        Assert.Equal("Batch hello.", results[0].Chat!.Message.GetAssistantText());
        Assert.Equal(20, results[0].Chat!.Usage!.InputTokens);
        Assert.IsType<BadRequestException>(results[1].Error);
        Assert.Contains("Invalid input", results[1].Error!.Message, StringComparison.Ordinal);
        Assert.NotNull(results[2].Error);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task A_batch_can_be_cancelled()
    {
        using var fx = OpenAIFx.Open(Dir + "cancel.json");
        BatchJob job = await OpenAIFx.OpenAI(fx).CancelBatchAsync("batch_1");
        Assert.Equal(BatchStatus.Cancelling, job.Status);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Embeddings_batches_use_the_embeddings_endpoint_and_return_vectors()
    {
        using var fx = OpenAIFx.Open(Dir + "embeddings.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        BatchJob job = await p.SubmitBatchAsync(new BatchRequest { Items = [new BatchItem { CustomId = "emb-1", Embedding = new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["hello"] } }] });
        Assert.Equal("batch_2", job.Id);
        BatchResultItem item = Assert.Single(await ToListAsync(p.GetBatchResultsAsync("batch_2")));
        Assert.Equal([0.5f, 0.25f], item.Embedding!.Embeddings[0].ToArray());
        Assert.Equal(4, item.Embedding.Usage!.InputTokens);
        fx.AssertAllUsed();
    }

    [Fact]
    public async Task Asking_for_results_before_there_are_any_says_so()
    {
        using var fx = OpenAIFx.Open(Dir + "no-results-yet.json");
        BadRequestException e = await Assert.ThrowsAsync<BadRequestException>(async () => await ToListAsync(OpenAIFx.OpenAI(fx).GetBatchResultsAsync("batch_1")));
        Assert.Contains("no results yet", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_holds_one_kind_of_request_with_unique_ids()
    {
        using var fx = OpenAIFx.Open(Dir + "lifecycle.json");
        OpenAIProvider p = OpenAIFx.OpenAI(fx);
        ChatRequest chat = new() { Model = "gpt-6.1-terra", Messages = [Message.FromUser("x")] };
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest { Items = [new BatchItem { CustomId = "a", Chat = chat }, new BatchItem { CustomId = "a", Chat = chat }] }));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest { Items = [new BatchItem { CustomId = "a", Chat = chat }, new BatchItem { CustomId = "b", Embedding = new EmbeddingRequest { Model = "m", Inputs = ["x"] } }] }));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitBatchAsync(new BatchRequest()));
        Assert.Empty(fx.Requests);
    }

    private static async Task<List<BatchResultItem>> ToListAsync(IAsyncEnumerable<BatchResultItem> source)
    {
        var list = new List<BatchResultItem>();
        await foreach (BatchResultItem item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
