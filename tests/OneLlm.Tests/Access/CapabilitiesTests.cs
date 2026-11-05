using System.Net;
using OneLlm.Access;
using OneLlm.Tests.Support;
using OneLlm.Tools;

namespace OneLlm.Tests.Access;

public class CapabilitiesTests
{
    private static bool Net(Capabilities c, string url) => c.IsNetworkAllowed(new Uri(url));

    [Fact]
    public void The_default_grants_the_working_directory_only()
    {
        using var s = new Scratch();
        Capabilities c = Capabilities.Default(s.Root);
        Assert.Equal([Path.GetFullPath(s.Root)], c.FileRoots);
        Assert.False(c.ShellEnabled);
        Assert.False(c.NetworkEnabled);
        Assert.False(Net(c, "https://example.com/"));
    }

    [Fact]
    public void Grants_compose_with_plus()
    {
        using var s = new Scratch();
        Capabilities c = Capabilities.Files(s.Root) + Capabilities.Shell + Capabilities.Network("api.example.com");
        Assert.True(c.ShellEnabled);
        Assert.True(Net(c, "https://api.example.com/x"));
        Assert.Equal(s.Root, c.WorkingDirectory);
        Assert.Contains("shell(open)", c.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com/x", true)]
    [InlineData("http://api.example.com:8080/x", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://evil-api.example.com/", false)]
    [InlineData("https://api.example.com.evil.net/", false)]
    [InlineData("https://API.EXAMPLE.COM./x", true)]
    [InlineData("http://api.example.com@evil.net/", false)]
    [InlineData("http://user:pw@api.example.com/", false)]
    [InlineData("ftp://api.example.com/", false)]
    [InlineData("file:///etc/passwd", false)]
    public void An_exact_host_entry_matches_only_that_host(string url, bool allowed) =>
        Assert.Equal(allowed, Net(Capabilities.Network("api.example.com"), url));

    [Theory]
    [InlineData("https://a.example.com/", true)]
    [InlineData("https://a.b.example.com/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://badexample.com/", false)]
    public void A_wildcard_covers_subdomains_but_not_the_apex(string url, bool allowed) =>
        Assert.Equal(allowed, Net(Capabilities.Network("*.example.com"), url));

    [Theory]
    [InlineData("https://example.com:8443/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com:9000/", false)]
    public void A_port_entry_covers_only_that_port(string url, bool allowed) =>
        Assert.Equal(allowed, Net(Capabilities.Network("example.com:8443"), url));

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://192.168.0.5/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    public void Any_public_access_refuses_loopback_private_and_metadata_addresses_in_every_spelling(string url) =>
        Assert.False(Net(Capabilities.AnyPublicHosts, url));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://93.184.216.34/")]
    [InlineData("http://[2606:2800:220:1:248:1893:25c8:1946]/")]
    public void Any_public_access_allows_public_names_and_addresses(string url) =>
        Assert.True(Net(Capabilities.AnyPublicHosts, url));

    [Fact]
    public void An_address_entry_grants_exactly_that_address_and_range()
    {
        Capabilities one = Capabilities.Network("127.0.0.1");
        Assert.True(Net(one, "http://127.0.0.1:5000/"));
        Assert.True(Net(one, "http://2130706433/"));
        Assert.False(Net(one, "http://127.0.0.2/"));
        Capabilities range = Capabilities.Network("10.0.0.0/8");
        Assert.True(Net(range, "http://10.9.8.7/"));
        Assert.False(Net(range, "http://11.0.0.1/"));
    }

    [Fact]
    public void A_name_that_resolves_to_a_private_address_is_refused_unless_an_address_entry_covers_it()
    {
        var uri = new Uri("https://files.example.com/");
        Capabilities byName = Capabilities.Network("files.example.com");
        Assert.True(byName.IsAddressAllowed(uri, IPAddress.Parse("93.184.216.34")));
        Assert.False(byName.IsAddressAllowed(uri, IPAddress.Parse("127.0.0.1")));
        Assert.False(byName.IsAddressAllowed(uri, IPAddress.Parse("169.254.169.254")));
        Assert.False(byName.IsAddressAllowed(uri, IPAddress.Parse("::ffff:10.0.0.1")));
        Capabilities withRange = Capabilities.Network("files.example.com", "10.0.0.0/8");
        Assert.True(withRange.IsAddressAllowed(uri, IPAddress.Parse("10.0.0.9")));
        Assert.False(withRange.IsAddressAllowed(uri, IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void The_localhost_entry_grants_loopback_only_for_that_name()
    {
        Capabilities c = Capabilities.Network("localhost");
        Assert.True(c.IsAddressAllowed(new Uri("http://localhost:3000/"), IPAddress.Loopback));
        Assert.True(c.IsAddressAllowed(new Uri("http://localhost:3000/"), IPAddress.IPv6Loopback));
        Assert.False(c.IsAddressAllowed(new Uri("http://localhost:3000/"), IPAddress.Parse("10.0.0.1")));
        Assert.False(Capabilities.AnyPublicHosts.IsAddressAllowed(new Uri("http://localhost/"), IPAddress.Loopback));
    }

    [Fact]
    public void Invalid_network_entries_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Capabilities.Network("*"));
        Assert.Throws<ArgumentException>(() => Capabilities.Network(""));
        Assert.Throws<ArgumentException>(() => Capabilities.Network("a.com:99999"));
        Assert.Throws<ArgumentException>(() => Capabilities.Network("10.0.0.0/40"));
    }

    // Sub-agent inheritance: whatever the child is allowed, the parent is allowed.
    [Fact]
    public void Intersect_never_widens_files()
    {
        using var s = new Scratch();
        s.Write("sub/f.txt", "x");
        Capabilities parent = Capabilities.Files(s.In("sub"));
        Capabilities wider = Capabilities.Files(s.Root);
        Capabilities child = parent.Intersect(wider);
        Assert.Equal([Path.GetFullPath(s.In("sub"))], child.FileRoots);
        Assert.Throws<CapabilityMissingException>(() => child.ResolveRead(s.In("other.txt")));
        Assert.Equal(child.FileRoots, wider.Intersect(parent).FileRoots);
        Assert.Empty(parent.Intersect(Capabilities.Files(s.Outside)).ReadRoots);
        Assert.Empty(Capabilities.ReadOnlyFiles(s.Root).Intersect(Capabilities.Files(s.Root)).WriteRoots);
        Assert.Single(Capabilities.ReadOnlyFiles(s.Root).Intersect(Capabilities.Files(s.Root)).ReadRoots);
    }

    [Fact]
    public void Intersect_never_widens_the_shell()
    {
        Capabilities open = Capabilities.Shell;
        Capabilities git = Capabilities.ShellCommands("git status", "ls");
        Capabilities ls = Capabilities.ShellCommands("ls", "cat");
        Assert.False(Capabilities.None.Intersect(open).ShellEnabled);
        Assert.False(open.Intersect(Capabilities.None).ShellEnabled);
        Assert.True(open.Intersect(git).ShellAccess.IsAllowList);
        Assert.Equal(["ls"], git.Intersect(ls).ShellAccess.Commands!);
        Assert.False(Capabilities.ShellCommands("rm").Intersect(ls).ShellEnabled);

        // A child that asks for "git" when the parent only has "git status" gets "git status".
        Capabilities child = git.Intersect(Capabilities.ShellCommands("git"));
        Assert.True(child.ShellAccess.AllowsCommand("git", ["status", "-s"]));
        Assert.False(child.ShellAccess.AllowsCommand("git", ["push"]));

        // Approval from the parent survives intersection with an open child; the child cannot drop it.
        Capabilities approval = Capabilities.ShellWithApproval((_, _) => ValueTask.FromResult(true));
        Capabilities merged = approval.Intersect(open);
        Assert.True(merged.ShellAccess.RequiresApproval);
        Assert.True(open.Intersect(approval).ShellAccess.RequiresApproval);
    }

    [Fact]
    public async Task Intersecting_two_approvers_asks_both()
    {
        int a = 0;
        int b = 0;
        Capabilities one = Capabilities.ShellWithApproval((_, _) => { a++; return ValueTask.FromResult(true); });
        Capabilities two = Capabilities.ShellWithApproval((_, _) => { b++; return ValueTask.FromResult(false); });
        ShellPolicy p = one.Intersect(two).ShellAccess;
        bool approved = await p.Approver!(new ShellRequest("ls", [], ".", false), CancellationToken.None);
        Assert.False(approved);
        Assert.Equal((1, 1), (a, b));
    }

    [Fact]
    public void Intersect_never_widens_the_network()
    {
        Capabilities parent = Capabilities.Network("*.example.com", "api.other.org");
        Capabilities child = parent.Intersect(Capabilities.Network("a.example.com", "evil.net", "api.other.org:443"));
        Assert.True(Net(child, "https://a.example.com/"));
        Assert.False(Net(child, "https://b.example.com/"));
        Assert.False(Net(child, "https://evil.net/"));
        Assert.True(Net(child, "https://api.other.org/"));
        Assert.False(Net(child, "https://api.other.org:8443/"));
        Assert.False(Net(Capabilities.None.Intersect(Capabilities.AnyPublicHosts), "https://example.com/"));
        Assert.True(Net(Capabilities.AnyPublicHosts.Intersect(Capabilities.Network("example.com")), "https://example.com/"));
        Assert.False(Net(Capabilities.AnyPublicHosts.Intersect(Capabilities.Network("example.com")), "https://other.com/"));

        // Public-only access does not stretch to an address entry.
        Assert.False(Net(Capabilities.AnyPublicHosts.Intersect(Capabilities.Network("127.0.0.1")), "http://127.0.0.1/"));
    }

    [Fact]
    public void Intersection_is_never_wider_than_either_side_over_a_grid_of_grants()
    {
        using var s = new Scratch();
        s.Write("a/f.txt", "x");
        s.Write("b/f.txt", "x");
        Capabilities[] grants =
        [
            Capabilities.None,
            Capabilities.Files(s.Root),
            Capabilities.Files(s.In("a")),
            Capabilities.ReadOnlyFiles(s.Root),
            Capabilities.Files(s.In("b")) + Capabilities.Shell,
            Capabilities.Files(s.In("a")) + Capabilities.ShellCommands("ls") + Capabilities.Network("*.example.com"),
            Capabilities.AnyPublicHosts + Capabilities.ShellWithApproval((_, _) => ValueTask.FromResult(true)),
            Capabilities.Network("a.example.com", "127.0.0.1"),
        ];
        string[] paths = [s.In("a/f.txt"), s.In("b/f.txt"), s.In("nope.txt"), Path.Combine(s.Outside, "z")];
        string[] urls = ["https://a.example.com/", "https://x.example.com/", "https://other.org/", "http://127.0.0.1/", "http://10.0.0.1/"];
        string[][] commands = [["ls", ""], ["rm", ""], ["git", "status"]];

        foreach (Capabilities p in grants)
        {
            foreach (Capabilities q in grants)
            {
                Capabilities c = p.Intersect(q);
                foreach (string path in paths)
                {
                    Assert.True(!Allows(c, path, false) || (Allows(p, path, false) && Allows(q, path, false)), $"read {path}");
                    Assert.True(!Allows(c, path, true) || (Allows(p, path, true) && Allows(q, path, true)), $"write {path}");
                }

                foreach (string url in urls)
                {
                    Assert.True(!Net(c, url) || (Net(p, url) && Net(q, url)), $"net {url}: {p} ^ {q}");
                }

                foreach (string[] cmd in commands)
                {
                    bool Cmd(Capabilities x) => x.ShellAccess.AllowsCommand(cmd[0], cmd[1].Length == 0 ? [] : [cmd[1]]);
                    Assert.True(!Cmd(c) || (Cmd(p) && Cmd(q)), $"shell {cmd[0]}");
                }

                Assert.True(!c.ShellAccess.RequiresApproval || p.ShellAccess.RequiresApproval || q.ShellAccess.RequiresApproval || !c.ShellEnabled);
                Assert.True(!(p.ShellAccess.RequiresApproval && c.ShellEnabled) || c.ShellAccess.RequiresApproval, "approval kept");
            }
        }

        static bool Allows(Capabilities c, string path, bool write)
        {
            try
            {
                _ = write ? c.ResolveWrite(path) : c.ResolveRead(path);
                return true;
            }
            catch (CapabilityMissingException)
            {
                return false;
            }
        }
    }
}

public class EgressClientTests
{
    private static async Task<(int Status, string Body)> Get(Capabilities caps, string url, params (string, string)[] headers)
    {
        using HttpResponseMessage r = await EgressClient.SendAsync(caps, HttpMethod.Get, new Uri(url), [.. headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2))], null, CancellationToken.None);
        return ((int)r.StatusCode, (await EgressClient.ReadBodyAsync(r, 1 << 20, CancellationToken.None)).Bytes is { } b ? System.Text.Encoding.UTF8.GetString(b) : "");
    }

    [Fact]
    public async Task A_granted_host_is_reached()
    {
        using var server = new StubServer(_ => new StubResponse(200, "hello"));
        (int status, string body) = await Get(Capabilities.Network("127.0.0.1"), server.BaseUri + "x");
        Assert.Equal((200, "hello"), (status, body));
    }

    [Fact]
    public async Task An_ungranted_host_is_refused_before_anything_is_sent()
    {
        using var server = new StubServer(_ => new StubResponse(200, "hello"));
        await Assert.ThrowsAsync<CapabilityMissingException>(() => Get(Capabilities.Network("example.com"), server.BaseUri + "x"));
        await Assert.ThrowsAsync<CapabilityMissingException>(() => Get(Capabilities.None, server.BaseUri + "x"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_an_ungranted_host_is_refused_and_never_requested()
    {
        using var hidden = new StubServer(_ => new StubResponse(200, "secret"));
        using var server = new StubServer(_ => new StubResponse(302, "", Headers: new() { ["Location"] = $"http://localhost:{hidden.Port}/s" }));
        await Assert.ThrowsAsync<CapabilityMissingException>(() => Get(Capabilities.Network("127.0.0.1"), server.BaseUri + "go"));
        Assert.Empty(hidden.Requests);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_granted_host_is_followed_but_credentials_do_not_cross_hosts()
    {
        using var target = new StubServer(_ => new StubResponse(200, "landed"));
        using var server = new StubServer(_ => new StubResponse(302, "", Headers: new() { ["Location"] = $"http://localhost:{target.Port}/landed" }));
        (int status, string body) = await Get(Capabilities.Network("127.0.0.1", "localhost"), server.BaseUri + "go",
            ("Authorization", "Bearer abc"), ("X-Api-Key", "k1"), ("Accept", "text/plain"));
        Assert.Equal((200, "landed"), (status, body));
        Assert.Equal("Bearer abc", server.Requests.Single().Headers["Authorization"]);
        StubRequest second = target.Requests.Single();
        Assert.False(second.Headers.ContainsKey("Authorization"));
        Assert.False(second.Headers.ContainsKey("X-Api-Key"));
        Assert.Equal("text/plain", second.Headers["Accept"]);
    }

    [Fact]
    public async Task Credentials_stay_on_a_same_origin_redirect()
    {
        using var server = new StubServer(r => r.Target == "/start"
            ? new StubResponse(302, "", Headers: new() { ["Location"] = "/final" })
            : new StubResponse(200, "ok"));
        await Get(Capabilities.Network("127.0.0.1"), server.BaseUri + "start", ("Authorization", "Bearer abc"));
        Assert.Equal("Bearer abc", server.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task A_redirect_loop_stops()
    {
        using var server = new StubServer(_ => new StubResponse(302, "", Headers: new() { ["Location"] = "/again" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(Capabilities.Network("127.0.0.1"), server.BaseUri + "x"));
        Assert.Equal(6, server.Requests.Count);
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_refused_at_connect_time_under_public_access()
    {
        using var server = new StubServer(_ => new StubResponse(200, "internal"));
        // "localhost" passes the name check under public access, then resolves to a loopback address the grants do not cover.
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(Capabilities.AnyPublicHosts, $"http://localhost:{server.Port}/"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_localhost_entry_allows_the_loopback_connection()
    {
        using var server = new StubServer(_ => new StubResponse(200, "ok"));
        (int status, _) = await Get(Capabilities.Network("localhost"), $"http://localhost:{server.Port}/");
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task A_post_keeps_its_body_and_a_303_turns_into_a_get()
    {
        using var server = new StubServer(r => r.Target == "/post"
            ? new StubResponse(303, "", Headers: new() { ["Location"] = "/result" })
            : new StubResponse(200, "done"));
        using HttpResponseMessage r = await EgressClient.SendAsync(Capabilities.Network("127.0.0.1"), HttpMethod.Post, new Uri(server.BaseUri, "post"), [new("Content-Type", "application/json")], "{\"a\":1}", CancellationToken.None);
        Assert.Equal("{\"a\":1}", server.Requests[0].Body);
        Assert.Equal("application/json", server.Requests[0].Headers["Content-Type"]);
        Assert.Equal("GET", server.Requests[1].Method);
        Assert.Equal("", server.Requests[1].Body);
    }
}
