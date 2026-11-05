using System.Diagnostics;
using System.Text.Json.Serialization;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Telemetry;
using OneLlm.Templating;
using OneLlm.Testing;

namespace OneLlm.Tests.Running;

public sealed record Invoice(string Customer, decimal Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Invoice))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class RunningJson : JsonSerializerContext;

/// <summary>A runtime wired to a scripted provider, a synthetic priced catalog entry, a fake clock and a temp records directory.</summary>
internal sealed class Harness : IDisposable
{
    public const string Yaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        aliases: { demo: demo-1 }
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing:
              asOf: "2026-11-01"
              source: https://example.test/prices
              inputPerMillion: 1.00
              outputPerMillion: 2.00
          - name: embed-1
            pricing: { asOf: "2026-11-01", source: https://example.test/prices, inputPerMillion: 0.10 }
        """;

    private int _ids;

    public Harness()
    {
        Directory = Path.Combine(Path.GetTempPath(), "onellm-run-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Provider = new ScriptedProvider();
        Runtime = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, Yaml)]),
            TimeProvider = Clock,
            IdSource = () => (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetEnvironmentVariable = name => Environment.TryGetValue(name, out string? v) ? v : null,
            Providers = new ProviderRegistry(name => Environment.TryGetValue(name, out string? v) ? v : null),
        };
        Runtime.Providers.Set("scripted", Provider);
        Records = new JsonlRecordSink(Path.Combine(Directory, "records"));
        Runtime.Sinks.Add(Records);
    }

    public string Directory { get; }

    public ScriptedProvider Provider { get; }

    public Runtime Runtime { get; }

    public JsonlRecordSink Records { get; }

    public ManualTimeProvider Clock { get; } = new();

    public Dictionary<string, string> Environment { get; } = [];

    public List<RecordEntry> ReadRecords(string instanceId) => RecordReader.ReadFile(Records.PathOf(instanceId));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

[Collection("llm-default")]
public class RuntimeAskTests
{
    [Fact]
    public async Task Ask_runs_as_an_instance_with_cost_records_and_usage()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.TextWithUsage("Hello there", 1000, 500));

        AskResult r = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi", System = "Be brief." });

        Assert.Equal("Hello there", r.Text);
        Assert.Equal("scripted:demo-1", r.Model);
        // 1000 input at $1/M + 500 output at $2/M.
        Assert.Equal(0.002m, r.Cost.Total);
        Assert.Equal(2, r.Cost.Lines.Count);
        Assert.Equal(0.002m, r.Usage!.Cost);
        Assert.Equal(0.002m, h.Runtime.Spent);
        ChatRequest sent = Assert.Single(h.Provider.Requests);
        Assert.Equal("demo-1", sent.Model);
        Assert.Equal("Be brief.", sent.System);

        Instance saved = (await h.Runtime.Store.LoadAsync(r.Instance.InstanceId))!;
        Assert.Equal(InstanceStates.Stopped, saved.State);
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("hi", saved.Messages[0].GetUserText());
        Assert.Equal("Hello there", saved.Messages[1].GetAssistantText());
        Assert.Equal("Be brief.", saved.RenderedSystemPrompt);

        List<string> kinds = h.ReadRecords(r.Instance.InstanceId).Select(e => e.Kind).ToList();
        Assert.Equal(["run", "message", "inference", "message", "run"], kinds);
    }

    [Fact]
    public async Task Unknown_models_unregistered_providers_and_missing_keys_are_typed_errors()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<ModelNotFoundException>(() => h.Runtime.AskAsync(new AskRequest { Model = "no-such-model-xyz", Prompt = "hi" }));

        var keyed = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, "name: acme\napiKeyEnvVar: ACME_KEY\nmodelPatterns: [\"acme-*\"]\n")]),
            Providers = new ProviderRegistry(_ => null),
        };
        CapabilityMissingException none = await Assert.ThrowsAsync<CapabilityMissingException>(() => keyed.AskAsync(new AskRequest { Model = "acme-1", Prompt = "hi" }));
        Assert.Contains("acme", none.Message, StringComparison.Ordinal);

        keyed.Providers.Register("acme", _ => new ScriptedProvider("acme"));
        AuthException auth = await Assert.ThrowsAsync<AuthException>(() => keyed.AskAsync(new AskRequest { Model = "acme-1", Prompt = "hi" }));
        Assert.Contains("ACME_KEY", auth.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_factory_gets_the_key_from_the_environment_once()
    {
        int made = 0;
        Secret? seen = null;
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, "name: acme\napiKeyEnvVar: ACME_KEY\nmodelPatterns: [\"acme-*\"]\n")]),
            Providers = new ProviderRegistry(name => name == "ACME_KEY" ? "k-123" : null),
        };
        rt.Providers.Register("acme", ctx =>
        {
            made++;
            seen = ctx.ApiKey;
            return new ScriptedProvider("acme").EnqueueText("a").EnqueueText("b");
        });

        await rt.AskAsync(new AskRequest { Model = "acme-1", Prompt = "x" });
        await rt.AskAsync(new AskRequest { Model = "acme-1", Prompt = "y" });

        Assert.Equal(1, made);
        Assert.Equal("k-123", seen!.Value);
    }

    [Fact]
    public async Task Spend_caps_refuse_before_anything_is_sent()
    {
        using var h = new Harness();
        h.Runtime.SpendCap = 0.001m; // the call could cost up to 0.002 (1000 output tokens at $2/M)
        h.Provider.EnqueueText("never sent");

        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }));

        Assert.Equal(BudgetKind.SpendCap, e.Kind);
        Assert.Empty(h.Provider.Requests);
        Assert.Equal(0m, h.Runtime.Spent);
    }

    [Fact]
    public async Task The_instance_cap_counts_what_earlier_turns_of_the_chat_cost()
    {
        using var h = new Harness();
        h.Runtime.InstanceSpendCap = 0.003m;
        h.Provider.Enqueue(ScriptStep.TextWithUsage("one", 1000, 1000)); // costs 0.003
        h.Provider.EnqueueText("two");
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo" });

        await chat.AskAsync("first");
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(() => chat.AskAsync("second"));

        Assert.Equal(BudgetKind.SpendCap, e.Kind);
        Assert.Single(h.Provider.Requests);
        // The refused message does not stay in the conversation.
        Assert.Equal(2, chat.Instance.Messages.Count);
    }

    [Fact]
    public async Task A_provider_failure_puts_the_instance_in_error_and_records_it()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.Fail(new AuthException("scripted", "bad key")));

        await Assert.ThrowsAsync<AuthException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }));

        InstanceSummary summary = await h.Runtime.Store.ListAsync().SingleAsync();
        Assert.Equal(InstanceStates.Error, summary.State);
        Assert.Contains(h.ReadRecords(summary.InstanceId), e => e.Kind == "error");
        Assert.Equal(0m, h.Runtime.Spent);
    }

    [Fact]
    public async Task Operations_are_traced_under_a_listener()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.TextWithUsage("ok", 4242, 5));
        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GenAiTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (started) { started.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);

        await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        Activity span;
        lock (started)
        {
            // The runtime tests share one non-parallel collection with the statics tests, but other test classes also trace; this call is the one that reported 4242 input tokens.
            span = Assert.Single(started, a => a.GetTagItem("gen_ai.usage.input_tokens") is 4242L);
        }

        Assert.Equal("scripted", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
    }

    [Fact]
    public async Task Streaming_builds_the_same_message_as_a_whole_answer()
    {
        using var h = new Harness();
        h.Provider.Enqueue(
            new ScriptStep { Chunks = ["Hel", "lo ", "world"], Usage = new Usage { InputTokens = 7, OutputTokens = 3 } },
            new ScriptStep { Chunks = ["Hel", "lo ", "world"], Usage = new Usage { InputTokens = 7, OutputTokens = 3 } });

        AskResult whole = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" });
        StreamingAnswer stream = h.Runtime.StreamAsync(new AskRequest { Model = "demo", Prompt = "hi" });
        var pieces = new List<string>();
        await foreach (string piece in stream.TextAsync())
        {
            pieces.Add(piece);
        }

        AskResult streamed = await stream.Result;
        Assert.Equal(["Hel", "lo ", "world"], pieces);
        Assert.Equal(whole.Text, streamed.Text);
        Assert.Equal(whole.Usage!.InputTokens, streamed.Usage!.InputTokens);
        Assert.Equal(whole.Cost.Total, streamed.Cost.Total);
        Assert.Equal(whole.Message.Assistant.Count, streamed.Message.Assistant.Count);
        Assert.Equal(InstanceStates.Stopped, streamed.Instance.State);
    }

    [Fact]
    public async Task Cancelling_a_stream_keeps_the_partial_text_in_a_stopped_cancelled_instance()
    {
        using var h = new Harness();
        h.Provider.Enqueue(new ScriptStep
        {
            Chunks = ["Hel", "lo"],
            BeforeChunk = (i, ct) => i == 1 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask,
        });
        using var cts = new CancellationTokenSource();
        StreamingAnswer stream = h.Runtime.StreamAsync(new AskRequest { Model = "demo", Prompt = "hi" });

        RunCancelledException e = await Assert.ThrowsAsync<RunCancelledException>(async () =>
        {
            await foreach (StreamEvent ev in stream.WithCancellation(cts.Token))
            {
                if (ev.Kind == StreamEventKind.TextDelta)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Equal("Hel", e.Partial!.GetAssistantText());
        Instance saved = (await h.Runtime.Store.LoadAsync(e.Instance.InstanceId))!;
        Assert.Equal(InstanceStates.Stopped, saved.State);
        Assert.True(saved.ExtensionData!.ContainsKey("cancelled"));
        Assert.Equal("Hel", saved.Messages[^1].GetAssistantText());
        Assert.Contains(h.ReadRecords(saved.InstanceId), r => r.Kind == "run" && r.Json.GetProperty("event").GetString() == "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Result);
    }

    [Fact]
    public async Task A_non_streaming_cancellation_is_recorded_and_throws_RunCancelledException()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Provider.Enqueue(new ScriptStep { Chunks = ["x"], BeforeChunk = async (_, ct) => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); } });

        RunCancelledException e = await Assert.ThrowsAsync<RunCancelledException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "hi" }, cts.Token));

        Assert.Equal(InstanceStates.Stopped, e.Instance.State);
        Assert.Null(e.Partial);
    }

    [Fact]
    public async Task Typed_output_sends_a_strict_schema_and_reads_valid_json()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("""{"customer":"Ada","total":12.5}""");

        AskResult<Invoice> r = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "extract" }, RunningJson.Default.Invoice);

        Assert.Equal(new Invoice("Ada", 12.5m), r.Value);
        ResponseFormat format = h.Provider.Requests[0].ResponseFormat!;
        Assert.Equal(ResponseFormatKind.JsonSchema, format.Kind);
        Assert.Equal("Invoice", format.SchemaName);
        string schema = format.Schema!.ToJsonString();
        Assert.Contains("\"additionalProperties\":false", schema, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"customer\",\"total\"]", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_output_with_invalid_json_is_a_typed_error_and_the_instance_ends_in_error()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("""{"customer": "Ada", "total": "not a number"}""");

        StructuredOutputException e = await Assert.ThrowsAsync<StructuredOutputException>(
            () => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "extract" }, RunningJson.Default.Invoice));

        Assert.Contains("not a number", e.RawText, StringComparison.Ordinal);
        InstanceSummary summary = await h.Runtime.Store.ListAsync().SingleAsync();
        Assert.Equal(InstanceStates.Error, summary.State);
    }

    [Fact]
    public async Task Typed_output_needs_an_object_type()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "x" }, RunningJson.Default.Int32Array));
    }

    [Fact]
    public async Task Embeddings_are_priced_and_counted_against_the_runtime()
    {
        using var h = new Harness();
        EmbedResult r = await h.Runtime.EmbedAsync("scripted:embed-1", ["hello", "hi"]);

        Assert.Equal(2, r.Embeddings.Count);
        Assert.Equal(5f, r.Embeddings[0].Span[0]);
        Assert.Equal(7m * 0.1m / 1_000_000m, r.Cost.Total);
        Assert.Equal(r.Cost.Total, h.Runtime.Spent);
    }

    [Fact]
    public async Task A_chat_is_one_instance_and_sends_the_whole_conversation_each_turn()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("A1").EnqueueText("A2");
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo", System = "sys" });

        Assert.Equal("A1", await chat.AskAsync("ask1"));
        Assert.Equal("A2", await chat.AskAsync("ask2"));

        Assert.Equal(["ask1", "A1", "ask2"], h.Provider.Requests[1].Messages.Select(m => m.GetUserText() + m.GetAssistantText()).ToArray());
        Assert.Equal("sys", h.Provider.Requests[1].System);
        Instance saved = (await h.Runtime.Store.LoadAsync(chat.InstanceId))!;
        Assert.Equal(4, saved.Messages.Count);
        Assert.Equal(InstanceStates.Waiting, saved.State);
    }

    [Fact]
    public async Task A_failed_chat_turn_leaves_no_unanswered_message_so_the_next_turn_can_retry()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.Fail(new TransportException("scripted", "down")));
        h.Provider.EnqueueText("fine");
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo" });

        await Assert.ThrowsAsync<TransportException>(() => chat.AskAsync("hello"));
        Assert.Empty(chat.Instance.Messages);
        Assert.Equal("fine", await chat.AskAsync("hello again"));
        Assert.Equal(2, chat.Instance.Messages.Count);
    }

    [Fact]
    public async Task A_chat_streams_and_a_second_turn_waits_for_the_first()
    {
        using var h = new Harness();
        h.Provider.Enqueue(ScriptStep.Text("a", "b"), ScriptStep.Text("c"));
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo" });

        StreamingAnswer first = chat.StreamAsync("one");
        string joined = "";
        await foreach (string s in first.TextAsync())
        {
            joined += s;
        }

        Assert.Equal("ab", joined);
        Assert.Equal("c", await chat.AskAsync("two"));
        Assert.Equal(4, chat.Instance.Messages.Count);
    }

    [Fact]
    public async Task Fork_and_duplicate_copy_through_the_store()
    {
        using var h = new Harness();
        h.Provider.EnqueueText("A1").EnqueueText("A2");
        using ChatSession chat = h.Runtime.Chat(new AskRequest { Model = "demo" });
        await chat.AskAsync("ask1");
        await chat.AskAsync("ask2");

        Instance fork = await h.Runtime.ForkAsync(chat.InstanceId, chat.Instance.Messages[1].Id);
        Instance copy = await h.Runtime.DuplicateAsync(chat.InstanceId);

        Assert.NotEqual(chat.InstanceId, fork.InstanceId);
        Assert.Equal(2, fork.Messages.Count);
        Assert.Equal(4, copy.Messages.Count);
        Assert.NotNull(await h.Runtime.Store.LoadAsync(fork.InstanceId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Runtime.ForkAsync("missing"));
    }

    [Fact]
    public async Task The_result_cache_is_opt_in()
    {
        using var h = new Harness();
        h.Runtime.Cache = new OneLlm.Caching.ResultCache();
        h.Provider.Enqueue(ScriptStep.TextWithUsage("cached", 5, 5));

        AskResult a = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "same" });
        AskResult b = await h.Runtime.AskAsync(new AskRequest { Model = "demo", Prompt = "same" });

        Assert.Equal("cached", a.Text);
        Assert.Equal("cached", b.Text);
        Assert.Single(h.Provider.Requests);
    }
}

internal static class RunningTestExtensions
{
    public static async Task<T> SingleAsync<T>(this IAsyncEnumerable<T> items)
    {
        T? found = default;
        int count = 0;
        await foreach (T item in items)
        {
            found = item;
            count++;
        }

        Assert.Equal(1, count);
        return found!;
    }
}
