using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools;
using OneLlm.Tools.Media;

namespace OneLlm.Tests.Media;

/// <summary>A provider that implements the four media interfaces, records what it was asked and answers with a fixed cost.</summary>
internal sealed class FakeMediaProvider : IImageProvider, ITextToSpeechProvider, ISpeechToTextProvider, IModerationProvider
{
    public string Name => "fake";

    public string Deployment => "direct";

    public ImageRequest? Image { get; private set; }

    public SpeechRequest? Speech { get; private set; }

    public TranscriptionRequest? Transcription { get; private set; }

    public ModerationRequest? Moderation { get; private set; }

    public static Cost OneLine(string counter, decimal quantity, decimal unitPrice) => new([CostLine.Create(counter, quantity, 1_000_000m, unitPrice)]);

    public Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        Image = request;
        return Task.FromResult(new ImageResponse
        {
            Images = Enumerable.Range(0, request.Count).Select(i => new GeneratedImage(new byte[] { 1, 2, (byte)i }, "image/png", i == 0 ? "a revised prompt" : null)).ToArray(),
            Cost = OneLine("outputImageTokens", 1000, 30m),
        });
    }

    public Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        Speech = request;
        return Task.FromResult(new SpeechResponse { Audio = new byte[] { 9, 9 }, ContentType = "audio/mpeg", Cost = OneLine("characters", request.Input.Length, 15m) });
    }

    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        Transcription = request;
        return Task.FromResult(new TranscriptionResponse { Text = "hello there", Cost = OneLine("audioSeconds", 10, 100m) });
    }

    public Task<ModerationResponse> ModerateAsync(ModerationRequest request, CancellationToken cancellationToken = default)
    {
        Moderation = request;
        return Task.FromResult(new ModerationResponse
        {
            Results =
            [
                new ModerationResult
                {
                    Flagged = true,
                    Categories = new Dictionary<string, bool> { ["violence"] = true, ["hate"] = false },
                    Scores = new Dictionary<string, double> { ["violence"] = 0.9, ["hate"] = 0.01 },
                },
            ],
            Cost = Cost.Zero,
        });
    }
}

internal sealed class RootedCapabilities(params string[] roots) : ICapabilities
{
    public IReadOnlyList<string> FileRoots => roots;

    public bool ShellEnabled => false;

    public bool IsNetworkAllowed(Uri uri) => false;
}

