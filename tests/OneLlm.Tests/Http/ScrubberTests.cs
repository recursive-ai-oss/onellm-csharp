using OneLlm.Conformance;

namespace OneLlm.Tests.Http;

public class ScrubberTests
{
    // Secret-shaped values are assembled at run time so the repository holds none.
    private static readonly string s_openAiKey = "sk-" + new string('a', 30);
    private static readonly string s_anthropicKey = "sk-ant-" + "api03-" + new string('b', 30);
    private static readonly string s_googleKey = "AI" + "za" + new string('c', 35);
    private static readonly string s_org = "org-" + new string('d', 20);
    private static readonly string s_project = "proj_" + new string('e', 20);
    private static readonly string s_request = "req_" + new string('f', 20);
    private static readonly string s_jwt = "ey" + "J" + new string('g', 20) + "." + new string('h', 20) + "." + new string('i', 20);
    private static readonly string s_awsKey = "AK" + "IA" + new string('J', 16);

    private static RecordedFixture Dirty()
    {
        string json = $$$"""
            {"format":1,"exchanges":[{
              "request":{"method":"POST","path":"/v1/x?key={{{s_googleKey}}}",
                "headers":{"content-type":"application/json","authorization":"Bearer {{{s_openAiKey}}}","x-api-key":"{{{s_anthropicKey}}}"},
                "body":{"model":"m","user":"person-17","metadata":{"user_id":"u-1","organization":"{{{s_org}}}"},"prompt":"mail me at someone@private.test using {{{s_project}}}"}},
              "response":{"status":200,"headers":{"content-type":"application/json","openai-organization":"{{{s_org}}}","x-request-id":"{{{s_request}}}","set-cookie":"a=b","date":"now"},
                "body":{"id":"resp_1","request_id":"{{{s_request}}}","error":"bad token {{{s_jwt}}} and {{{s_awsKey}}}"}}
            }]}
            """;
        return RecordedFixture.Parse(json);
    }

    [Fact]
    public void Violations_are_found_and_named_without_printing_the_value()
    {
        IReadOnlyList<string> found = FixtureScrubber.FindViolations(Dirty());
        string all = string.Join("\n", found);
        Assert.Contains("request header 'authorization'", all, StringComparison.Ordinal);
        Assert.Contains("request header 'x-api-key'", all, StringComparison.Ordinal);
        Assert.Contains("response header 'openai-organization'", all, StringComparison.Ordinal);
        Assert.Contains("response header 'x-request-id'", all, StringComparison.Ordinal);
        Assert.Contains("$.user", all, StringComparison.Ordinal);
        Assert.Contains("$.metadata.user_id", all, StringComparison.Ordinal);
        Assert.Contains("$.request_id", all, StringComparison.Ordinal);
        Assert.Contains("request path", all, StringComparison.Ordinal);
        Assert.Contains("$.prompt", all, StringComparison.Ordinal);
        Assert.Contains("$.error", all, StringComparison.Ordinal);
        foreach (string secret in new[] { s_openAiKey, s_anthropicKey, s_googleKey, s_org, s_project, s_request, s_jwt, s_awsKey })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scrubbing_removes_every_violation_and_is_idempotent()
    {
        RecordedFixture fixture = Dirty();
        FixtureScrubber.Scrub(fixture);
        Assert.Empty(FixtureScrubber.FindViolations(fixture));

        string once = fixture.ToJson();
        foreach (string secret in new[] { s_openAiKey, s_anthropicKey, s_googleKey, s_org, s_project, s_request, s_jwt, s_awsKey, "someone@private.test", "person-17" })
        {
            Assert.DoesNotContain(secret, once, StringComparison.Ordinal);
        }

        // Things that are not secret stay: the model, the response object id, and the harmless headers.
        Assert.Contains("\"model\": \"m\"", once, StringComparison.Ordinal);
        Assert.Contains("resp_1", once, StringComparison.Ordinal);
        Assert.Contains("content-type", once, StringComparison.Ordinal);

        RecordedFixture again = RecordedFixture.Parse(once);
        FixtureScrubber.Scrub(again);
        Assert.Equal(once, again.ToJson());
    }

    [Fact]
    public void Literal_secrets_given_by_the_caller_are_removed_wherever_they_appear()
    {
        const string literal = "plain-literal-secret-value";
        var fixture = new RecordedFixture { Description = "uses " + literal };
        fixture.Exchanges.Add(new RecordedExchange { Request = { Path = "/p/" + literal, Body = { Text = "body " + literal } } });
        FixtureScrubber.Scrub(fixture, [literal]);
        Assert.DoesNotContain(literal, fixture.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Example_addresses_and_ordinary_words_are_left_alone()
    {
        Assert.Equal("write to user@example.com about sky-blue things", FixtureScrubber.ScrubText("write to user@example.com about sky-blue things"));
        Assert.Equal("a key sk-short is not one", FixtureScrubber.ScrubText("a key sk-short is not one"));
    }

    [Fact]
    public void Every_committed_fixture_is_scrubbed()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "testdata");
        int checkedFiles = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("\"exchanges\"", StringComparison.Ordinal))
            {
                continue;
            }

            checkedFiles++;
            IReadOnlyList<string> found = FixtureScrubber.FindViolations(RecordedFixture.Parse(text));
            Assert.True(found.Count == 0, $"{Path.GetRelativePath(root, file)}: {string.Join("; ", found)}");
        }

        Assert.True(checkedFiles > 0, "no fixtures found to check");
    }
}
