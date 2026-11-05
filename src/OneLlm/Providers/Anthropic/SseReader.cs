using System.Buffers;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// Reads server-sent events from a stream (WHATWG HTML Living Standard, section 9.2) into pooled buffers, one event at a time, without
/// making a string of any line. Only the <c>data</c> field is kept: Anthropic repeats the event name inside the JSON.
/// </summary>
/// <remarks>
/// Lines end with LF, CR or CRLF, possibly split across reads. Comment lines (starting with a colon, such as keep-alives) are ignored. Several
/// <c>data</c> lines in one event are joined with LF. An event that is cut off before its blank line is dropped, as the standard says.
/// Buffers grow up to <see cref="MaxEventBytes"/>; a longer line or event is a malformed response.
/// </remarks>
internal sealed class SseReader : IDisposable
{
    /// <summary>The largest line or event accepted: a web search result block can be large, but nothing legitimate is this big.</summary>
    public const int MaxEventBytes = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private int _start;
    private int _end;
    private bool _eof;
    private bool _skipLf;
    private bool _checkedBom;
    private byte[] _data = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private int _dataLength;
    private bool _hasData;

    public SseReader(Stream stream) => _stream = stream;

    /// <summary>The data of the event last returned by <see cref="ReadAsync"/>, valid until the next call.</summary>
    public ReadOnlyMemory<byte> Data => _data.AsMemory(0, _dataLength);

    /// <summary>Reads the next event that has data.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False at the end of the stream.</returns>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        _dataLength = 0;
        _hasData = false;
        while (true)
        {
            if (TryTakeLine(out int lineStart, out int lineLength))
            {
                if (lineLength == 0)
                {
                    if (_hasData)
                    {
                        return true;
                    }

                    continue;
                }

                ProcessLine(_buffer.AsSpan(lineStart, lineLength));
                continue;
            }

            if (_eof)
            {
                // A last line without a terminator still counts as a line; an event without its blank line does not dispatch.
                if (_end > _start)
                {
                    ProcessLine(_buffer.AsSpan(_start, _end - _start));
                    _start = _end;
                }

                _hasData = false;
                _dataLength = 0;
                return false;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryTakeLine(out int lineStart, out int lineLength)
    {
        if (_skipLf && _start < _end)
        {
            if (_buffer[_start] == (byte)'\n')
            {
                _start++;
            }

            _skipLf = false;
        }

        ReadOnlySpan<byte> pending = _buffer.AsSpan(_start, _end - _start);
        int i = pending.IndexOfAny((byte)'\n', (byte)'\r');
        if (i < 0)
        {
            if (pending.Length > MaxEventBytes)
            {
                throw new MalformedResponseException(AnthropicErrors.Provider, "a stream line is longer than the limit.");
            }

            lineStart = lineLength = 0;
            return false;
        }

        lineStart = _start;
        lineLength = i;
        bool cr = pending[i] == (byte)'\r';
        _start += i + 1;
        if (cr)
        {
            if (_start < _end)
            {
                if (_buffer[_start] == (byte)'\n')
                {
                    _start++;
                }
            }
            else
            {
                // The LF of a CRLF may arrive in the next read.
                _skipLf = true;
            }
        }

        return true;
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start == _end)
        {
            _start = _end = 0;
        }
        else if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                if (_buffer.Length >= MaxEventBytes)
                {
                    throw new MalformedResponseException(AnthropicErrors.Provider, "a stream line is longer than the limit.");
                }

                byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = bigger;
            }
        }

        int read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return;
        }

        _end += read;
        if (!_checkedBom && _end - _start >= 3)
        {
            _checkedBom = true;
            if (_buffer[_start] == 0xEF && _buffer[_start + 1] == 0xBB && _buffer[_start + 2] == 0xBF)
            {
                _start += 3;
            }
        }
        else if (!_checkedBom && _end - _start > 0 && _buffer[_start] != 0xEF)
        {
            _checkedBom = true;
        }
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
            // event, id, retry and unknown fields do not matter to this client.
            return;
        }

        ReadOnlySpan<byte> value = colon < 0 ? default : line[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
        {
            value = value[1..];
        }

        int needed = _dataLength + value.Length + 1;
        if (needed > MaxEventBytes)
        {
            throw new MalformedResponseException(AnthropicErrors.Provider, "a stream event is larger than the limit.");
        }

        if (needed > _data.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _data.Length * 2));
            Buffer.BlockCopy(_data, 0, bigger, 0, _dataLength);
            ArrayPool<byte>.Shared.Return(_data);
            _data = bigger;
        }

        if (_hasData)
        {
            _data[_dataLength++] = (byte)'\n';
        }

        value.CopyTo(_data.AsSpan(_dataLength));
        _dataLength += value.Length;
        _hasData = true;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<byte>.Shared.Return(_data);
        _buffer = [];
        _data = [];
    }
}
