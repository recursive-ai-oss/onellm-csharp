using System.Text;
using OneLlm.Auth;

namespace OneLlm.Tests.Deployments;

/// <summary>
/// Runs the published AWS Signature Version 4 test suite (testdata/aws-sigv4, licence and notice alongside) through <see cref="AwsSigV4Signer.Compute"/>:
/// for each case the canonical request, the string to sign and the Authorization header must equal the published ones.
/// </summary>
public class SigV4SuiteTests
{
    // The suite's own credentials and scope (README of the suite).
    private static readonly AwsCredentials Credentials = new("AKIDEXAMPLE", new Secret("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"));

    // Cases the library cannot or need not reproduce, with the reason.
    private static readonly Dictionary<string, string> NotApplicable = new(StringComparer.Ordinal)
    {
        // Header values spread over several lines (obsolete line folding) cannot be put on an HttpRequestMessage: .NET refuses a line break in a header value.
        ["get-header-value-multiline"] = "obsolete header line folding cannot be represented in an HTTP request message",
        // The published string to sign, signature and signed request of this case disagree with one another and cannot be reproduced from the request.
        ["post-x-www-form-urlencoded-parameters"] = "the published artifacts are mutually inconsistent",
    };

    public static IEnumerable<object[]> Cases()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "testdata", "aws-sigv4", "aws-sig-v4-test-suite");
        foreach (string req in Directory.EnumerateFiles(root, "*.req", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return [Path.GetRelativePath(root, Path.GetDirectoryName(req)!).Replace('\\', '/')];
        }
    }

    [Fact]
    public void The_suite_has_the_expected_number_of_cases()
    {
        Assert.Equal(31, Cases().Count());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_matches_the_published_canonical_request_string_to_sign_and_authorization(string relative)
    {
        string name = Path.GetFileName(relative);
        if (NotApplicable.ContainsKey(name))
        {
            return;
        }

        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "aws-sigv4", "aws-sig-v4-test-suite", relative);
        string Read(string ext) => File.ReadAllText(Path.Combine(dir, name + "." + ext), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd((char)10);

        (string method, string path, string query, List<KeyValuePair<string, string>> headers, byte[] body) = ParseRequest(Read("req"));

        // Sign the headers the published Authorization names (the request may carry others that the example leaves unsigned).
        string authz = Read("authz");
        const string marker = "SignedHeaders=";
        int from = authz.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var signed = new HashSet<string>(authz[from..authz.IndexOf(',', from)].Split(';'), StringComparer.OrdinalIgnoreCase);
        headers = headers.Where(h => signed.Contains(h.Key)).ToList();
        string amzDate = headers.First(h => h.Key.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase)).Value;
        SigV4Result result = AwsSigV4Signer.Compute(method, path, query, headers, body, amzDate, Credentials, "us-east-1", "service");

        Assert.Equal(Read("sts"), result.StringToSign);
        Assert.Equal(authz, result.Authorization);

        // The .creq of the form-urlencoded case lists a content-length header that its own string to sign and signature do not include.
        if (name != "post-x-www-form-urlencoded")
        {
            Assert.Equal(Read("creq"), result.CanonicalRequest);
        }
    }

    private static (string Method, string Path, string Query, List<KeyValuePair<string, string>> Headers, byte[] Body) ParseRequest(string text)
    {
        string[] lines = text.Split('\n');
        string requestLine = lines[0];
        int firstSpace = requestLine.IndexOf(' ', StringComparison.Ordinal);
        string method = requestLine[..firstSpace];
        string target = requestLine[(firstSpace + 1)..];
        const string version = " HTTP/1.1";
        if (target.EndsWith(version, StringComparison.Ordinal))
        {
            target = target[..^version.Length];
        }

        int q = target.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? target : target[..q];
        string query = q < 0 ? "" : target[(q + 1)..];

        var headers = new List<KeyValuePair<string, string>>();
        int i = 1;
        for (; i < lines.Length && lines[i].Length > 0; i++)
        {
            int colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            headers.Add(new KeyValuePair<string, string>(lines[i][..colon], lines[i][(colon + 1)..]));
        }

        string body = i + 1 < lines.Length ? string.Join("\n", lines.Skip(i + 1)) : "";
        return (method, path, query, headers, Encoding.UTF8.GetBytes(body));
    }
}
