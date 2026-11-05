using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Media;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

// Image and speech output from generateContent (https://ai.google.dev/api/generate-content): the request asks for the IMAGE or AUDIO response
// modality and the answer carries the media as base64 inlineData parts. The usage metadata reports tokens per modality, and that is what is priced.
public sealed partial class GeminiProvider : IImageProvider, ITextToSpeechProvider
{
    // The most images one call is asked for; Gemini makes one image per generateContent call, so a larger count is several calls.
    private const int MaxImagesPerRequest = 8;

    private const int DefaultSpeechRate = 24000;

    /// <summary>
    /// Generates images with an image-output Gemini model (<c>generateContent</c> with <c>responseModalities: ["IMAGE"]</c>), or edits the images in
    /// <see cref="ImageRequest.Inputs"/> by sending them with the prompt. <see cref="ImageRequest.Size"/> is the image size word (<c>512</c>, <c>1K</c>, <c>2K</c>, <c>4K</c>; a square pixel
    /// size such as <c>1024x1024</c> is mapped to it) and <see cref="ImageRequest.AspectRatio"/> a ratio such as <c>16:9</c>. A request for several images makes several
    /// calls, one image each, and adds their usage. Quality, format and mask have no Gemini counterpart: they are left out and a warning says so.
    /// </summary>
    /// <param name="request">The request. <see cref="ImageRequest.Count"/> is 1 to 8.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The images, any text the model wrote with them, and the usage and cost.</returns>
    /// <exception cref="ArgumentException">The request has no model or prompt, or asks for too many images.</exception>
    /// <exception cref="ContentFilterException">The prompt or the image was blocked.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public async Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("An image request needs a model.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("An image request needs a prompt.", nameof(request));
        }

        if (request.Count is < 1 or > MaxImagesPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"The number of images must be from 1 to {MaxImagesPerRequest}.");
        }

        var warnings = new List<ResponseWarning>();
        byte[] body = ImageBody(request, warnings);
        var images = new List<GeneratedImage>();
        var text = new StringBuilder();
        Usage? total = null;
        for (int i = 0; i < request.Count; i++)
        {
            using JsonDocument doc = await SendForJsonAsync(HttpMethod.Post, _endpoint.Model(request.Model, "generateContent"), body, cancellationToken).ConfigureAwait(false);
            Usage usage = ReadMediaUsage(doc.RootElement, request.Model);
            total = total is null ? usage : MediaCosting.Sum(total, usage);
            ReadImageParts(doc.RootElement, images, text);
        }

        (Usage priced, Cost? cost) = MediaCosting.Price(_catalog, Name, request.Model, total!, _time, warnings);
        return new ImageResponse
        {
            Images = images,
            Text = text.Length == 0 ? null : text.ToString(),
            Model = request.Model,
            Usage = priced,
            Cost = cost,
            Warnings = warnings,
        };
    }

    internal static byte[] ImageBody(ImageRequest request, List<ResponseWarning> warnings)
    {
        if (request.Quality is not null)
        {
            warnings.Add(new ResponseWarning("parameter_stripped", "Gemini image models have no quality setting; it was not sent.", "quality"));
        }

        if (request.Format is not null)
        {
            warnings.Add(new ResponseWarning("parameter_stripped", "Gemini returns the image in the format it chooses; the requested format was not sent.", "format"));
        }

        if (request.Mask is not null)
        {
            warnings.Add(new ResponseWarning("parameter_stripped", "Gemini image models take no mask; describe the region to change in the prompt. The mask was not sent.", "mask"));
        }

        string? size = ImageSizeWord(request.Size);
        if (request.Size is not null && size is null)
        {
            warnings.Add(new ResponseWarning("parameter_stripped", $"'{request.Size}' is not an image size Gemini takes (512, 1K, 2K or 4K); it was not sent.", "size"));
        }

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", request.Prompt);
            w.WriteEndObject();
            foreach (ImageInput input in request.Inputs)
            {
                w.WriteStartObject();
                w.WriteStartObject("inlineData");
                w.WriteString("mimeType", input.ContentType.Length > 0 ? input.ContentType : "image/png");
                w.WriteBase64String("data", input.Data.Span);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteStartArray("responseModalities");
            w.WriteStringValue("IMAGE");
            w.WriteEndArray();
            if (size is not null || request.AspectRatio is not null)
            {
                w.WriteStartObject("imageConfig");
                if (request.AspectRatio is not null)
                {
                    w.WriteString("aspectRatio", request.AspectRatio);
                }

                if (size is not null)
                {
                    w.WriteString("imageSize", size);
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
            WriteExtras(w, request.ExtraParameters);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // imageConfig.imageSize takes 512, 1K, 2K or 4K (generate-content reference, "ImageConfig"). Square pixel sizes are mapped to them for callers
    // who think in pixels.
    private static string? ImageSizeWord(string? size)
    {
        if (size is null)
        {
            return null;
        }

        string s = size.Trim().ToUpperInvariant();
        if (s is "512" or "1K" or "2K" or "4K")
        {
            return s;
        }

        return s switch
        {
            "512X512" => "512",
            "1024X1024" => "1K",
            "2048X2048" => "2K",
            "4096X4096" => "4K",
            _ => null,
        };
    }

    private void ReadImageParts(JsonElement root, List<GeneratedImage> images, StringBuilder text)
    {
        int before = images.Count;
        if (root.TryGetProperty("candidates", out JsonElement candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            JsonElement candidate = candidates[0];
            if (candidate.TryGetProperty("content", out JsonElement content) && content.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    // Thought parts are the model's drafts (thinking image models); only the final output is returned.
                    if (part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    if (part.TryGetProperty("inlineData", out JsonElement inline) && Str(inline, "data"u8) is { } data)
                    {
                        images.Add(new GeneratedImage(DecodeBase64(data), Str(inline, "mimeType"u8) ?? "image/png", null));
                    }
                    else if (Str(part, "text"u8) is { Length: > 0 } t)
                    {
                        text.Append(t);
                    }
                }
            }

            if (images.Count == before)
            {
                string? finish = Str(candidate, "finishReason"u8);
                if (GeminiFinish.Map(finish) == FinishReason.ContentFilter)
                {
                    throw new ContentFilterException(Name, $"Gemini returned no image because it was blocked ({finish}).");
                }
            }
        }

        if (images.Count == before)
        {
            if (root.TryGetProperty("promptFeedback", out JsonElement feedback) && Str(feedback, "blockReason"u8) is { } reason)
            {
                throw new ContentFilterException(Name, $"Gemini blocked the prompt ({reason}).");
            }

            throw new MalformedResponseException(Name, "the response held no image.");
        }
    }

    /// <summary>
    /// Turns text into speech with a Gemini text-to-speech model (<c>generateContent</c> with <c>responseModalities: ["AUDIO"]</c>). The voice is a prebuilt voice
    /// name (<c>Kore</c> when none is given); <see cref="SpeechRequest.Speakers"/> sets up a dialogue, in which the input marks each line as <c>Name: text</c>.
    /// <see cref="SpeechRequest.Instructions"/> becomes the turn's style for models that take speech metadata, and is put in front of the text otherwise. The audio is a WAV file
    /// by default (<c>wav</c>); <c>pcm</c> returns the raw 16-bit samples. The cost is priced from the audio tokens the API reports.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The audio, usage and cost.</returns>
    /// <exception cref="ArgumentException">The request has no model or text, or asks for a format Gemini does not produce (only <c>wav</c> and <c>pcm</c>).</exception>
    /// <exception cref="ContentFilterException">The text was blocked.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public async Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("A speech request needs a model.", nameof(request));
        }

        if (string.IsNullOrEmpty(request.Input))
        {
            throw new ArgumentException("A speech request needs text to speak.", nameof(request));
        }

        string format = (request.Format ?? "wav").ToLowerInvariant();
        if (format is not ("wav" or "pcm"))
        {
            throw new ArgumentException($"Gemini speech comes as WAV or raw PCM, not '{request.Format}'.", nameof(request));
        }

        var warnings = new List<ResponseWarning>();
        if (request.Speed is not null)
        {
            warnings.Add(new ResponseWarning("parameter_stripped", "Gemini speech has no speed setting; say it in the instructions. Speed was not sent.", "speed"));
        }

        byte[] body = SpeechBody(request);
        using JsonDocument doc = await SendForJsonAsync(HttpMethod.Post, _endpoint.Model(request.Model, "generateContent"), body, cancellationToken).ConfigureAwait(false);
        JsonElement root = doc.RootElement;
        (byte[] audio, string mime) = ReadAudioPart(root);

        Usage usage = ReadMediaUsage(root, request.Model, audioOutput: true);
        (usage, Cost? cost) = MediaCosting.Price(_catalog, Name, request.Model, usage, _time, warnings);
        (byte[] bytes, string contentType) = ShapeAudio(audio, mime, format);
        return new SpeechResponse { Audio = bytes, ContentType = contentType, Usage = usage, Cost = cost, Warnings = warnings };
    }

    internal byte[] SpeechBody(SpeechRequest request)
    {
        ModelSpec? spec = SpecFor(request.Model);
        bool metadata = spec?.Properties is { } props && props.TryGetValue("speechMetadata", out object? v) && v is true or "true";
        IReadOnlyList<SpeakerVoice>? speakers = request.Speakers is { Count: > 0 } ? request.Speakers : null;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("parts");
            if (metadata)
            {
                // Speech models from 3.8 take the text as a verbatim transcript and the delivery as speechMetadata on each part
                // (the model page's migration notes); inline "say it like this" directions would be read aloud.
                foreach ((string? speaker, string line) in speakers is null ? [(null, request.Input)] : SplitTurns(request.Input, speakers))
                {
                    w.WriteStartObject();
                    w.WriteString("text", line);
                    if (speaker is not null || request.Instructions is not null)
                    {
                        w.WriteStartObject("speechMetadata");
                        if (speaker is not null)
                        {
                            w.WriteString("speaker", speaker);
                        }

                        if (request.Instructions is not null)
                        {
                            w.WriteString("style", request.Instructions);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }
            }
            else
            {
                // Earlier speech models take their direction in the prompt ("Say cheerfully: ..."), and speaker labels as "Name: line".
                w.WriteStartObject();
                w.WriteString("text", request.Instructions is null ? request.Input : request.Instructions + ": " + request.Input);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteStartArray("responseModalities");
            w.WriteStringValue("AUDIO");
            w.WriteEndArray();
            w.WriteStartObject("speechConfig");
            if (speakers is null)
            {
                w.WriteStartObject("voiceConfig");
                WritePrebuilt(w, request.Voice ?? "Kore");
                w.WriteEndObject();
            }
            else
            {
                w.WriteStartObject("multiSpeakerVoiceConfig");
                w.WriteStartArray("speakerVoiceConfigs");
                foreach (SpeakerVoice s in speakers)
                {
                    w.WriteStartObject();
                    w.WriteString("speaker", s.Speaker);
                    w.WriteStartObject("voiceConfig");
                    WritePrebuilt(w, s.Voice);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteEndObject();
            WriteExtras(w, request.ExtraParameters);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePrebuilt(Utf8JsonWriter w, string voice)
    {
        w.WriteStartObject("prebuiltVoiceConfig");
        w.WriteString("voiceName", voice);
        w.WriteEndObject();
    }

    // "Joe: How are you?\nJane: Fine." becomes two turns. A line that starts with no known speaker continues the previous turn.
    private static List<(string? Speaker, string Text)> SplitTurns(string input, IReadOnlyList<SpeakerVoice> speakers)
    {
        var turns = new List<(string? Speaker, string Text)>();
        foreach (string line in input.Split('\n'))
        {
            string? matched = null;
            foreach (SpeakerVoice s in speakers)
            {
                if (line.StartsWith(s.Speaker + ":", StringComparison.Ordinal))
                {
                    matched = s.Speaker;
                    break;
                }
            }

            if (matched is not null)
            {
                turns.Add((matched, line[(matched.Length + 1)..].Trim()));
            }
            else if (turns.Count > 0)
            {
                turns[^1] = (turns[^1].Speaker, turns[^1].Text + "\n" + line);
            }
            else
            {
                turns.Add((speakers[0].Speaker, line));
            }
        }

        return turns;
    }

    private (byte[] Audio, string Mime) ReadAudioPart(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out JsonElement candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            JsonElement candidate = candidates[0];
            if (candidate.TryGetProperty("content", out JsonElement content) && content.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                // A long text can come back in several audio parts; the samples are joined in order.
                using var joined = new MemoryStream();
                string? mime = null;
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out JsonElement inline) && Str(inline, "data"u8) is { } data)
                    {
                        mime ??= Str(inline, "mimeType"u8);
                        joined.Write(DecodeBase64(data));
                    }
                }

                if (joined.Length > 0)
                {
                    return (joined.ToArray(), mime ?? "audio/L16;rate=24000");
                }
            }

            string? finish = Str(candidate, "finishReason"u8);
            if (GeminiFinish.Map(finish) == FinishReason.ContentFilter)
            {
                throw new ContentFilterException(Name, $"Gemini returned no audio because it was blocked ({finish}).");
            }
        }

        if (root.TryGetProperty("promptFeedback", out JsonElement feedback) && Str(feedback, "blockReason"u8) is { } reason)
        {
            throw new ContentFilterException(Name, $"Gemini blocked the text ({reason}).");
        }

        throw new MalformedResponseException(Name, "the response held no audio.");
    }

    // Older speech models return headerless 16-bit PCM (audio/L16, 24 kHz, mono); Gemini 3.8 returns a WAV file. Callers get what they asked for.
    private static (byte[] Audio, string ContentType) ShapeAudio(byte[] audio, string mime, string format)
    {
        bool isPcm = mime.StartsWith("audio/l16", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase);
        bool isWav = mime.StartsWith("audio/wav", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/x-wav", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/wave", StringComparison.OrdinalIgnoreCase);
        if (isPcm)
        {
            int rate = WavAudio.RateFrom(mime, DefaultSpeechRate);
            return format == "pcm"
                ? (audio, "audio/L16;rate=" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : (WavAudio.FromPcm16(audio, rate), "audio/wav");
        }

        if (isWav && format == "pcm" && WavAudio.TryGetData(audio, out ReadOnlyMemory<byte> samples))
        {
            int rate = audio.Length >= 28 ? BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(24)) : DefaultSpeechRate;
            return (samples.ToArray(), "audio/L16;rate=" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return (audio, mime);
    }

    // The usage metadata lists tokens per modality (generateContent reference, "UsageMetadata"). The names follow the chat codec's: inputImageTokens,
    // outputImageTokens, outputAudioTokens. Output includes thinking tokens, as in chat.
    private static Usage ReadMediaUsage(JsonElement root, string model, bool audioOutput = false)
    {
        if (!root.TryGetProperty("usageMetadata", out JsonElement u) || u.ValueKind != JsonValueKind.Object)
        {
            return new Usage { Model = model };
        }

        long prompt = Count(u, "promptTokenCount"u8);
        long cached = Count(u, "cachedContentTokenCount"u8);
        long candidates = Count(u, "candidatesTokenCount"u8);
        long thoughts = Count(u, "thoughtsTokenCount"u8);
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        if (thoughts != 0)
        {
            counters["reasoningTokens"] = thoughts;
        }

        bool outputDetail = false;
        foreach ((string detail, string prefix) in new[] { ("promptTokensDetails", "input"), ("candidatesTokensDetails", "output") })
        {
            if (u.TryGetProperty(detail, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string modality = Str(item, "modality"u8) ?? "";
                    long tokens = Count(item, "tokenCount"u8);
                    if (modality is "AUDIO" or "IMAGE" or "VIDEO" or "DOCUMENT" && tokens != 0)
                    {
                        counters[prefix + char.ToUpperInvariant(modality[0]) + modality[1..].ToLowerInvariant() + "Tokens"] = tokens;
                        outputDetail |= prefix == "output";
                    }
                }
            }
        }

        // A media-only call whose answer carries no per-modality detail made its candidate tokens in that modality.
        if (!outputDetail && candidates > 0)
        {
            counters[audioOutput ? "outputAudioTokens" : "outputImageTokens"] = candidates;
        }

        return new Usage
        {
            Model = model,
            InputTokens = Math.Max(0, prompt - cached),
            CacheReadInputTokens = cached,
            OutputTokens = candidates + thoughts,
            Counters = counters.Count == 0 ? null : counters,
        };
    }

    private static long Count(JsonElement e, ReadOnlySpan<byte> name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v))
        {
            return 0;
        }

        // int64 counts may arrive as strings in protobuf JSON.
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long s) ? s : 0;
    }

    private byte[] DecodeBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new MalformedResponseException(Name, "the media data was not valid base64.");
        }
    }
}
