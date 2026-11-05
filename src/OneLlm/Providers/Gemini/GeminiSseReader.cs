using System.Buffers;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// Reads server-sent events (WHATWG HTML Living Standard, section 9.2) from a stream into reused buffers. Each call to <see cref="ReadAsync"/>
/// yields the <c>data</c> of the next event as UTF-8 bytes with no string made for it; the bytes stay valid until the next call.
/// </summary>
/// <remarks>
/// Lines end in LF, CR or CRLF, a CR at the end of a read waits for the next read to see whether an LF follows. Comment lines (starting with a
/// colon), and the <c>event</c>, <c>id</c> and <c>retry</c> fields are ignored: Gemini names no events and puts everything in <c>data</c>. Several
/// <c>data</c> lines of one event are joined with LF. An event with no data lines is not reported. A final event the stream ends in the middle of
/// (no blank line) is dropped, as the standard says.
/// </remarks>
internal sealed class GeminiSseReader : IDisposable
{
    private const int MaxEventBytes = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _eof;
    private bool _sawCr;
    private bool _first = true;
    private readonly ArrayBufferWriter<byte> _data = new(4096);
    private bool _hasData;

    public GeminiSseReader(Stream stream, int bufferSize = 16 * 1024)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>The data of the event last returned by <see cref="ReadAsync"/>.</summary>
    public ReadOnlyMemory<byte> Data => _data.WrittenMemory;

    /// <summary>Reads the next event.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when an event was read; false at the end of the stream.</returns>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        _data.ResetWrittenCount();
        _hasData = false;
        while (true)
        {
            int lineEnd = FindLineEnd(out int terminatorLength);
            if (lineEnd < 0)
            {
                if (_eof)
                {
                    return false;
                }

                await FillAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            ReadOnlySpan<byte> line = _buffer.AsSpan(_start, lineEnd - _start);
            if (_first)
            {
                // A leading byte order mark is not part of the first line.
                if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
                {
                    line = line[3..];
                }

                _first = false;
            }

            bool dispatch = line.IsEmpty;
            if (!dispatch)
            {
                ProcessLine(line);
            }

            _start = lineEnd + terminatorLength;
            if (dispatch && _hasData)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        byte[] b = _buffer;
        _buffer = [];
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    // Finds the end of the next complete line in the buffer. A CR at the very end of the buffered bytes is not complete yet, as an LF may follow.
    private int FindLineEnd(out int terminatorLength)
    {
        ReadOnlySpan<byte> span = _buffer.AsSpan(_start, _end - _start);
        int i;
        if (_sawCr && span.Length > 0)
        {
            // The LF of a CRLF whose CR ended the previous read belongs to a line already reported.
            if (span[0] == (byte)'\n')
            {
                _start++;
                span = span[1..];
            }

            _sawCr = false;
        }

        i = span.IndexOfAny((byte)'\n', (byte)'\r');
        if (i < 0)
        {
            terminatorLength = 0;
            if (span.Length > MaxEventBytes)
            {
                throw new InvalidDataException("A server-sent event line is too long.");
            }

            return -1;
        }

        if (span[i] == (byte)'\n')
        {
            terminatorLength = 1;
            return _start + i;
        }

        if (i + 1 < span.Length)
        {
            terminatorLength = span[i + 1] == (byte)'\n' ? 2 : 1;
            return _start + i;
        }

        if (_eof)
        {
            terminatorLength = 1;
            return _start + i;
        }

        // CR at the end of the buffered bytes: take the line now and skip an LF that may open the next read.
        terminatorLength = 1;
        _sawCr = true;
        return _start + i;
    }

    private void ProcessLine(ReadOnlySpan<byte> line)
    {
        if (line[0] == (byte)':')
        {
            return;
        }

        int colon = line.IndexOf((byte)':');
        ReadOnlySpan<byte> field = colon < 0 ? line : line[..colon];
        if (!field.SequenceEqual("data"u8))
        {
            return;
        }

        ReadOnlySpan<byte> value = colon < 0 ? default : line[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
        {
            value = value[1..];
        }

        if (_hasData)
        {
            _data.Write("\n"u8);
        }

        if (_data.WrittenCount + value.Length > MaxEventBytes)
        {
            throw new InvalidDataException("A server-sent event is too large.");
        }

        _data.Write(value);
        _hasData = true;
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0 && _start == _end)
        {
            _start = 0;
            _end = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = bigger;
            }
        }

        int n = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (n == 0)
        {
            _eof = true;
        }
        else
        {
            _end += n;
        }
    }
}
