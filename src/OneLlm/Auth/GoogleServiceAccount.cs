using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneLlm.Http;

namespace OneLlm.Auth;

/// <summary>
/// A Google Cloud service account key (the JSON file Google issues) and the OAuth 2.0 JWT-bearer exchange that turns it into access tokens:
/// a JWT signed RS256 with the account's private key is posted to the token endpoint (RFC 7523; Google's "Using OAuth 2.0 for server to
/// server applications"). The key is read from a path or from JSON text you pass; gcloud configuration and the metadata server are never used.
/// </summary>
public sealed class GoogleServiceAccount
{
    /// <summary>The scope Vertex AI needs.</summary>
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    /// <summary>The token endpoint Google documents for access token requests.</summary>
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private const string JwtBearerGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private readonly Secret _privateKeyPem;

    private GoogleServiceAccount(string clientEmail, string? privateKeyId, string? projectId, Uri tokenUri, Secret privateKeyPem)
    {
        ClientEmail = clientEmail;
        PrivateKeyId = privateKeyId;
        ProjectId = projectId;
        TokenUri = tokenUri;
        _privateKeyPem = privateKeyPem;
    }

    /// <summary>The service account's email, the JWT issuer.</summary>
    public string ClientEmail { get; }

    /// <summary>The id of the signing key (<c>kid</c> in the JWT header), when the file has one.</summary>
    public string? PrivateKeyId { get; }

    /// <summary>The project the account belongs to (<c>project_id</c>), when the file has one. Vertex AI addresses need a project; this is the usual source.</summary>
    public string? ProjectId { get; }

    /// <summary>Where the JWT is exchanged. The file's <c>token_uri</c>, or <see cref="DefaultTokenUri"/>.</summary>
    public Uri TokenUri { get; }

    /// <summary>The private key as a secret, for scrubbing.</summary>
    public Secret PrivateKey => _privateKeyPem;

