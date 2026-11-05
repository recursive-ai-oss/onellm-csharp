using System.Buffers;
using System.Globalization;
using System.Text.Json;
using OneLlm.Messages;

namespace OneLlm.Records;

/// <summary>Receives records. An implementation appends and never rewrites. A sink that throws never fails a run; see <see cref="RecordSinks"/>.</summary>
public interface IRecordSink
{
    /// <summary>Writes one record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Several sinks behind one. Writes go to every sink in registration order. A sink that throws is skipped for that record, the
/// failure is reported to <see cref="OnError"/>, and the others still get the record: a broken log must not stop a run.
/// </summary>
public sealed class RecordSinks : IRecordSink
{
    private IRecordSink[] _sinks = [];

    /// <summary>Called with the sink and the exception when a sink fails. Exceptions thrown by this callback are swallowed. Null to ignore failures.</summary>
    public Action<IRecordSink, Exception>? OnError { get; set; }

    /// <summary>Adds a sink.</summary>
    /// <param name="sink">The sink.</param>
    public void Add(IRecordSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sinks = [.. _sinks, sink];
    }

    /// <summary>True when no sink is registered, so the caller can skip building records at all.</summary>
    public bool IsEmpty => _sinks.Length == 0;

    /// <inheritdoc />
    public async ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        foreach (IRecordSink sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                try
                {
                    OnError?.Invoke(sink, e);
                }
                catch (Exception)
                {
                    // A failing error callback must not turn a log failure into a run failure.
                }
            }
        }
    }
}

/// <summary>Hands every record to listeners in the same process.</summary>
public sealed class EventRecordSink : IRecordSink
{
    /// <summary>Raised for each record, on the writer's thread. A listener that throws is reported to the <see cref="RecordSinks"/> that holds this sink.</summary>
    public event Action<AuditRecord>? Written;

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        Written?.Invoke(record);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Appends records as JSON lines, one file per instance: <c>&lt;directory&gt;/&lt;instanceId&gt;.jsonl</c>. Records with no instance go to
/// <c>_runtime.jsonl</c>. Each line is <c>{"kind":...,"time":...,"instanceId":...,"messageId":...,...payload}</c>; read them back with <see cref="RecordReader"/>.
/// </summary>
public sealed class JsonlRecordSink : IRecordSink
{
    private readonly string _directory;
    private readonly object _gate = new();

