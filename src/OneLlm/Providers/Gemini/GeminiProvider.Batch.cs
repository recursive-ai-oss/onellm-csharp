using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

// Batch mode (https://ai.google.dev/gemini-api/docs/batch-api): a batch of generateContent requests becomes a long-running operation named
// batches/{id}. Its metadata holds the state and counts; when it succeeds the answers are inline in the operation or in a result file (JSON lines).
public sealed partial class GeminiProvider
{
    /// <inheritdoc />
    public async Task<BatchJob> SubmitBatchAsync(BatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new BadRequestException(Name, "a batch needs at least one request.", null);
        }

        if (request.Items.Any(i => i.Chat is null))
        {
            throw new BadRequestException(Name, "Gemini batches carry chat requests; embedding batches are not supported.", null);
        }

        string model = request.Items[0].Chat!.Model;
        if (request.Items.Any(i => i.Chat!.Model != model))
        {
            throw new BadRequestException(Name, "all requests of a Gemini batch must use the same model.", null);
        }

        if (request.Items.Select(i => i.CustomId).Distinct(StringComparer.Ordinal).Count() != request.Items.Count)
        {
            throw new BadRequestException(Name, "the custom ids of a batch must be unique.", null);
        }

        ModelSpec? spec = SpecFor(model);
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("batch");
            if (request.ExtraParameters?["displayName"]?.GetValue<string>() is { Length: > 0 } display)
            {
                w.WriteString("displayName", display);
            }

