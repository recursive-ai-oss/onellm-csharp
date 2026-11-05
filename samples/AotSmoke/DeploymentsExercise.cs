using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneLlm;
using OneLlm.Auth;
using OneLlm.Catalog;
using OneLlm.Http;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;

namespace OneLlm.AotSmoke;

/// <summary>
/// Drives the cloud deployments against canned HTTP answers so a NativeAOT build proves their signing (SigV4, RS256 JWT), the binary event
/// stream reader and the request paths work without reflection or a network. Call <see cref="RunAsync"/> from the program.
/// </summary>
internal static class DeploymentsExercise
{
    private const string ClaudeAnswer = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"Hello!"}],"stop_reason":"end_turn","usage":{"input_tokens":14,"output_tokens":4}}""";
    private const string GeminiAnswer = """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello!"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":2,"totalTokenCount":9},"responseId":"r0"}""";
    private const string ResponsesAnswer = """{"id":"resp_1","object":"response","status":"completed","model":"gpt-6.1-terra","output":[{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello!","annotations":[]}]}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}""";

    public static async Task<bool> RunAsync()
    {
        var time = TimeProvider.System;
        var request = new ChatRequest { Model = "claude-sonnet-5-5", Messages = [Message.FromUser("Say hello.")], MaxOutputTokens = 64 };

        // SigV4: signing a request sets the headers the service checks.
        var creds = new AwsCredentials("AKIDEXAMPLE", new Secret("smoke-secret-0000"));
        using (var signed = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/model/m/invoke"))
        {
            new AwsSigV4Signer(creds, "us-east-1", "bedrock", time).Sign(signed, "{}"u8);
            Check(signed.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth) && auth.Single().Contains("Signature=", StringComparison.Ordinal), "sigv4 authorization");
        }

        // JWT: a generated key signs an assertion that its public key verifies.
        using RSA rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();
        string keyJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_email"] = "smoke@example.iam.gserviceaccount.com",
            ["private_key"] = pem,
            ["project_id"] = "smoke-project",
        }, DeploymentsJson.Default.DictionaryStringString);
        GoogleServiceAccount account = GoogleServiceAccount.FromJson(keyJson);
        string jwt = account.CreateAssertion(time.GetUtcNow());
        string[] parts = jwt.Split('.');
        byte[] sig = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/').PadRight((parts[2].Length + 3) / 4 * 4, '='));
        Check(parts.Length == 3 && rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "jwt signature verifies");

        // Bedrock, whole and streamed through the binary event stream.
        var bedrock = new BedrockDeployment("us-east-1", creds, time, new BedrockOptions { InferenceProfile = "us", BaseAddress = new Uri("http://localhost/") });
        using var http = new HttpClient(new Canned());
        var bedrockProvider = new AnthropicProvider(bedrock, time, new AnthropicOptions { Http = http, Retry = RetryPolicy.None });
        ChatResponse whole = await bedrockProvider.SendAsync(request);
        Check(whole.Message.GetAssistantText() == "Hello!", "bedrock whole");
        var acc = new MessageAccumulator();
        await foreach (StreamEvent e in bedrockProvider.StreamAsync(request))
        {
            acc.Add(e);
        }

        Check(acc.ToMessage().GetAssistantText() == "Hi", "bedrock event stream");

        // Vertex, Claude and Gemini, with tokens from a delegate.
        var vertex = new VertexAnthropicDeployment(
            "smoke-project", "us-east5", _ => ValueTask.FromResult(new AccessToken(new Secret("ya29.smoke"), time.GetUtcNow().AddHours(1))), time,
            new VertexAnthropicOptions { BaseAddress = new Uri("http://localhost/") });
        ChatResponse vc = await new AnthropicProvider(vertex, time, new AnthropicOptions { Http = http, Retry = RetryPolicy.None }).SendAsync(request);
        Check(vc.Message.GetAssistantText() == "Hello!", "vertex claude");

        var tokens = new AccessTokenCache(_ => ValueTask.FromResult(new AccessToken(new Secret("ya29.smoke"), time.GetUtcNow().AddHours(1))), time);
        var gemini = new GeminiProvider(new GeminiOptions
        {
            Time = time,
            Auth = new GoogleBearerAuth(tokens),
            Endpoint = new VertexGeminiEndpoint("smoke-project", "us-central1", new Uri("http://localhost/")),
            Deployment = "vertex",
            Http = http,
            Retry = RetryPolicy.None,
            Catalog = ModelCatalog.Create([GeminiProvider.GetCatalogLayer()]),
        });
        ChatResponse vg = await gemini.SendAsync(new ChatRequest { Model = "gemini-2.5-flash", Messages = [Message.FromUser("Say hello.")] });
        Check(vg.Message.GetAssistantText() == "Hello!", "vertex gemini");

        // Azure OpenAI.
        var azure = new OpenAIProvider(
            new OpenAIOptions
            {
                Endpoint = new AzureOpenAIEndpoint(new Uri("http://localhost/"), new Secret("azure-smoke-key")),
                HttpClient = http,
                Catalog = ModelCatalog.Create([OpenAICatalog.Shipped()]),
                Retry = RetryPolicy.None,
            },
            time);
        ChatResponse az = await azure.SendAsync(new ChatRequest { Model = "gpt-6.1-terra", Messages = [Message.FromUser("Say hello.")] });
        Check(az.Message.GetAssistantText() == "Hello!", "azure openai");
        Console.WriteLine("deployments smoke ok");
        return true;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException("deployments smoke failed: " + what);
        }
    }

    private static byte[] Str(string name, string value)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        byte[] v = Encoding.UTF8.GetBytes(value);
        return [(byte)n.Length, .. n, 7, (byte)(v.Length >> 8), (byte)v.Length, .. v];
    }

    // One event stream message (prelude, headers, payload, CRC-32 of prelude and of the whole message).
    private static byte[] Chunk(string eventJson)
    {
        byte[] hb = [.. Str(":event-type", "chunk"), .. Str(":content-type", "application/json"), .. Str(":message-type", "event")];
        byte[] payload = Encoding.UTF8.GetBytes("{\"bytes\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(eventJson)) + "\"}");
        int total = 12 + hb.Length + payload.Length + 4;
        var bytes = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)hb.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), Crc32(bytes.AsSpan(0, 8)));
        hb.CopyTo(bytes, 12);
        payload.CopyTo(bytes, 12 + hb.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(total - 4), Crc32(bytes.AsSpan(0, total - 4)));
        return bytes;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class Canned : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/invoke-with-response-stream", StringComparison.Ordinal))
            {
                byte[] bytes =
                [
                    .. Chunk("""{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-sonnet-5-5","usage":{"input_tokens":10,"output_tokens":1}}}"""),
                    .. Chunk("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                    .. Chunk("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}"""),
                    .. Chunk("""{"type":"content_block_stop","index":0}"""),
                    .. Chunk("""{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}"""),
                    .. Chunk("""{"type":"message_stop"}"""),
                ];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }

            string body = path.Contains("/publishers/google/", StringComparison.Ordinal) ? GeminiAnswer
                : path.EndsWith("/responses", StringComparison.Ordinal) ? ResponsesAnswer
                : ClaudeAnswer;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DeploymentsJson : System.Text.Json.Serialization.JsonSerializerContext;
