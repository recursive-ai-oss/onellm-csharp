using OneLlm.Messages;

namespace OneLlm.Tests.Messages;

public class CostTests
{
    [Fact]
    public void A_line_is_quantity_over_per_times_price_times_multiplier_in_exact_decimals()
    {
        // 1,234,567 input tokens at $3 per million, flex tier (x0.5): 1.234567 * 3 * 0.5 = 1.8518505 exactly.
        CostLine line = CostLine.Create("inputTokens", 1_234_567m, 1_000_000m, 3m, 0.5m, "input (flex)", "https://example.com/p", "2026-03-01");
        Assert.Equal(1.8518505m, line.Amount);
        Assert.Equal("inputTokens", line.Counter);
        Assert.Equal("https://example.com/p", line.Source);
    }

    [Fact]
    public void Totals_add_without_rounding_and_costs_combine_with_their_lines()
    {
        Cost a = new([CostLine.Create("inputTokens", 1000, 1_000_000m, 3m), CostLine.Create("outputTokens", 500, 1_000_000m, 15m)]);
        Cost b = new([CostLine.Create("inputTokens", 2000, 1_000_000m, 3m)]);
        Cost sum = a + b;
        Assert.Equal(0.003m + 0.0075m + 0.006m, sum.Total);
        Assert.Equal(3, sum.Lines.Count);
        Assert.Equal(0.009m, sum.TotalsByCounter()["inputTokens"]);
        Assert.Same(a, a + Cost.Zero);
        Assert.Same(a, Cost.Zero + a);
        Assert.Equal(sum.Total, Cost.Sum([a, null, b]).Total);
        Assert.Equal(0m, Cost.Sum([]).Total);
    }

    [Fact]
    public void Display_is_the_only_rounding_and_rounds_half_away_from_zero()
    {
        var cost = new Cost([CostLine.Create("x", 1, 1, 0.0012345m)]);
        Assert.Equal(0.0012345m, cost.Total);
        Assert.Equal("$0.001235", cost.Display());
        Assert.Equal("$0.00", cost.Display(2));
        Assert.Equal("$0", cost.Display(0));
        Assert.Equal("$0.0012345", cost.Display(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => cost.Display(13));
    }

    [Fact]
    public void Usage_without_lines_becomes_one_total_line()
    {
        Assert.Equal(0.5m, new Usage { Cost = 0.5m }.ToCost().Total);
        Assert.Same(Cost.Zero, new Usage().ToCost());
    }

    [Fact]
    public void Per_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CostLine.Create("x", 1, 0m, 1m));
    }
}
