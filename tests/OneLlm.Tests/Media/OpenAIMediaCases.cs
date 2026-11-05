using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Media;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Tests.Media;

// Conformance cases for the OpenAI media endpoints. Each answer is written from the endpoint's API reference page (named in the case); the request
// bytes in the fixtures are what the client sent, checked field by field in the case. Costs are worked out by hand in the comments.
internal static class OpenAIMediaCases
{
    private const string Ref = "https://developers.openai.com/api/reference/resources/";

    private static string B64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    public static IEnumerable<MCase> All()
    {
        // ---- images ----

        yield return new MCase("openai", "images", "generate", Ref + "images/methods/generate", async run =>
        {
            ImageResponse r = await run.OpenAI().GenerateImageAsync(new ImageRequest { Model = "gpt-image-2.5-flare", Prompt = "A paper boat", Size = "1024x1024", Quality = "medium", Format = "png" });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1/images/generations", s.Path);
            JsonObject b = s.Json();
            Assert.Equal("gpt-image-2.5-flare", (string?)b["model"]);
            Assert.Equal("A paper boat", (string?)b["prompt"]);
            Assert.Equal("1024x1024", (string?)b["size"]);
            Assert.Equal("medium", (string?)b["quality"]);
            Assert.Equal("png", (string?)b["output_format"]);
            Assert.Null(b["n"]);
            GeneratedImage img = Assert.Single(r.Images);
            Assert.Equal("IMG-ONE", Encoding.ASCII.GetString(img.Data.Span));
            Assert.Equal("image/png", img.ContentType);
            Assert.Equal(12, r.Usage!.InputTokens);
            Assert.Equal(1056, r.Usage.OutputTokens);
            Assert.Equal(1056, r.Usage.GetCounter("outputImageTokens"));
            // 12 text input tokens at $5 per million = 0.00006; 1056 image output tokens at $30 per million = 0.03168.
            Assert.Equal(0.03174m, r.Cost!.Total);
            Assert.Equal(["inputTokens", "outputImageTokens"], r.Cost.Lines.Select(l => l.Counter).ToArray());
            Assert.Equal(r.Cost.Total, r.Usage.Cost);
            Assert.Empty(r.Warnings);
        }, new MReply(200, """{"created":1713833628,"output_format":"png","quality":"medium","size":"1024x1024","data":[{"b64_json":"@@0@@"}],"usage":{"input_tokens":12,"input_tokens_details":{"text_tokens":12,"image_tokens":0},"output_tokens":1056,"output_tokens_details":{"image_tokens":1056,"text_tokens":0},"total_tokens":1068}}""".Replace("@@0@@", B64("IMG-ONE"))));

        yield return new MCase("openai", "images", "generate-several", Ref + "images/methods/generate", async run =>
        {
            ImageResponse r = await run.OpenAI().GenerateImageAsync(new ImageRequest
            {
                Model = "gpt-image-1-mini",
                Prompt = "Two lanterns",
                Count = 2,
                ExtraParameters = new JsonObject { ["background"] = "transparent", ["moderation"] = "low" },
            });
            JsonObject b = Assert.Single(run.Sent).Json();
            Assert.Equal(2, (int?)b["n"]);
            Assert.Equal("transparent", (string?)b["background"]);
            Assert.Equal("low", (string?)b["moderation"]);
            Assert.Equal(2, r.Images.Count);
            Assert.Equal("image/webp", r.Images[0].ContentType);
            Assert.Equal("a lantern", r.Images[1].RevisedPrompt);
            // gpt-image-1-mini: text input $2, image output $8 per million. 20 * 2 / 1e6 = 0.00004; 2000 * 8 / 1e6 = 0.016.
            Assert.Equal(0.01604m, r.Cost!.Total);
        }, new MReply(200, """{"created":1,"output_format":"webp","data":[{"b64_json":"@@0@@"},{"b64_json":"@@1@@","revised_prompt":"a lantern"}],"usage":{"input_tokens":20,"input_tokens_details":{"text_tokens":20,"image_tokens":0},"output_tokens":2000,"output_tokens_details":{"image_tokens":2000,"text_tokens":0},"total_tokens":2020}}""".Replace("@@0@@", B64("A")).Replace("@@1@@", B64("B"))));

        yield return new MCase("openai", "images", "edit", Ref + "images/methods/edit", async run =>
        {
            byte[] source = Encoding.ASCII.GetBytes("SRC-IMAGE");
            ImageResponse r = await run.OpenAI().GenerateImageAsync(new ImageRequest
            {
                Model = "gpt-image-1.5",
                Prompt = "Add a watercolor effect",
                Inputs = [new ImageInput(source, "image/png")],
                Mask = new ImageInput(Encoding.ASCII.GetBytes("MASK"), "image/png"),
                Quality = "high",
            });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1/images/edits", s.Path);
            JsonObject b = s.Json();
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(source), (string?)b["images"]![0]!["image_url"]);
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String("MASK"u8), (string?)b["mask"]!["image_url"]);
            Assert.Equal("high", (string?)b["quality"]);
            // gpt-image-1.5: text in $5, image in $8, text out $10, image out $32. Input 60 = 20 text + 40 image; output 200 = 50 text + 150 image.
            // 20*5/1e6 = 0.0001; 40*8/1e6 = 0.00032; 50*10/1e6 = 0.0005; 150*32/1e6 = 0.0048. Sum 0.00572.
            Assert.Equal(0.00572m, r.Cost!.Total);
            Assert.Equal(4, r.Cost.Lines.Count);
            Assert.Equal(20m, r.Cost.Lines.Single(l => l.Counter == "inputTokens").Quantity);
        }, new MReply(200, """{"created":2,"quality":"high","size":"1024x1024","data":[{"b64_json":"@@0@@"}],"usage":{"input_tokens":60,"input_tokens_details":{"text_tokens":20,"image_tokens":40},"output_tokens":200,"output_tokens_details":{"image_tokens":150,"text_tokens":50},"total_tokens":260}}""".Replace("@@0@@", B64("EDITED"))));

        yield return new MCase("openai", "images", "dated-snapshot", Ref + "images/methods/generate", async run =>
        {
            ImageResponse r = await run.OpenAI().GenerateImageAsync(new ImageRequest { Model = "gpt-image-2-2026-04-21", Prompt = "A kite" });
            // The snapshot name has no catalog entry of its own and is priced as gpt-image-2: 10 * 5/1e6 + 500 * 30/1e6 = 0.00005 + 0.015.
            Assert.Equal(0.01505m, r.Cost!.Total);
        }, new MReply(200, """{"created":3,"data":[{"b64_json":"@@0@@"}],"usage":{"input_tokens":10,"input_tokens_details":{"text_tokens":10,"image_tokens":0},"output_tokens":500,"output_tokens_details":{"image_tokens":500,"text_tokens":0},"total_tokens":510}}""".Replace("@@0@@", B64("K"))));

        yield return new MCase("openai", "images", "stream", Ref + "images/methods/generate", async run =>
        {
            var events = new List<ImageStreamEvent>();
            await foreach (ImageStreamEvent e in run.OpenAI().StreamImageAsync(new ImageRequest { Model = "gpt-image-2.5-flare", Prompt = "A paper boat", PartialImages = 2 }))
            {
                events.Add(e);
            }

            JsonObject b = Assert.Single(run.Sent).Json();
            Assert.True((bool?)b["stream"]);
            Assert.Equal(2, (int?)b["partial_images"]);
            Assert.Equal([ImageStreamEventKind.Partial, ImageStreamEventKind.Partial, ImageStreamEventKind.Completed], events.Select(e => e.Kind).ToArray());
            Assert.Equal(1, events[1].Index);
            Assert.Equal("P2", Encoding.ASCII.GetString(events[1].Image.Data.Span));
            Assert.Equal("FINAL", Encoding.ASCII.GetString(events[2].Image.Data.Span));
            // 50 input tokens (10 text + 40 image) with only the text priced here is not the case; this model prices image input, so 10*5/1e6 + 40*8/1e6 + 100*30/1e6.
            Assert.Equal(0.00005m + 0.00032m + 0.003m, events[2].Cost!.Total);
        }, new MReply(200, Sse: [
            $"event: image_generation.partial_image\ndata: {{\"type\":\"image_generation.partial_image\",\"b64_json\":\"{B64("P1")}\",\"partial_image_index\":0}}\n\n",
            $"event: image_generation.partial_image\ndata: {{\"type\":\"image_generation.partial_image\",\"b64_json\":\"{B64("P2")}\",\"partial_image_index\":1}}\n\n",
            $"event: image_generation.completed\ndata: {{\"type\":\"image_generation.completed\",\"b64_json\":\"{B64("FINAL")}\",\"usage\":{{\"total_tokens\":150,\"input_tokens\":50,\"output_tokens\":100,\"input_tokens_details\":{{\"text_tokens\":10,\"image_tokens\":40}}}}}}\n\n",
        ]));

        yield return new MCase("openai", "images", "unauthorized", Ref + "images/methods/generate", async run =>
        {
            AuthException e = await Assert.ThrowsAsync<AuthException>(() => run.OpenAI().GenerateImageAsync(new ImageRequest { Model = "gpt-image-2", Prompt = "x" }));
            Assert.DoesNotContain(MediaRun.FakeKey, e.Message, StringComparison.Ordinal);
        }, new MReply(401, """{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","param":null,"code":"invalid_api_key"}}"""));

        // ---- speech ----

        yield return new MCase("openai", "speech", "tts-1", Ref + "audio/subresources/speech/methods/create", async run =>
        {
            SpeechResponse r = await run.OpenAI().SynthesizeAsync(new SpeechRequest { Model = "tts-1", Input = "Hello there", Voice = "nova" });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1/audio/speech", s.Path);
            JsonObject b = s.Json();
            Assert.Equal("nova", (string?)b["voice"]);
            Assert.Null(b["response_format"]);
            Assert.Equal("MP3-BYTES", Encoding.ASCII.GetString(r.Audio.Span));
            Assert.Equal("audio/mpeg", r.ContentType);
            // 11 characters at $15 per million characters = 0.000165.
            Assert.Equal(0.000165m, r.Cost!.Total);
            Assert.Equal(11, r.Usage!.GetCounter("characters"));
        }, new MReply(200, Bytes: Encoding.ASCII.GetBytes("MP3-BYTES"), ContentType: "audio/mpeg"));

        yield return new MCase("openai", "speech", "gpt-4o-mini-tts", Ref + "audio/subresources/speech/methods/create", async run =>
        {
            SpeechResponse r = await run.OpenAI().SynthesizeAsync(new SpeechRequest { Model = "gpt-4o-mini-tts", Input = "Good morning", Format = "wav", Instructions = "calm and warm", Speed = 1.25 });
            JsonObject b = Assert.Single(run.Sent).Json();
            Assert.Equal("alloy", (string?)b["voice"]);
            Assert.Equal("wav", (string?)b["response_format"]);
            Assert.Equal("calm and warm", (string?)b["instructions"]);
            Assert.Equal(1.25, (double?)b["speed"]);
            Assert.Equal("audio/wav", r.ContentType);
            // The endpoint reports no usage and this model is priced per token: no cost, and the warning says why.
            Assert.Null(r.Cost);
            Assert.Equal("usage_missing", Assert.Single(r.Warnings).Code);
        }, new MReply(200, Bytes: Encoding.ASCII.GetBytes("WAV-BYTES"), ContentType: "audio/wav"));

        yield return new MCase("openai", "speech", "stream", Ref + "audio/subresources/speech/methods/create", async run =>
        {
            await using SpeechAudio audio = await run.OpenAI().StreamSynthesisAsync(new SpeechRequest { Model = "tts-1-hd", Input = "Streaming", Format = "opus" });
            using var ms = new MemoryStream();
            await audio.Audio.CopyToAsync(ms);
            Assert.Equal("OPUS-BYTES", Encoding.ASCII.GetString(ms.ToArray()));
            Assert.Equal("audio/opus", audio.ContentType);
            JsonObject b = Assert.Single(run.Sent).Json();
            Assert.Equal("audio", (string?)b["stream_format"]);
            // 9 characters at $30 per million = 0.00027.
            Assert.Equal(0.00027m, audio.Cost!.Total);
        }, new MReply(200, Bytes: Encoding.ASCII.GetBytes("OPUS-BYTES"), ContentType: "audio/opus"));

        // ---- transcription ----

        yield return new MCase("openai", "transcription", "whisper-json", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest
            {
                Model = "whisper-1",
                Audio = Encoding.ASCII.GetBytes("AUDIO-BYTES"),
                ContentType = "audio/mpeg",
                Language = "en",
                Prompt = "Ada, Lovelace",
            });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1/audio/transcriptions", s.Path);
            Assert.StartsWith("multipart/form-data; boundary=onellm-", s.ContentType, StringComparison.Ordinal);
            string form = s.Text();
            Assert.Contains("name=\"file\"; filename=\"audio.mp3\"", form, StringComparison.Ordinal);
            Assert.Contains("Content-Type: audio/mpeg\r\n\r\nAUDIO-BYTES\r\n", form, StringComparison.Ordinal);
            Assert.Contains("name=\"model\"\r\n\r\nwhisper-1\r\n", form, StringComparison.Ordinal);
            Assert.Contains("name=\"language\"\r\n\r\nen\r\n", form, StringComparison.Ordinal);
            Assert.Equal("Hello, Ada.", r.Text);
            // Usage is 4 seconds of audio at $0.006 per minute = $0.0001 per second: 4 * 0.0001.
            Assert.Equal(0.0004m, r.Cost!.Total);
            Assert.Equal(4, r.Usage!.GetCounter("audioSeconds"));
        }, new MReply(200, """{"text":"Hello, Ada.","usage":{"type":"duration","seconds":4}}"""));

        yield return new MCase("openai", "transcription", "verbose-words", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest
            {
                Model = "whisper-1",
                Audio = Encoding.ASCII.GetBytes("A"),
                ContentType = "audio/wav",
                FileName = "clip.wav",
                ResponseFormat = "verbose_json",
                TimestampGranularities = ["word", "segment"],
            });
            string form = Assert.Single(run.Sent).Text();
            Assert.Contains("name=\"response_format\"\r\n\r\nverbose_json\r\n", form, StringComparison.Ordinal);
            Assert.Equal(2, form.Split("name=\"timestamp_granularities[]\"").Length - 1);
            Assert.Equal("english", r.Language);
            Assert.Equal(TimeSpan.FromSeconds(0.5), r.Duration);
            Assert.Equal("Hello", (string?)r.Details!["words"]![0]!["word"]);
            Assert.Equal(1, r.Usage!.GetCounter("audioSeconds"));
        }, new MReply(200, """{"task":"transcribe","language":"english","duration":0.5,"text":"Hello.","words":[{"word":"Hello","start":0.0,"end":0.5}],"segments":[{"id":0,"seek":0,"start":0.0,"end":0.5,"text":" Hello.","tokens":[1],"temperature":0.0,"avg_logprob":-0.2,"compression_ratio":1.0,"no_speech_prob":0.01}],"usage":{"type":"duration","seconds":1}}"""));

        yield return new MCase("openai", "transcription", "gpt-4o-tokens", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest { Model = "gpt-4o-transcribe", Audio = Encoding.ASCII.GetBytes("A"), ContentType = "audio/flac" });
            Assert.Contains("filename=\"audio.flac\"", Assert.Single(run.Sent).Text(), StringComparison.Ordinal);
            Assert.Equal(14, r.Usage!.InputTokens);
            Assert.Equal(14, r.Usage.GetCounter("inputAudioTokens"));
            Assert.Equal(45, r.Usage.OutputTokens);
            // gpt-4o-transcribe: $2.50 per million input tokens, $10 per million output tokens. 14*2.5/1e6 = 0.000035; 45*10/1e6 = 0.00045.
            Assert.Equal(0.000485m, r.Cost!.Total);
        }, new MReply(200, """{"text":"Imagine the wildest idea.","usage":{"type":"tokens","input_tokens":14,"input_token_details":{"text_tokens":0,"audio_tokens":14},"output_tokens":45,"total_tokens":59}}"""));

        yield return new MCase("openai", "transcription", "diarized", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest { Model = "gpt-4o-transcribe-diarize", Audio = Encoding.ASCII.GetBytes("A"), ContentType = "audio/wav", ResponseFormat = "diarized_json" });
            string form = Assert.Single(run.Sent).Text();
            Assert.Contains("name=\"response_format\"\r\n\r\ndiarized_json\r\n", form, StringComparison.Ordinal);
            Assert.Equal("agent", (string?)r.Details!["segments"]![0]!["speaker"]);
            Assert.Equal(TimeSpan.FromSeconds(27.4), r.Duration);
            // Duration usage of 27 seconds at the estimated $0.006 per minute: 27 * 0.0001 = 0.0027.
            Assert.Equal(0.0027m, r.Cost!.Total);
        }, new MReply(200, """{"task":"transcribe","duration":27.4,"text":"Agent: Thanks for calling.\nA: Hi.","segments":[{"type":"transcript.text.segment","id":"seg_001","start":0.0,"end":4.7,"text":"Thanks for calling.","speaker":"agent"},{"type":"transcript.text.segment","id":"seg_002","start":4.7,"end":6.0,"text":"Hi.","speaker":"A"}],"usage":{"type":"duration","seconds":27}}"""));

        yield return new MCase("openai", "transcription", "translate", Ref + "audio/subresources/translations/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest { Model = "whisper-1", Audio = Encoding.ASCII.GetBytes("A"), ContentType = "audio/mp4", Translate = true, Language = "de" });
            Sent s = Assert.Single(run.Sent);
            Assert.Equal("/v1/audio/translations", s.Path);
            // The translations endpoint takes no language field.
            Assert.DoesNotContain("name=\"language\"", s.Text(), StringComparison.Ordinal);
            Assert.Equal("Hello, my name is Wolfgang.", r.Text);
            // No usage object and no duration: nothing to price.
            Assert.Null(r.Cost);
            Assert.Equal("usage_missing", Assert.Single(r.Warnings).Code);
        }, new MReply(200, """{"text":"Hello, my name is Wolfgang."}"""));

        yield return new MCase("openai", "transcription", "subtitles", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            TranscriptionResponse r = await run.OpenAI().TranscribeAsync(new TranscriptionRequest { Model = "whisper-1", Audio = Encoding.ASCII.GetBytes("A"), ContentType = "audio/wav", ResponseFormat = "srt" });
            Assert.StartsWith("1\n00:00:00,000 --> 00:00:02,000\nHello", r.Text, StringComparison.Ordinal);
            Assert.Null(r.Cost);
        }, new MReply(200, Bytes: Encoding.ASCII.GetBytes("1\n00:00:00,000 --> 00:00:02,000\nHello\n"), ContentType: "text/plain"));

        yield return new MCase("openai", "transcription", "stream", Ref + "audio/subresources/transcriptions/methods/create", async run =>
        {
            var deltas = new StringBuilder();
            TranscriptionResponse? done = null;
            await foreach (TranscriptionStreamEvent e in run.OpenAI().StreamTranscribeAsync(new TranscriptionRequest { Model = "gpt-4o-mini-transcribe", Audio = Encoding.ASCII.GetBytes("A"), ContentType = "audio/mpeg" }))
            {
                deltas.Append(e.Delta);
                done = e.Completed ?? done;
            }

            Assert.Contains("name=\"stream\"\r\n\r\ntrue\r\n", Assert.Single(run.Sent).Text(), StringComparison.Ordinal);
            Assert.Equal("I see skies", deltas.ToString());
            Assert.Equal("I see skies", done!.Text);
            // gpt-4o-mini-transcribe: $1.25 and $5 per million. 14*1.25/1e6 = 0.0000175; 45*5/1e6 = 0.000225.
            Assert.Equal(0.0002425m, done.Cost!.Total);
        }, new MReply(200, Sse: [
            "data: {\"type\":\"transcript.text.delta\",\"delta\":\"I see\"}\n\n",
            "data: {\"type\":\"transcript.text.delta\",\"delta\":\" skies\"}\n\n",
            "data: {\"type\":\"transcript.text.done\",\"text\":\"I see skies\",\"usage\":{\"input_tokens\":14,\"input_token_details\":{\"text_tokens\":0,\"audio_tokens\":14},\"output_tokens\":45,\"total_tokens\":59}}\n\n",
        ]));

        // ---- moderation ----

        yield return new MCase("openai", "moderation", "texts", Ref + "moderations/methods/create", async run =>
        {
            ModerationResponse r = await run.OpenAI().ModerateAsync(new ModerationRequest { Model = "omni-moderation-latest", Inputs = ["fine text", "angry text"] });
            JsonObject b = Assert.Single(run.Sent).Json();
            Assert.Equal(["fine text", "angry text"], b["input"]!.AsArray().Select(n => (string?)n).ToArray());
            Assert.Equal(2, r.Results.Count);
            Assert.False(r.Results[0].Flagged);
            Assert.True(r.Results[1].Flagged);
            Assert.True(r.Results[1].Categories["harassment"]);
            Assert.False(r.Results[1].Categories["illicit"]);
            Assert.Equal(0.81, r.Results[1].Scores["harassment"], 6);
            Assert.Equal(["text", "image"], r.Results[1].CategoryInputTypes!["violence"]);
            // The endpoint is free.
            Assert.Equal(0m, r.Cost!.Total);
        }, new MReply(200, """{"id":"modr-1","model":"omni-moderation-latest","results":[{"flagged":false,"categories":{"harassment":false,"illicit":null},"category_scores":{"harassment":0.001,"illicit":0.0},"category_applied_input_types":{"harassment":["text"],"illicit":["text"]}},{"flagged":true,"categories":{"harassment":true,"illicit":null,"violence":false},"category_scores":{"harassment":0.81,"illicit":0.0,"violence":0.02},"category_applied_input_types":{"harassment":["text"],"illicit":["text"],"violence":["text","image"]}}]}"""));

        yield return new MCase("openai", "moderation", "image-and-text", Ref + "moderations/methods/create", async run =>
        {
            ModerationResponse r = await run.OpenAI().ModerateAsync(new ModerationRequest
            {
                Model = "omni-moderation-latest",
                Inputs = ["a caption"],
                Images = [new ImageInput(Encoding.ASCII.GetBytes("IMG"), "image/jpeg")],
            });
            JsonArray input = Assert.Single(run.Sent).Json()["input"]!.AsArray();
            Assert.Equal("text", (string?)input[0]!["type"]);
            Assert.Equal("a caption", (string?)input[0]!["text"]);
            Assert.Equal("image_url", (string?)input[1]!["type"]);
            Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String("IMG"u8), (string?)input[1]!["image_url"]!["url"]);
            Assert.Single(r.Results);
            Assert.True(r.Results[0].Flagged);
        }, new MReply(200, """{"id":"modr-2","model":"omni-moderation-latest","results":[{"flagged":true,"categories":{"violence":true},"category_scores":{"violence":0.99},"category_applied_input_types":{"violence":["text","image"]}}]}"""));
    }
}
