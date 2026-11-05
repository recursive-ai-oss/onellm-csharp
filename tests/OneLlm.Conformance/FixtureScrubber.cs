using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OneLlm.Conformance;

/// <summary>
/// Removes secrets and account details from a fixture before it is written, and checks that a fixture on disk has none.
/// Scrubbing covers API keys and tokens, request ids, organization, project and account ids, email addresses, and authentication headers.
/// Prompts in fixtures are synthetic, so personal data is not expected; email addresses are scrubbed as a safety net.
/// </summary>
public static partial class FixtureScrubber
{
    /// <summary>The text that replaces a scrubbed value.</summary>
    public const string Placeholder = "[scrubbed]";

    // Request headers worth keeping: they change what the provider does. Everything else is dropped on record.
    private static readonly HashSet<string> s_requestHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "accept", "anthropic-version", "anthropic-beta", "openai-beta", "x-goog-api-client", "api-version",
    };

    // Response headers worth keeping: the content type and the headers clients act on. Request ids and account headers are never kept.
    private static readonly string[] s_responseHeaderPrefixes = ["content-type", "retry-after", "x-amzn-errortype", "x-ratelimit-", "anthropic-ratelimit-", "openai-processing-ms"];

    private static readonly string[] s_secretHeaderParts =
    [
        "authorization", "api-key", "apikey", "x-goog-api-key", "cookie", "token", "secret", "request-id", "requestid", "organization", "project",
        "account", "cf-ray", "x-amz-security-token", "x-amz-date", "signature",
    ];

    // JSON keys whose string values identify an account, project, organization, request or person.
    private static readonly HashSet<string> s_identifyingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "organization", "organization_id", "org_id", "project", "project_id", "account_id", "account", "request_id", "requestid", "x-request-id",
        "user", "user_id", "email", "api_key", "apikey", "access_token", "refresh_token", "client_secret", "private_key", "private_key_id", "client_email",
    };

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{10,}|sk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex ApiKeyOpenAiStyle();

    [GeneratedRegex(@"AIza[0-9A-Za-z_-]{20,}")]
    private static partial Regex ApiKeyGoogleStyle();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyId();

    [GeneratedRegex(@"\borg-[A-Za-z0-9]{12,}|\bproj_[A-Za-z0-9]{12,}|\breq_[A-Za-z0-9]{12,}|\bresp_req_[A-Za-z0-9]{12,}")]
    private static partial Regex AccountOrRequestId();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?!example\.(?:com|org|net)\b)[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyHeader();

    private static readonly Regex[] s_patterns =
    [
        ApiKeyOpenAiStyle(), ApiKeyGoogleStyle(), BearerToken(), JsonWebToken(), AwsAccessKeyId(), AccountOrRequestId(), EmailAddress(), PrivateKeyHeader(),
    ];

    /// <summary>True for header names that carry credentials or identify an account or request. Their values are never written or printed.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>Whether the header is sensitive.</returns>
    public static bool IsSecretHeaderName(string name) =>
        s_secretHeaderParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a request header is kept in recordings.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>Whether to record it.</returns>
    public static bool KeepRequestHeader(string name) => s_requestHeaderAllowList.Contains(name) && !IsSecretHeaderName(name);

    /// <summary>True when a response header is kept in recordings.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>Whether to record it.</returns>
    public static bool KeepResponseHeader(string name) =>
        !IsSecretHeaderName(name) && s_responseHeaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scrubs a fixture in place.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="extraSecrets">Literal secrets known to the caller (the key used for the recording); every occurrence is replaced.</param>
    public static void Scrub(RecordedFixture fixture, IEnumerable<string>? extraSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        string[] literals = [.. (extraSecrets ?? []).Where(s => s.Length >= 6)];
        foreach (RecordedExchange e in fixture.Exchanges)
        {
            ScrubHeaders(e.Request.Headers, KeepRequestHeader, literals);
            ScrubHeaders(e.Response.Headers, KeepResponseHeader, literals);
            e.Request.Path = ScrubText(e.Request.Path, literals);
            ScrubBody(e.Request.Body, literals);
            ScrubBody(e.Response.Body, literals);
            if (e.Response.Sse is { } chunks)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    chunks[i] = ScrubText(chunks[i], literals);
                }
            }
        }

        if (fixture.Description is not null)
        {
            fixture.Description = ScrubText(fixture.Description, literals);
        }
    }

    /// <summary>Scrubs a piece of text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="extraSecrets">Literal secrets to replace.</param>
    /// <returns>The text with keys, tokens, ids and email addresses replaced by <see cref="Placeholder"/>.</returns>
    public static string ScrubText(string text, IEnumerable<string>? extraSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string literal in extraSecrets ?? [])
        {
            if (literal.Length >= 6)
            {
                text = text.Replace(literal, Placeholder, StringComparison.Ordinal);
            }
        }

        foreach (Regex pattern in s_patterns)
        {
            text = pattern.Replace(text, Placeholder);
        }

        return text;
    }

    private static bool IsPlaceholderUploadAddress(string name, string value) =>
        name.Equals("x-goog-upload-url", StringComparison.OrdinalIgnoreCase) && value.EndsWith("upload_id=PLACEHOLDER", StringComparison.Ordinal);

    /// <summary>Finds what a fixture still contains that should have been scrubbed.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <returns>One line per finding, naming where it is and what kind it is, never the value. Empty when the fixture is clean.</returns>
    public static IReadOnlyList<string> FindViolations(RecordedFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var found = new List<string>();
        for (int i = 0; i < fixture.Exchanges.Count; i++)
        {
            RecordedExchange e = fixture.Exchanges[i];
            string at = $"exchange {i}";
            foreach (string name in e.Request.Headers.Keys)
            {
                if (!KeepRequestHeader(name))
                {
                    found.Add($"{at} request header '{name}' should not be recorded");
                }
            }

            foreach (string name in e.Response.Headers.Keys)
            {
                // A hand-written fixture may carry an upload address made of a placeholder; the recorder still never keeps one.
                if (!KeepResponseHeader(name) && !IsPlaceholderUploadAddress(name, e.Response.Headers[name]))
                {
                    found.Add($"{at} response header '{name}' should not be recorded");
                }
            }

            CheckText(e.Request.Path, $"{at} request path", found);
            CheckBody(e.Request.Body, $"{at} request body", found);
            CheckBody(e.Response.Body, $"{at} response body", found);
            if (e.Response.Sse is { } chunks)
            {
                for (int c = 0; c < chunks.Count; c++)
                {
                    CheckText(chunks[c], $"{at} response chunk {c}", found);
                }
            }
        }

        if (fixture.Description is not null)
        {
            CheckText(fixture.Description, "description", found);
        }

        return found;
    }

    private static void ScrubHeaders(SortedDictionary<string, string> headers, Func<string, bool> keep, string[] literals)
    {
        foreach (string name in headers.Keys.ToArray())
        {
            if (!keep(name))
            {
                headers.Remove(name);
            }
            else
            {
                headers[name] = ScrubText(headers[name], literals);
            }
        }
    }

    private static void ScrubBody(RecordedBody body, string[] literals)
    {
        if (body.Json is not null)
        {
            body.Json = ScrubNode(body.Json, literals);
        }

        if (body.Text is not null)
        {
            body.Text = ScrubText(body.Text, literals);
        }
    }

    private static JsonNode? ScrubNode(JsonNode? node, string[] literals)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj.ToArray())
                {
                    if (value is JsonValue v && v.TryGetValue(out string? s) && s is not null && s_identifyingKeys.Contains(key))
                    {
                        obj[key] = Placeholder;
                    }
                    else
                    {
                        // A container is scrubbed in place; assigning it back would fail because it already has this parent.
                        JsonNode? scrubbed = ScrubNode(value, literals);
                        if (!ReferenceEquals(scrubbed, value))
                        {
                            obj[key] = scrubbed;
                        }
                    }
                }

                return obj;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? item = array[i];
                    JsonNode? scrubbed = ScrubNode(item, literals);
                    if (!ReferenceEquals(scrubbed, item))
                    {
                        array[i] = scrubbed;
                    }
                }

                return array;
            case JsonValue val when val.TryGetValue(out string? text) && text is not null:
                return JsonValue.Create(ScrubText(text, literals));
            default:
                return node;
        }
    }

    private static void CheckBody(RecordedBody body, string at, List<string> found)
    {
        if (body.Json is not null)
        {
            CheckNode(body.Json, at + " $", found);
        }

        if (body.Text is not null)
        {
            CheckText(body.Text, at, found);
        }
    }

    private static void CheckNode(JsonNode? node, string path, List<string> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (value is JsonValue v && v.TryGetValue(out string? s) && s is not null && s_identifyingKeys.Contains(key) && s != Placeholder)
                    {
                        found.Add($"{path}.{key} identifies an account, request or person");
                    }
                    else
                    {
                        CheckNode(value, path + "." + key, found);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    CheckNode(array[i], $"{path}[{i}]", found);
                }

                break;
            case JsonValue val when val.TryGetValue(out string? text) && text is not null:
                CheckText(text, path, found);
                break;
        }
    }

    private static void CheckText(string text, string at, List<string> found)
    {
        foreach (Regex pattern in s_patterns)
        {
            if (pattern.IsMatch(text))
            {
                found.Add($"{at} matches the secret pattern {pattern}");
            }
        }
    }
}
