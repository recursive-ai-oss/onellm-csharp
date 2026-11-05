using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Extensions.AI.Tests;

public class MediaClientTests
{
    private sealed class FakeMedia : ISpeechToTextProvider, ITextToSpeechProvider, IImageProvider
    {
        public string Name => "fake";

        public string Deployment => "direct";

        public TranscriptionRequest? Transcription { get; private set; }

        public SpeechRequest? Speech { get; private set; }

        public ImageRequest? Image { get; private set; }

        public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            Transcription = request;
            return Task.FromResult(new TranscriptionResponse { Text = "hello there", Duration = TimeSpan.FromSeconds(2), Usage = new Usage { Model = request.Model, InputTokens = 5 } });
        }

        public Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        {
            Speech = request;
            return Task.FromResult(new SpeechResponse { Audio = new byte[] { 1, 2, 3 }, ContentType = "audio/mpeg" });
        }

        public Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
        {
            Image = request;
            return Task.FromResult(new ImageResponse { Images = [new GeneratedImage(new byte[] { 9, 9 }, "image/png", null)] });
        }
    }

    [Fact]
    public async Task Speech_to_text_reads_the_stream_and_sniffs_the_container()
    {
        var media = new FakeMedia();
        using var client = new OneLlmSpeechToTextClient(media, "stt-1", Kit.Time);
        byte[] wav = [.. "RIFF\0\0\0\0WAVEfmt "u8.ToArray()];
        SpeechToTextResponse r = await client.GetTextAsync(new MemoryStream(wav), new SpeechToTextOptions { SpeechLanguage = "en" });
        Assert.Equal("hello there", r.Text);
        Assert.Equal("audio/wav", media.Transcription!.ContentType);
        Assert.Equal("en", media.Transcription.Language);
        Assert.Equal(wav, media.Transcription.Audio.ToArray());

        var given = new SpeechToTextOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [OneLlmProperties.ContentType] = "audio/x-custom" } };
        await client.GetTextAsync(new MemoryStream([1, 2, 3]), given);
        Assert.Equal("audio/x-custom", media.Transcription.ContentType);

        var updates = new List<SpeechToTextResponseUpdate>();
        await foreach (SpeechToTextResponseUpdate u in client.GetStreamingTextAsync(new MemoryStream(wav)))
        {
            updates.Add(u);
        }

        Assert.Equal("hello there", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task Text_to_speech_returns_audio_content()
    {
        var media = new FakeMedia();
        using var client = new OneLlmTextToSpeechClient(media, "tts-1", Kit.Time);
        TextToSpeechResponse r = await client.GetAudioAsync("hi", new TextToSpeechOptions { VoiceId = "alloy", AudioFormat = "mp3" });
        DataContent audio = Assert.IsType<DataContent>(Assert.Single(r.Contents));
        Assert.Equal("audio/mpeg", audio.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Data.ToArray());
        Assert.Equal("alloy", media.Speech!.Voice);
        Assert.Equal("mp3", media.Speech.Format);
    }

    [Fact]
    public async Task Image_generation_passes_size_count_and_original_images()
    {
        var media = new FakeMedia();
        using var generator = new OneLlmImageGenerator(media, "img-1", Kit.Time);
        ImageGenerationResponse r = await generator.GenerateAsync(
            new ImageGenerationRequest("a lighthouse", [new DataContent(new byte[] { 7 }, "image/png")]),
            new ImageGenerationOptions { Count = 2, ImageSize = new System.Drawing.Size(1024, 512) });
        DataContent image = Assert.IsType<DataContent>(Assert.Single(r.Contents));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("1024x512", media.Image!.Size);
        Assert.Equal(2, media.Image.Count);
        Assert.Single(media.Image.Inputs);
    }
}
