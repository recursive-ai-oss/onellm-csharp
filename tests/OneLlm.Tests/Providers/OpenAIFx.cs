using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Conformance;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

/// <summary>
/// A recorded exchange file opened for a test. Normally a replay: the provider must send exactly the recorded requests. With
/// <c>ONELLM_AUTHOR_DIR</c> set it instead fills each exchange's request from what the provider sent and writes the file there, which is how
/// request bodies are first written into a fixture whose response was written from the vendor reference; the result is read before it is kept.
/// </summary>
internal sealed class OpenAIFx : IDisposable
{
    private readonly ReplayHandler? _replay;
    private readonly AuthoringHandler? _author;

    private OpenAIFx(string relative)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "providers", relative);
        string? authorDir = Environment.GetEnvironmentVariable("ONELLM_AUTHOR_DIR");
        if (authorDir is { Length: > 0 })
        {
            _author = new AuthoringHandler(path, System.IO.Path.Combine(authorDir, relative));
            Client = new HttpClient(_author);
        }
        else
        {
            _replay = ReplayHandler.FromFile(path);
            Client = new HttpClient(_replay);
        }
    }

    public HttpClient Client { get; }

    public IReadOnlyList<IncomingRequest> Requests => _replay?.Requests ?? [];

    public static OpenAIFx Open(string relative) => new(relative);

    public void AssertAllUsed() => _replay?.AssertAllUsed();

    public void Dispose()
    {
        _author?.Save();
        Client.Dispose();
    }

    public static ModelCatalog Catalog { get; } = ModelCatalog.Create([OpenAICatalog.Shipped()]);

    public static OpenAIProvider OpenAI(OpenAIFx fx, OpenAIApi api = OpenAIApi.Responses, RetryPolicy? retry = null, TimeProvider? time = null, bool store = false) =>
        new(new OpenAIOptions { ApiKey = new Secret("test-key"), HttpClient = fx.Client, Catalog = Catalog, Retry = retry ?? RetryPolicy.None, Store = store }, time ?? new ManualTimeProvider(), api);

    public static OpenAICompatibleProvider Preset(OpenAIFx fx, string preset) =>
        OpenAICompatibleProvider.FromPreset(Catalog, preset, new ManualTimeProvider(), new Secret("test-key"), fx.Client);

    public static async Task<ChatResponse> CollectAsync(IChatProvider provider, ChatRequest request)
    {
        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in provider.StreamAsync(request))
        {
            acc.Add(e);
        }

        Assert.True(acc.IsDone);
        return new ChatResponse { Message = acc.ToMessage(), Id = acc.ResponseId, Model = acc.Model, Finish = acc.Finish, FinishReasonRaw = acc.FinishReasonRaw };
    }

    public static string Json(Message m) => JsonSerializer.Serialize(m, OneLlmJsonContext.Default.Message);

    public static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private sealed class AuthoringHandler : HttpMessageHandler
    {
        private readonly JsonObject _doc;
        private readonly string _output;
        private int _next;

        public AuthoringHandler(string source, string output)
        {
            _doc = (JsonObject)JsonNode.Parse(File.ReadAllText(source))!;
            _output = output;
        }

        public void Save()
        {
            // A test that never sent a request has nothing to add, and must not overwrite what another test wrote.
            if (_next == 0)
            {
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_output)!);
            JsonObject doc = _doc;
            if (File.Exists(_output))
            {
                // Several tests share a fixture and may each use only its first exchanges: keep what an earlier one wrote.
                doc = (JsonObject)JsonNode.Parse(File.ReadAllText(_output))!;
                for (int i = 0; i < _next; i++)
                {
                    doc["exchanges"]!.AsArray()[i]!["request"] = _doc["exchanges"]!.AsArray()[i]!["request"]!.DeepClone();
                }
            }

            File.WriteAllText(_output, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IncomingRequest incoming = await ReplayHandler.ReadAsync(request, cancellationToken);
            JsonObject exchange = _doc["exchanges"]!.AsArray()[_next++]!.AsObject();
            JsonObject recorded = exchange["request"]!.AsObject();
            recorded["method"] = incoming.Method;
            recorded["path"] = incoming.Path;
            recorded.Remove("body");
            recorded.Remove("bodyText");
            if (incoming.Body.Length > 0)
            {
                incoming.Headers.TryGetValue("content-type", out string? contentType);
                if (contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    recorded["body"] = JsonNode.Parse(incoming.Body);
                }
                else
                {
                    recorded["bodyText"] = Encoding.UTF8.GetString(incoming.Body);
                }
            }

            var single = new JsonObject { ["format"] = 1, ["exchanges"] = new JsonArray(exchange.DeepClone()) };
            var replay = new ReplayHandler(RecordedFixture.Parse(single.ToJsonString()));
            using var invoker = new HttpMessageInvoker(replay);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}
