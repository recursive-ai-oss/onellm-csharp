using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

public class OpenAICodecTests
{
    // A stream that hands back its bytes a few at a time, so every boundary gets crossed.
    private sealed class Dribble(byte[] data, int step) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(step, buffer.Length), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
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

    private static async Task<List<string>> ReadAll(string text, int step)
    {
        var events = new List<string>();
        using var reader = new SseReader(new Dribble(Encoding.UTF8.GetBytes(text), step), "openai");
        while (await reader.ReadEventAsync(CancellationToken.None))
        {
            events.Add(Encoding.UTF8.GetString(reader.Data.Span));
        }

        return events;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Sse_events_split_at_any_byte_are_read_whole(int step)
    {
        string text = "event: a\ndata: {\"x\":1}\n\ndata: second\n\n";
        Assert.Equal(["{\"x\":1}", "second"], await ReadAll(text, step));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Several_data_lines_are_joined_with_a_line_feed(int step)
    {
        Assert.Equal(["one\ntwo\nthree", "next"], await ReadAll("data: one\ndata: two\ndata:three\n\ndata: next\n\n", step));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Comments_pings_and_other_fields_are_skipped(int step)
    {
        string text = ": OPENROUTER PROCESSING\n\nevent: ping\nid: 7\nretry: 100\n\n:keep-alive\ndata: real\n\n";
        Assert.Equal(["real"], await ReadAll(text, step));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task All_three_line_endings_work_and_crlf_is_one_ending(int step)
    {
        Assert.Equal(["a", "b", "c"], await ReadAll("data: a\r\n\r\ndata: b\r\rdata: c\n\n", step));
    }

    [Fact]
    public async Task A_final_event_without_its_blank_line_is_dropped_as_the_standard_says()
    {
        Assert.Equal(["a"], await ReadAll("data: a\n\ndata: b\n", 4));
        Assert.Empty(await ReadAll("", 4));
    }

    [Fact]
    public async Task A_done_marker_is_an_ordinary_event()
    {
        Assert.Equal(["{}", "[DONE]"], await ReadAll("data: {}\n\ndata: [DONE]\n\n", 2));
    }

    [Fact]
    public async Task Large_events_grow_the_buffers()
    {
        string big = new('x', 100_000);
        Assert.Equal([big], await ReadAll("data: " + big + "\n\n", 1000));
    }

    private static List<StreamEvent> Decode(ResponsesDecoder d, string json)
    {
        d.ProcessEvent(Encoding.UTF8.GetBytes(json));
        return [.. d.Events];
    }

    [Fact]
    public void An_escaped_delta_is_decoded_to_utf8_without_a_string_round_trip()
    {
        var d = new ResponsesDecoder("openai");
        StreamEvent e = Assert.Single(Decode(d, """{"type":"response.output_text.delta","output_index":0,"content_index":0,"item_id":"m","delta":"line\n\"quoted\" \u00e9 \ud83d\ude00"}"""));
        Assert.Equal(StreamEventKind.TextDelta, e.Kind);
        Assert.Equal("line\n\"quoted\" \u00e9 \U0001F600", e.GetText());
    }

    [Fact]
    public void A_delta_that_comes_before_the_type_is_still_read()
    {
        var d = new ResponsesDecoder("openai");
        StreamEvent e = Assert.Single(Decode(d, """{"delta":"abc","output_index":2,"content_index":1,"type":"response.output_text.delta"}"""));
        Assert.Equal(StreamEventKind.TextDelta, e.Kind);
        Assert.Equal(2 * 64 + 1, e.Index);
        Assert.Equal("abc", e.GetText());
        StreamEvent esc = Assert.Single(Decode(d, """{"delta":"a\tb","output_index":0,"type":"response.output_text.delta"}"""));
        Assert.Equal("a\tb", esc.GetText());
    }

    [Fact]
    public void Events_of_kinds_that_build_nothing_are_ignored_whatever_they_carry()
    {
        var d = new ResponsesDecoder("openai");
        Assert.Empty(Decode(d, """{"type":"response.web_search_call.searching","output_index":0,"item_id":"x"}"""));
        Assert.Empty(Decode(d, """{"type":"response.from_the_future","payload":{"a":[1,2,{"b":null}]}}"""));
        Assert.Empty(Decode(d, """{"type":"response.in_progress","response":{"id":"r"}}"""));
    }

    [Fact]
    public void Unknown_fields_of_an_annotation_survive_as_extension_data()
    {
        var d = new ResponsesDecoder("openai");
        StreamEvent e = Assert.Single(Decode(d, """{"type":"response.output_text.annotation.added","output_index":0,"content_index":0,"annotation":{"type":"container_file_citation","container_id":"c1","file_id":"f1","filename":"a.txt","start_index":1,"end_index":4,"confidence":0.5,"nested":{"k":1}}}"""));
        Annotation a = e.Annotation!;
        Assert.Equal("container_file_citation", a.Type);
        Assert.Equal("f1", a.SourceId);
        Assert.Equal("c1", a.ExtensionData!["container_id"]);
        Assert.Equal("{\"k\":1}", a.ExtensionData["nested"]);
        Assert.True(a.ExtensionData.ContainsKey("confidence"));
    }

    [Fact]
    public void A_function_call_whose_arguments_never_streamed_gets_them_from_the_finished_item()
    {
        var d = new ResponsesDecoder("openai");
        var acc = new MessageAccumulator();
        foreach (string json in new[]
        {
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc","call_id":"c1","name":"f","arguments":""}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc","call_id":"c1","name":"f","arguments":"{\"a\":1}"}}""",
            """{"type":"response.completed","response":{"id":"r","status":"completed","model":"m","output":[]}}""",
        })
        {
            foreach (StreamEvent e in Decode(d, json))
            {
                acc.Add(e);
            }
        }

        Message m = acc.ToMessage();
        Assert.Equal(1, m.ToolCalls[0].Inputs!["a"]!.GetValue<int>());
        Assert.Equal(FinishReason.ToolCalls, acc.Finish);
        Assert.True(d.Finished);
    }

    [Fact]
    public void Text_that_only_appears_in_the_finished_item_is_added_once()
    {
        var d = new ResponsesDecoder("openai");
        var acc = new MessageAccumulator();
        foreach (StreamEvent e in Decode(d, """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[{"type":"output_text","text":"whole","annotations":[]}]}}"""))
        {
            acc.Add(e);
        }

        Assert.Equal("whole", acc.ToMessage().GetAssistantText());
    }

    [Fact]
    public void Garbage_events_are_a_malformed_response_not_a_crash()
    {
        var d = new ResponsesDecoder("openai");
        Assert.Throws<MalformedResponseException>(() => d.ProcessEvent("{not json"u8.ToArray()));
        Assert.Throws<MalformedResponseException>(() => d.ProcessEvent("[1,2]"u8.ToArray()));
        Assert.Throws<MalformedResponseException>(() => d.ProcessResponse("nope"u8.ToArray()));
    }

    [Fact]
    public void Chat_completions_chunks_with_odd_shapes_are_handled()
    {
        var d = new ChatCompletionsDecoder("compat");
        var acc = new MessageAccumulator();
        foreach (string chunk in new[]
        {
            """{"id":"c","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"f","arguments":"{\"a\":"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"1}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4}}}""",
        })
        {
            d.Process(Encoding.UTF8.GetBytes(chunk));
            foreach (StreamEvent e in d.Events)
            {
                acc.Add(e);
            }
        }

        d.Complete();
        foreach (StreamEvent e in d.Events)
        {
            acc.Add(e);
        }

        Message m = acc.ToMessage();
        Assert.Equal("t1", m.ToolCalls[0].Id);
        Assert.Equal(1, m.ToolCalls[0].Inputs!["a"]!.GetValue<int>());
        Assert.Equal(6, m.Usage!.InputTokens);
        Assert.Equal(4, m.Usage.CacheReadInputTokens);
        Assert.Equal(FinishReason.ToolCalls, acc.Finish);
    }

    [Fact]
    public void A_tool_call_with_no_id_gets_a_stable_one()
    {
        var d = new ChatCompletionsDecoder("compat");
        d.Process("""{"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"type":"function","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""u8.ToArray());
        Assert.Contains(d.Events, e => e.Kind == StreamEventKind.ToolCallStart && e.Id == "call_0");
    }

    [Fact]
    public void An_input_type_the_embeddings_api_cannot_take_is_reported_in_the_warnings()
    {
        var wire = new OpenAIWire("openai", "openai", OpenAIProtocol.Responses, ChatDialect.OpenAI, OpenAIEndpoint.Direct(null), new OpenAIOptions(), TimeProvider.System);
        var warnings = new List<ResponseWarning>();
        wire.WriteEmbeddings(new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["x"], InputType = EmbeddingInputType.Query }, warnings);
        ResponseWarning w = Assert.Single(warnings);
        Assert.Equal("parameter_stripped", w.Code);
        Assert.Equal("input type", w.Parameter);
    }

    [Fact]
    public void Embeddings_parse_without_a_node_tree_and_survive_extra_fields()
    {
        var wire = new OpenAIWire("openai", "openai", OpenAIProtocol.Responses, ChatDialect.OpenAI, OpenAIEndpoint.Direct(null), new OpenAIOptions(), TimeProvider.System);
        EmbeddingResponse r = wire.ParseEmbeddings("""{"object":"list","extra":{"a":[1]},"data":[{"object":"embedding","embedding":[1.5,2],"index":0,"more":true}],"model":"m","usage":{"prompt_tokens":3}}"""u8.ToArray(), "x");
        Assert.Equal([1.5f, 2f], r.Embeddings[0].ToArray());
        Assert.Equal(3, r.Usage!.InputTokens);
        Assert.Throws<MalformedResponseException>(() => wire.ParseEmbeddings("""{"data":[{"embedding":"nope"}]}"""u8.ToArray(), "x"));
    }

    [Fact]
    public void Line_reader_handles_crlf_blank_lines_and_a_last_line_with_no_ending()
    {
        using var reader = new LineReader(new MemoryStream("a\r\n\r\nbb\nccc"u8.ToArray()), "openai");
        var lines = new List<string>();
        while (reader.ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
        {
            lines.Add(Encoding.UTF8.GetString(reader.Line.Span));
        }

        Assert.Equal(["a", "", "bb", "ccc"], lines);
    }
}