    /// <summary>Reads a service account key from JSON text.</summary>
    /// <param name="json">The contents of the key file.</param>
    /// <returns>The account.</returns>
    /// <exception cref="AuthException">The JSON is not a service account key: no <c>client_email</c> or <c>private_key</c>, a key that is not an RSA PKCS#8 PEM, or a token address that is not https. The message never contains the key.</exception>
    public static GoogleServiceAccount FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? email = Str(root, "client_email");
            string? pem = Str(root, "private_key");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pem))
            {
                throw Fail("the service account JSON needs client_email and private_key.");
            }

            string? tokenText = Str(root, "token_uri");
            Uri token = tokenText is null ? new Uri(DefaultTokenUri) : new Uri(tokenText);
            RequireSecureAddress(token);

            // The key is parsed once here so a bad file fails when it is loaded, not on the first request.
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportFromPem(pem);
            }

            return new GoogleServiceAccount(email, Str(root, "private_key_id"), Str(root, "project_id"), token, new Secret(pem));
        }
        catch (JsonException)
        {
            throw Fail("the service account file is not valid JSON.");
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException or UriFormatException)
        {
            throw Fail("the service account private key or token address is not valid.");
        }
    }

    /// <summary>Reads a service account key file.</summary>
    /// <param name="path">The path of the JSON key file.</param>
    /// <returns>The account.</returns>
    /// <exception cref="AuthException">The file cannot be read, or is not a service account key.</exception>
    public static GoogleServiceAccount FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Fail("the service account file could not be read.");
        }

        return FromJson(text);
    }

    /// <summary>Builds the signed JWT for a token request.</summary>
    /// <param name="now">The issue time. The JWT expires one hour later, the longest Google allows.</param>
    /// <param name="scope">The space-delimited scopes requested.</param>
    /// <param name="subject">A user to impersonate under domain-wide delegation, or null.</param>
    /// <returns>The JWT: <c>header.claims.signature</c>, each part base64url without padding.</returns>
    public string CreateAssertion(DateTimeOffset now, string scope = CloudPlatformScope, string? subject = null)
    {
        var header = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(header))
        {
            w.WriteStartObject();
            w.WriteString("alg", "RS256");
            w.WriteString("typ", "JWT");
            if (PrivateKeyId is not null)
            {
                w.WriteString("kid", PrivateKeyId);
            }

            w.WriteEndObject();
        }

        var claims = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(claims))
        {
            long iat = now.ToUnixTimeSeconds();
            w.WriteStartObject();
            w.WriteString("iss", ClientEmail);
            w.WriteString("scope", scope);
            // The audience for an access token request is always the documented endpoint (Google's service account OAuth guide).
            w.WriteString("aud", TokenUri.AbsoluteUri);
            w.WriteNumber("exp", iat + 3600);
            w.WriteNumber("iat", iat);
            if (subject is not null)
            {
                w.WriteString("sub", subject);
            }

            w.WriteEndObject();
        }

        string signingInput = Base64Url.EncodeToString(header.WrittenSpan) + "." + Base64Url.EncodeToString(claims.WrittenSpan);
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem.Value);
        return signingInput + "." + SignRs256(rsa, signingInput);
    }

    /// <summary>RS256 (RFC 7518 section 3.3): RSASSA-PKCS1-v1_5 with SHA-256 over the ASCII signing input, base64url encoded without padding.</summary>
    internal static string SignRs256(RSA rsa, string signingInput) =>
        Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    /// <summary>Creates a token cache that exchanges this account's JWT for access tokens and renews them before they expire.</summary>
    /// <param name="time">The clock for JWT times, token expiry and the request time limit.</param>
    /// <param name="http">The client for the token request; the shared client (redirects off) when null. Tests pass a client over a replay handler.</param>
    /// <param name="scope">The scopes; Vertex AI needs <see cref="CloudPlatformScope"/>, the default.</param>
    /// <returns>The cache. Its <see cref="AccessTokenCache.Secrets"/> also holds the private key, so both are scrubbed from error text.</returns>
    public AccessTokenCache CreateTokenCache(TimeProvider time, HttpClient? http = null, string scope = CloudPlatformScope)
    {
        ArgumentNullException.ThrowIfNull(time);
        HttpClient client = http ?? SharedHttpClient.Instance;
        var cache = new AccessTokenCache(ct => ExchangeAsync(client, time, scope, ct), time);
        cache.Secrets.Add(_privateKeyPem);
        return cache;
    }

    private async ValueTask<AccessToken> ExchangeAsync(HttpClient client, TimeProvider time, string scope, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            string assertion = CreateAssertion(time.GetUtcNow(), scope);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
            {
                Content = new StringContent(
                    "grant_type=" + Uri.EscapeDataString(JwtBearerGrant) + "&assertion=" + assertion,
                    Encoding.ASCII,
                    "application/x-www-form-urlencoded"),
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            HttpStatusCode status;
            string body;
            try
            {
                using HttpResponseMessage response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new AuthException("google", "the token request failed (" + e.GetType().Name + ").", null);
            }

            if ((int)status is >= 500 or 429 && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return ReadToken(status, body, time.GetUtcNow());
        }
    }

    private AccessToken ReadToken(HttpStatusCode status, string body, DateTimeOffset now)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if ((int)status is >= 200 and < 300 && Str(root, "access_token") is { Length: > 0 } token)
            {
                long seconds = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt64(out long s) ? s : 3600;
                return new AccessToken(new Secret(token), now.AddSeconds(seconds));
            }

            // OAuth error response (RFC 6749 section 5.2): error and error_description. The description never carries the assertion.
            string error = Str(root, "error") ?? "unknown_error";
            string description = Str(root, "error_description") ?? "";
            string text = $"the token request was refused ({(int)status} {error}{(description.Length > 0 ? ": " + description : "")}).";
            throw new AuthException("google", Secret.ScrubAll(text, [_privateKeyPem]), (int)status);
        }
        catch (JsonException)
        {
            throw new AuthException("google", $"the token endpoint answered {(int)status} with a body that is not JSON.", (int)status);
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void RequireSecureAddress(Uri address)
    {
        // The signed JWT must never travel in clear text; plain http is accepted only for loopback test servers.
        if (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        {
            throw Fail("the token address must be https.");
        }
    }

    private static AuthException Fail(string message) => new("google", message, null);
}
