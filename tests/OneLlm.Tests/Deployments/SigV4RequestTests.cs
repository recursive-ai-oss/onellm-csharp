using System.Text.Json.Nodes;
using OneLlm.Auth;
using OneLlm.Providers.Anthropic;

namespace OneLlm.Tests.Deployments;

/// <summary>Signing a Bedrock request end to end: the signer's output on a real request message equals values computed independently.</summary>
public class SigV4RequestTests
{
    private static JsonObject Golden() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "aws-sigv4", "bedrock-invoke.json")))!;

    private static (HttpRequestMessage Request, byte[] Body, AwsSigV4Signer Signer) Build(JsonObject g, bool withToken)
    {
        var time = new ManualTimeProvider();
        time.Advance(DateTimeOffset.Parse(g["time"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) - time.GetUtcNow());
        var creds = new AwsCredentials(
            g["accessKeyId"]!.GetValue<string>(),
            new Secret(g["secretAccessKey"]!.GetValue<string>()),
            withToken ? new Secret(g["sessionToken"]!.GetValue<string>()) : null);
        byte[] body = System.Text.Encoding.UTF8.GetBytes(g["body"]!.GetValue<string>());
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(g["url"]!.GetValue<string>())) { Content = new ReadOnlyMemoryContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return (request, body, new AwsSigV4Signer(creds, g["region"]!.GetValue<string>(), g["service"]!.GetValue<string>(), time));
    }

    [Fact]
    public void A_signed_bedrock_request_has_exactly_the_authorization_computed_independently()
    {
        JsonObject g = Golden();
        (HttpRequestMessage request, byte[] body, AwsSigV4Signer signer) = Build(g, false);
        signer.Sign(request, body);
        Assert.Equal(g["plain"]!["authorization"]!.GetValue<string>(), request.Headers.GetValues("Authorization").Single());
        Assert.Equal("20260115T102030Z", request.Headers.GetValues("x-amz-date").Single());
    }

    [Fact]
    public void A_session_token_is_sent_and_signed()
    {
        JsonObject g = Golden();
        (HttpRequestMessage request, byte[] body, AwsSigV4Signer signer) = Build(g, true);
        signer.Sign(request, body);
        Assert.Equal(g["withSessionToken"]!["authorization"]!.GetValue<string>(), request.Headers.GetValues("Authorization").Single());
        Assert.Equal(g["sessionToken"]!.GetValue<string>(), request.Headers.GetValues("x-amz-security-token").Single());
    }

    [Fact]
    public void The_colon_stays_percent_encoded_on_the_wire_and_is_encoded_again_in_the_canonical_path()
    {
        JsonObject g = Golden();
        (HttpRequestMessage request, _, _) = Build(g, false);
        // What the HTTP stack sends is what was signed: the address must not have been normalized.
        Assert.Equal("/model/us.anthropic.claude-sonnet-4-5-20250929-v1%3A0/invoke", request.RequestUri!.PathAndQuery);
        var result = AwsSigV4Signer.Compute(
            "POST",
            request.RequestUri.PathAndQuery,
            "",
            [new("host", request.RequestUri.Authority), new("x-amz-date", "20260115T102030Z")],
            [],
            "20260115T102030Z",
            new AwsCredentials("AKIDEXAMPLE", new Secret("k")),
            "us-east-1",
            "bedrock");
        Assert.Contains("v1%253A0", result.CanonicalRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_again_replaces_the_date_and_authorization()
    {
        JsonObject g = Golden();
        (HttpRequestMessage request, byte[] body, AwsSigV4Signer signer) = Build(g, false);
        signer.Sign(request, body);
        string first = request.Headers.GetValues("Authorization").Single();
        request.Headers.Remove("Authorization");
        signer.Sign(request, body);
        Assert.Equal(first, request.Headers.GetValues("Authorization").Single());
        Assert.Single(request.Headers.GetValues("x-amz-date"));
    }

    [Fact]
    public void A_model_arn_is_sent_with_its_slashes_encoded_and_signed_as_sent()
    {
        var deployment = new BedrockDeployment("us-east-1", new AwsCredentials("AKIDEXAMPLE", new Secret("secret-secret")), new ManualTimeProvider());
        const string arn = "arn:aws:bedrock:us-east-1:123456789012:inference-profile/us.anthropic.claude-sonnet-4-5-20250929-v1:0";
        HttpRequestMessage r = deployment.CreateRequest(new AnthropicRequestInfo(AnthropicOperation.Messages, "claude-sonnet", false, null, arn), """{"model":"x","max_tokens":1}"""u8.ToArray());
        Assert.Equal("/model/arn%3Aaws%3Abedrock%3Aus-east-1%3A123456789012%3Ainference-profile%2Fus.anthropic.claude-sonnet-4-5-20250929-v1%3A0/invoke", r.RequestUri!.PathAndQuery);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", r.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_come_from_the_standard_variables_only()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS_ACCESS_KEY_ID"] = "AKIDEXAMPLE",
            ["AWS_SECRET_ACCESS_KEY"] = "secret-secret-secret",
            ["AWS_SESSION_TOKEN"] = "token-token-token",
            ["AWS_PROFILE"] = "ignored",
        };
        AwsCredentials c = AwsCredentials.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("AKIDEXAMPLE", c.AccessKeyId);
        Assert.NotNull(c.SessionToken);
        Assert.DoesNotContain("secret-secret", c.SecretAccessKey.ToString(), StringComparison.Ordinal);

        env.Remove("AWS_SECRET_ACCESS_KEY");
        var e = Assert.Throws<AuthException>(() => AwsCredentials.FromEnvironment(k => env.GetValueOrDefault(k)));
        Assert.DoesNotContain("AKIDEXAMPLE", e.Message, StringComparison.Ordinal);
        Assert.Contains("AWS_SECRET_ACCESS_KEY", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bedrock_api_key_in_the_environment_wins_and_is_sent_as_a_bearer_token()
    {
        var deployment = BedrockDeployment.FromEnvironment("us-east-1", new ManualTimeProvider(), lookup: k => k == "AWS_BEARER_TOKEN_BEDROCK" ? "bedrock-key-0000" : null);
        HttpRequestMessage r = deployment.CreateRequest(new AnthropicRequestInfo(AnthropicOperation.Messages, "claude-sonnet-4-6", true, null), """{"model":"x","stream":true,"max_tokens":1}"""u8.ToArray());
        Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
        Assert.False(r.Headers.Contains("x-amz-date"));
        Assert.EndsWith("/invoke-with-response-stream", r.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("us-east-1.evil.example/x")]
    [InlineData("US-EAST-1")]
    [InlineData("")]
    public void A_region_that_is_not_a_plain_region_name_is_refused(string region)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BedrockDeployment(region, new Secret("k-k-k-k-k-k"), new ManualTimeProvider()));
    }

    [Fact]
    public void The_body_loses_model_and_stream_and_gains_the_version_and_betas()
    {
        var d = new BedrockDeployment("eu-west-1", new Secret("key-key-key-key"), new ManualTimeProvider(), new BedrockOptions { Beta = ["interleaved-thinking-2025-05-14"] });
        HttpRequestMessage r = d.CreateRequest(new AnthropicRequestInfo(AnthropicOperation.Messages, "claude-sonnet-4-6", true, null), """{"model":"claude-sonnet-4-6","stream":true,"max_tokens":9,"temperature":1.50}"""u8.ToArray());
        string body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.Equal("""{"anthropic_version":"bedrock-2023-05-31","anthropic_beta":["interleaved-thinking-2025-05-14"],"max_tokens":9,"temperature":1.50}""", body);
        Assert.Equal("/model/anthropic.claude-sonnet-4-6/invoke-with-response-stream", r.RequestUri!.AbsolutePath);
    }
}