            w.WriteStartObject("inputConfig");
            w.WriteStartObject("requests");
            w.WriteStartArray("requests");
            foreach (BatchItem item in request.Items)
            {
                w.WriteStartObject();
                w.WritePropertyName("request");
                w.WriteRawValue(_writer.GenerateContent(item.Chat!, spec), skipInputValidation: true);
                w.WriteStartObject("metadata");
                w.WriteString("key", item.CustomId);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Post, _endpoint.Model(model, "batchGenerateContent"), buffer.WrittenSpan.ToArray(), cancellationToken).ConfigureAwait(false);
        return ToJob(doc.RootElement);
    }

    /// <inheritdoc />
    public async Task<BatchJob> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Get, _endpoint.Resource(BatchName(batchId)), null, cancellationToken).ConfigureAwait(false);
        return ToJob(doc.RootElement);
    }

    /// <inheritdoc />
    public async Task<BatchJob> CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        string name = BatchName(batchId);
        (await SendForJsonAsync(HttpMethod.Post, _endpoint.Resource(name + ":cancel"), "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false)).Dispose();

        return await GetBatchAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BatchResultItem> GetBatchResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string name = BatchName(batchId);
        // The operation is read once and its answers copied out, so no document is open across the yields.
        var items = new List<BatchResultItem>();
        string? file = null;
        using (JsonDocument doc = await SendForJsonAsync(HttpMethod.Get, _endpoint.Resource(name), null, cancellationToken).ConfigureAwait(false))
        {
            BatchJob job = ToJob(doc.RootElement);
            if (job.Status != BatchStatus.Completed)
            {
                throw new BadRequestException(Name, $"the batch is {job.Status}, not completed, so it has no results yet.", null);
            }

            JsonElement holder = Holder(doc.RootElement);
            if (holder.ValueKind == JsonValueKind.Object)
            {
                file = Str(holder, "responsesFile"u8) ?? Str(holder, "fileName"u8);
                bool has = holder.TryGetProperty("inlinedResponses"u8, out JsonElement inlined);
                if (has && inlined.ValueKind == JsonValueKind.Object && inlined.TryGetProperty("inlinedResponses"u8, out JsonElement nested))
                {
                    inlined = nested;
                }

                if (has && inlined.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in inlined.EnumerateArray())
                    {
                        items.Add(ToItem(Key(entry), entry));
                    }
                }
            }
        }

        foreach (BatchResultItem item in items)
        {
            yield return item;
        }

        if (items.Count == 0 && file is not null)
        {
            using HttpResponseMessage response = await SendHttpAsync(HttpMethod.Get, _endpoint.Download(file), null, cancellationToken).ConfigureAwait(false);
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                BatchResultItem item;
                using (JsonDocument entry = ParseDocument(Encoding.UTF8.GetBytes(line)))
                {
                    item = ToItem(Key(entry.RootElement), entry.RootElement);
                }

                yield return item;
            }
        }
    }

    // The operation's result lives under "response" (REST) or "dest" (the batch resource); both are read.
    private static JsonElement Holder(JsonElement root)
    {
        if (root.TryGetProperty("response"u8, out JsonElement r) && r.ValueKind == JsonValueKind.Object)
        {
            return r;
        }

        return root.TryGetProperty("dest"u8, out JsonElement d) ? d : default;
    }

    private static string Key(JsonElement entry)
    {
        if (entry.TryGetProperty("metadata"u8, out JsonElement m) && Str(m, "key"u8) is { } k)
        {
            return k;
        }

        return Str(entry, "key"u8) ?? "";
    }

    private BatchResultItem ToItem(string key, JsonElement entry)
    {
        if (entry.TryGetProperty("error"u8, out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            return new BatchResultItem { CustomId = key, Error = GeminiErrors.FromErrorElement(Name, error) };
        }

        if (!entry.TryGetProperty("response"u8, out JsonElement response))
        {
            return new BatchResultItem { CustomId = key, Error = new MalformedResponseException(Name, "a batch result has neither a response nor an error.") };
        }

        try
        {
            return new BatchResultItem { CustomId = key, Chat = ToResponse(Encoding.UTF8.GetBytes(response.GetRawText()), "") };
        }
        catch (ProviderException e)
        {
            // A blocked prompt or a malformed answer belongs to one request, not to the whole batch.
            return new BatchResultItem { CustomId = key, Error = e };
        }
    }

    private static string BatchName(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return id.StartsWith("batches/", StringComparison.Ordinal) ? id : "batches/" + id;
    }

    private BatchJob ToJob(JsonElement op)
    {
        string name = Str(op, "name"u8) ?? throw new MalformedResponseException(Name, "a batch answer has no name.");
        JsonElement meta = op.TryGetProperty("metadata"u8, out JsonElement m) ? m : default;
        string state = Str(meta, "state"u8) ?? Str(op, "state"u8) ?? "";
        bool done = op.TryGetProperty("done"u8, out JsonElement d) && d.ValueKind == JsonValueKind.True;
        JsonElement stats = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("batchStats"u8, out JsonElement s) ? s : default;
        DateTimeOffset? created = Str(meta, "createTime"u8) is { } c && DateTimeOffset.TryParse(c, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset t) ? t : null;
        return new BatchJob
        {
            Id = name,
            Status = MapState(state, done, op),
            Total = Int(stats, "requestCount"u8),
            Succeeded = Int(stats, "successfulRequestCount"u8),
            Failed = Int(stats, "failedRequestCount"u8),
            CreatedAt = created,
        };
    }

    // States are JOB_STATE_* in the SDK documents and BATCH_STATE_* on the wire; only the tail is compared.
    private static BatchStatus MapState(string state, bool done, JsonElement op)
    {
        string tail = state.Contains("STATE_", StringComparison.Ordinal) ? state[(state.LastIndexOf("STATE_", StringComparison.Ordinal) + 6)..] : state;
        return tail switch
        {
            "PENDING" or "QUEUED" => BatchStatus.Validating,
            "RUNNING" => BatchStatus.InProgress,
            "SUCCEEDED" => BatchStatus.Completed,
            "FAILED" => BatchStatus.Failed,
            "CANCELLING" => BatchStatus.Cancelling,
            "CANCELLED" => BatchStatus.Cancelled,
            "EXPIRED" => BatchStatus.Expired,
            // No state word: an operation that is done with an error failed, one that is done without is complete.
            _ => done ? (op.TryGetProperty("error"u8, out _) ? BatchStatus.Failed : BatchStatus.Completed) : BatchStatus.InProgress,
        };
    }
}
