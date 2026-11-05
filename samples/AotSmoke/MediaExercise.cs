using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;
using OneLlm.Tools;
using OneLlm.Tools.Media;

namespace OneLlm.AotSmoke;

/// <summary>A media provider that answers without a network: enough to run the four tools.</summary>
internal sealed class StubMedia : IImageProvider, ITextToSpeechProvider, ISpeechToTextProvider, IModerationProvider
{
    public string Name => "stub";

    public string Deployment => "direct";

    public Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImageResponse { Images = [new GeneratedImage(new byte[] { 1 }, "image/png", null)], Cost = new Cost([CostLine.Create("outputImageTokens", 1000m, 1_000_000m, 30m)]) });

    public Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SpeechResponse { Audio = new byte[] { 2 }, ContentType = "audio/wav" });

    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResponse { Text = "hi" });

    public Task<ModerationResponse> ModerateAsync(ModerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModerationResponse { Results = [new ModerationResult { Flagged = false }] });
}

/// <summary>Builds the media tools, runs them through the invoker, and prices media usage from the shipped catalog, under NativeAOT.</summary>
internal static class MediaExercise
{
    /// <summary>Runs the exercise.</summary>
    /// <returns>0 when every step gave the expected result.</returns>
    public static int Run()
    {
        IReadOnlyList<ITool> tools = MediaTools.Create(new StubMedia(), "img", "tts", "stt", "mod");
        var ctx = () => new ToolContext("instance", "call", TimeProvider.System);
        ToolResult image = ToolInvoker.InvokeAsync(tools[0], (JsonObject)JsonNode.Parse("""{"prompt":"a boat"}""")!, ctx(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        ToolResult speech = ToolInvoker.InvokeAsync(tools[1], (JsonObject)JsonNode.Parse("""{"text":"hello"}""")!, ctx(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        ToolResult denied = ToolInvoker.InvokeAsync(tools[2], (JsonObject)JsonNode.Parse("""{"path":"a.wav"}""")!, ctx(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        ToolResult moderated = ToolInvoker.InvokeAsync(tools[3], (JsonObject)JsonNode.Parse("""{"texts":["x"]}""")!, ctx(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        ModelCatalog catalog = ModelCatalog.Create([OpenAICatalog.Shipped(), GeminiProvider.GetCatalogLayer()]);
        var usage = new Usage { InputTokens = 1060, OutputTokens = 1000, Counters = new() { ["inputImageTokens"] = 60, ["outputImageTokens"] = 900 } };
        Cost cost = CostCalculator.Price(catalog.Resolve("gpt-image-1.5"), usage)!;

        bool ok = !image.IsError && image.CostLines.Count == 1 && image.CostLines[0].Amount == 0.03m
            && !speech.IsError && speech.Parts[0] is AssetPart
            && denied.IsError
            && !moderated.IsError && moderated.Parts[0] is JsonPart
            && cost.Total == 0.03528m;
        return ok ? 0 : 1;
    }
}
