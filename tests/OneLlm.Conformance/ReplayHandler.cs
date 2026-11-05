using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OneLlm.Conformance;

/// <summary>Thrown when a request has no matching unused exchange, or when exchanges are left unused.</summary>
public sealed class FixtureMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What did not match.</param>
    public FixtureMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a <see cref="RecordedFixture"/>. Each request must match an exchange that has not been
/// used yet (method, path and query, every recorded header, and the body exactly); that exchange's response is returned and the exchange
/// is used up. A request that matches nothing throws <see cref="FixtureMismatchException"/> with the first difference from the closest
/// exchange, and <see cref="AssertAllUsed"/> throws if any exchange was never requested.
/// </summary>
public sealed class ReplayHandler : HttpMessageHandler
{
    private readonly RecordedFixture _fixture;
    private readonly bool[] _used;
    private readonly List<IncomingRequest> _requests = [];
    private readonly object _gate = new();

    /// <summary>Creates a handler over a fixture.</summary>
    /// <param name="fixture">The fixture.</param>
    public ReplayHandler(RecordedFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _used = new bool[fixture.Exchanges.Count];
    }

    /// <summary>Loads a fixture file and creates a handler over it.</summary>
    /// <param name="path">The fixture file.</param>
    /// <returns>The handler.</returns>
    public static ReplayHandler FromFile(string path) => new(RecordedFixture.Load(path));

    /// <summary>The requests received so far, in order, with their bodies.</summary>
    public IReadOnlyList<IncomingRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Throws if an exchange was never requested.</summary>
    /// <exception cref="FixtureMismatchException">At least one exchange is unused.</exception>
    public void AssertAllUsed()
    {
        lock (_gate)
        {
            int[] unused = [.. Enumerable.Range(0, _used.Length).Where(i => !_used[i])];
            if (unused.Length > 0)
            {
                string list = string.Join(", ", unused.Select(i => $"#{i} {_fixture.Exchanges[i].Request.Method} {_fixture.Exchanges[i].Request.Path}"));
                throw new FixtureMismatchException($"{unused.Length} recorded exchange(s) were never requested: {list}");
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IncomingRequest incoming = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
        RecordedExchange chosen;
        lock (_gate)
        {
            _requests.Add(incoming);
            int match = -1;
            string? closest = null;
            for (int i = 0; i < _used.Length && match < 0; i++)
            {
                if (_used[i])
                {
                    continue;
                }

                string? diff = FixtureMatcher.Diff(_fixture.Exchanges[i].Request, incoming);
                if (diff is null)
                {
                    match = i;
                }
                else if (closest is null || diff.StartsWith("body", StringComparison.Ordinal))
                {
                    // Prefer reporting a body difference: it means method, path and headers matched.
                    closest = $"exchange #{i}: {diff}";
                }
            }

            if (match < 0)
            {
                throw new FixtureMismatchException($"No unused recorded exchange matches {incoming.Method} {incoming.Path}. Closest: {closest ?? "none left"}");
            }

            _used[match] = true;
            chosen = _fixture.Exchanges[match];
        }

        return BuildResponse(chosen.Response, request);
    }

    /// <summary>Reads a request into the form the matcher compares.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The request.</returns>
    public static async Task<IncomingRequest> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(", ", h.Value);
        }

        byte[] body = [];
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
            {
                headers[h.Key] = string.Join(", ", h.Value);
            }

            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        return new IncomingRequest(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/", headers, body);
    }

    private static HttpResponseMessage BuildResponse(RecordedResponse recorded, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage((HttpStatusCode)recorded.Status) { RequestMessage = request };
        HttpContent content = recorded.Sse is { } chunks
            ? new StreamContent(new ChunkedStream([.. chunks.Select(c => Encoding.UTF8.GetBytes(c))]))
            : new ByteArrayContent(recorded.Body.ToWireBytes());
        foreach ((string name, string value) in recorded.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (recorded.Sse is not null && content.Headers.ContentType is null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        response.Content = content;
        return response;
    }
}

/// <summary>A read-only stream that returns one recorded chunk per read, so a parser sees the same buffer boundaries the recording had.</summary>
internal sealed class ChunkedStream : Stream
{
    private readonly byte[][] _chunks;
    private int _chunk;
    private int _offset;

    public ChunkedStream(byte[][] chunks) => _chunks = chunks;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_chunk < _chunks.Length && _offset >= _chunks[_chunk].Length)
        {
            _chunk++;
            _offset = 0;
        }

        if (_chunk >= _chunks.Length)
        {
            return 0;
        }

        byte[] current = _chunks[_chunk];
        int n = Math.Min(buffer.Length, current.Length - _offset);
        current.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return Read(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
