using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;

namespace OneLlm.Tests.Media;

// Prices are read from the vendors' pricing pages; every expected value is worked out by hand in the comment above it.
public class MediaCatalogTests
{
    private static ModelCatalog Catalog => MediaRun.Catalog;

    public static TheoryData<string> MediaModels()
    {
        var data = new TheoryData<string>();
        foreach (string m in new[]
        {
            "gpt-image-2.6-sunburst", "gpt-image-2.6-flare", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare", "gpt-image-2", "gpt-image-1.5", "gpt-image-1-mini",
            "chatgpt-image-latest", "tts-1", "tts-1-hd", "gpt-4o-mini-tts", "whisper-1", "gpt-transcribe", "gpt-4o-transcribe", "gpt-4o-mini-transcribe",
            "gpt-4o-transcribe-diarize", "omni-moderation-latest", "omni-moderation-2024-09-26", "text-moderation-latest", "text-moderation-stable",
            "gemini-nano-banana-2.1", "gemini-3.1-flash-image", "gemini-3.1-flash-lite-image", "gemini-3-pro-image", "gemini-3.8-flash-tts",
            "gemini-3.8-flash-lite-tts", "gemini-3.1-flash-tts-preview", "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts",
        })
        {
            data.Add(m);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MediaModels))]
    public void Every_media_model_resolves_with_a_dated_sourced_price(string name)
    {
        ResolvedModel r = Catalog.Resolve(name);
        ModelSpec model = Assert.IsType<ModelSpec>(r.Model);
        Assert.Contains(model.Kind, new[] { "image", "speech", "transcription", "moderation" });
        PricingSpec p = Assert.IsType<PricingSpec>(model.Pricing);
        Assert.False(string.IsNullOrEmpty(p.AsOf));
        Assert.StartsWith("https://", p.Source, StringComparison.Ordinal);
        foreach (PriceItem item in p.Items ?? [])
        {
            Assert.False(string.IsNullOrEmpty(item.AsOf), name + " " + item.Counter);
            Assert.StartsWith("https://", item.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Merging_the_media_files_keeps_the_chat_models()
    {
        Assert.NotNull(Catalog.Resolve("gpt-5.5").Model);
        Assert.NotNull(Catalog.Resolve("gemini-2.5-flash").Model);
        Assert.Equal(1, Catalog.Providers.Count(p => p.Name == "openai"));
        Assert.Equal(1, Catalog.Providers.Count(p => p.Name == "gemini"));
    }

    private static Cost Price(string model, Usage usage, PricingContext? context = null) => CostCalculator.Price(Catalog.Resolve(model), usage, context)!;

    private static Usage U(long input = 0, long output = 0, params (string Counter, long Count)[] counters) => new()
    {
        InputTokens = input,
        OutputTokens = output,
        Counters = counters.ToDictionary(c => c.Counter, c => c.Count),
    };

    [Fact]
    public void Image_tokens_are_taken_out_of_the_text_totals()
    {
        // gpt-image-1.5: text in $5, image in $8, text out $10, image out $32 per million.
        // Input 1060 total, 60 of them image: 1000 text x 5 = 0.005 and 60 x 8 = 0.00048.
        // Output 1000 total, 900 of them image: 100 text x 10 = 0.001 and 900 x 32 = 0.0288. Sum 0.03528.
        Cost c = Price("gpt-image-1.5", U(1060, 1000, ("inputImageTokens", 60), ("outputImageTokens", 900)));
        Assert.Equal(0.03528m, c.Total);
        Assert.Equal(["inputImageTokens", "inputTokens", "outputImageTokens", "outputTokens"], c.Lines.Select(l => l.Counter).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Batch_halves_image_prices()
    {
        // The same usage as above with the batch multiplier 0.5: 0.03528 / 2.
        Assert.Equal(0.01764m, Price("gpt-image-1.5", U(1060, 1000, ("inputImageTokens", 60), ("outputImageTokens", 900)), new PricingContext { Batch = true }).Total);
    }

    [Fact]
    public void Speech_is_priced_per_character()
    {
        // tts-1: $15 per million characters. 2000 characters = 0.03.
        Cost c = Price("tts-1", U(0, 0, ("characters", 2000)));
        Assert.Equal(0.03m, c.Total);
        Assert.Equal("characters", Assert.Single(c.Lines).Counter);
    }

    [Fact]
    public void Transcription_is_priced_per_second()
    {
        // whisper-1: $0.006 per minute, written as $0.0001 per second. 90 seconds = 0.009.
        Assert.Equal(0.009m, Price("whisper-1", U(0, 0, ("audioSeconds", 90))).Total);
    }

    [Fact]
    public void Transcription_tokens_are_priced_by_the_token_classes()
    {
        // gpt-4o-mini-transcribe is token priced; the total must equal the sum of its lines, whatever the rates are.
        Cost c = Price("gpt-4o-mini-transcribe", U(1000, 100));
        Assert.Equal(c.Lines.Sum(l => l.Amount), c.Total);
        Assert.True(c.Total > 0m);
    }

    [Fact]
    public void Moderation_is_free()
    {
        Assert.Equal(0m, Price("omni-moderation-latest", U(1000, 0)).Total);
    }

    [Fact]
    public void Gemini_audio_output_and_batch()
    {
        // gemini-3.8-flash-tts: input $0.5, audio output $9 per million. 100 in = 0.00005; 1000 audio out = 0.009. Sum 0.00905.
        Usage u = U(100, 1000, ("outputAudioTokens", 1000));
        Assert.Equal(0.00905m, Price("gemini-3.8-flash-tts", u).Total);
        // Batch mode halves it: 0.004525.
        Assert.Equal(0.004525m, Price("gemini-3.8-flash-tts", u, new PricingContext { Batch = true }).Total);
    }

    [Fact]
    public void Gemini_image_output_tokens()
    {
        // gemini-nano-banana-2.1: 1000 image output tokens at $30 per million = 0.03; no input.
        Assert.Equal(0.03m, Price("gemini-nano-banana-2.1", U(0, 1000, ("outputImageTokens", 1000))).Total);
    }
}
