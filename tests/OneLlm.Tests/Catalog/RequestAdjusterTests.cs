using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.Tests.Catalog;

public class RequestAdjusterTests
{
    private const string Yaml = """
        name: acme
        modelPatterns: ["acme-*"]
        capabilities: { promptCaching: true, imageInputs: true, imageToolResults: false, tools: true }
        models:
          - name: acme-plain
            supportsTemperature: false
            supportsCaching: false
            supportsVision: false
            supportsTools: false
            supportsStructuredOutput: false
            supportsPdfInput: false
            supportsAudioInput: false
            maxOutputTokens: 4000
          - name: acme-think
            supportsTemperature: true
            maxOutputTokens: 64000
            serviceTiers: { flex: { pricingMultiplier: 0.5 } }
            thinking:
              efforts: [low, medium, high]
              defaultEffort: medium
              acceptsBudget: true
              minBudget: 1024
              maxBudget: 32000
          - name: acme-fixed
            thinking:
              efforts: [high]
              acceptsBudget: false
        """;

    private const string DecisionYaml = """
        name: jevlike
        modelPatterns: ["jev-*"]
        capabilities: { systemOne: true }
        models:
          - name: jev-1
        """;

    private static readonly ModelCatalog Catalog = ModelCatalog.Create([CatalogLayer.FromYaml("shipped", Yaml), CatalogLayer.FromYaml("shipped", DecisionYaml)]);

    private static AdjustedRequest Adjust(string model, ChatRequest request) =>
        RequestAdjuster.Adjust(Catalog.Resolve(model), request);

    private static ChatRequest Request(Func<ChatRequest, ChatRequest>? edit = null)
    {
        var r = new ChatRequest { Model = "x", Messages = [Message.FromUser("hello")] };
        return edit is null ? r : edit(r);
    }

    private static Message WithAsset(string contentType) =>
        new() { User = [new AssetPart(new AssetRef { Storage = "local", Key = "k", ContentType = contentType })] };

