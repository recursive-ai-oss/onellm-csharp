using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;
using OneLlm.Providers;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using OneChatResponse = OneLlm.Providers.ChatResponse;

namespace OneLlm.Extensions.AI.Tests;

/// <summary>
/// The recorded provider fixtures, replayed through <see cref="OneLlmChatClient"/>. Each provider sees byte-for-byte the requests its own tests
/// send (the replay handler rejects any difference), and the bridged answer must say what the provider's own answer says.
/// </summary>
public class ConformanceThroughBridgeTests
{
    private static readonly JsonElement s_weatherSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"location":{"type":"string","description":"The city and state, e.g. San Francisco, CA"}},"required":["location"]}""").RootElement.Clone();

    private static AIFunctionDeclaration Weather() =>
        AIFunctionFactory.CreateDeclaration("get_weather", "Get the current weather in a given location", s_weatherSchema);

    private static List<ChatMessage> Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    private static List<ChatMessage> TerseHello() => [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Say hello.")];

    private static ChatMessage Result(string id, object? value, bool error = false)
    {
        var content = new FunctionResultContent(id, value);
        if (error)
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary { [OneLlmProperties.IsError] = true };
        }

        return new ChatMessage(ChatRole.Tool, [content]);
    }

    // ---- Anthropic Messages ----

    [Fact]
    public async Task Anthropic_chat_text_usage_and_finish()
    {
        var request = new ChatRequest { Model = "claude-sonnet", System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 1024 };
        (IChatProvider direct, _) = Kit.Anthropic("chat/basic");
        OneChatResponse expected = await direct.SendAsync(request);

        (IChatProvider p, var h) = Kit.Anthropic("chat/basic");
        using var client = new OneLlmChatClient(p, "claude-sonnet", Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(TerseHello(), new ChatOptions { MaxOutputTokens = 1024 });
        h.AssertAllUsed();
        Kit.AssertSame(r, expected);
        Assert.Equal(ChatRole.Assistant, r.Messages[0].Role);
    }

    [Fact]
    public async Task Anthropic_stream_builds_the_response_the_whole_call_builds()
    {
        (IChatProvider whole, _) = Kit.Anthropic("chat/stream-text-whole");
        OneChatResponse expected = await whole.SendAsync(new ChatRequest { Model = "claude-sonnet", System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 1024 });

        (IChatProvider p, var h) = Kit.Anthropic("chat/stream-text");
        using var client = new OneLlmChatClient(p, "claude-sonnet", Kit.Time);
        var updates = new List<ChatResponseUpdate>();
        MeaiChatResponse r = await Kit.Collect(client.GetStreamingResponseAsync(TerseHello(), new ChatOptions { MaxOutputTokens = 1024 }), updates);
        h.AssertAllUsed();
        Assert.True(updates.Count > 2, "text arrives as several updates");
        Assert.Equal(expected.Message.GetAssistantText(), r.Text);
        Assert.Equal(expected.Finish, ChatMapping.FromFinish(r.FinishReason).Finish);
        Assert.Equal(expected.Usage!.OutputTokens, r.Usage!.OutputTokenCount);
    }

    [Fact]
    public async Task Anthropic_two_parallel_tool_calls_and_their_results_round_trip()
    {
        ChatRequest direct = new() { Model = "claude-sonnet", Messages = [Message.FromUser("What is the weather in San Francisco and in Paris?")], Tools = [Kit_Weather()], MaxOutputTokens = 1024 };
        (IChatProvider d, _) = Kit.Anthropic("tools/client-tools-roundtrip");
        OneChatResponse expected = await d.SendAsync(direct);

        (IChatProvider p, var h) = Kit.Anthropic("tools/client-tools-roundtrip");
        using var client = new OneLlmChatClient(p, "claude-sonnet", Kit.Time);
        var options = new ChatOptions { Tools = [Weather()], MaxOutputTokens = 1024 };
        List<ChatMessage> conversation = Ask("What is the weather in San Francisco and in Paris?");
        MeaiChatResponse first = await client.GetResponseAsync(conversation, options);

        Kit.AssertSame(first, expected);
        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        FunctionCallContent[] calls = [.. Kit.Calls(first)];
        Assert.Equal(2, calls.Length);
        Assert.Equal("San Francisco, CA", calls[0].Arguments!["location"]!.ToString());

        conversation.AddRange(first.Messages);
        conversation.Add(new ChatMessage(ChatRole.Tool, [
            new FunctionResultContent(calls[0].CallId, "15 degrees"),
            Err(calls[1].CallId)]));
        MeaiChatResponse second = await client.GetResponseAsync(conversation, options);
        h.AssertAllUsed();
        Assert.Equal("San Francisco is 15 degrees. The Paris lookup failed.", second.Text);
    }

    // Two text parts stay two parts: a multi-part result is a list of contents.
    private static FunctionResultContent Err(string id) =>
        new(id, new List<AIContent> { new TextContent("raining"), new TextContent("9 degrees") }) { AdditionalProperties = new AdditionalPropertiesDictionary { [OneLlmProperties.IsError] = true } };

    private static ToolDefinition Kit_Weather() => new(
        "get_weather",
        "Get the current weather in a given location",
        (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string","description":"The city and state, e.g. San Francisco, CA"}},"required":["location"]}""")!);

    [Fact]
    public async Task Anthropic_streamed_tool_calls_equal_the_whole_ones()
    {
        (IChatProvider pw, _) = Kit.Anthropic("tools/client-tools-roundtrip");
        (IChatProvider ps, var hs) = Kit.Anthropic("tools/client-tools-stream");
        var options = new ChatOptions { Tools = [Weather()], MaxOutputTokens = 1024 };
        using var whole = new OneLlmChatClient(pw, "claude-sonnet", Kit.Time);
        using var streamed = new OneLlmChatClient(ps, "claude-sonnet", Kit.Time);
        var messages = Ask("What is the weather in San Francisco and in Paris?");
        MeaiChatResponse a = await whole.GetResponseAsync(messages, options);
        MeaiChatResponse b = await Kit.Collect(streamed.GetStreamingResponseAsync(messages, options));
        hs.AssertAllUsed();
        Assert.Equal(Kit.Calls(a).Select(c => c.CallId), Kit.Calls(b).Select(c => c.CallId));
        Assert.Equal(Kit.Calls(a).Select(c => JsonBridge.FromArguments(c.Arguments).ToJsonString()), Kit.Calls(b).Select(c => JsonBridge.FromArguments(c.Arguments).ToJsonString()));
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(Kit.Body(ContentMapper.ToTurn(a.Messages)), Kit.Body(ContentMapper.ToTurn(b.Messages)));
    }

    [Fact]
    public async Task Anthropic_structured_output_sends_the_schema()
    {
        JsonElement schema = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"},"email":{"type":"string"},"demo_requested":{"type":"boolean"}},"required":["name","email","demo_requested"],"additionalProperties":false}""").RootElement.Clone();
        (IChatProvider p, var h) = Kit.Anthropic("structured/json-schema");
        using var client = new OneLlmChatClient(p, "claude-sonnet", Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(
            Ask("Extract: John Smith (john@example.com) wants a demo."),
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "contact"), MaxOutputTokens = 1024 });
        h.AssertAllUsed();
        JsonNode parsed = JsonNode.Parse(r.Text)!;
        Assert.Equal("John Smith", parsed["name"]!.GetValue<string>());
        Assert.True(parsed["demo_requested"]!.GetValue<bool>());
    }

    // ---- OpenAI Responses ----

    private const string GptModel = "gpt-6.1-terra";

    [Fact]
    public async Task OpenAI_chat_system_text_usage_ids()
    {
        var request = new ChatRequest { Model = GptModel, System = "You are terse.", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64 };
        (IChatProvider d, _) = Kit.OpenAI("responses/chat");
        OneChatResponse expected = await d.SendAsync(request);

        (IChatProvider p, var h) = Kit.OpenAI("responses/chat");
        using var client = new OneLlmChatClient(p, GptModel, Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Say hello.")],
            new ChatOptions { MaxOutputTokens = 64 });
        h.AssertAllUsed();
        Kit.AssertSame(r, expected);
        Assert.Equal("Hello! How can I help?", r.Text);
        Assert.Equal("resp_chat_1", r.ResponseId);
    }

    [Fact]
    public async Task OpenAI_stream_builds_the_response_the_whole_call_builds()
    {
        (IChatProvider ps, var h) = Kit.OpenAI("responses/stream");
        using var client = new OneLlmChatClient(ps, GptModel, Kit.Time);
        MeaiChatResponse r = await Kit.Collect(client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Say hello.")],
            new ChatOptions { MaxOutputTokens = 64 }));
        h.AssertAllUsed();
        Assert.Equal("Hello! How can I help?", r.Text);
        Assert.Equal("resp_chat_1", r.ResponseId);
        Assert.Equal(24, r.Usage!.InputTokenCount);
        Assert.Equal(9, r.Usage.OutputTokenCount);
    }

    private static AIFunctionDeclaration OpenAiWeather() => AIFunctionFactory.CreateDeclaration(
        "get_weather", "Gets the weather for a city.", JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""").RootElement.Clone());

    private static ToolDeclaration OpenAiTime() => new ToolDeclaration(
        "get_time", "Gets the time in a zone.", JsonDocument.Parse("""{"type":"object","properties":{"zone":{"type":"string"}},"required":["zone"],"additionalProperties":false}""").RootElement.Clone(), strict: true);

    [Fact]
    public async Task OpenAI_parallel_function_calls_and_results_in_call_order()
    {
        (IChatProvider p, var h) = Kit.OpenAI("responses/function-tools");
        using var client = new OneLlmChatClient(p, GptModel, Kit.Time);
        var options = new ChatOptions { Tools = [OpenAiWeather(), OpenAiTime()] };
        List<ChatMessage> conversation = Ask("Weather and time in Paris?");
        MeaiChatResponse first = await client.GetResponseAsync(conversation, options);

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        FunctionCallContent[] calls = [.. Kit.Calls(first)];
        Assert.Equal(["call_a1", "call_b2"], calls.Select(c => c.CallId));
        Assert.Equal(["get_weather", "get_time"], calls.Select(c => c.Name));
        Assert.Equal("Let me check.", first.Text);
        // The text comes first and both calls after it: the order the model produced them in.
        Assert.IsType<TextContent>(first.Messages[0].Contents[0]);

        conversation.AddRange(first.Messages);
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_a1", "Sunny, 21C"), new FunctionResultContent("call_b2", "14:05")]));
        MeaiChatResponse second = await client.GetResponseAsync(conversation, options);
        h.AssertAllUsed();
        Assert.Equal("It is sunny and 14:05 in Paris.", second.Text);
        Assert.Equal(64, second.Usage!.CachedInputTokenCount);
    }

    [Fact]
    public async Task OpenAI_streamed_function_calls_equal_the_whole_ones()
    {
        (IChatProvider pw, _) = Kit.OpenAI("responses/function-tools");
        (IChatProvider ps, var hs) = Kit.OpenAI("responses/function-tools-stream");
        var options = new ChatOptions { Tools = [OpenAiWeather(), OpenAiTime()] };
        var messages = Ask("Weather and time in Paris?");
        MeaiChatResponse a = await new OneLlmChatClient(pw, GptModel, Kit.Time).GetResponseAsync(messages, options);
        MeaiChatResponse b = await Kit.Collect(new OneLlmChatClient(ps, GptModel, Kit.Time).GetStreamingResponseAsync(messages, options));
        hs.AssertAllUsed();
        Assert.Equal(Kit.Body(ContentMapper.ToTurn(a.Messages)), Kit.Body(ContentMapper.ToTurn(b.Messages)));
    }

    [Fact]
    public async Task OpenAI_structured_output_sends_a_strict_schema()
    {
        JsonElement schema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"},"temp_c":{"type":"integer"}},"required":["city","temp_c"],"additionalProperties":false}""").RootElement.Clone();
        (IChatProvider p, var h) = Kit.OpenAI("responses/structured-output");
        using var client = new OneLlmChatClient(p, GptModel, Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(Ask("Weather in Paris as JSON."), new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "weather") });
        h.AssertAllUsed();
        Assert.Equal("{\"city\":\"Paris\",\"temp_c\":14}", r.Text);
    }

    [Fact]
    public async Task OpenAI_reasoning_is_replayed_on_the_next_turn()
    {
        (IChatProvider p, var h) = Kit.OpenAI("responses/reasoning");
        using var client = new OneLlmChatClient(p, GptModel, Kit.Time);
        var options = new ChatOptions { Reasoning = new Microsoft.Extensions.AI.ReasoningOptions { Effort = ReasoningEffort.Medium, Output = ReasoningOutput.Summary } };
        List<ChatMessage> conversation = Ask("What is the answer?");
        MeaiChatResponse first = await client.GetResponseAsync(conversation, options);
        TextReasoningContent thought = first.Messages[0].Contents.OfType<TextReasoningContent>().Single();
        Assert.Equal("Considering the question.\n\nChoosing a short answer.", thought.Text);
        Assert.Contains("gAAAAA_synthetic_encrypted_content_1", thought.ProtectedData);
        Assert.Equal(120, first.Usage!.ReasoningTokenCount);

        conversation.AddRange(first.Messages);
        conversation.Add(new ChatMessage(ChatRole.User, "Why?"));
        MeaiChatResponse next = await client.GetResponseAsync(conversation, options);
        h.AssertAllUsed();
        Assert.Equal("Because it is 6 times 7.", next.Text);
        Assert.Equal(128, next.Usage!.CachedInputTokenCount);
    }

    // ---- Gemini generateContent ----

    private const string Flash = "gemini-3.5-flash-lite";

    [Fact]
    public async Task Gemini_chat_with_system_and_sampling_options()
    {
        var request = new ChatRequest
        {
            Model = Flash,
            System = "Be brief.",
            Messages = [Message.FromUser("Say hello in one word.")],
            MaxOutputTokens = 64,
            Temperature = 0.2,
            TopP = 0.9,
            StopSequences = ["END"],
        };
        (IChatProvider d, _) = Kit.Gemini("chat/basic");
        OneChatResponse expected = await d.SendAsync(request);

        (IChatProvider p, var h) = Kit.Gemini("chat/basic");
        using var client = new OneLlmChatClient(p, Flash, Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "Be brief."), new ChatMessage(ChatRole.User, "Say hello in one word.")],
            new ChatOptions { MaxOutputTokens = 64, Temperature = 0.2f, TopP = 0.9f, StopSequences = ["END"] });
        h.AssertAllUsed();
        Kit.AssertSame(r, expected);
        Assert.Equal("Hello!", r.Text);
    }

    [Fact]
    public async Task Gemini_stream_text()
    {
        (IChatProvider p, var h) = Kit.Gemini("stream/text");
        using var client = new OneLlmChatClient(p, Flash, Kit.Time);
        var updates = new List<ChatResponseUpdate>();
        MeaiChatResponse r = await Kit.Collect(client.GetStreamingResponseAsync(Ask("Greet the world.")), updates);
        h.AssertAllUsed();
        Assert.Equal("Hello, world!", r.Text);
        Assert.Equal(9, r.Usage!.InputTokenCount);
        Assert.Equal(5, r.Usage.OutputTokenCount);
        Assert.Equal(3, updates.Count(u => u.Contents.OfType<TextContent>().Any()));
    }

    [Fact]
    public async Task Gemini_function_call_with_thought_signature_and_result()
    {
        AIFunctionDeclaration weather = AIFunctionFactory.CreateDeclaration(
            "get_weather", "Gets the weather for a city.", JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone());
        (IChatProvider p, var h) = Kit.Gemini("functions/call-and-result");
        using var client = new OneLlmChatClient(p, "gemini-3.8-flash", Kit.Time);
        var options = new ChatOptions { Tools = [weather], ToolMode = ChatToolMode.Auto };
        List<ChatMessage> conversation = Ask("Weather in Paris?");
        MeaiChatResponse first = await client.GetResponseAsync(conversation, options);

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        // The signature rides as reasoning content just before the call it belongs to, and returns to the provider on the next turn.
        Assert.IsType<TextReasoningContent>(first.Messages[0].Contents[0]);
        FunctionCallContent call = Assert.Single(Kit.Calls(first));
        Assert.Equal("call-abc", call.CallId);

        conversation.AddRange(first.Messages);
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-abc", JsonDocument.Parse("""{"temp_c":18}""").RootElement.Clone())]));
        MeaiChatResponse second = await client.GetResponseAsync(conversation, new ChatOptions { Tools = [weather] });
        h.AssertAllUsed();
        Assert.Equal("It is 18 degrees in Paris.", second.Text);
    }

    [Fact]
    public async Task Gemini_structured_output()
    {
        JsonElement schema = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"},"total":{"type":"number"},"paid":{"type":"boolean"}},"required":["name","total"],"additionalProperties":false}""").RootElement.Clone();
        (IChatProvider p, var h) = Kit.Gemini("structured/json-schema");
        using var client = new OneLlmChatClient(p, Flash, Kit.Time);
        MeaiChatResponse r = await client.GetResponseAsync(Ask("Extract: Acme owes 12.5 and has not paid."), new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "invoice") });
        h.AssertAllUsed();
        JsonNode value = JsonNode.Parse(r.Text)!;
        Assert.Equal("Acme", value["name"]!.GetValue<string>());
        Assert.Equal(12.5, value["total"]!.GetValue<double>());
    }
}
