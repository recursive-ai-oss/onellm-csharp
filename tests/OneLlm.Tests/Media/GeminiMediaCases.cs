using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Tests.Media;

// Conformance cases for Gemini image and speech output through generateContent. Answers are written from the generateContent reference and the
// image and speech generation guides; usage numbers are chosen so each cost can be checked by hand.
internal static class GeminiMediaCases
{
    private const string Ref = "https://ai.google.dev/api/generate-content";

    private static string B64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    public static IEnumerable<MCase> All()
    {
        yield return new MCase("gemini", "images", "generate", Ref, async run =>
        {
            ImageResponse r = await run.Gemini().GenerateImageAsync(new ImageRequest
            {
                Model = "gemini-nano-banana-2.1",
                Prompt = "A paper boat on a pond",
                Size = "2K",
                AspectRatio = "16:9",
            });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1beta/models/gemini-nano-banana-2.1:generateContent", s.Path);
            JsonObject b = s.Json();
            Assert.Equal("A paper boat on a pond", (string?)b["contents"]![0]!["parts"]![0]!["text"]);
            Assert.Equal("IMAGE", (string?)b["generationConfig"]!["responseModalities"]![0]);
            Assert.Equal("16:9", (string?)b["generationConfig"]!["imageConfig"]!["aspectRatio"]);
            Assert.Equal("2K", (string?)b["generationConfig"]!["imageConfig"]!["imageSize"]);
            GeneratedImage img = Assert.Single(r.Images);
            Assert.Equal("IMG-BYTES", Encoding.ASCII.GetString(img.Data.Span));
            Assert.Equal("image/png", img.ContentType);
            Assert.Equal("Here is your boat.", r.Text);
            // Input 10 tokens at $1.50 per million = 0.000015. Output 1700 = 1680 image + 20 text: text at $7.50 = 0.00015, image at $30 = 0.0504.
            Assert.Equal(0.050565m, r.Cost!.Total);
            Assert.Equal(1680, r.Usage!.GetCounter("outputImageTokens"));
            Assert.Empty(r.Warnings);
        }, new MReply(200, """{"candidates":[{"content":{"role":"model","parts":[{"text":"Here is your boat."},{"inlineData":{"mimeType":"image/png","data":"@@0@@"}}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":1700,"totalTokenCount":1710,"promptTokensDetails":[{"modality":"TEXT","tokenCount":10}],"candidatesTokensDetails":[{"modality":"IMAGE","tokenCount":1680},{"modality":"TEXT","tokenCount":20}]},"modelVersion":"gemini-nano-banana-2.1"}""".Replace("@@0@@", B64("IMG-BYTES"))));

        yield return new MCase("gemini", "images", "edit-two", Ref, async run =>
        {
            ImageResponse r = await run.Gemini().GenerateImageAsync(new ImageRequest
            {
                Model = "gemini-3.1-flash-lite-image",
                Prompt = "Make the sky orange",
                Inputs = [new ImageInput(Encoding.ASCII.GetBytes("SRC"), "image/jpeg")],
                Count = 2,
                Quality = "high",
                Size = "1024x1024",
            });
            Assert.Equal(2, run.Sent.Count);
            JsonObject b = run.Sent[0].Json();
            Assert.Equal("image/jpeg", (string?)b["contents"]![0]!["parts"]![1]!["inlineData"]!["mimeType"]);
            Assert.Equal(Convert.ToBase64String("SRC"u8), (string?)b["contents"]![0]!["parts"]![1]!["inlineData"]!["data"]);
            Assert.Equal("1K", (string?)b["generationConfig"]!["imageConfig"]!["imageSize"]);
            Assert.Equal(2, r.Images.Count);
            Assert.Equal("image/jpeg", r.Images[1].ContentType);
            // The model has no quality setting: it is dropped and a warning says so.
            Assert.Equal("quality", Assert.Single(r.Warnings).Parameter);
            // Per call: input 30 at $0.25 = 0.0000075; image output 1120 at $30 = 0.0336. Two calls: 2 x 0.0336075 = 0.067215.
            Assert.Equal(0.067215m, r.Cost!.Total);
            Assert.Equal(60, r.Usage!.InputTokens);
        }, new MReply(200, """{"candidates":[{"content":{"role":"model","parts":[{"thought":true,"text":"planning"},{"inlineData":{"mimeType":"image/jpeg","data":"@@0@@"}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":1120,"promptTokensDetails":[{"modality":"TEXT","tokenCount":10},{"modality":"IMAGE","tokenCount":20}],"candidatesTokensDetails":[{"modality":"IMAGE","tokenCount":1120}]}}""".Replace("@@0@@", B64("EDIT"))));

        yield return new MCase("gemini", "images", "blocked", Ref, async run =>
        {
            ContentFilterException e = await Assert.ThrowsAsync<ContentFilterException>(() => run.Gemini().GenerateImageAsync(new ImageRequest { Model = "gemini-3-pro-image", Prompt = "blocked prompt" }));
            Assert.Contains("PROHIBITED_CONTENT", e.Message, StringComparison.Ordinal);
        }, new MReply(200, """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},"usageMetadata":{"promptTokenCount":3,"totalTokenCount":3}}"""));

        yield return new MCase("gemini", "speech", "pcm-to-wav", Ref, async run =>
        {
            SpeechResponse r = await run.Gemini().SynthesizeAsync(new SpeechRequest { Model = "gemini-2.5-flash-preview-tts", Input = "Say cheerfully: have a nice day", Instructions = "Say softly", Voice = "Puck" });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1beta/models/gemini-2.5-flash-preview-tts:generateContent", s.Path);
            JsonObject b = s.Json();
            Assert.Equal("Say softly: Say cheerfully: have a nice day", (string?)b["contents"]![0]!["parts"]![0]!["text"]);
            Assert.Equal("AUDIO", (string?)b["generationConfig"]!["responseModalities"]![0]);
            Assert.Equal("Puck", (string?)b["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]);
            // Raw 16-bit PCM at 24 kHz comes back wrapped as a WAV file.
            Assert.Equal("audio/wav", r.ContentType);
            byte[] wav = r.Audio.ToArray();
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
            Assert.Equal(44 + 8, wav.Length);
            // Input 8 tokens at $0.50 = 0.000004; 100 audio output tokens at $10 = 0.001.
            Assert.Equal(0.001004m, r.Cost!.Total);
        }, new MReply(200, """{"candidates":[{"content":{"role":"model","parts":[{"inlineData":{"mimeType":"audio/L16;codec=pcm;rate=24000","data":"@@0@@"}}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":8,"candidatesTokenCount":100,"totalTokenCount":108,"promptTokensDetails":[{"modality":"TEXT","tokenCount":8}],"candidatesTokensDetails":[{"modality":"AUDIO","tokenCount":100}]}}""".Replace("@@0@@", Convert.ToBase64String(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }))));

        yield return new MCase("gemini", "speech", "dialogue", Ref, async run =>
        {
            SpeechResponse r = await run.Gemini().SynthesizeAsync(new SpeechRequest
            {
                Model = "gemini-3.8-flash-tts",
                Input = "Joe: How are you today?\nJane: Not bad.\nHow about you?",
                Speakers = [new SpeakerVoice("Joe", "Puck"), new SpeakerVoice("Jane", "Kore")],
                Instructions = "friendly",
            });
            JsonObject b = Assert.Single(run.Sent).Json();
            JsonArray parts = b["contents"]![0]!["parts"]!.AsArray();
            Assert.Equal(2, parts.Count);
            Assert.Equal("How are you today?", (string?)parts[0]!["text"]);
            Assert.Equal("Joe", (string?)parts[0]!["speechMetadata"]!["speaker"]);
            Assert.Equal("friendly", (string?)parts[0]!["speechMetadata"]!["style"]);
            // A line with no speaker name continues the turn before it.
            Assert.Equal("Not bad.\nHow about you?", (string?)parts[1]!["text"]);
            JsonArray voices = b["generationConfig"]!["speechConfig"]!["multiSpeakerVoiceConfig"]!["speakerVoiceConfigs"]!.AsArray();
            Assert.Equal("Kore", (string?)voices[1]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]);
            // Gemini 3.8 answers with a WAV file already; it is returned as it came.
            Assert.Equal("audio/wav", r.ContentType);
            Assert.Equal("RIFF-WAV", Encoding.ASCII.GetString(r.Audio.Span));
            // Input 20 tokens at $0.50 = 0.00001; 250 audio tokens at $9 = 0.00225.
            Assert.Equal(0.00226m, r.Cost!.Total);
        }, new MReply(200, """{"candidates":[{"content":{"role":"model","parts":[{"inlineData":{"mimeType":"audio/wav","data":"@@0@@"}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":250,"candidatesTokensDetails":[{"modality":"AUDIO","tokenCount":250}]}}""".Replace("@@0@@", B64("RIFF-WAV"))));

        yield return new MCase("gemini", "speech", "raw-pcm", Ref, async run =>
        {
            SpeechResponse r = await run.Gemini().SynthesizeAsync(new SpeechRequest { Model = "gemini-3.1-flash-tts-preview", Input = "Hi", Format = "pcm" });
            Assert.Equal("audio/L16;rate=24000", r.ContentType);
            Assert.Equal([9, 0], r.Audio.ToArray());
            // No per-modality detail in the usage: the candidate tokens of a speech call are audio. 5 * 1/1e6 + 50 * 20/1e6.
            Assert.Equal(0.001005m, r.Cost!.Total);
        }, new MReply(200, """{"candidates":[{"content":{"role":"model","parts":[{"inlineData":{"mimeType":"audio/L16;codec=pcm;rate=24000","data":"@@0@@"}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":50}}""".Replace("@@0@@", Convert.ToBase64String(new byte[] { 9, 0 }))));
    }
}
