using OneLlm.Catalog;
using OneLlm.LiveSmoke;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Testing;

namespace OneLlm.Tests.GateTools;

/// <summary>The live-smoke console's spend caps and scrubbing, rehearsed on the scripted provider so no key or network is involved.</summary>
public class LiveSmokeTests
{
    private const string CatalogYaml = """
        name: scripted
        modelPatterns: ["demo-*"]
        models:
          - name: demo-1
            contextWindow: 100000
            maxOutputTokens: 1000
            pricing: { asOf: "2026-11-01", source: "https://example.test/prices", inputPerMillion: 1.00, outputPerMillion: 2.00 }
        """;

    private static ModelCatalog Catalog() => ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, CatalogYaml)]);

    private static CostEstimate Worst(decimal dollars) => new(100, TokenCountTier.Estimate, 100, dollars, []);

    [Fact]
    public void The_budget_refuses_a_call_that_could_pass_the_cap_and_counts_what_is_in_flight()
    {
        var budget = new SmokeBudget("p", 0.010m);

        Assert.True(budget.TryReserve(Worst(0.006m), out decimal first, out _));
        // 0.006 is in flight, so a second 0.006 call could reach 0.012.
        Assert.False(budget.TryReserve(Worst(0.006m), out _, out string? reason));
        Assert.Contains("cap", reason, StringComparison.Ordinal);

        budget.Settle(first, 0.002m);
        Assert.Equal(0.002m, budget.Spent);
        Assert.True(budget.TryReserve(Worst(0.006m), out decimal second, out _));
        budget.Settle(second, 0.006m);
        // 0.008 spent: a call that could cost 0.003 would reach 0.011.
        Assert.False(budget.TryReserve(Worst(0.003m), out _, out _));
        Assert.True(budget.TryReserve(Worst(0.002m), out _, out _));
    }

    [Fact]
    public void A_call_whose_cost_cannot_be_bounded_is_refused()
    {
        var budget = new SmokeBudget("p", 1m);
        Assert.False(budget.TryReserve(new CostEstimate(10, TokenCountTier.Estimate, null, null, []), out _, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void An_unknown_actual_cost_is_charged_at_the_reserved_worst_case()
    {
        var budget = new SmokeBudget("p", 1m);
        Assert.True(budget.TryReserve(Worst(0.25m), out decimal r, out _));
        budget.Settle(r, null);
        Assert.Equal(0.25m, budget.Spent);
    }

    [Fact]
    public async Task The_runner_does_not_send_a_call_that_could_pass_the_cap()
    {
        var provider = new ScriptedProvider { Fallback = _ => ScriptStep.TextWithUsage("pong", 200, 200) };
        var lines = new List<string>();
        // One chat call is worth at most (25 + 300) input and 300 output tokens: about $0.0009. A cap of $0.0012 allows one call (which then
        // really costs $0.0006), and the second is refused before it is sent.
        var budget = new SmokeBudget("scripted", 0.0012m);
        var runner = new SmokeRunner(new SmokeTarget { Name = "scripted", Chat = provider, ChatModel = "demo-1" }, budget, Catalog(), new Scrubber([]), lines.Add);

        await runner.RunAsync();

        Assert.Equal(SmokeStatus.Pass, runner.Results[0].Status);
        Assert.All(runner.Results.Skip(1), r => Assert.Equal(SmokeStatus.Skip, r.Status));
        Assert.Single(provider.Requests);
        Assert.True(budget.Spent <= 0.0012m);
        Assert.Contains(lines, l => l.StartsWith("SKIP", StringComparison.Ordinal) && l.Contains("not sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cap_of_nothing_sends_nothing()
    {
        var provider = new ScriptedProvider { Fallback = _ => ScriptStep.Text("pong") };
        var runner = new SmokeRunner(new SmokeTarget { Name = "scripted", Chat = provider, ChatModel = "demo-1" }, new SmokeBudget("scripted", 0m), Catalog(), new Scrubber([]), _ => { });

        await runner.RunAsync();

        Assert.Empty(provider.Requests);
        Assert.All(runner.Results, r => Assert.Equal(SmokeStatus.Skip, r.Status));
    }

    [Fact]
    public void The_scrubber_removes_given_keys_key_shapes_and_identifiers()
    {
        // Key-shaped values are assembled at run time so no key-shaped literal sits in the repository.
        string openai = "sk-" + "proj-" + new string('a', 30);
        string google = "AIza" + "Sy" + new string('b', 33);
        string known = "plain-secret-" + new string('c', 12);
        var scrub = new Scrubber([known]);

        string body = $"{{\"error\":{{\"message\":\"Incorrect API key provided: {openai}. You sent {known} and {google}\"}}}} "
            + "Authorization: Bearer " + new string('d', 40) + " request req_" + new string('e', 20) + " org-" + new string('f', 12) + " url ?key=" + new string('g', 20) + "&alt=sse";
        string clean = scrub.Scrub(body);

        foreach (string leaked in new[] { openai, google, known, new string('d', 40), new string('e', 20), new string('f', 12), new string('g', 20) })
        {
            Assert.DoesNotContain(leaked, clean, StringComparison.Ordinal);
        }

        Assert.Contains("Incorrect API key provided", clean, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_in_a_provider_error_body_does_not_reach_the_output()
    {
        string key = "live-key-" + new string('x', 16);
        string shaped = "sk-" + new string('q', 24);
        var provider = new ScriptedProvider { Fallback = _ => ScriptStep.Fail(new InvalidOperationException($"401 Unauthorized: bad key {key}, did you mean {shaped}? request req_{new string('z', 14)}")) };
        var lines = new List<string>();
        var runner = new SmokeRunner(new SmokeTarget { Name = "scripted", Chat = provider, ChatModel = "demo-1" }, new SmokeBudget("scripted", 1m), Catalog(), new Scrubber([key]), lines.Add);

        await runner.RunAsync();

        string all = string.Join("\n", lines) + runner.Summary();
        Assert.Contains("FAIL", all, StringComparison.Ordinal);
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
        Assert.DoesNotContain(shaped, all, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 14), all, StringComparison.Ordinal);
        // A failed call was refused by the provider and cost nothing.
        Assert.Contains("cost=$0.000000", all, StringComparison.Ordinal);
    }
}
