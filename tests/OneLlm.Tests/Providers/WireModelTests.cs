using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Deciders;
using OneLlm.Http;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;
using OneLlm.Providers.TypeSafe;

namespace OneLlm.Tests.Providers;

/// <summary>The model string that goes on the wire is the catalog's wire name, not the catalog name or the alias the caller used.</summary>
public class WireModelTests
{
    private sealed class Capture(string reply) : HttpMessageHandler
    {
        public List<(string Url, JsonObject Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsoluteUri, body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private const string DecisionsReply = """
        {"model":"gpt-6-luna","answers":[{"type":"predicate","probability":0.25}],
         "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":0,"cache_write_tokens":0},"output_tokens":0,"total_tokens":10}}
        """;

    private const string Decider = """
        model: openai-decisions
        state: { subject: "{{ .inputs.subject }}" }
        questions:
          angry:
            type: noul
            instructions: Is the customer angry?
        """;

    [Theory]
    [InlineData("openai-decisions")]
    [InlineData("gpt-6-luna-decisions")]
    [InlineData("openai:gpt-6-luna-decisions")]
    public async Task A_decisions_request_sends_the_wire_model_not_the_catalog_name(string model)
    {
        var handler = new Capture(DecisionsReply);
        var rt = new Runtime
        {
            Catalog = ModelCatalog.Create([OpenAICatalog.Shipped()]),
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
        };
        rt.Providers.Set("openai", new OpenAIProvider(new OpenAIOptions { ApiKey = new Secret("test-key"), HttpClient = new HttpClient(handler), Catalog = rt.Catalog, Retry = RetryPolicy.None }, TimeProvider.System));

        global::OneLlm.Deciders.Decider d = global::OneLlm.Deciders.Decider.FromYaml(Decider.Replace("openai-decisions", model, StringComparison.Ordinal), "t");
        await rt.DecideAsync(d, System.Text.Json.JsonDocument.Parse("""{"subject":"s"}""").RootElement);

        (string url, JsonObject body) = Assert.Single(handler.Requests);
        Assert.EndsWith("/decisions", url, StringComparison.Ordinal);
        Assert.Equal("gpt-6-luna", (string?)body["model"]);
    }

    // The names a caller types are aliases; the wire carries the catalog's resolved name. The reply is empty on purpose: only the request matters here.
    private static async Task<(string Url, string Body)> Send(string provider, Func<HttpClient, ModelCatalog, IProvider> make, Func<Runtime, Task> call)
    {
        var handler = new Capture("{}");
        var rt = new Runtime
        {
            GetEnvironmentVariable = _ => null,
            Providers = new ProviderRegistry(_ => null),
        };
        rt.Providers.Set(provider, make(new HttpClient(handler), rt.Catalog));
        try
        {
            await call(rt);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The empty reply cannot be parsed; the request was still sent.
        }

        (string url, JsonObject body) = handler.Requests[0];
        return (url, body.ToJsonString());
    }

    [Fact]
    public async Task OpenAI_chat_sends_the_resolved_name_for_an_alias()
    {
        const string real = "gpt-6-luna";
        (_, string body) = await Send("openai", (http, cat) => new OpenAIProvider(new OpenAIOptions { ApiKey = new Secret("k"), HttpClient = http, Catalog = cat, Retry = RetryPolicy.None }, TimeProvider.System),
            rt => rt.AskAsync(new AskRequest { Model = "openai:gpt-6-luna", Prompt = "hi" }));
        Assert.Contains($"\"model\":\"{real}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_embeddings_send_the_resolved_name_for_an_alias()
    {
        (string url, string body) = await Send("openai", (http, cat) => new OpenAIProvider(new OpenAIOptions { ApiKey = new Secret("k"), HttpClient = http, Catalog = cat, Retry = RetryPolicy.None }, TimeProvider.System),
            rt => rt.EmbedAsync("embedding-small", ["hi"]));
        Assert.EndsWith("/embeddings", url, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"text-embedding-3-small\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_sends_the_resolved_name_for_an_alias()
    {
        string real = Catalog().Resolve("claude-sonnet").Name;
        Assert.NotEqual("claude-sonnet", real);
        (_, string body) = await Send("anthropic", (http, cat) => AnthropicProvider.Create(new Secret("k"), TimeProvider.System, new AnthropicOptions { Http = http, BaseAddress = new Uri("http://localhost/"), Retry = RetryPolicy.None }),
            rt => rt.AskAsync(new AskRequest { Model = "claude-sonnet", Prompt = "hi" }));
        Assert.Contains($"\"model\":\"{real}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_puts_the_resolved_name_in_the_url_for_an_alias()
    {
        string real = Catalog().Resolve("gemini-flash").Name;
        Assert.NotEqual("gemini-flash", real);
        (string url, _) = await Send("gemini", (http, cat) => new GeminiProvider(new GeminiOptions
            {
                Time = TimeProvider.System,
                Auth = new GeminiApiKeyAuth(new Secret("test-key-0000")),
                Http = http,
                Catalog = cat,
                Retry = new RetryPolicy { JitterFraction = 0 },
            }),
            rt => rt.AskAsync(new AskRequest { Model = "gemini-flash", Prompt = "hi" }));
        Assert.Contains($"/models/{real}:generateContent", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jev_decisions_send_the_resolved_name_for_an_alias()
    {
        var handler = new Capture("{}");
        var rt = new Runtime { GetEnvironmentVariable = _ => null, Providers = new ProviderRegistry(_ => null) };
        rt.Providers.Set("typesafe", new JevProvider(new JevOptions { ApiKey = new Secret("k"), BaseUrl = new Uri("https://stub.invalid/v1/"), HttpClient = new HttpClient(handler) }, TimeProvider.System));
        global::OneLlm.Deciders.Decider d = global::OneLlm.Deciders.Decider.FromYaml(Decider.Replace("openai-decisions", "jev-latest", StringComparison.Ordinal), "t");
        try
        {
            await rt.DecideAsync(d, System.Text.Json.JsonDocument.Parse("""{"subject":"s"}""").RootElement);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }

        Assert.Contains("\"model\":\"jev-1.14.0\"", handler.Requests[0].Body.ToJsonString(), StringComparison.Ordinal);
    }

    private static ModelCatalog Catalog() => new Runtime { GetEnvironmentVariable = _ => null }.Catalog;
}
