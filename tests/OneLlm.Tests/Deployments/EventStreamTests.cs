using System.Buffers.Binary;
using System.Text;
using OneLlm.Providers.Anthropic;

namespace OneLlm.Tests.Deployments;

/// <summary>Decoding of the AWS event stream encoding and its presentation as server-sent events for the Messages codec.</summary>
public class EventStreamTests
{
    // One "chunk" message built independently with Python's zlib.crc32 (see the fixture notes): three string headers and the payload
    // {"bytes": "eyJ0eXBlIjogIm1lc3NhZ2Vfc3RvcCJ9"} whose bytes are {"type": "message_stop"}.
    private const string GoldenHex =
        "000000880000004b05babc220b3a6576656e742d747970650700056368756e6b0d3a636f6e74656e742d747970650700106170706c69636174696f6e2f6a736f6e0d3a6d6573736167652d747970650700056576656e747b226279746573223a202265794a306558426c496a6f67496d316c63334e685a3256666333527663434a39227d11dfd5fd";

    private sealed class SlowStream(byte[] data, int step) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, step), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = Math.Min(Math.Min(buffer.Length, step), data.Length - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return ValueTask.FromResult(n);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Builds a message with the library's own checksum; the golden message above is the independent check of that checksum.
    private static byte[] Frame(IEnumerable<byte[]> headers, byte[] payload)
    {
        byte[] hb = headers.SelectMany(h => h).ToArray();
        int total = 12 + hb.Length + payload.Length + 4;
        var bytes = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)hb.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), EventStreamReader.Crc32(bytes.AsSpan(0, 8)));
        hb.CopyTo(bytes, 12);
        payload.CopyTo(bytes, 12 + hb.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(total - 4), EventStreamReader.Crc32(bytes.AsSpan(0, total - 4)));
        return bytes;
    }

    private static byte[] Str(string name, string value)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        byte[] v = Encoding.UTF8.GetBytes(value);
        return [(byte)n.Length, .. n, 7, (byte)(v.Length >> 8), (byte)v.Length, .. v];
    }

    private static byte[] Chunk(string eventJson) =>
        Frame(
            [Str(":event-type", "chunk"), Str(":content-type", "application/json"), Str(":message-type", "event")],
            Encoding.UTF8.GetBytes("{\"bytes\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(eventJson)) + "\"}"));

    private static byte[] Exception(string type, string message) =>
        Frame(
            [Str(":exception-type", type), Str(":content-type", "application/json"), Str(":message-type", "exception")],
            Encoding.UTF8.GetBytes("{\"message\":\"" + message + "\"}"));

    private static async Task<List<EventStreamMessage>> ReadAll(Stream s)
    {
        var reader = new EventStreamReader(s, "anthropic");
        var all = new List<EventStreamMessage>();
        while (await reader.ReadAsync(CancellationToken.None) is { } m)
        {
            all.Add(m);
        }

        return all;
    }

    [Fact]
    public void The_checksum_is_standard_crc32()
    {
        // The CRC-32 check value for "123456789" is 0xCBF43926.
        Assert.Equal(0xCBF43926u, EventStreamReader.Crc32("123456789"u8));
    }

    [Fact]
    public async Task A_message_built_by_an_independent_encoder_decodes_with_its_headers_and_payload()
    {
        List<EventStreamMessage> messages = await ReadAll(new MemoryStream(Convert.FromHexString(GoldenHex)));
        EventStreamMessage m = Assert.Single(messages);
        Assert.Equal("chunk", m.Text(":event-type"));
        Assert.Equal("event", m.Text(":message-type"));
        Assert.Equal("application/json", m.Text(":content-type"));
        Assert.Equal("{\"bytes\": \"eyJ0eXBlIjogIm1lc3NhZ2Vfc3RvcCJ9\"}", Encoding.UTF8.GetString(m.Payload));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public async Task Messages_split_at_any_point_across_reads_decode_the_same(int step)
    {
        byte[] stream = [.. Chunk("{\"type\":\"a\"}"), .. Chunk("{\"type\":\"b\"}"), .. Convert.FromHexString(GoldenHex)];
        List<EventStreamMessage> messages = await ReadAll(new SlowStream(stream, step));
        Assert.Equal(3, messages.Count);
        Assert.Equal(3, messages.Count(x => x.Text(":event-type") == "chunk"));
    }

    [Fact]
    public async Task A_flipped_bit_in_the_prelude_fails_the_prelude_checksum()
    {
        byte[] bytes = Convert.FromHexString(GoldenHex);
        bytes[5] ^= 0x01;
        var e = await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(bytes)));
        Assert.Contains("prelude", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flipped_bit_in_the_payload_fails_the_message_checksum()
    {
        byte[] bytes = Convert.FromHexString(GoldenHex);
        bytes[80] ^= 0x01;
        var e = await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(bytes)));
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_message_is_an_error_but_one_that_ends_between_messages_is_not()
    {
        byte[] bytes = Convert.FromHexString(GoldenHex);
        await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(bytes[..^3])));
        await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(bytes[..6])));
        Assert.Single(await ReadAll(new MemoryStream(bytes)));
        Assert.Empty(await ReadAll(new MemoryStream([])));
    }

    [Fact]
    public async Task An_impossible_length_is_refused_before_anything_is_allocated()
    {
        var prelude = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(prelude, 0xFFFFFFF0u);
        BinaryPrimitives.WriteUInt32BigEndian(prelude.AsSpan(8), EventStreamReader.Crc32(prelude.AsSpan(0, 8)));
        await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(prelude)));

        // Headers longer than the message.
        var bad = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bad, 20);
        BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(4), 500);
        BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(8), EventStreamReader.Crc32(bad.AsSpan(0, 8)));
        await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(bad)));
    }

    [Fact]
    public async Task Every_header_type_is_read_and_unknown_headers_are_kept_without_changing_the_event()
    {
        byte[] all =
        [
            .. Str(":event-type", "chunk"),
            2, (byte)'a', 2, 0xFE,                                           // byte -2
            .. new byte[] { 1, (byte)'t', 0 },                               // bool true
            .. new byte[] { 1, (byte)'f', 1 },                               // bool false
            .. new byte[] { 1, (byte)'s', 3, 0xFF, 0xFE },                   // short -2
            .. new byte[] { 1, (byte)'i', 4, 0, 0, 1, 0 },                   // int 256
            .. new byte[] { 1, (byte)'l', 5, 0, 0, 0, 0, 0, 0, 1, 0 },       // long 256
            .. new byte[] { 1, (byte)'b', 6, 0, 2, 9, 8 },                   // byte array
            .. new byte[] { 1, (byte)'m', 8, 0, 0, 1, 0x8F, 0xE0, 0xB4, 0x1C, 0x00 }, // timestamp
            .. new byte[] { 1, (byte)'u', 9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, // uuid
        ];
        byte[] frame = Frame([all], "{}"u8.ToArray());
        EventStreamMessage m = Assert.Single(await ReadAll(new MemoryStream(frame)));
        Assert.Equal("chunk", m.Text(":event-type"));
        Assert.Equal(-2, m.Headers["a"].Number);
        Assert.Equal(1, m.Headers["t"].Number);
        Assert.Equal(0, m.Headers["f"].Number);
        Assert.Equal(-2, m.Headers["s"].Number);
        Assert.Equal(256, m.Headers["i"].Number);
        Assert.Equal(256, m.Headers["l"].Number);
        Assert.Equal([9, 8], m.Headers["b"].Bytes);
        Assert.Equal(16, m.Headers["u"].Bytes!.Length);
        Assert.Equal(0x018FE0B41C00L, m.Headers["m"].Number);
    }

    [Fact]
    public async Task A_header_type_outside_the_closed_set_means_the_framing_is_damaged()
    {
        byte[] frame = Frame([[1, (byte)'x', 10, 0]], "{}"u8.ToArray());
        await Assert.ThrowsAsync<MalformedResponseException>(() => ReadAll(new MemoryStream(frame)));
    }

    // ---- as server-sent events ----

    private static async Task<List<string>> SseData(byte[] eventStream)
    {
        await using var body = new BedrockEventStream(new MemoryStream(eventStream));
        using var sse = new SseReader(body);
        var events = new List<string>();
        while (await sse.ReadAsync(CancellationToken.None))
        {
            events.Add(Encoding.UTF8.GetString(sse.Data.Span));
        }

        return events;
    }

    [Fact]
    public async Task Chunks_become_server_sent_events_carrying_the_decoded_messages_events()
    {
        byte[] stream = [.. Chunk("{\"type\":\"message_start\"}"), .. Chunk("{\"type\":\"content_block_delta\",\"delta\":{\"text\":\"caf\u00e9 \u65e5\u672c\"}}")];
        List<string> data = await SseData(stream);
        Assert.Equal(["{\"type\":\"message_start\"}", "{\"type\":\"content_block_delta\",\"delta\":{\"text\":\"café 日本\"}}"], data);
    }

    [Fact]
    public async Task A_payload_with_a_line_break_between_tokens_is_rejoined()
    {
        List<string> data = await SseData(Chunk("{\"type\":\n\"x\"}"));
        Assert.Equal("{\"type\":\n\"x\"}", Assert.Single(data));
    }

    [Fact]
    public async Task Events_that_are_not_chunks_are_skipped()
    {
        byte[] other = Frame([Str(":event-type", "initial-response"), Str(":message-type", "event")], "{}"u8.ToArray());
        List<string> data = await SseData([.. other, .. Chunk("{\"type\":\"ping\"}")]);
        Assert.Equal(["{\"type\":\"ping\"}"], data);
    }

    [Theory]
    [InlineData("throttlingException", typeof(RateLimitException))]
    [InlineData("validationException", typeof(BadRequestException))]
    [InlineData("serviceUnavailableException", typeof(OverloadedException))]
    [InlineData("internalServerException", typeof(ProviderException))]
    [InlineData("modelStreamErrorException", typeof(ProviderException))]
    [InlineData("modelTimeoutException", typeof(ProviderException))]
    public async Task An_exception_message_is_thrown_as_the_matching_typed_error_after_the_events_before_it(string kind, Type expected)
    {
        byte[] stream = [.. Chunk("{\"type\":\"message_start\"}"), .. Exception(kind, "boom")];
        await using var body = new BedrockEventStream(new MemoryStream(stream));
        using var sse = new SseReader(body);
        Assert.True(await sse.ReadAsync(CancellationToken.None));
        ProviderException e = await Assert.ThrowsAnyAsync<ProviderException>(async () =>
        {
            while (await sse.ReadAsync(CancellationToken.None))
            {
            }
        });
        Assert.Equal(expected, e.GetType());
        Assert.Contains("boom", e.Message, StringComparison.Ordinal);
        Assert.Equal("anthropic", e.Provider);
    }

    [Fact]
    public async Task Throttling_in_a_stream_is_transient_and_a_validation_error_is_not()
    {
        byte[] throttled = Exception("throttlingException", "slow down");
        ProviderException a = await Assert.ThrowsAnyAsync<ProviderException>(() => SseData(throttled));
        Assert.True(a.IsTransient);
        byte[] invalid = Exception("validationException", "bad");
        ProviderException b = await Assert.ThrowsAnyAsync<ProviderException>(() => SseData(invalid));
        Assert.False(b.IsTransient);
    }

    [Fact]
    public async Task An_unmodeled_error_message_is_thrown_with_its_code_and_text()
    {
        byte[] frame = Frame([Str(":message-type", "error"), Str(":error-code", "InternalError"), Str(":error-message", "An internal server error occurred.")], []);
        ProviderException e = await Assert.ThrowsAnyAsync<ProviderException>(() => SseData(frame));
        Assert.Equal("InternalError", e.Code);
        Assert.Contains("internal server error", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunk_without_valid_bytes_is_a_malformed_response()
    {
        byte[] bad = Frame([Str(":event-type", "chunk"), Str(":message-type", "event")], "{\"bytes\":\"!!!\"}"u8.ToArray());
        await Assert.ThrowsAsync<MalformedResponseException>(() => SseData(bad));
        byte[] none = Frame([Str(":event-type", "chunk"), Str(":message-type", "event")], "{}"u8.ToArray());
        await Assert.ThrowsAsync<MalformedResponseException>(() => SseData(none));
    }
}
