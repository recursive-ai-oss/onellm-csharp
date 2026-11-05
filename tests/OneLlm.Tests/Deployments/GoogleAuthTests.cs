using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Auth;

namespace OneLlm.Tests.Deployments;

/// <summary>Service-account JWTs, the OAuth token exchange and token renewal.</summary>
public class GoogleAuthTests
{
    private const string Email = "ci-bot@example-project.iam.gserviceaccount.com";

    private static (string Json, RSA Key) Account(string? tokenUri = "http://localhost/token", string? projectId = "example-project")
    {
        RSA rsa = RSA.Create(2048);
        var o = new JsonObject
        {
            ["type"] = "service_account",
            ["client_email"] = Email,
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["private_key_id"] = "0123456789abcdef",
        };
        if (projectId is not null)
        {
            o["project_id"] = projectId;
        }

        if (tokenUri is not null)
        {
            o["token_uri"] = tokenUri;
        }

        return (o.ToJsonString(), rsa);
    }

    private static byte[] Decode(string part) => System.Buffers.Text.Base64Url.DecodeFromChars(part);

    private sealed class TokenServer : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, string ContentType, string Body)> Calls { get; } = [];

        public Func<int, (int Status, string Body)> Respond { get; set; } = n => (200, $"{{\"access_token\":\"ya29.test-token-{n}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}");

        public int Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            int n;
            lock (Calls)
            {
                Calls.Add((request.Method.Method, request.RequestUri!, request.Content.Headers.ContentType!.MediaType!, body));
                n = Calls.Count;
            }

            if (Delay > 0)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            (int status, string json) = Respond(n);
            return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void RS256_reproduces_the_signature_published_in_RFC_7515_appendix_A2()
    {
        JsonObject v = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "jwt", "rfc7515-a2.json")))!;
        JsonObject k = (JsonObject)v["jwk"]!;
        byte[] P(string n) => Decode(k[n]!.GetValue<string>());
        using RSA rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = P("n"), Exponent = P("e"), D = P("d"), P = P("p"), Q = P("q"), DP = P("dp"), DQ = P("dq"), InverseQ = P("qi") });
        Assert.Equal(v["signature"]!.GetValue<string>(), GoogleServiceAccount.SignRs256(rsa, v["signingInput"]!.GetValue<string>()));
    }

    [Fact]
    public void The_assertion_has_the_header_and_claims_Google_documents_and_a_signature_that_verifies()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            GoogleServiceAccount account = GoogleServiceAccount.FromJson(json);
            var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            string jwt = account.CreateAssertion(now);
            string[] parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);

            Assert.Equal("""{"alg":"RS256","typ":"JWT","kid":"0123456789abcdef"}""", Encoding.UTF8.GetString(Decode(parts[0])));
            long iat = now.ToUnixTimeSeconds();
            Assert.Equal(
                $$"""{"iss":"{{Email}}","scope":"https://www.googleapis.com/auth/cloud-platform","aud":"http://localhost/token","exp":{{iat + 3600}},"iat":{{iat}}}""",
                Encoding.UTF8.GetString(Decode(parts[1])));
            Assert.False(jwt.Contains('='));
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }

    [Fact]
    public void Without_token_uri_the_audience_is_the_documented_endpoint_and_a_subject_is_added_on_request()
    {
        (string json, RSA key) = Account(tokenUri: null);
        using (key)
        {
            GoogleServiceAccount account = GoogleServiceAccount.FromJson(json);
            Assert.Equal("https://oauth2.googleapis.com/token", account.TokenUri.AbsoluteUri);
            JsonObject claims = (JsonObject)JsonNode.Parse(Decode(account.CreateAssertion(DateTimeOffset.UnixEpoch, subject: "user@example.com").Split('.')[1]))!;
            Assert.Equal("https://oauth2.googleapis.com/token", claims["aud"]!.GetValue<string>());
            Assert.Equal("user@example.com", claims["sub"]!.GetValue<string>());
            Assert.Equal(["iss", "scope", "aud", "exp", "iat", "sub"], claims.Select(p => p.Key));
        }
    }

    [Fact]
    public async Task The_exchange_posts_the_jwt_bearer_grant_and_returns_the_token()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer();
            var time = new ManualTimeProvider();
            GoogleServiceAccount account = GoogleServiceAccount.FromJson(json);
            AccessTokenCache cache = account.CreateTokenCache(time, new HttpClient(server));
            Secret token = await cache.GetAsync();

            Assert.Equal("ya29.test-token-1", token.Value);
            var call = Assert.Single(server.Calls);
            Assert.Equal("POST", call.Method);
            Assert.Equal("http://localhost/token", call.Uri.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", call.ContentType);
            string[] fields = call.Body.Split('&');
            Assert.Equal("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", fields[0]);
            string assertion = fields[1]["assertion=".Length..];
            string[] parts = assertion.Split('.');
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            JsonObject claims = (JsonObject)JsonNode.Parse(Decode(parts[1]))!;
            Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
            Assert.Contains(cache.Secrets, s => s.Value == "ya29.test-token-1");
        }
    }

    [Fact]
    public async Task A_token_is_reused_until_five_minutes_before_it_expires_and_then_replaced()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer();
            var time = new ManualTimeProvider();
            AccessTokenCache cache = GoogleServiceAccount.FromJson(json).CreateTokenCache(time, new HttpClient(server));

            Assert.Equal("ya29.test-token-1", (await cache.GetAsync()).Value);
            time.Advance(TimeSpan.FromMinutes(54));
            Assert.Equal("ya29.test-token-1", (await cache.GetAsync()).Value);
            Assert.Single(server.Calls);

            time.Advance(TimeSpan.FromMinutes(1.5)); // 55.5 minutes: inside the five-minute margin
            Assert.Equal("ya29.test-token-2", (await cache.GetAsync()).Value);
            Assert.Equal(2, server.Calls.Count);
            Assert.Equal("ya29.test-token-2", cache.Current!.Value);

            // Both tokens stay in the scrub set; the new one's expiry is an hour from its issue time.
            Assert.Equal(2, cache.Secrets.Count(s => s.Value.StartsWith("ya29.", StringComparison.Ordinal)));
            time.Advance(TimeSpan.FromMinutes(61));
            Assert.Null(cache.Current);
        }
    }

    [Fact]
    public async Task Callers_that_find_the_token_stale_at_the_same_time_share_one_exchange()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer { Delay = 50 };
            AccessTokenCache cache = GoogleServiceAccount.FromJson(json).CreateTokenCache(new ManualTimeProvider(), new HttpClient(server));
            Secret[] all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync().AsTask()));
            Assert.All(all, s => Assert.Equal("ya29.test-token-1", s.Value));
            Assert.Single(server.Calls);
        }
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_cancel_the_exchange_for_the_others()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer { Delay = 100 };
            AccessTokenCache cache = GoogleServiceAccount.FromJson(json).CreateTokenCache(new ManualTimeProvider(), new HttpClient(server));
            using var cts = new CancellationTokenSource();
            Task<Secret> impatient = cache.GetAsync(cts.Token).AsTask();
            Task<Secret> patient = cache.GetAsync().AsTask();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
            Assert.Equal("ya29.test-token-1", (await patient).Value);
        }
    }

    [Fact]
    public async Task A_refused_exchange_is_an_auth_error_without_the_key_or_the_assertion_in_its_text()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer { Respond = _ => (400, """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""") };
            AccessTokenCache cache = GoogleServiceAccount.FromJson(json).CreateTokenCache(new ManualTimeProvider(), new HttpClient(server));
            AuthException e = await Assert.ThrowsAsync<AuthException>(async () => await cache.GetAsync());
            Assert.Equal(400, e.StatusCode);
            Assert.Contains("invalid_grant", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(server.Calls[0].Body.Split('=')[^1][..20], e.Message, StringComparison.Ordinal);
            Assert.Single(server.Calls); // a client error is not retried
        }
    }

    [Fact]
    public async Task A_server_error_is_retried_and_a_later_success_is_used()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            var server = new TokenServer { Respond = n => n == 1 ? (503, "{}") : (200, """{"access_token":"ya29.after-retry","expires_in":600}""") };
            var time = new ManualTimeProvider();
            AccessTokenCache cache = GoogleServiceAccount.FromJson(json).CreateTokenCache(time, new HttpClient(server));
            Task<Secret> pending = cache.GetAsync().AsTask();
            // The retry waits on the injected clock: wait for that timer to exist, then let the time pass.
            while (!time.CreatedDueTimes.Contains(TimeSpan.FromMilliseconds(500)))
            {
                await Task.Delay(5);
            }

            time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Equal("ya29.after-retry", (await pending).Value);
            Assert.Equal(2, server.Calls.Count);
        }
    }

    [Theory]
    [InlineData("""{"client_email":"a@b.c"}""")]
    [InlineData("""{"private_key":"-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n","client_email":"a@b.c"}""")]
    [InlineData("not json")]
    public void A_file_that_is_not_a_usable_key_fails_when_loaded_without_echoing_it(string json)
    {
        var e = Assert.Throws<AuthException>(() => GoogleServiceAccount.FromJson(json));
        Assert.DoesNotContain("AAAA", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_address_must_be_https_unless_it_is_this_machine()
    {
        (string json, RSA key) = Account(tokenUri: "http://token.example.com/token");
        using (key)
        {
            Assert.Throws<AuthException>(() => GoogleServiceAccount.FromJson(json));
        }
    }

    [Fact]
    public async Task A_token_delegate_is_cached_the_same_way()
    {
        var time = new ManualTimeProvider();
        int calls = 0;
        var cache = new AccessTokenCache(_ => ValueTask.FromResult(new AccessToken(new Secret("entra-token-" + ++calls), time.GetUtcNow().AddHours(1))), time);
        Assert.Equal("entra-token-1", (await cache.GetAsync()).Value);
        Assert.Equal("entra-token-1", (await cache.GetAsync()).Value);
        time.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal("entra-token-2", (await cache.GetAsync()).Value);
    }

    [Fact]
    public void The_project_comes_from_the_key_file_unless_given()
    {
        (string json, RSA key) = Account();
        using (key)
        {
            Assert.Equal("example-project", GoogleServiceAccount.FromJson(json).ProjectId);
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(Email, doc.RootElement.GetProperty("client_email").GetString());
    }
}