    [Fact]
    public void A_request_the_model_can_serve_passes_through_unchanged()
    {
        ChatRequest request = Request(r => r with { Temperature = 0.2, MaxOutputTokens = 1000 });
        AdjustedRequest result = Adjust("acme-think", request);
        Assert.Same(request, result.Request);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Sampling_parameters_are_stripped_for_a_model_that_takes_none_and_the_strip_is_reported()
    {
        AdjustedRequest result = Adjust("acme-plain", Request(r => r with { Temperature = 0.7, TopP = 0.9 }));
        Assert.Null(result.Request.Temperature);
        Assert.Null(result.Request.TopP);
        ResponseWarning a = Assert.Single(result.Warnings);
        Assert.Equal("parameter_stripped", a.Code);
        Assert.Contains("supportsTemperature", a.Message);
        Assert.Equal("temperature/topP", a.Parameter);
    }

    [Fact]
    public void Reasoning_is_stripped_when_the_model_has_no_thinking_settings()
    {
        AdjustedRequest result = Adjust("acme-plain", Request(r => r with { Reasoning = new ReasoningOptions { Effort = "high" } }));
        Assert.Null(result.Request.Reasoning);
        Assert.Equal("parameter_stripped", Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public void A_thinking_budget_is_stripped_when_not_accepted_and_clamped_into_range_when_accepted()
    {
        AdjustedRequest fixedModel = Adjust("acme-fixed", Request(r => r with { Reasoning = new ReasoningOptions { Effort = "high", BudgetTokens = 5000 } }));
        Assert.Null(fixedModel.Request.Reasoning!.BudgetTokens);
        Assert.Equal("high", fixedModel.Request.Reasoning.Effort);

        AdjustedRequest low = Adjust("acme-think", Request(r => r with { Reasoning = new ReasoningOptions { BudgetTokens = 10 } }));
        Assert.Equal(1024, low.Request.Reasoning!.BudgetTokens);
        Assert.Equal("parameter_clamped", Assert.Single(low.Warnings).Code);

        AdjustedRequest high = Adjust("acme-think", Request(r => r with { Reasoning = new ReasoningOptions { BudgetTokens = 99_000 } }));
        Assert.Equal(32_000, high.Request.Reasoning!.BudgetTokens);
    }

    [Fact]
    public void An_effort_the_model_lacks_is_refused_because_it_changes_what_was_asked()
    {
        var e = Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-fixed", Request(r => r with { Reasoning = new ReasoningOptions { Effort = "low" } })));
        Assert.Equal("reasoning.effort", e.Parameter);
        Assert.Equal("acme:acme-fixed", e.Model);
    }

    [Fact]
    public void Caching_is_stripped_and_the_output_limit_is_clamped()
    {
        AdjustedRequest result = Adjust("acme-plain", Request(r => r with { Cache = new CachePolicy(), MaxOutputTokens = 9000 }));
        Assert.False(result.Request.Cache!.Enabled);
        Assert.Equal(4000, result.Request.MaxOutputTokens);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Tools_a_forced_tool_and_a_schema_are_refused_for_models_without_them()
    {
        var tool = new ToolDefinition("lookup", "Looks up.", new JsonObject { ["type"] = "object" });
        Assert.Equal("tools", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-plain", Request(r => r with { Tools = [tool] }))).Parameter);
        Assert.Equal("tools", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-plain", Request(r => r with { ToolChoice = ToolChoice.Required }))).Parameter);
        Assert.Equal("responseFormat", Assert.Throws<UnsupportedParameterException>(() =>
            Adjust("acme-plain", Request(r => r with { ResponseFormat = ResponseFormat.FromSchema("s", new JsonObject()) }))).Parameter);

        // Provider-level capability applies where the model says nothing; a model that does not state it is not refused.
        Assert.Empty(Adjust("acme-think", Request(r => r with { Tools = [tool] })).Warnings);
    }

    [Fact]
    public void Inputs_the_model_cannot_read_are_refused()
    {
        Assert.Equal("image input", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-plain", Request(r => r with { Messages = [WithAsset("image/png")] }))).Parameter);
        Assert.Equal("PDF input", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-plain", Request(r => r with { Messages = [WithAsset("application/pdf")] }))).Parameter);
        Assert.Equal("audio input", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-plain", Request(r => r with { Messages = [WithAsset("audio/wav")] }))).Parameter);
        Assert.Empty(Adjust("acme-think", Request(r => r with { Messages = [WithAsset("image/png")] })).Warnings);

        // The provider says images in tool results are not accepted.
        var call = new ToolCall("c1", "shot") { Result = [new AssetPart(new AssetRef { Storage = "local", Key = "k", ContentType = "image/png" })] };
        Assert.Equal("image in a tool result", Assert.Throws<UnsupportedParameterException>(() =>
            Adjust("acme-think", Request(r => r with { Messages = [new Message { ToolCalls = [call] }] }))).Parameter);
    }

    [Fact]
    public void A_service_tier_the_model_lacks_is_refused_and_a_standard_name_is_not()
    {
        Assert.Empty(Adjust("acme-think", Request(r => r with { ServiceTier = "flex" })).Warnings);
        Assert.Empty(Adjust("acme-think", Request(r => r with { ServiceTier = "auto" })).Warnings);
        Assert.Equal("serviceTier", Assert.Throws<UnsupportedParameterException>(() => Adjust("acme-think", Request(r => r with { ServiceTier = "priority" }))).Parameter);
    }

    [Fact]
    public void A_decisions_provider_refuses_chat_and_a_model_known_only_by_pattern_is_not_adjusted()
    {
        Assert.Equal("chat", Assert.Throws<UnsupportedParameterException>(() => Adjust("jev-1", Request())).Parameter);

        ChatRequest request = Request(r => r with { Temperature = 1.0, Tools = [new ToolDefinition("t", "d", new JsonObject())] });
        AdjustedRequest result = Adjust("acme-unlisted", request);
        Assert.Same(request, result.Request);
        Assert.Empty(result.Warnings);
    }
}
