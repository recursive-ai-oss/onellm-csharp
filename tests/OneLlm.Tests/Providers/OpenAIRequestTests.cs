using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAIRequestTests
{
    private static OpenAIWire Wire(OpenAIProtocol protocol = OpenAIProtocol.Responses, bool store = false, ChatDialect? dialect = null, string scope = "openai") =>
        new("openai", scope, protocol, dialect ?? ChatDialect.OpenAI, OpenAIEndpoint.Direct(null), new OpenAIOptions { Catalog = OpenAIFx.Catalog, Store = store }, TimeProvider.System);

    private static JsonObject Body(OpenAIWire wire, ChatRequest r, bool stream = false, List<ResponseWarning>? warnings = null) =>
        (JsonObject)JsonNode.Parse(wire.WriteChat(r, stream, warnings).Span)!;

    private static ChatRequest Basic(string model = "gpt-6.1-terra") => new() { Model = model, Messages = [Message.FromUser("hi")] };

    [Fact]
    public void Temperature_is_dropped_and_logged_for_a_model_that_takes_none_and_kept_for_one_that_does()
    {
        var log = new List<ResponseWarning>();
        OpenAIWire wire = Wire();
        Assert.Null(Body(wire, Basic() with { Temperature = 0.3, TopP = 0.9 }, warnings: log)["temperature"]);
        Assert.Single(log);
        Assert.Contains("temperature", log[0].Message, StringComparison.Ordinal);
        Assert.Equal(0.3, Body(wire, Basic("gpt-4.1") with { Temperature = 0.3 }, warnings: log)["temperature"]!.GetValue<double>());
        Assert.Single(log);
    }

    [Fact]
    public void An_effort_the_model_does_not_take_is_refused_and_a_model_without_reasoning_drops_it()
    {
        var log = new List<ResponseWarning>();
        OpenAIWire wire = Wire();
        OneLlm.Catalog.UnsupportedParameterException e = Assert.Throws<OneLlm.Catalog.UnsupportedParameterException>(() => wire.WriteChat(Basic("gpt-6-astra") with { Reasoning = new ReasoningOptions { Effort = "none" } }, false));
        Assert.Contains("low, medium, high, xhigh, max", e.Message, StringComparison.Ordinal);
        Assert.Null(Body(wire, Basic("gpt-4.1") with { Reasoning = new ReasoningOptions { Effort = "high" } }, warnings: log)["reasoning"]);
        Assert.Contains(log, l => l.Message.Contains("reason", StringComparison.Ordinal));
        Assert.Equal("high", Body(wire, Basic("gpt-6-astra") with { Reasoning = new ReasoningOptions { Effort = "high" } })["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Stop_sequences_are_refused_by_responses_and_sent_by_chat_completions()
    {
        Assert.Throws<BadRequestException>(() => Wire().WriteChat(Basic() with { StopSequences = ["END"] }, false));
        JsonObject chat = Body(Wire(OpenAIProtocol.ChatCompletions), Basic("gpt-4.1") with { StopSequences = ["END"] });
        Assert.Equal("END", chat["stop"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Extra_parameters_replace_what_the_codec_writes_and_the_rest_are_appended()
    {
        JsonObject body = Body(Wire(), Basic() with { MaxOutputTokens = 10, ExtraParameters = OpenAIFx.Obj("""{"max_output_tokens":99,"metadata":{"k":"v"},"store":true}""") });
        Assert.Equal(99, body["max_output_tokens"]!.GetValue<int>());
        Assert.True(body["store"]!.GetValue<bool>());
        Assert.Equal("v", body["metadata"]!["k"]!.GetValue<string>());
        Assert.Equal(1, body.Count(p => p.Key == "store"));
        Assert.Equal("store", body.Last().Key);
    }

    [Fact]
    public void Nothing_is_stored_by_default_and_reasoning_models_ask_for_encrypted_content()
    {
        JsonObject body = Body(Wire(), Basic());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", body["include"]![0]!.GetValue<string>());
        JsonObject plain = Body(Wire(), Basic("gpt-4.1"));
        Assert.Null(plain["include"]);
        JsonObject stored = Body(Wire(store: true), Basic());
        Assert.True(stored["store"]!.GetValue<bool>());
        Assert.Null(stored["include"]);
    }

    [Fact]
    public void Tool_choice_maps_to_each_protocols_words()
    {
        ToolDefinition tool = new("f", "d", OpenAIFx.Obj("""{"type":"object"}"""));
        HostedToolDefinition web = new("web_search");
        ChatRequest r = Basic() with { Tools = [tool], HostedTools = [web] };
        Assert.Equal("required", Body(Wire(), r with { ToolChoice = ToolChoice.Required })["tool_choice"]!.GetValue<string>());
        Assert.Equal("none", Body(Wire(), r with { ToolChoice = ToolChoice.None })["tool_choice"]!.GetValue<string>());
        JsonNode fn = Body(Wire(), r with { ToolChoice = ToolChoice.Tool("f") })["tool_choice"]!;
        Assert.Equal("function", fn["type"]!.GetValue<string>());
        Assert.Equal("f", fn["name"]!.GetValue<string>());
        Assert.Equal("web_search", Body(Wire(), r with { ToolChoice = ToolChoice.Tool("web_search") })["tool_choice"]!["type"]!.GetValue<string>());
        ChatDialect mistral = ChatDialect.OpenAI with { RequiredWord = "any" };
        ChatRequest chat = Basic("m") with { Tools = [tool], ToolChoice = ToolChoice.Required };
        Assert.Equal("any", Body(Wire(OpenAIProtocol.ChatCompletions, dialect: mistral, scope: "mistral"), chat)["tool_choice"]!.GetValue<string>());
        Assert.Equal("f", Body(Wire(OpenAIProtocol.ChatCompletions), chat with { ToolChoice = ToolChoice.Tool("f") })["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void The_token_limit_field_follows_the_dialect_and_streamed_usage_is_asked_for_only_where_it_is_understood()
    {
        ChatRequest r = Basic("m") with { MaxOutputTokens = 5 };
        JsonObject openai = Body(Wire(OpenAIProtocol.ChatCompletions), r, stream: true);
        Assert.Equal(5, openai["max_completion_tokens"]!.GetValue<int>());
        Assert.True(openai["stream_options"]!["include_usage"]!.GetValue<bool>());
        ChatDialect plain = ChatDialect.OpenAI with { MaxTokensField = "max_tokens", StreamUsage = false };
        JsonObject other = Body(Wire(OpenAIProtocol.ChatCompletions, dialect: plain), r, stream: true);
        Assert.Equal(5, other["max_tokens"]!.GetValue<int>());
        Assert.Null(other["stream_options"]);
        Assert.Null(Body(Wire(OpenAIProtocol.ChatCompletions), r, stream: false)["stream_options"]);
    }

    [Fact]
    public void An_assistant_turn_keeps_text_and_calls_in_their_original_order()
    {
        var call = new ToolCall("c1", "f") { Inputs = [], After = 1, Result = [new TextPart("done")] };
        var turn = new Message { Assistant = [new TextPart("before"), new TextPart("after")], ToolCalls = [call] };
        JsonArray input = Body(Wire(), Basic() with { Messages = [Message.FromUser("go"), turn] })["input"]!.AsArray();
        Assert.Equal(
            ["user", "assistant", "function_call", "assistant", "function_call_output"],
            input.Select(i => i!["type"]?.GetValue<string>() ?? i["role"]!.GetValue<string>()));
        Assert.Equal("before", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("c1", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("done", input[4]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void Reasoning_and_hosted_items_of_another_provider_are_left_out_and_openai_ones_are_written_verbatim()
    {
        JsonObject item = OpenAIFx.Obj("""{"type":"reasoning","id":"rs_x","summary":[],"encrypted_content":"abc","future":{"k":[1]}}""");
        var turn = new Message
        {
            Assistant =
            [
                new ReasoningPart("anthropic") { Data = OpenAIFx.Obj("""{"signature":"zzz"}""") },
                new ReasoningPart("openai") { Data = item },
                new TextPart("answer"),
            ],
        };
        JsonArray input = Body(Wire(), Basic() with { Messages = [Message.FromUser("q"), turn] })["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.Equal(item.ToJsonString(), input[1]!.ToJsonString());
    }

    [Fact]
    public void An_asset_with_no_bytes_and_no_url_is_a_clear_error_and_a_public_url_is_used_as_is()
    {
        var empty = new AssetPart(new AssetRef { Storage = "s", Key = "k", ContentType = "image/png" });
        Assert.Throws<BadRequestException>(() => Wire().WriteChat(Basic() with { Messages = [new Message { User = [empty] }] }, false));
        var linked = new AssetPart(new AssetRef { Storage = "s", Key = "k", ContentType = "image/png" }) { PublicUrl = "https://example.com/a.png" };
        JsonNode part = Body(Wire(), Basic() with { Messages = [new Message { User = [linked] }] })["input"]![0]!["content"]![0]!;
        Assert.Equal("input_image", part["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/a.png", part["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void Sampling_and_output_limits_are_not_part_of_a_token_count()
    {
        ReadOnlyMemory<byte> body = Wire().WriteCount(Basic() with { Temperature = 0.1, MaxOutputTokens = 5, Cache = new CachePolicy { Key = "k" }, ServiceTier = "flex" });
        JsonObject o = (JsonObject)JsonNode.Parse(body.Span)!;
        Assert.Equal(["model", "input"], o.Select(p => p.Key));
    }

    [Fact]
    public void Non_ascii_text_is_written_as_utf8_not_escapes()
    {
        string json = Encoding.UTF8.GetString(Wire().WriteChat(Basic() with { Messages = [Message.FromUser("caf\u00e9 <b>")] }, false).Span);
        Assert.Contains("caf\u00e9 <b>", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Dated_snapshots_use_their_familys_rules()
    {
        var log = new List<ResponseWarning>();
        Body(Wire(), Basic("gpt-5.5-2026-04-23") with { Temperature = 1 }, warnings: log);
        Assert.Single(log);
    }
}
