using System.Net;

namespace OneLlm.Tests;

public class ErrorTests
{
    [Fact]
    public void Statuses_map_to_the_typed_errors()
    {
        Assert.IsType<AuthException>(ProviderErrors.FromStatus("p", HttpStatusCode.Unauthorized, "m"));
        Assert.IsType<AuthException>(ProviderErrors.FromStatus("p", HttpStatusCode.Forbidden, "m"));
        var rate = Assert.IsType<RateLimitException>(ProviderErrors.FromStatus("p", (HttpStatusCode)429, "m", TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(3), rate.RetryAfter);
        Assert.IsType<OverloadedException>(ProviderErrors.FromStatus("p", (HttpStatusCode)529, "m"));
        Assert.IsType<OverloadedException>(ProviderErrors.FromStatus("p", HttpStatusCode.ServiceUnavailable, "m"));
        Assert.IsType<BadRequestException>(ProviderErrors.FromStatus("p", HttpStatusCode.BadRequest, "m"));
        Assert.IsType<ProviderException>(ProviderErrors.FromStatus("p", HttpStatusCode.InternalServerError, "m"));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void Only_transient_statuses_are_retryable(int status, bool transient)
    {
        Assert.Equal(transient, ProviderErrors.FromStatus("p", (HttpStatusCode)status, "m").IsTransient);
    }

    [Fact]
    public void A_rate_limit_that_means_no_quota_is_not_transient()
    {
        Assert.False(new RateLimitException("p", "m") { IsQuotaExhausted = true }.IsTransient);
        Assert.True(new RateLimitException("p", "m").IsTransient);
        Assert.False(new ContextOverflowException("p", "m").IsTransient);
    }

    [Fact]
    public void Messages_name_the_provider()
    {
        var e = new AuthException("anthropic", "invalid x-api-key");
        Assert.Equal("anthropic: invalid x-api-key", e.Message);
        Assert.Equal("anthropic", e.Provider);
        Assert.Equal(401, e.StatusCode);
    }

    [Fact]
    public void Capability_missing_names_the_package_or_the_grant()
    {
        var pkg = new CapabilityMissingException("run_javascript", "the RecursiveAI.OneLlm.JavaScript package", isPackage: true);
        Assert.Equal("run_javascript is not available. Add the RecursiveAI.OneLlm.JavaScript package.", pkg.Message);
        var grant = new CapabilityMissingException("execute_terminal_command", "shell access to the runtime");
        Assert.Equal("execute_terminal_command is not available. Grant shell access to the runtime.", grant.Message);
        Assert.False(grant.IsPackage);
    }

    [Fact]
    public void Bad_definition_says_file_line_and_column()
    {
        var e = new BadDefinitionException("unknown key 'foo'", "agents/a.yaml", 12, 3);
        Assert.Equal("agents/a.yaml:12:3: unknown key 'foo'", e.Message);
        Assert.Equal(12, e.Line);
        Assert.Equal(3, e.Column);
        Assert.Equal("agents/a.yaml: broken", new BadDefinitionException("broken", "agents/a.yaml").Message);
        Assert.Equal("broken", new BadDefinitionException("broken").Message);
    }

    [Fact]
    public void Budget_errors_carry_the_numbers_as_decimals()
    {
        var e = new BudgetExceededException(BudgetKind.SpendCap, "over the cap", 1.00m, 0.30m, 0.90m);
        Assert.Equal(0.90m, e.Spent);
        Assert.Equal(1.00m, e.Limit);
        Assert.Equal(BudgetKind.SpendCap, e.Kind);
    }

    [Fact]
    public void Every_error_is_a_OneLlmException()
    {
        OneLlmException[] all =
        [
            new AuthException("p", "m"), new RateLimitException("p", "m"), new OverloadedException("p", "m"), new ContextOverflowException("p", "m"),
            new ContentFilterException("p", "m"), new BadRequestException("p", "m"), new TransportException("p", "m"), new MalformedResponseException("p", "m"),
            new RefusedException("m"), new BudgetExceededException(BudgetKind.PerCall, "m", null, null, 0m), new CapabilityMissingException("c", "r"),
            new BadDefinitionException("m"), new ModelNotFoundException("x", "m"),
        ];
        Assert.Equal(13, all.Length);
    }
}
