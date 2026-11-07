using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Http;

namespace OneLlm.Providers.Subscriptions;

// Only this layer sees the CLI credential document. It never includes parser, IO or upstream error text in an exception.
internal sealed class SubscriptionCredentials
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly string _path;
    private readonly string _provider;
    private readonly bool _claude;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly Uri _tokenEndpoint;

    internal SubscriptionCredentials(string directory, bool claude, TimeProvider time, SubscriptionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _claude = claude;
        _provider = claude ? "claude-code" : "codex";
        _path = Path.Combine(_directory, claude ? ".credentials.json" : "auth.json");
        _time = time;
        _http = options.HttpClient ?? SharedHttpClient.Instance;
        _tokenEndpoint = Validate(options.TokenEndpoint ?? new Uri(claude ? "https://platform.claude.com/v1/oauth/token" : "https://auth.openai.com/oauth/token"));
    }

    internal LiveSecrets Secrets { get; } = new();

    internal static Uri Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || uri.UserInfo.Length != 0)
        {
            throw new ArgumentException("Subscription endpoints require HTTPS (HTTP is permitted only on loopback).");
        }
        return uri;
    }

    internal AuthException Failure() => new(_provider, $"Subscription authentication failed for config directory '{_directory}'. Log in with the official CLI again.");

    internal async Task<Credential> GetAsync(string? rejectedAccess, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = s_gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // FileShare.None coordinates cooperating OneLLM processes. The CLI does not use this lock; do not run CLI login/refresh concurrently.
            await using FileStream lease = await LockAsync(cancellationToken).ConfigureAwait(false);
            JsonObject document = JsonNode.Parse(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false)) as JsonObject ?? throw Failure();
            JsonObject tokens = document[_claude ? "claudeAiOauth" : "tokens"] as JsonObject ?? throw Failure();
            Credential current = Read(tokens);
            bool rejected = rejectedAccess is not null && current.Access.Value == rejectedAccess;
            if (!rejected && !Expired(document, tokens, current.Access.Value))
            {
                return current;
            }

            if (_claude && tokens["refreshTokenExpiresAt"] is JsonValue refreshExpiry && refreshExpiry.GetValue<long>() > 0 && refreshExpiry.GetValue<long>() <= _time.GetUtcNow().ToUnixTimeMilliseconds())
            {
                throw Failure();
            }
            string refresh = Text(tokens, _claude ? "refreshToken" : "refresh_token");
            if (refresh.Length == 0)
            {
                throw Failure();
            }
            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = _claude ? "9d1c250a-e61b-44d9-88ed-5944d1962f5e" : "app_EMoamEEZ73f0CkXaXp7hrann",
            };
            if (_claude)
            {
                string scopes = ScopeText(tokens["scopes"]);
                body["scope"] = scopes.Length == 0 ? "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload" : scopes;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = JsonContent(body) };
            if (_claude)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.280");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure();
            }
            // Bound credentials responses; never preserve their body in an exception or a record.
            await response.Content.LoadIntoBufferAsync(64 * 1024, linked.Token).ConfigureAwait(false);
            JsonObject result = JsonNode.Parse(await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false)) as JsonObject ?? throw Failure();
            string access = Text(result, "access_token");
            if (access.Length == 0)
            {
                throw Failure();
            }
            Remember(result, "access_token", "refresh_token", "id_token");
            tokens[_claude ? "accessToken" : "access_token"] = access;
            foreach ((string wire, string cli) in _claude
                ? new[] { ("refresh_token", "refreshToken") }
                : new[] { ("refresh_token", "refresh_token"), ("id_token", "id_token") })
            {
                if (Text(result, wire) is { Length: > 0 } value)
                {
                    tokens[cli] = value;
                }
            }
            DateTimeOffset now = _time.GetUtcNow();
            if (_claude)
            {
                tokens["expiresAt"] = now.AddSeconds(result["expires_in"]?.GetValue<long>() ?? 3600).ToUnixTimeMilliseconds();
                if (result["refresh_token_expires_in"] is JsonValue lifetime)
                {
                    tokens["refreshTokenExpiresAt"] = now.AddSeconds(lifetime.GetValue<long>()).ToUnixTimeMilliseconds();
                }
                if (ScopeText(result["scope"]) is { Length: > 0 } scopes)
                {
                    tokens["scopes"] = new JsonArray(scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static s => (JsonNode?)JsonValue.Create(s)).ToArray());
                }
            }
            else
            {
                document["last_refresh"] = now.ToString("O", CultureInfo.InvariantCulture);
            }
            current = Read(tokens);
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or HttpRequestException or ArgumentOutOfRangeException)
        {
            throw Failure();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure();
        }
        finally
        {
            gate.Release();
        }
    }

    private Credential Read(JsonObject tokens)
    {
        Remember(tokens, "accessToken", "refreshToken", "access_token", "refresh_token", "id_token", "account_id");
        string access = Text(tokens, _claude ? "accessToken" : "access_token");
        if (access.Length == 0 || access.Any(static c => c <= 32 || c >= 127))
        {
            throw Failure();
        }
        string account = Text(tokens, "account_id");
        if (!_claude && account.Length == 0 && Claims(Text(tokens, "id_token")) is { } claims)
        {
            account = Text(claims, "chatgpt_account_id");
            if (account.Length == 0 && claims["https://api.openai.com/auth"] is JsonObject auth)
            {
                account = Text(auth, "chatgpt_account_id");
            }
            if (account.Length == 0 && claims["organizations"] is JsonArray organizations && organizations.FirstOrDefault() is JsonObject org)
            {
                account = Text(org, "id");
            }
            if (account.Length != 0)
            {
                tokens["account_id"] = account;
            }
        }
        if (account.Length != 0)
        {
            Secrets.Add(new Secret(account));
        }
        return new Credential(new Secret(access), account);
    }

    private void Remember(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (Text(obj, name) is { Length: > 0 } value)
            {
                Secrets.Add(new Secret(value));
            }
        }
    }

    private bool Expired(JsonObject document, JsonObject tokens, string access)
    {
        DateTimeOffset soon = _time.GetUtcNow().AddMinutes(2);
        if (_claude)
        {
            long expiry = tokens["expiresAt"]?.GetValue<long>() ?? 0;
            return expiry > 0 && soon.ToUnixTimeMilliseconds() >= expiry;
        }
        if (Claims(access)?["exp"] is JsonValue exp)
        {
            return soon.ToUnixTimeSeconds() >= exp.GetValue<long>();
        }
        return !DateTimeOffset.TryParse(Text(document, "last_refresh"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last) || soon >= last.AddMinutes(25);
    }

    private static JsonObject? Claims(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        try
        {
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            return JsonNode.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow().AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(_directory, ".onellm-oauth.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Directory.Exists(_directory) && _time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SaveAsync(JsonObject document, CancellationToken cancellationToken)
    {
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temp, options))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    internal static HttpContent JsonContent(JsonObject body) => new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
    private static string ScopeText(JsonNode? node) => node is JsonArray array
        ? string.Join(' ', array.Select(static n => n?.GetValue<string>() ?? ""))
        : node?.GetValue<string>() ?? "";

    private static string Text(JsonObject obj, string name) => obj[name]?.GetValue<string>() ?? "";
    internal sealed record Credential(Secret Access, string Account);
}