public class MediaToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onellm-media-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly FakeMediaProvider _provider = new();

    public MediaToolTests()
    {
        _root = Path.Combine(_dir, "granted");
        _outside = Path.Combine(_dir, "secret");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Path.Combine(_root, "in.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "mask.png"), [4, 5]);
        File.WriteAllBytes(Path.Combine(_root, "talk.wav"), [7, 7, 7]);
        File.WriteAllBytes(Path.Combine(_outside, "private.png"), [6]);
        File.WriteAllBytes(Path.Combine(_outside, "private.wav"), [6]);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ITool Tool(string name, MediaToolOptions? options = null) =>
        MediaTools.Create(_provider, "img", "tts", "stt", "mod", options).Single(t => t.Definition.Name == name);

    private ToolContext Context(ICapabilities? caps = null) =>
        new("instance", "call", new ManualTimeProvider()) { Capabilities = caps ?? new RootedCapabilities(_root) };

    private ValueTask<ToolResult> Run(string tool, string json, ICapabilities? caps = null, MediaToolOptions? options = null) =>
        ToolInvoker.InvokeAsync(Tool(tool, options), (JsonObject)JsonNode.Parse(json)!, Context(caps), CancellationToken.None);

    private static string TextOf(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));

    [Fact]
    public void Four_tools_are_made_and_a_missing_model_leaves_one_out()
    {
        Assert.Equal(["generate_image", "speak", "transcribe", "moderate"], MediaTools.Create(_provider, "a", "b", "c", "d").Select(t => t.Definition.Name).ToArray());
        Assert.Equal(["speak"], MediaTools.Create(_provider, speechModel: "b").Select(t => t.Definition.Name).ToArray());
    }

    [Fact]
    public async Task Generate_image_returns_assets_and_the_cost_line()
    {
        ToolResult r = await Run("generate_image", """{"prompt":"a boat","count":2,"size":"1024x1024","quality":"low"}""");
        Assert.False(r.IsError);
        Assert.Equal(2, r.Parts.OfType<AssetPart>().Count());
        Assert.Equal("a revised prompt", TextOf(r));
        Assert.Equal("img", _provider.Image!.Model);
        Assert.Equal(2, _provider.Image.Count);
        // 1000 image output tokens at $30 per million = $0.03.
        CostLine line = Assert.Single(r.CostLines);
        Assert.Equal("outputImageTokens", line.Counter);
        Assert.Equal(0.03m, line.Amount);
    }

    [Fact]
    public async Task Generate_image_edit_reads_the_granted_files()
    {
        ToolResult r = await Run("generate_image", """{"prompt":"sky","images":["in.png"],"mask":"mask.png"}""");
        Assert.False(r.IsError);
        Assert.Equal(new byte[] { 1, 2, 3 }, _provider.Image!.Inputs.Single().Data.ToArray());
        Assert.Equal("image/png", _provider.Image.Inputs[0].ContentType);
        Assert.Equal(new byte[] { 4, 5 }, _provider.Image.Mask!.Value.Data.ToArray());
    }

    [Fact]
    public async Task Speak_returns_audio_and_priced_characters()
    {
        ToolResult r = await Run("speak", """{"text":"hello","voice":"alloy"}""");
        AssetPart audio = Assert.IsType<AssetPart>(Assert.Single(r.Parts));
        Assert.Equal("audio/mpeg", audio.Asset!.ContentType);
        Assert.Equal("alloy", _provider.Speech!.Voice);
        // 5 characters at $15 per million = $0.000075.
        Assert.Equal(0.000075m, Assert.Single(r.CostLines).Amount);
    }

    [Fact]
    public async Task Speak_refuses_text_over_the_limit()
    {
        ToolResult r = await Run("speak", """{"text":"abcdef"}""", options: new MediaToolOptions { MaxSpeechCharacters = 5 });
        Assert.True(r.IsError);
        Assert.Contains("'text'", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Speech);
    }

    [Fact]
    public async Task Transcribe_reads_the_file_and_prices_seconds()
    {
        ToolResult r = await Run("transcribe", """{"path":"talk.wav","language":"en","translate":true}""");
        Assert.Equal("hello there", TextOf(r));
        Assert.Equal("audio/wav", _provider.Transcription!.ContentType);
        Assert.True(_provider.Transcription.Translate);
        // 10 seconds at $100 per million per second = $0.001.
        Assert.Equal(0.001m, Assert.Single(r.CostLines).Amount);
    }

    [Fact]
    public async Task Moderate_returns_verdicts_as_json_with_a_zero_cost()
    {
        ToolResult r = await Run("moderate", """{"texts":["x"],"images":["in.png"]}""");
        Assert.False(r.IsError);
        JsonObject first = (JsonObject)((JsonArray)((JsonObject)((JsonPart)r.Parts[0]).Value)["results"]!)[0]!;
        Assert.True((bool)first["flagged"]!);
        Assert.Equal("violence", (string?)((JsonArray)first["categories"]!)[0]);
        Assert.Single(_provider.Moderation!.Images);
        Assert.Empty(r.CostLines);
    }

    [Theory]
    [InlineData("generate_image", """{"prompt":"p","images":["../secret/private.png"]}""")]
    [InlineData("generate_image", """{"prompt":"p","images":["ABS_OUTSIDE.png"]}""")]
    [InlineData("generate_image", """{"prompt":"p","images":["in.png"],"mask":"../secret/private.png"}""")]
    [InlineData("transcribe", """{"path":"../secret/private.wav"}""")]
    [InlineData("transcribe", """{"path":"ABS_OUTSIDE.wav"}""")]
    [InlineData("moderate", """{"images":["../secret/private.png"]}""")]
    public async Task Paths_outside_the_roots_are_refused_before_any_provider_call(string tool, string json)
    {
        json = json.Replace("ABS_OUTSIDE", Path.Combine(_outside, "private"), StringComparison.Ordinal);
        ToolResult r = await Run(tool, json);
        Assert.True(r.IsError);
        Assert.Contains("outside", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Image);
        Assert.Null(_provider.Transcription);
        Assert.Null(_provider.Moderation);
    }

    [Fact]
    public async Task A_symlinked_file_leading_outside_is_refused()
    {
        string link = Path.Combine(_root, "link.png");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_outside, "private.png"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        ToolResult r = await Run("generate_image", """{"prompt":"p","images":["link.png"]}""");
        Assert.True(r.IsError);
        Assert.Contains("outside", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Image);
    }

    [Fact]
    public async Task A_symlinked_directory_leading_outside_is_refused()
    {
        string link = Path.Combine(_root, "dirlink");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        ToolResult r = await Run("transcribe", """{"path":"dirlink/private.wav"}""");
        Assert.True(r.IsError);
        Assert.Contains("outside", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Transcription);
    }

    [Fact]
    public async Task A_symlink_that_stays_inside_the_roots_is_allowed()
    {
        string link = Path.Combine(_root, "alias.wav");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_root, "talk.wav"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        ToolResult r = await Run("transcribe", """{"path":"alias.wav"}""");
        Assert.False(r.IsError);
    }

    [Fact]
    public async Task Without_a_file_grant_nothing_is_read()
    {
        ToolResult r = await Run("transcribe", """{"path":"talk.wav"}""", NoCapabilities.Instance);
        Assert.True(r.IsError);
        Assert.Contains("no file access", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Transcription);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused_and_a_missing_file_is_named()
    {
        ToolResult big = await Run("transcribe", """{"path":"talk.wav"}""", options: new MediaToolOptions { MaxInputBytes = 2 });
        Assert.True(big.IsError);
        Assert.Contains("3 bytes", TextOf(big), StringComparison.Ordinal);

        ToolResult none = await Run("transcribe", """{"path":"nope.wav"}""");
        Assert.True(none.IsError);
        Assert.Contains("existing file", TextOf(none), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("generate_image", """{"prompt":"p","surprise":1}""", "surprise")]
    [InlineData("generate_image", """{"prompt":"p","count":0}""", "count")]
    [InlineData("generate_image", """{"prompt":"p","count":5}""", "count")]
    [InlineData("generate_image", """{"prompt":"p","count":"two"}""", "count")]
    [InlineData("generate_image", """{"prompt":"p","mask":"mask.png"}""", "mask")]
    [InlineData("generate_image", """{}""", "prompt")]
    [InlineData("speak", """{"text":"  "}""", "text")]
    [InlineData("transcribe", """{"path":"talk.wav","translate":"yes"}""", "translate")]
    [InlineData("moderate", """{"texts":[1]}""", "texts[0]")]
    public async Task Bad_arguments_name_the_parameter(string tool, string json, string parameter)
    {
        ToolResult r = await Run(tool, json);
        Assert.True(r.IsError);
        Assert.Contains("'" + parameter + "'", TextOf(r), StringComparison.Ordinal);
        Assert.Null(_provider.Image);
        Assert.Null(_provider.Speech);
    }

    [Fact]
    public void Definitions_carry_the_options()
    {
        var options = new MediaToolOptions { RequiresApproval = true, CostHint = 0.05m, Timeout = TimeSpan.FromSeconds(30) };
        ToolSpec spec = Tool("generate_image", options).Definition;
        Assert.True(spec.RequiresApproval);
        Assert.Equal(0.05m, spec.CostHint);
        Assert.Equal(TimeSpan.FromSeconds(30), spec.Timeout);
        Assert.Equal(4, (int)spec.InputSchema["properties"]!["count"]!["maximum"]!);
    }
}
