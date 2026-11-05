using System.Net;
using System.Text;
using OneLlm.Catalog;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.Tests.Gemini;

/// <summary>A scripted answer to one request, used to write a fixture.</summary>
internal sealed record Reply(int Status, string? Json = null, string[]? Sse = null, string? RetryAfter = null);

/// <summary>One conformance case: what to call, what the service answers (as written from the API reference), and what must come out.</summary>
internal sealed record Case(string Feature, string Name, string Reference, Func<GeminiProvider, Task> Run, params Reply[] Script)
{
    public string Id => Feature + "/" + Name;

    /// <summary>The catalog the provider resolves models with; null uses the shipped Gemini catalog.</summary>
    public ModelCatalog? Catalog { get; init; }
}

internal static class GeminiHarness
{
    public const string ModelPage = "https://ai.google.dev/api/generate-content";

    // A made-up key, built so no source line looks like a real one.
    public static string FakeKey => "test" + "-key-" + "0000";

    public static string FixtureDir
    {
        get { return Path.Combine(AppContext.BaseDirectory, "testdata", "providers", "gemini", "direct"); }
    }

    /// <summary>Where fixtures are written when ONELLM_WRITE_FIXTURES names a directory (the testdata directory of the source tree).</summary>
    public static string? WriteDir => Environment.GetEnvironmentVariable("ONELLM_WRITE_FIXTURES") is { Length: > 0 } d ? d : null;

    public static string PathFor(Case c, string root) => Path.Combine(root, c.Feature, c.Name + ".json");

    public static GeminiProvider NewProvider(HttpClient http, TimeProvider? time = null, Action<string>? dropped = null, ModelCatalog? catalog = null) =>
        new(new GeminiOptions
        {
            Time = time ?? new FastTimeProvider(),
            Auth = new GeminiApiKeyAuth(new Secret(FakeKey)),
            Http = http,
            Catalog = catalog ?? DefaultCatalog,
            Retry = new RetryPolicy { JitterFraction = 0 },
            Timeout = TimeSpan.FromDays(1),
            Jitter = () => 0.0,
            OnParameterDropped = dropped,
        });

    public static ModelCatalog DefaultCatalog { get; } = ModelCatalog.Create([GeminiProvider.GetCatalogLayer()]);

    /// <summary>Runs a case against the answers in its fixture file.</summary>
    public static async Task ReplayAsync(Case c)
    {
        string? write = WriteDir;
        string path = PathFor(c, write ?? FixtureDir);
        if (write is not null)
        {
            await GenerateAsync(c, path);
        }

        ReplayHandler handler = ReplayHandler.FromFile(path);
        using var http = new HttpClient(handler);
        await c.Run(NewProvider(http, catalog: c.Catalog));
        handler.AssertAllUsed();
    }

    /// <summary>Runs a case against its scripted answers and writes the scrubbed requests and answers as the fixture.</summary>
    private static async Task GenerateAsync(Case c, string path)
    {
        var scripted = new ScriptedHandler(c.Script);
        var recorder = new RecordingHandler(scripted, path, $"Written from the API reference {c.Reference}; no live call was made. Synthetic prompts only.", [FakeKey]);
        using var http = new HttpClient(recorder);
        await c.Run(NewProvider(http, catalog: c.Catalog));
        recorder.Save();
    }

    private sealed class ScriptedHandler(Reply[] script) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reply r = script[Math.Min(_next++, script.Length - 1)];
            var response = new HttpResponseMessage((HttpStatusCode)r.Status);
            if (r.Sse is { } chunks)
            {
                response.Content = new StringContent(string.Concat(chunks), Encoding.UTF8, "text/event-stream");
            }
            else
            {
                response.Content = new StringContent(r.Json ?? "{}", Encoding.UTF8, "application/json");
            }

            if (r.RetryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", r.RetryAfter);
            }

            return Task.FromResult(response);
        }
    }
}

/// <summary>A clock whose short timers fire at once, so retry waits cost no time in tests. Timers longer than an hour (request time limits) never fire.</summary>
internal sealed class FastTimeProvider : TimeProvider
{
    public List<TimeSpan> Delays { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime <= TimeSpan.FromHours(1) && dueTime >= TimeSpan.Zero)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            return new ImmediateTimer(callback, state);
        }

        return new ImmediateTimer(null, state);
    }

    private sealed class ImmediateTimer : ITimer
    {
        public ImmediateTimer(TimerCallback? callback, object? state) => callback?.Invoke(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
