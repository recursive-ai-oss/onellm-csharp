using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Tests.Messages;

public class AccumulatorTests
{
    private static ReadOnlyMemory<byte> Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string ToJson(Message m) => JsonSerializer.Serialize(m, OneLlmJsonContext.Default.Message);

    [Fact]
    public void A_stream_with_reasoning_text_two_interleaved_tool_calls_and_usage_builds_the_expected_message()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.MessageStart("resp_1", "test-model"));
        acc.Add(StreamEvent.ReasoningDelta(0, Bytes("Let me ")));
        acc.Add(StreamEvent.ReasoningDelta(0, Bytes("think.")));
        acc.Add(StreamEvent.ReasoningEnd(0, "anthropic", new JsonObject { ["signature"] = "sig" }));
        acc.Add(StreamEvent.TextDelta(1, Bytes("I'll check ")));
        acc.Add(StreamEvent.TextDelta(1, Bytes("both files.")));
        acc.Add(StreamEvent.ToolCallStart(2, "call_a", "read_file"));
        acc.Add(StreamEvent.ToolCallStart(3, "call_b", "list_files"));
        // The two calls' arguments arrive interleaved and split mid-token.
        acc.Add(StreamEvent.ToolCallArgumentsDelta(2, Bytes("{\"pa")));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(3, Bytes("{\"dir\":")));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(2, Bytes("th\":\"a.txt\"}")));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(3, Bytes("\"src\"}")));
        acc.Add(StreamEvent.ToolCallEnd(3));
        acc.Add(StreamEvent.ToolCallEnd(2));
        acc.Add(StreamEvent.UsageReported(new Usage { InputTokens = 100, CacheReadInputTokens = 40 }));
        acc.Add(StreamEvent.UsageReported(new Usage { OutputTokens = 55 }));
        acc.Add(StreamEvent.Done(FinishReason.ToolCalls, "tool_use"));

        Message m = acc.ToMessage();

        Assert.Equal("resp_1", acc.ResponseId);
        Assert.Equal(FinishReason.ToolCalls, acc.Finish);
        Assert.Equal("tool_use", acc.FinishReasonRaw);
        Assert.True(acc.IsDone);

        Assert.Equal(2, m.Assistant.Count);
        var reasoning = Assert.IsType<ReasoningPart>(m.Assistant[0]);
        Assert.Equal("anthropic", reasoning.Provider);
        Assert.Equal("Let me think.", reasoning.Display);
        Assert.Equal("sig", reasoning.Data!["signature"]!.GetValue<string>());
        Assert.Equal("I'll check both files.", Assert.IsType<TextPart>(m.Assistant[1]).Text);

        // Calls come out in the order they started, after the two parts that preceded them.
        Assert.Equal(["call_a", "call_b"], m.ToolCalls.Select(c => c.Id));
        Assert.All(m.ToolCalls, c => Assert.Equal(2, c.After));
        Assert.Equal("a.txt", m.ToolCalls[0].Inputs!["path"]!.GetValue<string>());
        Assert.Equal("src", m.ToolCalls[1].Inputs!["dir"]!.GetValue<string>());

        Assert.Equal(100, m.Usage!.InputTokens);
        Assert.Equal(40, m.Usage.CacheReadInputTokens);
        Assert.Equal(55, m.Usage.OutputTokens);
        Assert.Equal("test-model", m.Usage.Model);
        Assert.Equal(195, m.Usage.TotalInputTokens + m.Usage.OutputTokens);
    }

    [Fact]
    public void A_multibyte_character_split_across_deltas_is_decoded_whole()
    {
        byte[] euro = Encoding.UTF8.GetBytes("\u20ac"); // three bytes
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, euro.AsMemory(0, 1)));
        acc.Add(StreamEvent.TextDelta(0, euro.AsMemory(1)));
        Assert.Equal("\u20ac", acc.ToMessage().GetAssistantText());
    }

    [Fact]
    public void Delta_buffers_are_copied_so_the_codec_can_reuse_them()
    {
        byte[] buffer = Encoding.UTF8.GetBytes("abc");
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, buffer));
        buffer[0] = (byte)'X';
        acc.Add(StreamEvent.TextDelta(0, buffer));
        Assert.Equal("abcXbc", acc.ToMessage().GetAssistantText());
    }

    [Fact]
    public void Empty_text_blocks_are_dropped_and_blocks_keep_the_order_of_their_first_event()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, Bytes("")));
        acc.Add(StreamEvent.TextDelta(2, Bytes("second")));
        acc.Add(StreamEvent.TextDelta(1, Bytes("first-started-later")));
        Message m = acc.ToMessage();
        Assert.Equal(["second", "first-started-later"], m.Assistant.Select(p => ((TextPart)p).Text));
    }

    [Fact]
    public void Tool_arguments_that_are_not_an_object_are_kept_as_text()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.ToolCallStart(0, "c1", "t"));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(0, Bytes("{\"a\": ")));
        acc.Add(StreamEvent.ToolCallStart(1, "c2", "t"));
        acc.Add(StreamEvent.ToolCallStart(2, "c3", "t"));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(2, Bytes("  ")));
        acc.Add(StreamEvent.ToolCallStart(3, "c4", "t"));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(3, Bytes("[1]")));
        Message m = acc.ToMessage();

        Assert.Null(m.ToolCalls[0].Inputs);
        Assert.Equal("{\"a\": ", m.ToolCalls[0].InvalidInputs);
        Assert.Null(m.ToolCalls[1].Inputs);
        Assert.Null(m.ToolCalls[1].InvalidInputs);
        Assert.Null(m.ToolCalls[2].InvalidInputs); // whitespace only is the same as no arguments
        Assert.Null(m.ToolCalls[3].Inputs);
        Assert.Equal("[1]", m.ToolCalls[3].InvalidInputs);
    }

    [Fact]
    public void Annotations_attach_to_their_text_block()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, Bytes("See the docs.")));
        acc.Add(StreamEvent.AnnotationAdded(0, new Annotation { Type = "url_citation", Url = "https://example.com" }));
        var text = Assert.IsType<TextPart>(Assert.Single(acc.ToMessage().Assistant));
        Assert.Equal("https://example.com", Assert.Single(text.Annotations!).Url);
    }

    [Fact]
    public void Hosted_tool_calls_take_their_inputs_from_deltas_and_their_result_from_the_end_event()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.HostedToolCallStart(0, new HostedToolCallPart("web_search") { Provider = "anthropic", Id = "srv_1" }));
        acc.Add(StreamEvent.HostedToolCallArgumentsDelta(0, Bytes("{\"query\":")));
        acc.Add(StreamEvent.HostedToolCallArgumentsDelta(0, Bytes("\"rain\"}")));
        acc.Add(StreamEvent.HostedToolCallEnd(0, new HostedToolCallPart("web_search") { Provider = "anthropic", Id = "srv_1", Status = "completed", Result = new JsonArray("r1"), Cost = 0.01m }));
        var hosted = Assert.IsType<HostedToolCallPart>(Assert.Single(acc.ToMessage().Assistant));
        Assert.Equal("rain", hosted.Inputs!["query"]!.GetValue<string>());
        Assert.Equal("completed", hosted.Status);
        Assert.Equal(0.01m, hosted.Cost);
    }

    [Fact]
    public void Whole_parts_and_refusals_are_kept_in_order()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.RefusalDelta(0, Bytes("I can't ")));
        acc.Add(StreamEvent.RefusalDelta(0, Bytes("do that.")));
        acc.Add(StreamEvent.PartAdded(1, new FileRefPart("gemini", "files/1")));
        Message m = acc.ToMessage();
        Assert.Equal("I can't do that.", Assert.IsType<RefusalPart>(m.Assistant[0]).Text);
        Assert.IsType<FileRefPart>(m.Assistant[1]);
    }

    [Fact]
    public void A_non_streaming_response_fed_through_the_helpers_equals_the_streamed_one()
    {
        var streamed = new MessageAccumulator();
        streamed.Add(StreamEvent.TextDelta(0, Bytes("Hello ")));
        streamed.Add(StreamEvent.TextDelta(0, Bytes("world")));
        streamed.Add(StreamEvent.ToolCallStart(1, "c1", "read_file"));
        streamed.Add(StreamEvent.ToolCallArgumentsDelta(1, Bytes("{\"path\":")));
        streamed.Add(StreamEvent.ToolCallArgumentsDelta(1, Bytes("\"a\"}")));
        streamed.Add(StreamEvent.UsageReported(new Usage { Model = "m", InputTokens = 5, OutputTokens = 6 }));

        var whole = new MessageAccumulator();
        whole.AddText(0, "Hello world");
        whole.AddToolCall(1, "c1", "read_file", "{\"path\":\"a\"}");
        whole.Add(StreamEvent.UsageReported(new Usage { Model = "m", InputTokens = 5, OutputTokens = 6 }));

        Assert.Equal(ToJson(streamed.ToMessage()), ToJson(whole.ToMessage()));
    }

    [Fact]
    public void Usage_reports_merge_so_later_non_zero_counters_win()
    {
        Usage merged = new Usage { Model = "m", InputTokens = 10, OutputTokens = 1, Counters = new() { ["reasoningTokens"] = 3 } }
            .MergeLater(new Usage { OutputTokens = 9, Counters = new() { ["reasoningTokens"] = 0, ["images"] = 2 } });
        Assert.Equal(10, merged.InputTokens);
        Assert.Equal(9, merged.OutputTokens);
        Assert.Equal("m", merged.Model);
        Assert.Equal(3, merged.GetCounter("reasoningTokens"));
        Assert.Equal(2, merged.GetCounter("images"));
        Assert.Equal(0, merged.GetCounter("missing"));
    }
}
