using System.Buffers.Binary;
using System.Text;

namespace OneLlm.Providers.Anthropic;

/// <summary>The value of one header of an event stream message. Only one of the value members is meaningful, as <see cref="Type"/> says.</summary>
/// <param name="Type">The wire type indicator, 0 to 9.</param>
/// <param name="Text">The value of a string header, otherwise null.</param>
/// <param name="Number">The value of a boolean (0 or 1), byte, short, integer, long or timestamp (milliseconds since the epoch) header.</param>
/// <param name="Bytes">The value of a byte array or uuid header, otherwise null.</param>
internal readonly record struct EventStreamHeader(byte Type, string? Text, long Number, byte[]? Bytes);

/// <summary>One decoded message of an <c>application/vnd.amazon.eventstream</c> stream.</summary>
internal sealed class EventStreamMessage
{
    public EventStreamMessage(IReadOnlyDictionary<string, EventStreamHeader> headers, byte[] payload)
    {
        Headers = headers;
        Payload = payload;
    }

    /// <summary>The headers by name.</summary>
    public IReadOnlyDictionary<string, EventStreamHeader> Headers { get; }

    /// <summary>The payload bytes.</summary>
    public byte[] Payload { get; }

    /// <summary>The value of a string header, or null when absent or not a string.</summary>
    public string? Text(string name) => Headers.TryGetValue(name, out EventStreamHeader h) && h.Type == 7 ? h.Text : null;
}

/// <summary>
/// Reads messages of the AWS event stream encoding from a stream: a big-endian prelude (total length, headers length, CRC-32 of those eight
/// bytes), the headers, the payload and a CRC-32 of everything before it (Amazon Event Stream Specification, "Message Format" and "Headers
/// Format"). Both checksums are verified; a mismatch ends the stream with an error, as the specification requires.
/// </summary>
internal sealed class EventStreamReader
{
    // The specification caps a payload at 24 MiB and the encoded headers at 128 KiB for services; a client must not enforce those, but must not
    // allocate whatever a corrupt length asks for either, so a message larger than both limits plus the framing is refused before it is read.
    private const int MaxMessageBytes = 24 * 1024 * 1024 + 128 * 1024 + 16 + 1024 * 1024;
    private const int PreludeBytes = 12;
    private const int OverheadBytes = 16;

    private static readonly uint[] s_crcTable = BuildCrcTable();

    private readonly Stream _stream;
    private readonly string _provider;
    private readonly byte[] _prelude = new byte[PreludeBytes];

    public EventStreamReader(Stream stream, string provider)
    {
        _stream = stream;
        _provider = provider;
    }

    /// <summary>Reads the next message.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message, or null at a clean end of the stream (between messages).</returns>
    /// <exception cref="MalformedResponseException">The stream ends inside a message, a length is impossible, or a checksum does not match.</exception>
    public async ValueTask<EventStreamMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        int got = await FillAsync(_prelude, PreludeBytes, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < PreludeBytes)
        {
            throw Bad("the event stream ended inside a message prelude.");
        }

        uint totalLength = BinaryPrimitives.ReadUInt32BigEndian(_prelude);
        uint headersLength = BinaryPrimitives.ReadUInt32BigEndian(_prelude.AsSpan(4));
        uint preludeCrc = BinaryPrimitives.ReadUInt32BigEndian(_prelude.AsSpan(8));
        if (Crc32(_prelude.AsSpan(0, 8)) != preludeCrc)
        {
            throw Bad("an event stream message prelude failed its checksum.");
        }

        if (totalLength < OverheadBytes || totalLength > MaxMessageBytes || headersLength > totalLength - OverheadBytes)
        {
            throw Bad("an event stream message has an impossible length.");
        }

        int rest = (int)totalLength - PreludeBytes;
        byte[] body = new byte[rest];
        if (await FillAsync(body, rest, cancellationToken).ConfigureAwait(false) < rest)
        {
            throw Bad("the event stream ended inside a message.");
        }

        // The message CRC covers the prelude (including its CRC) and everything up to the final four bytes.
        uint crc = Crc32Update(0xFFFFFFFFu, _prelude);
        crc = ~Crc32Update(crc, body.AsSpan(0, rest - 4));
        if (crc != BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(rest - 4)))
        {
            throw Bad("an event stream message failed its checksum.");
        }

        int headerEnd = (int)headersLength;
        Dictionary<string, EventStreamHeader> headers = ParseHeaders(body.AsSpan(0, headerEnd));
        byte[] payload = body.AsSpan(headerEnd, rest - 4 - headerEnd).ToArray();
        return new EventStreamMessage(headers, payload);
    }

    /// <summary>CRC-32 (the gzip polynomial, reflected) of a span, as the encoding's checksums use.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum.</returns>
    public static uint Crc32(ReadOnlySpan<byte> data) => ~Crc32Update(0xFFFFFFFFu, data);

    private static uint Crc32Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = s_crcTable[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private Dictionary<string, EventStreamHeader> ParseHeaders(ReadOnlySpan<byte> span)
    {
        var headers = new Dictionary<string, EventStreamHeader>(StringComparer.Ordinal);
        int i = 0;
        while (i < span.Length)
        {
            int nameLength = span[i++];
            if (nameLength == 0 || i + nameLength + 1 > span.Length)
            {
                throw Bad("an event stream header is cut off.");
            }

            string name = Encoding.UTF8.GetString(span.Slice(i, nameLength));
            i += nameLength;
            byte type = span[i++];
            EventStreamHeader value;
            switch (type)
            {
                case 0:
                    value = new EventStreamHeader(type, null, 1, null);
                    break;
                case 1:
                    value = new EventStreamHeader(type, null, 0, null);
                    break;
                case 2:
                    value = new EventStreamHeader(type, null, (sbyte)Take(span, ref i, 1)[0], null);
                    break;
                case 3:
                    value = new EventStreamHeader(type, null, BinaryPrimitives.ReadInt16BigEndian(Take(span, ref i, 2)), null);
                    break;
                case 4:
                    value = new EventStreamHeader(type, null, BinaryPrimitives.ReadInt32BigEndian(Take(span, ref i, 4)), null);
                    break;
                case 5 or 8:
                    value = new EventStreamHeader(type, null, BinaryPrimitives.ReadInt64BigEndian(Take(span, ref i, 8)), null);
                    break;
                case 6 or 7:
                    int length = BinaryPrimitives.ReadUInt16BigEndian(Take(span, ref i, 2));
                    ReadOnlySpan<byte> data = Take(span, ref i, length);
                    value = type == 7
                        ? new EventStreamHeader(type, Encoding.UTF8.GetString(data), 0, null)
                        : new EventStreamHeader(type, null, 0, data.ToArray());
                    break;
                case 9:
                    value = new EventStreamHeader(type, null, 0, Take(span, ref i, 16).ToArray());
                    break;
                default:
                    // The set of types is fixed and closed (specification, "Headers Format"), so anything else means the framing is damaged.
                    throw Bad("an event stream header has an unknown type.");
            }

            headers[name] = value;
        }

        return headers;
    }

    private ReadOnlySpan<byte> Take(ReadOnlySpan<byte> span, ref int index, int count)
    {
        if (index + count > span.Length)
        {
            throw Bad("an event stream header is cut off.");
        }

        ReadOnlySpan<byte> slice = span.Slice(index, count);
        index += count;
        return slice;
    }

    private async ValueTask<int> FillAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                // The inner exception is not kept: its text can carry what a server or proxy sent back.
                throw new TransportException(_provider, "the stream broke: " + e.Message);
            }

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private MalformedResponseException Bad(string message) => new(_provider, message);
}
