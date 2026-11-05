using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Conformance;

namespace OneLlm.Tests.Http;

public class FixtureTests
{
    private static string TestData(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", .. parts]);

    private static HttpRequestMessage Chat(string body, string path = "/v1/chat", bool version = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://api.test" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (version)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        request.Headers.TryAddWithoutValidation("x-api-key", "not-a-real-key"); // never part of matching
        return request;
    }

    private const string FirstBody = """{"model":"sample-model","messages":[{"role":"user","content":"Say hello."}],"max_tokens":64}""";
    private const string SecondBody = """{"model":"sample-model","stream":true}""";

    [Fact]
    public async Task A_matching_request_gets_the_recorded_response()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        using HttpResponseMessage response = await http.SendAsync(Chat(FirstBody));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Hello!", body["text"]!.GetValue<string>());
        Assert.Equal(9, body["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_stream_arrives_in_the_recorded_chunks_even_when_one_ends_inside_an_event()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        using HttpResponseMessage response = await http.SendAsync(Chat(SecondBody, "/v1/chat?stream=true", version: false), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        var reads = new List<string>();
        byte[] buffer = new byte[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            reads.Add(Encoding.UTF8.GetString(buffer, 0, n));
        }

        Assert.Equal(3, reads.Count);
        Assert.EndsWith("{\"text\":\"Hel", reads[0], StringComparison.Ordinal);
        Assert.StartsWith("lo\"}", reads[1], StringComparison.Ordinal);
        Assert.Equal("event: done\ndata: {}\n\n", reads[2]);
    }

    [Fact]
    public async Task A_request_with_a_different_body_is_refused_and_the_difference_is_named()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        var e = await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(Chat(FirstBody.Replace("64", "65", StringComparison.Ordinal))));
        Assert.Contains("$.max_tokens", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-key", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_order_in_the_body_matters_because_models_read_in_order()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        string reordered = """{"messages":[{"role":"user","content":"Say hello."}],"model":"sample-model","max_tokens":64}""";
        var e = await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(Chat(reordered)));
        Assert.Contains("key order", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_recorded_header_a_wrong_path_and_a_wrong_method_are_refused()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        Assert.Contains("anthropic-version", (await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(Chat(FirstBody, version: false)))).Message, StringComparison.Ordinal);
        Assert.Contains("path", (await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(Chat(FirstBody, "/v2/chat")))).Message, StringComparison.Ordinal);
        using var get = new HttpRequestMessage(HttpMethod.Get, "http://api.test/v1/chat");
        Assert.Contains("method", (await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(get))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_is_used_once_and_unused_exchanges_fail_the_check()
    {
        var handler = ReplayHandler.FromFile(TestData("fixtures", "replay-sample.json"));
        using var http = new HttpClient(handler);
        (await http.SendAsync(Chat(FirstBody))).Dispose();
        await Assert.ThrowsAsync<FixtureMismatchException>(() => http.SendAsync(Chat(FirstBody))); // already used
        var unused = Assert.Throws<FixtureMismatchException>(handler.AssertAllUsed);
        Assert.Contains("#1", unused.Message, StringComparison.Ordinal);

        (await http.SendAsync(Chat(SecondBody, "/v1/chat?stream=true", version: false))).Dispose();
        handler.AssertAllUsed();
    }

    [Fact]
    public void A_fixture_reads_and_writes_the_same_text()
    {
        string text = File.ReadAllText(TestData("fixtures", "replay-sample.json"));
        Assert.Equal(text, RecordedFixture.Parse(text).ToJson());
    }

    [Fact]
    public void An_unsupported_format_version_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => RecordedFixture.Parse("""{"format":2,"exchanges":[]}"""));
    }

    [Fact]
    public async Task Recording_scrubs_before_writing_and_the_recording_replays()
    {
        string key = "sk-" + new string('a', 30);
        string path = Path.Combine(Path.GetTempPath(), "onellm-rec-" + Guid.NewGuid().ToString("N"), "rec.json");
        try
        {
            // A live-looking server: echoes an organization id and a request id, and wants a key.
            var inner = new DelegateHandler(req =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"text":"hi","organization":"acme-org","request_id":"abc"}""", Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("x-request-id", "req-1");
                response.Headers.TryAddWithoutValidation("Retry-After", "3");
                return response;
            });
            var recorder = new RecordingHandler(inner, path, "recorded sample", [key]);
            using (var http = new HttpClient(recorder))
            {
                var request = Chat($$"""{"model":"m","note":"my key is {{key}}"}""");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                using HttpResponseMessage response = await http.SendAsync(request);
                Assert.Contains("acme-org", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); // the caller still gets the real answer
            }

            recorder.Save();
            string written = File.ReadAllText(path);
            Assert.DoesNotContain(key, written, StringComparison.Ordinal);
            Assert.DoesNotContain("acme-org", written, StringComparison.Ordinal);
            Assert.DoesNotContain("req-1", written, StringComparison.Ordinal);
            Assert.DoesNotContain("uthorization", written, StringComparison.Ordinal);
            Assert.Contains("\"retry-after\": \"3\"", written, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(FixtureScrubber.FindViolations(RecordedFixture.Parse(written)));
        }
        finally
        {
            if (File.Exists(path))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_stream_is_recorded_as_one_chunk_per_event()
    {
        string path = Path.Combine(Path.GetTempPath(), "onellm-rec-" + Guid.NewGuid().ToString("N"), "stream.json");
        try
        {
            var inner = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: a\ndata: 1\n\nevent: b\ndata: 2\n\n", Encoding.UTF8, "text/event-stream"),
            });
            var recorder = new RecordingHandler(inner, path);
            using (var http = new HttpClient(recorder))
            {
                (await http.SendAsync(Chat("{}"))).Dispose();
            }

            recorder.Save();
            RecordedFixture back = RecordedFixture.Load(path);
            Assert.Equal(["event: a\ndata: 1\n\n", "event: b\ndata: 2\n\n"], back.Exchanges[0].Response.Sse);
        }
        finally
        {
            if (File.Exists(path))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
