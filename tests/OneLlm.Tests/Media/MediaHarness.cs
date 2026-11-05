using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Media;

/// <summary>A scripted answer to one request, used to write a fixture.</summary>
internal sealed record MReply(int Status, string? Json = null, byte[]? Bytes = null, string ContentType = "application/json", string[]? Sse = null);

/// <summary>What the client sent, as the case sees it.</summary>
internal sealed record Sent(string Method, string Path, string ContentType, byte[] Body)
{
    public JsonObject Json() => (JsonObject)JsonNode.Parse(Body)!;

    public string Text() => Encoding.UTF8.GetString(Body);
}

/// <summary>A conformance case: what to call, what the service answers (written from the API reference), and what must come out.</summary>
internal sealed record MCase(string Provider, string Feature, string Name, string Reference, Func<MediaRun, Task> Run, params MReply[] Script)
{
    public string Id => Provider + "/" + Feature + "/" + Name;
}

/// <summary>The client and providers a case runs against, and the requests it sent.</summary>
internal sealed class MediaRun(HttpClient http, List<Sent> sent)
{
    public IReadOnlyList<Sent> Sent => sent;

    public static string FakeKey => "test" + "-key-" + "0000";

    public static ModelCatalog Catalog { get; } = ModelCatalog.Create([OpenAICatalog.Shipped(), GeminiProvider.GetCatalogLayer()]);

    public OpenAIProvider OpenAI() =>
        new(new OpenAIOptions { ApiKey = new Secret(FakeKey), HttpClient = http, Catalog = Catalog, Retry = RetryPolicy.None }, new ManualTimeProvider());

    public GeminiProvider Gemini() =>
        new(new GeminiOptions
        {
            Time = new ManualTimeProvider(),
            Auth = new GeminiApiKeyAuth(new Secret(FakeKey)),
            Http = http,
            Catalog = Catalog,
            Retry = RetryPolicy.None,
            Timeout = TimeSpan.FromDays(1),
        });
}

internal static class MediaHarness
{
    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "testdata", "providers");

    /// <summary>Where fixtures are written when ONELLM_WRITE_MEDIA_FIXTURES names the testdata/providers directory of the source tree.</summary>
    public static string? WriteDir => Environment.GetEnvironmentVariable("ONELLM_WRITE_MEDIA_FIXTURES") is { Length: > 0 } d ? d : null;

    public static string PathFor(MCase c, string root) => Path.Combine(root, c.Provider, "direct", c.Feature, c.Name + ".json");

    public static async Task ReplayAsync(MCase c)
    {
        string? write = WriteDir;
        string path = PathFor(c, write ?? FixtureRoot);
        if (write is not null)
        {
            await GenerateAsync(c, path);
        }

        ReplayHandler replay = ReplayHandler.FromFile(path);
        var sent = new List<Sent>();
        using var http = new HttpClient(new Capturing(replay, sent));
        await c.Run(new MediaRun(http, sent));
        replay.AssertAllUsed();
    }

    private static async Task GenerateAsync(MCase c, string path)
    {
        var recorder = new RecordingHandler(
            new Scripted(c.Script),
            path,
            $"Written from the API reference {c.Reference}; no live call was made. Synthetic inputs and data only.",
            [MediaRun.FakeKey]);
        var sent = new List<Sent>();
        using var http = new HttpClient(new Capturing(recorder, sent));
        await c.Run(new MediaRun(http, sent));
        recorder.Save();
    }

    private sealed class Capturing(HttpMessageHandler inner, List<Sent> sent) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            sent.Add(new Sent(request.Method.Method, request.RequestUri!.PathAndQuery, request.Content?.Headers.ContentType?.ToString() ?? "", body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Scripted(MReply[] script) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            MReply r = script[Math.Min(_next++, script.Length - 1)];
            var response = new HttpResponseMessage((HttpStatusCode)r.Status);
            if (r.Sse is { } chunks)
            {
                response.Content = new StringContent(string.Concat(chunks), Encoding.UTF8, "text/event-stream");
            }
            else if (r.Bytes is { } bytes)
            {
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(r.ContentType);
            }
            else
            {
                response.Content = new StringContent(r.Json ?? "{}", Encoding.UTF8, r.ContentType);
            }

            return Task.FromResult(response);
        }
    }
}