    /// <summary>Creates a sink over a directory, which is created on the first write.</summary>
    /// <param name="directory">The directory that holds the files.</param>
    public JsonlRecordSink(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>Serializes a record to its one-line JSON form, without a trailing newline. Other sinks store this text so every sink reads back the same shape.</summary>
    /// <param name="record">The record.</param>
    /// <returns>A JSON object.</returns>
    public static string ToJsonLine(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            RecordJson.Write(w, record);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The path of the file for an instance (or the runtime file for an empty id).</summary>
    /// <param name="instanceId">The instance id, or empty.</param>
    /// <returns>The file path.</returns>
    public string PathOf(string instanceId)
    {
        if (instanceId.Length != 0)
        {
            Instances.InstanceIds.Validate(instanceId);
        }

        return Path.Combine(_directory, (instanceId.Length == 0 ? "_runtime" : instanceId) + ".jsonl");
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            RecordJson.Write(w, record);
        }

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        string path = PathOf(record.InstanceId);
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(buffer.WrittenSpan);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>One line of a record file, read back.</summary>
/// <param name="Kind">The record kind.</param>
/// <param name="Time">When it happened.</param>
/// <param name="InstanceId">The instance, or empty.</param>
/// <param name="MessageId">The message, or null.</param>
/// <param name="Json">The whole line as a JSON object, including the payload fields of the kind.</param>
public sealed record RecordEntry(string Kind, DateTimeOffset Time, string InstanceId, string? MessageId, JsonElement Json);

/// <summary>Reads record files written by <see cref="JsonlRecordSink"/>.</summary>
public static class RecordReader
{
    /// <summary>Parses one record line, as written by <see cref="JsonlRecordSink"/> or <see cref="JsonlRecordSink.ToJsonLine"/>.</summary>
    /// <param name="line">One JSON object.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="JsonException">The line is not valid JSON or lacks <c>kind</c> or <c>time</c>.</exception>
    public static RecordEntry ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement.Clone();
        return new RecordEntry(
            root.GetProperty("kind").GetString() ?? "",
            DateTimeOffset.Parse(root.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            root.TryGetProperty("instanceId", out JsonElement i) ? i.GetString() ?? "" : "",
            root.TryGetProperty("messageId", out JsonElement m) ? m.GetString() : null,
            root);
    }

    /// <summary>Reads every line of a record file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The entries in file order. A missing file gives an empty list.</returns>
    /// <exception cref="JsonException">A line is not valid JSON.</exception>
    public static List<RecordEntry> ReadFile(string path)
    {
        var list = new List<RecordEntry>();
        if (!File.Exists(path))
        {
            return list;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            list.Add(ParseLine(line));
        }

        return list;
    }
}

internal static class RecordJson
{
    public static void Write(Utf8JsonWriter w, AuditRecord r)
    {
        w.WriteStartObject();
        w.WriteString("kind", r.Kind);
        w.WriteString("time", r.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
        if (r.InstanceId.Length != 0)
        {
            w.WriteString("instanceId", r.InstanceId);
        }

        if (r.MessageId is not null)
        {
            w.WriteString("messageId", r.MessageId);
        }

        switch (r)
        {
            case RunRecord run:
                w.WriteString("event", run.Event);
                WriteOptional(w, "state", run.State);
                WriteOptional(w, "model", run.Model);
                break;
            case MessageRecord m:
                w.WritePropertyName("message");
                JsonSerializer.Serialize(w, m.Message, OneLlmJsonContext.Default.Message);
                break;
            case InferenceRecord inf:
                WriteOptional(w, "provider", inf.Provider);
                w.WriteString("operation", inf.Operation);
                w.WriteNumber("durationMs", (long)inf.Duration.TotalMilliseconds);
                WriteOptional(w, "finishReason", inf.FinishReason);
                if (inf.Usage is not null)
                {
                    w.WritePropertyName("usage");
                    JsonSerializer.Serialize(w, inf.Usage, OneLlmJsonContext.Default.Usage);
                }

                break;
            case ToolCallRecord t:
                w.WritePropertyName("call");
                WriteToolCall(w, t.Call);
                w.WriteNumber("durationMs", (long)t.Duration.TotalMilliseconds);
                break;
            case TaskCompletionRecord tc:
                w.WriteString("tool", tc.Tool);
                WriteOptional(w, "result", tc.Result);
                break;
            case ApprovalRecord a:
                w.WriteString("toolCallId", a.ToolCallId);
                w.WriteString("tool", a.Tool);
                w.WriteString("decision", a.Decision);
                WriteOptional(w, "by", a.By);
                break;
            case CompactionRecord c:
                w.WriteString("strategy", c.Compaction.Strategy);
                WriteOptional(w, "at", c.Compaction.At);
                WriteOptional(w, "summaryMessageId", c.Compaction.SummaryMessageId);
                if (c.Compaction.Cost is decimal cost)
                {
                    w.WriteNumber("cost", cost);
                }

                if (c.Compaction.ReplacedMessageIds is { } ids)
                {
                    w.WriteStartArray("replacedMessageIds");
                    foreach (string id in ids)
                    {
                        w.WriteStringValue(id);
                    }

                    w.WriteEndArray();
                }

                break;
            case HookRecord h:
                w.WriteString("point", h.Point);
                w.WriteString("hook", h.Hook);
                w.WriteString("decision", h.Decision);
                WriteOptional(w, "reason", h.Reason);
                break;
            case ErrorRecord e:
                w.WriteString("errorType", e.ErrorType);
                w.WriteString("message", e.Message);
                WriteOptional(w, "where", e.Where);
                break;
            case RawTurnRecord raw:
                w.WriteString("request", raw.Request);
                w.WriteString("response", raw.Response);
                break;
        }

        w.WriteEndObject();
    }

    // A tool call has no serializer of its own; it is written as the one tool call of a message, which carries every field.
    private static void WriteToolCall(Utf8JsonWriter w, ToolCall call)
    {
        var holder = new Message { ToolCalls = [call] };
        using JsonDocument doc = JsonSerializer.SerializeToDocument(holder, OneLlmJsonContext.Default.Message);
        doc.RootElement.GetProperty("toolCalls")[0].WriteTo(w);
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }
}
