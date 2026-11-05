using System.Buffers;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// Reads server-sent events (WHATWG HTML Living Standard, section 9.2) from a stream into pooled buffers. Only the <c>data</c> field is kept:
/// both OpenAI streams carry the event type inside the JSON. Comment lines (used as pings), <c>event</c>, <c>id</c> and <c>retry</c> are skipped.
/// </summary>
internal sealed class SseReader : IDisposable
{
    private const int InitialBuffer = 16 * 1024;

    // A single event or line larger than this is treated as a broken stream rather than grown without bound.
    private const int MaxEventBytes = 32 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly string _provider;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _eof;
    private byte[] _data;
    private int _dataLength;
    private bool _hasData;

    public SseReader(Stream stream, string provider)
    {
        _stream = stream;
        _provider = provider;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBuffer);
        _data = ArrayPool<byte>.Shared.Rent(InitialBuffer);
    }

    /// <summary>The joined <c>data</c> lines of the last event read. Valid until the next call to <see cref="ReadEventAsync"/>.</summary>
    public ReadOnlyMemory<byte> Data => new(_data, 0, _dataLength);

    /// <summary>Reads the next event that has data.</summary>
    /// <returns>False at the end of the stream.</returns>
    public async ValueTask<bool> ReadEventAsync(CancellationToken cancellationToken)
    {
        _dataLength = 0;
        _hasData = false;
        while (true)
        {
            // Find the end of the next line in what is buffered.
            ReadOnlySpan<byte> window = _buffer.AsSpan(_start, _end - _start);
            int eol = window.IndexOfAny((byte)'\n', (byte)'\r');
            if (eol < 0)
            {
                if (_eof)
                {
                    // A final line with no terminator still counts; an unfinished event at end of stream is dropped, as the standard says.
                    if (window.Length > 0)
                    {
                        ProcessLine(window);
                        _start = _end;
                    }

                    return false;
                }

                await FillAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (window[eol] == (byte)'\r' && eol == window.Length - 1 && !_eof)
            {
                // A CR at the very end may be the first half of CRLF; read more before deciding.
                await FillAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            ReadOnlySpan<byte> line = window[..eol];
            int consumed = eol + 1;
            if (window[eol] == (byte)'\r' && consumed < window.Length && window[consumed] == (byte)'\n')
            {
                consumed++;
            }

            bool dispatch = line.IsEmpty && _hasData;
            if (!line.IsEmpty)
            {
                ProcessLine(line);
            }

            _start += consumed;
            if (dispatch)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<byte>.Shared.Return(_data);
        _buffer = [];
        _data = [];
    }

    private void ProcessLine(ReadOnlySpan<byte> line)
    {
        if (line[0] == (byte)':')
        {
            return;
        }

        int colon = line.IndexOf((byte)':');
        ReadOnlySpan<byte> name = colon < 0 ? line : line[..colon];
        if (!name.SequenceEqual("data"u8))
        {
            return;
        }

        ReadOnlySpan<byte> value = colon < 0 ? default : line[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
        {
            value = value[1..];
        }

        // Several data lines of one event are joined with a line feed.
        int needed = _dataLength + value.Length + (_hasData ? 1 : 0);
        if (needed > MaxEventBytes)
        {
            throw new MalformedResponseException(_provider, "a stream event is larger than the limit.");
        }

        if (needed > _data.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _data.Length * 2));
            _data.AsSpan(0, _dataLength).CopyTo(bigger);
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

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0 && _start == _end)
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
                    throw new MalformedResponseException(_provider, "a stream line is larger than the limit.");
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
        }
        else
        {
            _end += read;
        }
    }
}
