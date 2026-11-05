using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace OneLlm.Auth;

/// <summary>What <see cref="AwsSigV4Signer"/> computed for a request, step by step, so each step can be checked against published vectors.</summary>
/// <param name="CanonicalRequest">The canonical request (the text that is hashed into the string to sign).</param>
/// <param name="StringToSign">The string to sign.</param>
/// <param name="Signature">The signature, lower-case hex.</param>
/// <param name="SignedHeaders">The semicolon-separated names of the signed headers.</param>
/// <param name="Authorization">The value of the <c>Authorization</c> header.</param>
public readonly record struct SigV4Result(string CanonicalRequest, string StringToSign, string Signature, string SignedHeaders, string Authorization);

/// <summary>
/// AWS Signature Version 4 (header-based) on the base class library's HMAC and SHA-256. The algorithm is the one in the AWS
/// documentation "Signing AWS API requests"; <c>Compute</c> is checked against the published Signature Version 4 test suite.
/// </summary>
/// <remarks>
/// The path is canonicalized the way services other than S3 require: dot segments and empty segments are removed and every segment is
/// URI-encoded once more on top of the encoding the request line already has, so a model id with a colon is sent as <c>%3A</c> and
/// signed as <c>%253A</c>. Thread-safe.
/// </remarks>
public sealed class AwsSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    // Headers that are signed when present on a request. Others (user agent, accept-encoding, content-length) are added or changed by the HTTP
    // stack after signing and so cannot be signed; the documentation requires host and every x-amz-* header and allows the rest to be chosen.
    private static readonly string[] SignedNames = ["accept", "content-type"];

    private readonly AwsCredentials _credentials;
    private readonly string _region;
    private readonly string _service;
    private readonly TimeProvider _time;

    /// <summary>Creates a signer.</summary>
    /// <param name="credentials">The keys to sign with.</param>
    /// <param name="region">The region in the credential scope, for example <c>us-east-1</c>.</param>
    /// <param name="service">The service in the credential scope, for example <c>bedrock</c>.</param>
    /// <param name="time">The clock for the request time. Required: the library never reads the system clock on its own.</param>
    public AwsSigV4Signer(AwsCredentials credentials, string region, string service, TimeProvider time)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _region = region;
        _service = service;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Signs a request in place: adds <c>x-amz-date</c>, <c>x-amz-security-token</c> when the credentials have a session token, and
    /// <c>Authorization</c>. The host, <c>content-type</c> and <c>accept</c> headers present on the request, every <c>x-amz-*</c> and
    /// <c>x-amzn-*</c> header are signed.
    /// </summary>
    /// <param name="request">The request. It needs an absolute <see cref="HttpRequestMessage.RequestUri"/>; its content headers are read, its content is not.</param>
    /// <param name="body">The exact body bytes that will be sent.</param>
    /// <exception cref="ArgumentException">The request has no absolute address.</exception>
    public void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = request.RequestUri ?? throw new ArgumentException("The request has no address.", nameof(request));
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The request address must be absolute.", nameof(request));
        }

        string amzDate = FormatDate(_time.GetUtcNow());
        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-security-token");
        if (_credentials.SessionToken is { } token)
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", token.Value);
        }

        var headers = new List<KeyValuePair<string, string>> { new("host", uri.Authority) };
        AddSignable(headers, request.Headers);
        if (request.Content is not null)
        {
            AddSignable(headers, request.Content.Headers);
        }

        string pathAndQuery = uri.PathAndQuery;
        int q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        string query = q < 0 ? "" : pathAndQuery[(q + 1)..];
        SigV4Result result = Compute(request.Method.Method, path, query, headers, body, amzDate, _credentials, _region, _service);
        request.Headers.TryAddWithoutValidation("Authorization", result.Authorization);
    }

    /// <summary>The request time in the form SigV4 uses: <c>yyyyMMddTHHmmssZ</c>.</summary>
    /// <param name="utc">The time.</param>
    /// <returns>The text.</returns>
    public static string FormatDate(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Computes the signature of a described request without touching any HTTP types.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path as it appears in the request line (with its own percent-encoding, if any).</param>
    /// <param name="query">The query string without the leading <c>?</c>, as sent.</param>
    /// <param name="headers">The headers to sign, including <c>host</c> and <c>x-amz-date</c> (and <c>x-amz-security-token</c> if used). Names are case-insensitive; repeated names are joined with commas in order.</param>
    /// <param name="body">The body bytes.</param>
    /// <param name="amzDate">The request time as <c>yyyyMMddTHHmmssZ</c>; it must equal the <c>x-amz-date</c> header.</param>
    /// <param name="credentials">The keys.</param>
    /// <param name="region">The credential scope region.</param>
    /// <param name="service">The credential scope service.</param>
    /// <returns>Each step and the final header value.</returns>
    public static SigV4Result Compute(
        string method,
        string path,
        string query,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        ReadOnlySpan<byte> body,
        string amzDate,
        AwsCredentials credentials,
        string region,
        string service)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string payloadHash = Hex(SHA256.HashData(body));
        string canonical = BuildCanonicalRequest(method, path, query, headers, payloadHash, out string signedHeaders);
        string date = amzDate[..8];
        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";

        byte[] key = Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey.Value);
        byte[] kDate = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(date));
        byte[] kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        byte[] kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        byte[] kSigning = HMACSHA256.HashData(kService, "aws4_request"u8);
        string signature = Hex(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));
        CryptographicOperations.ZeroMemory(key);

        string authorization = $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new SigV4Result(canonical, stringToSign, signature, signedHeaders, authorization);
    }

    private static void AddSignable(List<KeyValuePair<string, string>> into, HttpHeaders from)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> h in from)
        {
            string name = h.Key.ToLowerInvariant();
            if (name.StartsWith("x-amz", StringComparison.Ordinal) || Array.IndexOf(SignedNames, name) >= 0)
            {
                foreach (string v in h.Value)
                {
                    into.Add(new KeyValuePair<string, string>(name, v));
                }
            }
        }
    }

    private static string BuildCanonicalRequest(
        string method,
        string path,
        string query,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string payloadHash,
        out string signedHeaders)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append('\n');
        sb.Append(CanonicalPath(path)).Append('\n');
        sb.Append(CanonicalQuery(query)).Append('\n');

        // Group by lower-case name, keep the order values were given in, join with commas, sort by name.
        var grouped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> h in headers)
        {
            string name = h.Key.Trim().ToLowerInvariant();
            if (!grouped.TryGetValue(name, out List<string>? values))
            {
                grouped[name] = values = [];
            }

            values.Add(CollapseSpaces(h.Value));
        }

        foreach (KeyValuePair<string, List<string>> g in grouped)
        {
            sb.Append(g.Key).Append(':').Append(string.Join(',', g.Value)).Append('\n');
        }

        sb.Append('\n');
        signedHeaders = string.Join(';', grouped.Keys);
        sb.Append(signedHeaders).Append('\n').Append(payloadHash);
        return sb.ToString();
    }

    // Trim, and fold runs of spaces and tabs inside the value into one space (documentation: "Create a canonical request", canonical headers).
    private static string CollapseSpaces(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value.Trim())
        {
            if (c is ' ' or '\t')
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string CanonicalPath(string path)
    {
        if (path.Length == 0)
        {
            return "/";
        }

        // Remove "." and empty segments and resolve ".." before encoding, then encode each remaining segment.
        string[] parts = path.Split('/');
        var stack = new List<string>(parts.Length);
        bool trailing = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string seg = parts[i];
            bool last = i == parts.Length - 1;
            switch (seg)
            {
                case "" or ".":
                    trailing = last;
                    break;
                case "..":
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    trailing = last;
                    break;
                default:
                    stack.Add(seg);
                    trailing = false;
                    break;
            }
        }

        var sb = new StringBuilder("/");
        for (int i = 0; i < stack.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('/');
            }

            AppendEncoded(sb, Encoding.UTF8.GetBytes(stack[i]));
        }

        if (trailing && stack.Count > 0)
        {
            sb.Append('/');
        }

        return sb.ToString();
    }

    private static string CanonicalQuery(string query)
    {
        if (query.Length == 0)
        {
            return "";
        }

        var pairs = new List<(string Name, string Value)>();
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string rawName = eq < 0 ? part : part[..eq];
            string rawValue = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add((EncodeDecoded(rawName), EncodeDecoded(rawValue)));
        }

        pairs.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
        });
        var sb = new StringBuilder(query.Length + 8);
        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('&');
            }

            sb.Append(pairs[i].Name).Append('=').Append(pairs[i].Value);
        }

        return sb.ToString();
    }

    // A query component is percent-decoded and then encoded in the canonical form, so "%7E" and "~" sign the same.
    private static string EncodeDecoded(string raw)
    {
        var bytes = new List<byte>(raw.Length);
        byte[] utf8 = Encoding.UTF8.GetBytes(raw);
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == '%' && i + 2 < utf8.Length && IsHex(utf8[i + 1]) && IsHex(utf8[i + 2]))
            {
                bytes.Add((byte)((HexValue(utf8[i + 1]) << 4) | HexValue(utf8[i + 2])));
                i += 2;
            }
            else
            {
                bytes.Add(utf8[i]);
            }
        }

        var sb = new StringBuilder(bytes.Count + 8);
        AppendEncoded(sb, bytes.ToArray());
        return sb.ToString();
    }

    // URI-encode every byte except the unreserved characters A-Z a-z 0-9 - _ . ~ (documentation: UriEncode), with upper-case hex.
    private static void AppendEncoded(StringBuilder sb, byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
            }
        }
    }

    private static bool IsHex(byte b) => b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');

    private static int HexValue(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}
