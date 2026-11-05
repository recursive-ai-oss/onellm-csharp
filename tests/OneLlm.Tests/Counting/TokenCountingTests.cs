using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Tests.Counting;

public class TokenCountingTests
{
    private const string Yaml = """
        name: openai
        modelPatterns: ["gpt-*"]
        models:
          - name: gpt-test
            contextWindow: 1000000
          - name: gpt-calibrated
            properties: { charsPerToken: 2 }
        """;

    private const string AnthropicYaml = """
        name: anthropic
        modelPatterns: ["claude-*"]
        capabilities: { tokenCounting: false }
        models:
          - name: claude-test
        """;

    private const string GeminiYaml = """
        name: google
        modelPatterns: ["gemini-*"]
        models:
          - name: gemini-test
        """;

    private static readonly ModelCatalog Catalog = ModelCatalog.Create(
        [CatalogLayer.FromYaml("shipped", Yaml), CatalogLayer.FromYaml("shipped", AnthropicYaml), CatalogLayer.FromYaml("shipped", GeminiYaml)]);

    private static ChatRequest Request(string text, string? system = null) =>
        new() { Model = "m", System = system, Messages = [Message.FromUser(text)] };

    [Fact]
    public void The_estimate_counts_ascii_at_four_characters_per_token_for_openai_models_plus_message_allowances()
    {
        // 400 ASCII characters at 4 chars/token = 100 tokens; one message adds 4, priming the reply adds 3: 107.
        TokenCount count = TokenEstimator.Estimate(Request(new string('a', 400)), Catalog.Resolve("gpt-test"));
        Assert.Equal(107, count.InputTokens);
        Assert.Equal(TokenCountTier.Estimate, count.Tier);
    }

    [Fact]
    public void The_generic_ratio_is_three_and_a_half_characters_per_token()
    {
        // 10,000 / 35 = 285 thousandths of a token per character; 400 characters = 114,000 thousandths = 114 tokens; + 7.
        Assert.Equal(121, TokenEstimator.Estimate(Request(new string('a', 400))).InputTokens);
    }

    [Fact]
    public void A_model_can_calibrate_its_own_ratio_with_charsPerToken()
    {
        // 2 chars/token: 100 characters = 50 tokens; + 7.
        Assert.Equal(57, TokenEstimator.Estimate(Request(new string('a', 100)), Catalog.Resolve("gpt-calibrated")).InputTokens);
    }

    [Fact]
    public void Non_ascii_text_costs_more_per_character()
    {
        ResolvedModel openai = Catalog.Resolve("gpt-test");
        Assert.Equal(5, TokenEstimator.EstimateText(new string('é', 10), openai));   // two-byte characters count double: 10 * 0.5 = 5
        Assert.Equal(3, TokenEstimator.EstimateText("日本語", openai));                // one token per CJK character
        Assert.Equal(1, TokenEstimator.EstimateText("😀", openai));                   // a surrogate pair is one token
        Assert.Equal(0, TokenEstimator.EstimateText("", openai));
    }

    [Fact]
    public void Tools_add_their_text_and_an_allowance_and_anthropic_adds_its_tool_prompt_once()
    {
        var tool = new ToolDefinition("get_weather", "Gets the weather.", new JsonObject { ["type"] = "object" });
        ChatRequest withTool = Request("hi") with { Tools = [tool] };
        long without = TokenEstimator.Estimate(Request("hi"), Catalog.Resolve("claude-test")).InputTokens;
        long anthropic = TokenEstimator.Estimate(withTool, Catalog.Resolve("claude-test")).InputTokens;
        long openai = TokenEstimator.Estimate(withTool, Catalog.Resolve("gpt-test")).InputTokens;
        Assert.True(anthropic - without >= 346 + 8);
        Assert.True(openai < anthropic - 300);
    }

    [Theory]
    [InlineData("anthropic", 1000, 1000, 1334)]   // 1,000 * 1,000 / 750 = 1,333, plus 1
    [InlineData("anthropic", 4000, 4000, 1600)]   // capped: larger images are scaled down to about 1,600 tokens
    [InlineData("openai", 1024, 1024, 765)]       // scaled to 768 x 768 = 2 x 2 tiles: 85 + 4 * 170
    [InlineData("openai", 2048, 4096, 1105)]      // fit to 1024 x 2048, short side to 768 x 1536 = 2 x 3 tiles: 85 + 6 * 170
    [InlineData("openai", 100, 100, 255)]         // one tile: 85 + 170
    [InlineData("gemini", 300, 300, 258)]         // up to 384 x 384
    [InlineData("gemini", 1000, 1000, 1032)]      // 2 x 2 tiles of 768 at 258
    public void Image_tokens_follow_each_vendors_documented_formula(string family, int width, int height, int expected) =>
        Assert.Equal(expected, TokenEstimator.ImageTokens(width, height, family));

