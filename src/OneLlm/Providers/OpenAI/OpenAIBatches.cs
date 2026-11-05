using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// The Batch API: requests are written as a JSONL file, uploaded with purpose <c>batch</c>, run as one batch against one endpoint, polled, and read
/// back from the output and error files (Batch API guide and Batches, Files references).
/// </summary>
internal sealed class OpenAIBatches(OpenAIWire wire)
{
    // A fixed multipart boundary keeps the upload bytes the same from run to run, which recorded fixtures rely on.
    private const string Boundary = "onellm-batch-upload";
    private const int MaxJsonBytes = 8 * 1024 * 1024;

    public async Task<BatchJob> SubmitAsync(BatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("A batch needs at least one item.", nameof(request));
        }

        bool embeddings = request.Items[0].Embedding is not null;
        string endpoint = wire.BatchEndpointPath(embeddings);
        var jsonl = new ArrayBufferWriter<byte>(4096);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BatchItem item in request.Items)
        {
            if (!seen.Add(item.CustomId))
            {
                throw new ArgumentException($"Batch custom id '{item.CustomId}' is used twice.", nameof(request));
            }

            if ((item.Embedding is not null) != embeddings || (item.Chat is null) == (item.Embedding is null))
            {
                throw new ArgumentException("A batch holds chat requests or embeddings requests, not both, and each item holds exactly one.", nameof(request));
            }

            ReadOnlyMemory<byte> body = embeddings ? wire.WriteEmbeddings(item.Embedding!) : wire.WriteChat(item.Chat!, stream: false);
            WriteLine(jsonl, item.CustomId, endpoint, body.Span);
        }

        string fileId = await UploadAsync(jsonl.WrittenMemory, cancellationToken).ConfigureAwait(false);

        var create = new ArrayBufferWriter<byte>(256);
        using (var body = new BodyWriter(request.ExtraParameters))
        {
            Utf8JsonWriter w = body.Writer;
            if (body.Field("input_file_id"))
            {
                w.WriteStringValue(fileId);
            }

            if (body.Field("endpoint"))
            {
                w.WriteStringValue(endpoint);
            }

            if (body.Field("completion_window"))
            {
                w.WriteStringValue("24h");
            }

            create.Write(body.Finish().Span);
        }

        using HttpResponseMessage response = await wire.PostAsync("batches", null, create.WrittenMemory, stream: false, cancellationToken).ConfigureAwait(false);
        return ParseJob(await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<BatchJob> GetAsync(string batchId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await wire.GetAsync("batches/" + Uri.EscapeDataString(batchId), cancellationToken).ConfigureAwait(false);
        return ParseJob(await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<BatchJob> CancelAsync(string batchId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await wire.PostAsync("batches/" + Uri.EscapeDataString(batchId) + "/cancel", null, ReadOnlyMemory<byte>.Empty, stream: false, cancellationToken).ConfigureAwait(false);
        return ParseJob(await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<BatchResultItem> ResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpResponseMessage info = await wire.GetAsync("batches/" + Uri.EscapeDataString(batchId), cancellationToken).ConfigureAwait(false);
        JsonObject batch = await ReadJsonAsync(info, cancellationToken).ConfigureAwait(false);
        string? outputFile = ResponsesDecoder.Str(batch["output_file_id"]);
        string? errorFile = ResponsesDecoder.Str(batch["error_file_id"]);
        string endpoint = ResponsesDecoder.Str(batch["endpoint"]) ?? "";
        if (outputFile is null && errorFile is null)
        {
            throw new BadRequestException(wire.Provider, $"batch '{batchId}' has no results yet (status {ResponsesDecoder.Str(batch["status"])}).", null);
        }

        foreach (string? fileId in new[] { outputFile, errorFile })
        {
            if (fileId is null)
            {
                continue;
            }

            using HttpResponseMessage content = await wire.GetAsync("files/" + Uri.EscapeDataString(fileId) + "/content", cancellationToken).ConfigureAwait(false);
            Stream stream = await content.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var lines = new LineReader(stream, wire.Provider);
                while (await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (lines.Line.IsEmpty)
                    {
                        continue;
                    }

                    yield return ParseResultLine(lines.Line, endpoint);
                }
            }
        }
    }

    private BatchResultItem ParseResultLine(ReadOnlyMemory<byte> line, string endpoint)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(line.Span) as JsonObject ?? throw new MalformedResponseException(wire.Provider, "a batch result line was not a JSON object.");
        }
        catch (JsonException)
        {
            throw new MalformedResponseException(wire.Provider, "a batch result line was not valid JSON.");
        }

        string customId = ResponsesDecoder.Str(root["custom_id"]) ?? "";
        if (root["error"] is JsonObject lineError)
        {
            return new BatchResultItem { CustomId = customId, Error = OpenAIErrors.FromStreamError(wire.Provider, ResponsesDecoder.Str(lineError["code"]), ResponsesDecoder.Str(lineError["message"]), null) };
        }

        JsonObject? response = root["response"] as JsonObject;
        long status = ResponsesDecoder.Long(response?["status_code"]);
        JsonObject? body = response?["body"] as JsonObject;
        if (status is < 200 or >= 300 || body is null)
        {
            JsonObject? error = body?["error"] as JsonObject;
            ProviderException failure = OpenAIErrors.Create(wire.Provider, status is >= 100 and < 600 ? (int)status : null, ResponsesDecoder.Str(error?["message"]) ?? $"the request failed with status {status}.", ResponsesDecoder.Str(error?["code"]), ResponsesDecoder.Str(error?["type"]), null);
            return new BatchResultItem { CustomId = customId, Error = failure };
        }

        try
        {
            if (endpoint.EndsWith("/embeddings", StringComparison.Ordinal))
            {
                EmbeddingResponse embedding = wire.ParseEmbeddings(Encoding.UTF8.GetBytes(body.ToJsonString()), "");
                return new BatchResultItem { CustomId = customId, Embedding = embedding };
            }

            return new BatchResultItem { CustomId = customId, Chat = wire.DecodeChatObject(body, "") };
        }
        catch (ProviderException e)
        {
            // One failed item must not hide the others.
            return new BatchResultItem { CustomId = customId, Error = e };
        }
    }

    private static void WriteLine(ArrayBufferWriter<byte> buffer, string customId, string endpoint, ReadOnlySpan<byte> body)
    {
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteString("custom_id", customId);
        writer.WriteString("method", "POST");
        writer.WriteString("url", endpoint);
        writer.WritePropertyName("body");
        writer.WriteRawValue(body, skipInputValidation: true);
        writer.WriteEndObject();
        writer.Flush();
        buffer.Write("\n"u8);
    }

    private async Task<string> UploadAsync(ReadOnlyMemory<byte> jsonl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await wire.Http.SendAsync(
            () =>
            {
                var content = new MultipartFormDataContent(Boundary);
                // The boundary is written without quotes so the part bytes are exactly the ones in the recorded request.
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + Boundary);
                content.Add(new StringContent("batch"), "purpose");
                var file = new ReadOnlyMemoryContent(jsonl);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
                content.Add(file, "file", "batch.jsonl");
                var message = new HttpRequestMessage(HttpMethod.Post, wire.Endpoint.GetUri("files", null)) { Content = content };
                wire.Endpoint.Authenticate(message);
                return message;
            },
            cancellationToken).ConfigureAwait(false);
        JsonObject file = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ResponsesDecoder.Str(file["id"]) ?? throw new MalformedResponseException(wire.Provider, "the file upload response had no id.");
    }

    private async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[] bytes = await wire.Http.ReadBodyAsync(response, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(bytes) as JsonObject ?? throw new MalformedResponseException(wire.Provider, "the response was not a JSON object.");
        }
        catch (JsonException)
        {
            throw new MalformedResponseException(wire.Provider, "the response was not valid JSON.");
        }
    }

    private static BatchJob ParseJob(JsonObject b)
    {
        JsonObject? counts = b["request_counts"] as JsonObject;
        return new BatchJob
        {
            Id = ResponsesDecoder.Str(b["id"]) ?? "",
            Status = ResponsesDecoder.Str(b["status"]) switch
            {
                "validating" => BatchStatus.Validating,
                "in_progress" or "finalizing" => BatchStatus.InProgress,
                "completed" => BatchStatus.Completed,
                "failed" => BatchStatus.Failed,
                "cancelling" => BatchStatus.Cancelling,
                "cancelled" => BatchStatus.Cancelled,
                "expired" => BatchStatus.Expired,
                _ => BatchStatus.InProgress,
            },
            Total = counts is null ? null : (int)ResponsesDecoder.Long(counts["total"]),
            Succeeded = counts is null ? null : (int)ResponsesDecoder.Long(counts["completed"]),
            Failed = counts is null ? null : (int)ResponsesDecoder.Long(counts["failed"]),
            CreatedAt = b["created_at"] is JsonValue c && c.TryGetValue(out long created) ? DateTimeOffset.FromUnixTimeSeconds(created) : null,
            ExpiresAt = b["expires_at"] is JsonValue e && e.TryGetValue(out long expires) ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
        };
    }
}

/// <summary>Reads newline-separated records (a batch output file) from a stream in pooled buffers, one line at a time.</summary>
internal sealed class LineReader : IDisposable
{
    private const int MaxLine = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly string _provider;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _start;
    private int _end;
    private int _lineLength;
    private bool _eof;

    public LineReader(Stream stream, string provider)
    {
        _stream = stream;
        _provider = provider;
    }

    /// <summary>The last line read, without its line ending. Valid until the next read.</summary>
    public ReadOnlyMemory<byte> Line => new(_buffer, _start - _lineLength - _terminator, _lineLength);

    private int _terminator;

    public async ValueTask<bool> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                int length = newline;
                _terminator = 1;
                if (length > 0 && _buffer[_start + length - 1] == (byte)'\r')
                {
                    length--;
                    _terminator = 2;
                }

                _lineLength = length;
                _start += newline + 1;
                return true;
            }

            if (_eof)
            {
                if (_end > _start)
                {
                    _lineLength = _end - _start;
                    _terminator = 0;
                    _start = _end;
                    return true;
                }

                return false;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                if (_buffer.Length >= MaxLine)
                {
                    throw new MalformedResponseException(_provider, "a result line is larger than the limit.");
                }

                byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = bigger;
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

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
