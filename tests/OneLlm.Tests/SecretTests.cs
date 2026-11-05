namespace OneLlm.Tests;

public class SecretTests
{
    // Fake keys are built at run time so no key-shaped literal sits in the repository.
    private static string FakeKey() => "fake-" + new string('k', 6) + "-1234567890-" + new string('z', 6);

    [Fact]
    public void It_never_prints_its_value()
    {
        var secret = new Secret(FakeKey());
        Assert.Equal("[redacted]", secret.ToString());
        Assert.Equal("[redacted]", $"{secret}");
        Assert.DoesNotContain(secret.Value, secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_removes_the_whole_key_and_runs_of_it()
    {
        string key = FakeKey();
        var secret = new Secret("  " + key + "\n");
        Assert.Equal("bad key [redacted] given", secret.ScrubAll($"bad key {key} given"));
        // Some servers echo only a part of the key; any run of 8 characters that is in the key goes.
        string part = key[3..14];
        string cleaned = secret.ScrubAll($"invalid key ...{part}... ");
        Assert.DoesNotContain(part, cleaned, StringComparison.Ordinal);
        Assert.Contains("[redacted]", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_cuts_long_text_but_ScrubAll_does_not()
    {
        var secret = new Secret(FakeKey());
        string long500 = new('x', 500);
        Assert.Equal(400, secret.Scrub(long500).Length);
        Assert.Equal(500, secret.ScrubAll(long500).Length);
    }

    [Fact]
    public void Scrub_handles_several_secrets_and_short_keys()
    {
        var a = new Secret(FakeKey());
        var b = new Secret("tok-" + new string('q', 12));
        Assert.Equal("[redacted] and [redacted]", Secret.ScrubAll($"{a.Value} and {b.Value}", [a, b]));
        Assert.Equal("abc", new Secret("").ScrubAll("abc"));
        Assert.True(new Secret(" ").IsEmpty);
    }
}
