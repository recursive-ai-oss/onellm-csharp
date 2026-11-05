using System.Diagnostics;
using System.Diagnostics.Metrics;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;
using OneLlm.Telemetry;

namespace OneLlm.Tests.Records;

public class RecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onellm-rec-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static List<AuditRecord> OneOfEach() =>
    [
        new RunRecord { InstanceId = "i1", Time = T0, Event = "started", State = "running", Model = "m" },
        new MessageRecord { InstanceId = "i1", Time = T0, MessageId = "m1", Message = new Message { Id = "m1", Source = new MessageSource { Kind = "agent", InstanceId = "p" }, User = [new TextPart("hi")] } },
        new InferenceRecord
        {
            InstanceId = "i1", Time = T0, Provider = "anthropic", Duration = TimeSpan.FromMilliseconds(1500), FinishReason = "stop",
            Usage = new Usage { Model = "m", InputTokens = 10, OutputTokens = 5, Cost = 0.5m, CostLines = [CostLine.Create("inputTokens", 10, 1_000_000m, 3m)] },
        },
        new ToolCallRecord { InstanceId = "i1", Time = T0, Duration = TimeSpan.FromMilliseconds(40), Call = new ToolCall("c1", "read_file") { Cost = 0.01m, Result = [new TextPart("ok")] } },
        new TaskCompletionRecord { InstanceId = "i1", Time = T0, Tool = "task_complete", Result = "done" },
        new ApprovalRecord { InstanceId = "i1", Time = T0, ToolCallId = "c1", Tool = "read_file", Decision = "approved", By = "user" },
        new CompactionRecord { InstanceId = "i1", Time = T0, Compaction = new Compaction { Strategy = "compact", ReplacedMessageIds = ["a", "b"], Cost = 0.25m } },
        new HookRecord { InstanceId = "i1", Time = T0, Point = "beforeToolCall", Hook = "audit.js", Decision = "block", Reason = "no" },
        new ErrorRecord { InstanceId = "i1", Time = T0, ErrorType = "RateLimitException", Message = "slow down", Where = "inference" },
        RawTurnRecord.Create("i1", T0, "{\"key\":\"sk-secret-1234567890\"}", "ok sk-secret-1234567890", [new Secret("sk-secret-1234567890")]),
        new RunRecord { Time = T0, Event = "stopped" },
    ];

    [Fact]
    public async Task Jsonl_writes_every_kind_and_reads_back()
    {
        var sink = new JsonlRecordSink(_dir);
        foreach (AuditRecord r in OneOfEach())
        {
            await sink.WriteAsync(r);
        }

        List<RecordEntry> entries = RecordReader.ReadFile(sink.PathOf("i1"));
        Assert.Equal(["run", "message", "inference", "toolCall", "taskCompletion", "approval", "compaction", "hook", "error", "rawTurn"], entries.Select(e => e.Kind));
        Assert.All(entries, e => Assert.Equal("i1", e.InstanceId));
        Assert.Equal(T0, entries[0].Time);
        Assert.Equal("m1", entries[1].MessageId);
        Assert.Equal("agent", entries[1].Json.GetProperty("message").GetProperty("source").GetProperty("kind").GetString());
        JsonElementAssert(entries[2].Json.GetProperty("usage").GetProperty("costLines")[0].GetProperty("unitPrice").GetDecimal(), 3m);
        Assert.Equal(1500, entries[2].Json.GetProperty("durationMs").GetInt64());
        Assert.Equal("read_file", entries[3].Json.GetProperty("call").GetProperty("tool").GetString());
        Assert.Equal(0.01m, entries[3].Json.GetProperty("call").GetProperty("cost").GetDecimal());
        Assert.Equal(0.25m, entries[6].Json.GetProperty("cost").GetDecimal());
        Assert.DoesNotContain("sk-secret", File.ReadAllText(sink.PathOf("i1")));

        List<RecordEntry> runtime = RecordReader.ReadFile(sink.PathOf(""));
        Assert.Equal("stopped", runtime.Single().Json.GetProperty("event").GetString());
        Assert.Empty(RecordReader.ReadFile(Path.Combine(_dir, "missing.jsonl")));
    }

    private static void JsonElementAssert(decimal actual, decimal expected) => Assert.Equal(expected, actual);

    private sealed class ThrowingSink : IRecordSink
    {
        public int Calls;

        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new IOException("disk full");
        }
    }

    [Fact]
    public async Task A_throwing_sink_does_not_stop_the_others_or_the_caller()
    {
        var bad = new ThrowingSink();
        var events = new EventRecordSink();
        var seen = new List<string>();
        events.Written += r => seen.Add(r.Kind);
        var failures = new List<Exception>();
        var sinks = new RecordSinks { OnError = (_, e) => failures.Add(e) };
        sinks.Add(bad);
        sinks.Add(new JsonlRecordSink(_dir));
        sinks.Add(events);

        foreach (AuditRecord r in OneOfEach().Take(3))
        {
            await sinks.WriteAsync(r);
        }

        Assert.Equal(3, bad.Calls);
        Assert.Equal(3, failures.Count);
        Assert.Equal(["run", "message", "inference"], seen);
        Assert.Equal(3, RecordReader.ReadFile(Path.Combine(_dir, "i1.jsonl")).Count);

        // An error callback that itself throws changes nothing either.
        sinks.OnError = (_, _) => throw new InvalidOperationException();
        await sinks.WriteAsync(OneOfEach()[0]);
    }

    [Fact]
    public async Task Cancellation_still_reaches_the_caller()
    {
        var sinks = new RecordSinks();
        sinks.Add(new JsonlRecordSink(_dir));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sinks.WriteAsync(OneOfEach()[0], cts.Token));
    }

    // The listener tests share the process-wide source and meter, so they run one after another in a single test.
    [Fact]
    public void Spans_and_metrics_are_emitted_under_a_listener_and_cost_nothing_without_one()
    {
        // Without a listener: nothing is allocated.
        var usage = new Usage { Model = "m", InputTokens = 100, OutputTokens = 20, CacheReadInputTokens = 5 };
        for (int i = 0; i < 100; i++)
        {
            Cycle(usage);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Cycle(usage);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // With listeners: a span with the GenAI attributes and two metrics.
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GenAiTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if ((a.GetTagItem("gen_ai.request.model") as string) == "otel-probe-model") { lock (activities) { activities.Add(a); } } },
        };
        ActivitySource.AddActivityListener(activityListener);

        var tokens = new List<(long Value, string Type)>();
        var durations = new List<double>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == GenAiTelemetry.SourceName)
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        // Other tests running in parallel use the same meter; keep only this test's model.
        static bool Ours(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (KeyValuePair<string, object?> t in tags)
            {
                if (t.Key == "gen_ai.request.model")
                {
                    return (t.Value as string) == "otel-probe-model";
                }
            }

            return false;
        }

        meterListener.SetMeasurementEventCallback<long>((inst, v, tags, _) =>
        {
            if (!Ours(tags))
            {
                return;
            }

            foreach (KeyValuePair<string, object?> t in tags)
            {
                if (t.Key == "gen_ai.token.type")
                {
                    lock (tokens)
                    {
                        tokens.Add((v, (string)t.Value!));
                    }
                }
            }
        });
        meterListener.SetMeasurementEventCallback<double>((inst, v, tags, _) =>
        {
            if (Ours(tags))
            {
                lock (durations)
                {
                    durations.Add(v);
                }
            }
        });
        meterListener.Start();

        Cycle(usage);
        Activity a = Assert.Single(activities);
        Assert.Equal("chat otel-probe-model", a.DisplayName);
        Assert.Equal("chat", a.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("anthropic", a.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("otel-probe-model", a.GetTagItem("gen_ai.request.model"));
        Assert.Equal("m", a.GetTagItem("gen_ai.response.model"));
        Assert.Equal(105L, a.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(20L, a.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(5L, a.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        Assert.Equal(ActivityKind.Client, a.Kind);
        Assert.Equal([(105L, "input"), (20L, "output")], tokens);
        Assert.Equal(0.25, Assert.Single(durations));

        Activity? failing = GenAiTelemetry.StartOperation("chat", "anthropic", "otel-probe-model");
        GenAiTelemetry.Fail(failing, "chat", "anthropic", "otel-probe-model", new RateLimitException("anthropic", "slow"), TimeSpan.FromSeconds(1));
        failing?.Dispose();
        Assert.Equal(ActivityStatusCode.Error, activities[^1].Status);
        Assert.Contains("RateLimitException", (string)activities[^1].GetTagItem("error.type")!);
    }

    private static void Cycle(Usage usage)
    {
        Activity? a = GenAiTelemetry.StartOperation("chat", "anthropic", "otel-probe-model");
        GenAiTelemetry.Complete(a, "chat", "anthropic", "otel-probe-model", usage, TimeSpan.FromMilliseconds(250), "stop");
        a?.Dispose();
    }
}
