using System.Text;

namespace OneLlm.Conformance;

/// <summary>
/// A delegating handler that records every exchange that passes through it and writes a scrubbed fixture. Nothing reaches the file before it is
/// scrubbed. Used by the recording tool against a live API with a key held only in memory.
/// </summary>
public sealed class RecordingHandler : DelegatingHandler
{
    private readonly string _outputPath;
    private readonly string[] _secrets;
    private readonly RecordedFixture _fixture;
    private readonly object _gate = new();

    /// <summary>Creates the handler.</summary>
    /// <param name="inner">The handler that makes the real calls.</param>
    /// <param name="outputPath">Where <see cref="Save"/> writes the fixture.</param>
    /// <param name="description">A sentence for the fixture.</param>
    /// <param name="secrets">The literal secrets in use (the API key), replaced wherever they appear.</param>
    public RecordingHandler(HttpMessageHandler inner, string outputPath, string? description = null, IEnumerable<string>? secrets = null)
        : base(inner)
    {
        _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        _secrets = [.. secrets ?? []];
        _fixture = new RecordedFixture { Description = description };
    }

    /// <summary>Scrubs what was recorded and writes the fixture file. The file is written only if the scrubbed fixture has no violations left.</summary>
    /// <exception cref="InvalidOperationException">The scrubbed fixture still holds something that looks like a secret.</exception>
    public void Save()
    {
        RecordedFixture copy;
        lock (_gate)
        {
            copy = RecordedFixture.Parse(_fixture.ToJson());
        }

        FixtureScrubber.Scrub(copy, _secrets);
        IReadOnlyList<string> violations = FixtureScrubber.FindViolations(copy);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The recording still holds secrets after scrubbing, so it was not written: " + string.Join("; ", violations));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_outputPath))!);
        File.WriteAllText(_outputPath, copy.ToJson(), new UTF8Encoding(false));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IncomingRequest incoming = await ReplayHandler.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var recordedRequest = new RecordedRequest { Method = incoming.Method, Path = incoming.Path };
        foreach ((string name, string value) in incoming.Headers)
        {
            if (FixtureScrubber.KeepRequestHeader(name))
            {
                recordedRequest.Headers[name] = value;
            }
        }

        string requestType = incoming.Headers.GetValueOrDefault("content-type") ?? "";
        recordedRequest.Body = RecordedBody.FromBytes(incoming.Body, requestType.Contains("json", StringComparison.OrdinalIgnoreCase));

        var recordedResponse = new RecordedResponse { Status = (int)response.StatusCode };
        foreach (var h in response.Headers.Concat(response.Content.Headers))
        {
            if (FixtureScrubber.KeepResponseHeader(h.Key))
            {
                recordedResponse.Headers[h.Key] = string.Join(", ", h.Value);
            }
        }

        string responseType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (responseType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            recordedResponse.Sse = SplitEvents(Encoding.UTF8.GetString(body));
        }
        else
        {
            recordedResponse.Body = RecordedBody.FromBytes(body, responseType.Contains("json", StringComparison.OrdinalIgnoreCase));
        }

        lock (_gate)
        {
            _fixture.Exchanges.Add(new RecordedExchange { Request = recordedRequest, Response = recordedResponse });
        }

        // Hand the caller the bytes that were read, as a fresh content with the same headers.
        var replayed = new ByteArrayContent(body);
        foreach (var h in response.Content.Headers)
        {
            replayed.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        response.Content = replayed;
        return response;
    }

    // Splits a recorded stream into one chunk per event (events end at a blank line).
    private static List<string> SplitEvents(string text)
    {
        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (end < 0)
            {
                chunks.Add(text[start..]);
                break;
            }

            chunks.Add(text[start..(end + 2)]);
            start = end + 2;
        }

        return chunks;
    }
}