    [Fact]
    public void Files_count_by_kind_and_unknown_image_sizes_use_a_typical_size()
    {
        Message Msg(string type, long size, ResizedImage? resized = null) =>
            new() { User = [new AssetPart(new AssetRef { Storage = "local", Key = "k", ContentType = type, Size = size }) { ResizedImage = resized }] };
        long Tokens(Message m, string model) => TokenEstimator.Estimate(new ChatRequest { Messages = [m] }, Catalog.Resolve(model)).InputTokens - 7;

        Assert.Equal(1600, Tokens(Msg("image/png", 0), "claude-test"));
        Assert.Equal(765, Tokens(Msg("image/png", 0), "gpt-test"));
        Assert.Equal(765, Tokens(Msg("image/png", 0, new ResizedImage { Width = 1024, Height = 1024 }), "gpt-test"));
        Assert.Equal(2 * 2250, Tokens(Msg("application/pdf", 100_000), "claude-test"));   // 100 KB = 2 pages of 60 KB
        Assert.Equal(258, Tokens(Msg("application/pdf", 10_000), "gemini-test"));
        Assert.Equal(10 * 32, Tokens(Msg("audio/wav", 160_000), "gemini-test"));            // 160 KB = 10 s at 32 tokens/s
        Assert.Equal(2 * 263, Tokens(Msg("video/mp4", 500_000), "gemini-test"));            // 500 KB = 2 s at 263 tokens/s
    }

    // ---- tiers ----

    private sealed class FakeCounter(Func<long> count) : ITokenCountingProvider
    {
        public string Name => "fake";

        public string Deployment => "direct";

        public int Calls { get; private set; }

        public Task<TokenCount> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TokenCount(count(), TokenCountTier.Estimate));   // the counter reports tier 1 whatever the provider says
        }
    }

    private sealed class FakeTokenizer : ILocalTokenizer
    {
        public bool Supports(ResolvedModel model) => model.Provider.Name == "openai";

        public long CountTokens(ResolvedModel model, string text) => text.Length;
    }

    [Fact]
    public async Task Tier_one_is_the_provider_count_and_reports_itself()
    {
        var provider = new FakeCounter(() => 321);
        TokenCount count = await new TokenCounter(new FakeTokenizer()).CountAsync(Request("hello"), Catalog.Resolve("gpt-test"), provider);
        Assert.Equal(new TokenCount(321, TokenCountTier.Provider), count);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_failing_provider_count_falls_back_to_the_local_tokenizer_and_logs()
    {
        var log = new List<string>();
        var failing = new FakeCounter(() => throw new RateLimitException("fake", "slow down"));
        var counter = new TokenCounter(new FakeTokenizer(), log.Add);
        // The tokenizer counts the 5 characters of "hello"; the allowances add 4 + 3 = 7.
        TokenCount count = await counter.CountAsync(Request("hello"), Catalog.Resolve("gpt-test"), failing);
        Assert.Equal(new TokenCount(12, TokenCountTier.Tokenizer), count);
        Assert.Contains("fake", Assert.Single(log));
    }

    [Fact]
    public async Task Without_a_matching_tokenizer_the_estimate_answers_and_says_so()
    {
        var counter = new TokenCounter(new FakeTokenizer());
        TokenCount count = await counter.CountAsync(Request("hello"), Catalog.Resolve("gemini-test"));
        Assert.Equal(TokenCountTier.Estimate, count.Tier);
        Assert.Equal(TokenCountTier.Estimate, (await new TokenCounter().CountAsync(Request("hello"))).Tier);
    }

    [Fact]
    public async Task A_model_whose_provider_cannot_count_tokens_skips_tier_one()
    {
        var provider = new FakeCounter(() => 1);
        TokenCount count = await new TokenCounter().CountAsync(Request("hello"), Catalog.Resolve("claude-test"), provider);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(TokenCountTier.Estimate, count.Tier);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var cancelled = new FakeCounter(() => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TokenCounter().CountAsync(Request("hello"), Catalog.Resolve("gpt-test"), cancelled));
    }

    [Fact]
    public void Context_usage_is_all_input_of_the_last_turn_over_the_window()
    {
        // 150,000 uncached + 50,000 cache reads = 200,000 of 1,000,000.
        var usage = new Usage { InputTokens = 150_000, CacheReadInputTokens = 50_000, OutputTokens = 9000 };
        Assert.Equal(0.2m, ContextUsage.Fraction(usage, Catalog.Resolve("gpt-test").Model!));
        Assert.Null(ContextUsage.Fraction(usage, Catalog.Resolve("claude-test").Model!));
    }
}
