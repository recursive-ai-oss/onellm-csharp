using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Media;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// The OpenAI media endpoints: <c>/images/generations</c>, <c>/images/edits</c>, <c>/audio/speech</c>, <c>/audio/transcriptions</c>,
/// <c>/audio/translations</c> and <c>/moderations</c>, over the wire machinery the chat client uses (endpoint, authentication, retries, errors).
/// Request and response fields are from the API reference pages for each endpoint.
/// </summary>
internal sealed class OpenAIMedia(OpenAIWire wire, ModelCatalog? catalog, TimeProvider time)
{
    private const int MaxBodyBytes = 256 * 1024 * 1024;

    // ---- images ----

    public async Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool edit = request.Inputs.Count > 0;
        ReadOnlyMemory<byte> body = WriteImageRequest(request, stream: false);
        using HttpResponseMessage response = await PostJsonAsync(edit ? "images/edits" : "images/generations", request.Model, body, sse: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await wire.Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        return ParseImages(bytes, request);
    }

    public async IAsyncEnumerable<ImageStreamEvent> StreamImageAsync(ImageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool edit = request.Inputs.Count > 0;
        ReadOnlyMemory<byte> body = WriteImageRequest(request, stream: true);
        using HttpResponseMessage response = await PostJsonAsync(edit ? "images/edits" : "images/generations", request.Model, body, sse: true, cancellationToken).ConfigureAwait(false);
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var sse = new SseReader(stream, wire.Provider);
            bool done = false;
            while (await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false))
            {
                ImageStreamEvent? e = DecodeImageEvent(sse.Data, request);
                if (e is null)
                {
                    continue;
                }

                yield return e;
                if (e.Kind == ImageStreamEventKind.Completed)
                {
                    done = true;
                }
            }

            if (!done)
            {
                throw new MalformedResponseException(wire.Provider, "the image stream ended before the finished image.");
            }
        }
    }

    public static ReadOnlyMemory<byte> WriteImageRequest(ImageRequest request, bool stream)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("An image request needs a model.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("An image request needs a prompt.", nameof(request));
        }

        // The reference limits n to 1..10 for both endpoints.
        if (request.Count is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The number of images must be from 1 to 10.");
        }

        bool edit = request.Inputs.Count > 0;
        if (edit && request.Inputs.Count > 16)
        {
            throw new ArgumentException("An image edit takes at most 16 input images.", nameof(request));
        }

        using var body = new BodyWriter(request.ExtraParameters);
        Utf8JsonWriter w = body.Writer;
        if (body.Field("model"))
        {
            w.WriteStringValue(request.Model);
        }

        if (body.Field("prompt"))
        {
            w.WriteStringValue(request.Prompt);
        }

        if (edit && body.Field("images"))
        {
            // The edit endpoint takes JSON with images as URLs or base64 data URLs (images/edits reference, "json_with_url").
            w.WriteStartArray();
            foreach (ImageInput input in request.Inputs)
            {
                w.WriteStartObject();
                w.WriteString("image_url", DataUrl(input));
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (edit && request.Mask is { } mask && body.Field("mask"))
        {
            w.WriteStartObject();
            w.WriteString("image_url", DataUrl(mask));
            w.WriteEndObject();
        }

        if (request.Count != 1 && body.Field("n"))
        {
            w.WriteNumberValue(request.Count);
        }

        if (request.Size is not null && body.Field("size"))
        {
            w.WriteStringValue(request.Size);
        }

        if (request.Quality is not null && body.Field("quality"))
        {
            w.WriteStringValue(request.Quality);
        }

        if (request.Format is not null && body.Field("output_format"))
        {
            w.WriteStringValue(request.Format);
        }

        if (stream)
        {
            if (body.Field("stream"))
            {
                w.WriteBooleanValue(true);
            }

            if (request.PartialImages is { } partial && body.Field("partial_images"))
            {
                w.WriteNumberValue(Math.Clamp(partial, 0, 3));
            }
        }

        return body.Finish();
    }

    public ImageResponse ParseImages(ReadOnlyMemory<byte> bytes, ImageRequest request)
    {
        using JsonDocument doc = ParseDocument(bytes);
        JsonElement root = doc.RootElement;
        ThrowIfError(root);
        string contentType = MediaCosting.ImageContentType(Str(root, "output_format") ?? request.Format);
        var images = new List<GeneratedImage>();
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                string? b64 = Str(item, "b64_json");
                if (b64 is null)
                {
                    // GPT image models always return base64 (images/generations reference); a URL would need a second request the caller did not allow.
                    throw new MalformedResponseException(wire.Provider, "the image came back as a URL instead of image data.");
                }

                images.Add(new GeneratedImage(Decode(b64), contentType, Str(item, "revised_prompt")));
            }
        }

        if (images.Count == 0)
        {
            throw new MalformedResponseException(wire.Provider, "the response held no images.");
        }

        var warnings = new List<ResponseWarning>();
        Usage? usage = null;
        Cost? cost = null;
        if (root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            (usage, cost) = MediaCosting.Price(
                catalog,
                wire.CatalogScope,
                request.Model,
                ReadImageUsage(u, request.Model),
                time,
                warnings,
                MediaCosting.ImageQualifiers(Str(root, "size") ?? request.Size, Str(root, "quality") ?? request.Quality));
        }
        else
        {
            warnings.Add(new ResponseWarning("usage_missing", "The response reported no usage, so the cost was not computed."));
        }

        return new ImageResponse { Images = images, Usage = usage, Cost = cost, Model = request.Model, Warnings = warnings };
    }

    private ImageStreamEvent? DecodeImageEvent(ReadOnlyMemory<byte> data, ImageRequest request)
    {
        using JsonDocument doc = ParseDocument(data);
        JsonElement root = doc.RootElement;
        string type = Str(root, "type") ?? "";
        if (type == "error" || root.TryGetProperty("error", out _))
        {
            JsonElement error = root.TryGetProperty("error", out JsonElement inner) ? inner : root;
            throw OpenAIErrors.FromStreamError(wire.Provider, Str(error, "code"), Str(error, "message"), Str(error, "type"));
        }

        bool partial = type.EndsWith(".partial_image", StringComparison.Ordinal);
        bool completed = type.EndsWith(".completed", StringComparison.Ordinal);
        if (!partial && !completed)
        {
            return null;
        }

        string contentType = MediaCosting.ImageContentType(Str(root, "output_format") ?? request.Format);
        string b64 = Str(root, "b64_json") ?? throw new MalformedResponseException(wire.Provider, "an image event had no image data.");
        var image = new GeneratedImage(Decode(b64), contentType, null);
        if (partial)
        {
            int index = root.TryGetProperty("partial_image_index", out JsonElement i) && i.TryGetInt32(out int n) ? n : 0;
            return new ImageStreamEvent { Kind = ImageStreamEventKind.Partial, Index = index, Image = image };
        }

        var warnings = new List<ResponseWarning>();
        Usage usage = root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object
            ? ReadImageUsage(u, request.Model)
            : new Usage { Model = request.Model };
        Cost? cost = null;
        if (root.TryGetProperty("usage", out _))
        {
            (usage, cost) = MediaCosting.Price(
                catalog,
                wire.CatalogScope,
                request.Model,
                usage,
                time,
                warnings,
                MediaCosting.ImageQualifiers(Str(root, "size") ?? request.Size, Str(root, "quality") ?? request.Quality));
        }
        else
        {
            warnings.Add(new ResponseWarning("usage_missing", "The final event reported no usage, so the cost was not computed."));
        }

        return new ImageStreamEvent { Kind = ImageStreamEventKind.Completed, Image = image, Usage = usage, Cost = cost, Warnings = warnings };
    }

    // The Images API reports input_tokens (text and image together) and output_tokens with a split in the *_details objects. Following the
    // library's rule, the totals go in inputTokens / outputTokens and the image parts are subset counters, which the pricing engine takes out
    // of the totals when the model prices them separately.
    private static Usage ReadImageUsage(JsonElement u, string model)
    {
        long input = Long(u, "input_tokens");
        long output = Long(u, "output_tokens");
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        if (u.TryGetProperty("input_tokens_details", out JsonElement inDetails) && inDetails.ValueKind == JsonValueKind.Object)
        {
            long image = Long(inDetails, "image_tokens");
            if (image > 0)
            {
                counters["inputImageTokens"] = image;
            }
        }

        long outImage = output;
        if (u.TryGetProperty("output_tokens_details", out JsonElement outDetails) && outDetails.ValueKind == JsonValueKind.Object && outDetails.TryGetProperty("image_tokens", out _))
        {
            outImage = Long(outDetails, "image_tokens");
        }

        if (outImage > 0)
        {
            counters["outputImageTokens"] = outImage;
        }

        return new Usage { Model = model, InputTokens = input, OutputTokens = output, Counters = counters.Count == 0 ? null : counters };
    }

    // ---- speech ----

    public async Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> body = WriteSpeechRequest(request, stream: false);
        using HttpResponseMessage response = await PostJsonAsync("audio/speech", request.Model, body, sse: false, cancellationToken, accept: "audio/*").ConfigureAwait(false);
        byte[] audio = await wire.Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        var warnings = new List<ResponseWarning>();
        (Usage? usage, Cost? cost) = PriceSpeech(request, warnings);
        return new SpeechResponse
        {
            Audio = audio,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? MediaCosting.AudioContentType(request.Format),
            Usage = usage,
            Cost = cost,
            Warnings = warnings,
        };
    }

    public async Task<SpeechAudio> StreamSynthesisAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // stream_format "audio" (the default) is the chunked audio body itself; "sse" would wrap each chunk in a JSON event.
        ReadOnlyMemory<byte> body = WriteSpeechRequest(request, stream: true);
        HttpResponseMessage response = await PostJsonAsync("audio/speech", request.Model, body, sse: false, cancellationToken, accept: "audio/*").ConfigureAwait(false);
        try
        {
            Stream audio = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var warnings = new List<ResponseWarning>();
            (_, Cost? cost) = PriceSpeech(request, warnings);
            return new SpeechAudio(audio, response.Content.Headers.ContentType?.ToString() ?? MediaCosting.AudioContentType(request.Format), response)
            {
                Cost = cost,
                Warnings = warnings,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static ReadOnlyMemory<byte> WriteSpeechRequest(SpeechRequest request, bool stream)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("A speech request needs a model.", nameof(request));
        }

        if (string.IsNullOrEmpty(request.Input))
        {
            throw new ArgumentException("A speech request needs text to speak.", nameof(request));
        }

        if (request.Speakers is { Count: > 0 })
        {
            throw new ArgumentException("OpenAI speech takes one voice; it has no multi-speaker mode.", nameof(request));
        }

        using var body = new BodyWriter(request.ExtraParameters);
        Utf8JsonWriter w = body.Writer;
        if (body.Field("model"))
        {
            w.WriteStringValue(request.Model);
        }

        if (body.Field("input"))
        {
            w.WriteStringValue(request.Input);
        }

        // The voice is required by the API; alloy is the first built-in voice the reference lists.
        if (body.Field("voice"))
        {
            w.WriteStringValue(request.Voice ?? "alloy");
        }

        if (request.Instructions is not null && body.Field("instructions"))
        {
            w.WriteStringValue(request.Instructions);
        }

        if (request.Format is not null && body.Field("response_format"))
        {
            w.WriteStringValue(request.Format);
        }

        if (request.Speed is { } speed && body.Field("speed"))
        {
            w.WriteNumberValue(speed);
        }

        if (stream && body.Field("stream_format"))
        {
            w.WriteStringValue("audio");
        }

        return body.Finish();
    }

    // OpenAI's speech endpoint returns bare audio with no usage. tts-1 and tts-1-hd are priced per character, which is known from the input;
    // gpt-4o-mini-tts is priced per token and the response does not say how many, so its cost cannot be computed.
    private (Usage? Usage, Cost? Cost) PriceSpeech(SpeechRequest request, List<ResponseWarning> warnings)
    {
        if (catalog is null)
        {
            return (null, null);
        }

        ResolvedModel? resolved = MediaCosting.Resolve(catalog, wire.CatalogScope, request.Model);
        bool perCharacter = resolved?.Model?.Pricing?.Items?.Any(i => i.Counter == "characters") == true;
        if (!perCharacter)
        {
            if (resolved?.Model?.Pricing is null)
            {
                warnings.Add(new ResponseWarning("cost_unknown", $"The catalog has no price for '{request.Model}', so the cost was not computed."));
            }
            else
            {
                warnings.Add(new ResponseWarning("usage_missing", $"The API reports no usage for speech from '{request.Model}' and the catalog prices it per token, so the cost was not computed."));
            }

            return (null, null);
        }

        // "Characters" are counted as UTF-16 code units of the input text, the unit the pricing page names.
        var usage = new Usage { Model = request.Model, Counters = new Dictionary<string, long> { ["characters"] = request.Input.Length } };
        (Usage priced, Cost? cost) = MediaCosting.Price(catalog, wire.CatalogScope, request.Model, usage, time, warnings);
        return (priced, cost);
    }

    // ---- transcription ----

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (byte[] body, string contentType) = WriteTranscriptionRequest(request, stream: false);
        string path = request.Translate ? "audio/translations" : "audio/transcriptions";
        using HttpResponseMessage response = await PostBytesAsync(path, request.Model, body, contentType, sse: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await wire.Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        return ParseTranscription(bytes, request);
    }

    public async IAsyncEnumerable<TranscriptionStreamEvent> StreamTranscribeAsync(TranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Translate)
        {
            throw new ArgumentException("Translation does not stream.", nameof(request));
        }

        (byte[] body, string contentType) = WriteTranscriptionRequest(request, stream: true);
        using HttpResponseMessage response = await PostBytesAsync("audio/transcriptions", request.Model, body, contentType, sse: true, cancellationToken).ConfigureAwait(false);
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var sse = new SseReader(stream, wire.Provider);
            bool done = false;
            while (await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false))
            {
                using JsonDocument doc = ParseDocument(sse.Data);
                JsonElement root = doc.RootElement;
                ThrowIfError(root);
                string type = Str(root, "type") ?? "";
                if (type == "transcript.text.delta")
                {
                    yield return new TranscriptionStreamEvent { Delta = Str(root, "delta") ?? "" };
                }
                else if (type == "transcript.text.done")
                {
                    done = true;
                    yield return new TranscriptionStreamEvent { Completed = BuildTranscription(root, request, text: Str(root, "text") ?? "") };
                }
            }

            if (!done)
            {
                throw new MalformedResponseException(wire.Provider, "the transcription stream ended before the final text.");
            }
        }
    }

    public static (byte[] Body, string ContentType) WriteTranscriptionRequest(TranscriptionRequest request, bool stream)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("A transcription request needs a model.", nameof(request));
        }

        if (request.Audio.IsEmpty)
        {
            throw new ArgumentException("A transcription request needs audio.", nameof(request));
        }

        var form = new MultipartBody();
        // The reference asks for a file name with an extension and a content type so the server can tell the format.
        string name = request.FileName ?? "audio" + MediaCosting.ExtensionFor(request.ContentType);
        form.AddFile("file", name, request.ContentType.Length > 0 ? request.ContentType : "application/octet-stream", request.Audio);
        form.Add("model", request.Model);
        if (request.Language is not null && !request.Translate)
        {
            form.Add("language", request.Language);
        }

        if (request.Prompt is not null)
        {
            form.Add("prompt", request.Prompt);
        }

        if (request.ResponseFormat is not null)
        {
            form.Add("response_format", request.ResponseFormat);
        }

        if (request.TimestampGranularities is { } granularities)
        {
            foreach (string g in granularities)
            {
                form.Add("timestamp_granularities[]", g);
            }
        }

        if (stream)
        {
            form.Add("stream", "true");
        }

        if (request.ExtraParameters is { } extras)
        {
            foreach ((string key, JsonNode? value) in extras)
            {
                form.Add(key, value is JsonValue v && v.TryGetValue(out string? s) ? s : value?.ToJsonString() ?? "");
            }
        }

        return form.Build();
    }

    public TranscriptionResponse ParseTranscription(ReadOnlyMemory<byte> bytes, TranscriptionRequest request)
    {
        string format = request.ResponseFormat ?? "json";
        if (format is "text" or "srt" or "vtt")
        {
            // These answer with the transcript itself, not JSON, and no usage.
            var notes = new List<ResponseWarning> { new("usage_missing", $"The '{format}' response form carries no usage, so the cost was not computed.") };
            return new TranscriptionResponse { Text = Encoding.UTF8.GetString(bytes.Span), Warnings = notes };
        }

        using JsonDocument doc = ParseDocument(bytes);
        JsonElement root = doc.RootElement;
        ThrowIfError(root);
        return BuildTranscription(root, request, text: Str(root, "text") ?? throw new MalformedResponseException(wire.Provider, "the transcription had no text."));
    }

    private TranscriptionResponse BuildTranscription(JsonElement root, TranscriptionRequest request, string text)
    {
        string? language = Str(root, "language");
        if (language is null && root.TryGetProperty("languages", out JsonElement langs) && langs.ValueKind == JsonValueKind.Array && langs.GetArrayLength() > 0)
        {
            language = Str(langs[0], "code");
        }

        TimeSpan? duration = root.TryGetProperty("duration", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(d.GetDouble()) : null;

        // Segments, words and the like are handed over as the provider wrote them.
        JsonObject? details = null;
        foreach (string key in new[] { "segments", "words", "languages", "logprobs" })
        {
            if (root.TryGetProperty(key, out JsonElement part) && part.ValueKind is JsonValueKind.Array)
            {
                details ??= new JsonObject();
                details[key] = JsonNode.Parse(part.GetRawText());
            }
        }

        var warnings = new List<ResponseWarning>();
        Usage? usage = null;
        Cost? cost = null;
        Usage? reported = ReadTranscriptionUsage(root, request.Model, duration);
        if (reported is null)
        {
            warnings.Add(new ResponseWarning("usage_missing", "The response reported no usage, so the cost was not computed."));
        }
        else
        {
            (usage, cost) = MediaCosting.Price(catalog, wire.CatalogScope, request.Model, reported, time, warnings);
        }

        return new TranscriptionResponse { Text = text, Language = language, Duration = duration, Details = details, Usage = usage, Cost = cost, Warnings = warnings };
    }

    // Usage is either tokens or the audio duration in seconds (the "type" field of the usage object).
    private static Usage? ReadTranscriptionUsage(JsonElement root, string model, TimeSpan? duration)
    {
        if (root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("seconds", out JsonElement s) && s.ValueKind == JsonValueKind.Number)
            {
                return new Usage { Model = model, Counters = new Dictionary<string, long> { ["audioSeconds"] = (long)Math.Ceiling(s.GetDouble()) } };
            }

            var counters = new Dictionary<string, long>(StringComparer.Ordinal);
            if (u.TryGetProperty("input_token_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object)
            {
                long audio = Long(details, "audio_tokens");
                if (audio > 0)
                {
                    counters["inputAudioTokens"] = audio;
                }
            }

            return new Usage { Model = model, InputTokens = Long(u, "input_tokens"), OutputTokens = Long(u, "output_tokens"), Counters = counters.Count == 0 ? null : counters };
        }

        // verbose_json without a usage object still says how long the audio was.
        return duration is { } span ? new Usage { Model = model, Counters = new Dictionary<string, long> { ["audioSeconds"] = (long)Math.Ceiling(span.TotalSeconds) } } : null;
    }

    // ---- moderation ----

    public async Task<ModerationResponse> ModerateAsync(ModerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> body = WriteModerationRequest(request);
        using HttpResponseMessage response = await PostJsonAsync("moderations", request.Model, body, sse: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await wire.Http.ReadBodyAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        return ParseModeration(bytes, request);
    }

    public static ReadOnlyMemory<byte> WriteModerationRequest(ModerationRequest request)
    {
        if (request.Inputs.Count == 0 && request.Images.Count == 0)
        {
            throw new ArgumentException("A moderation request needs at least one text or image.", nameof(request));
        }

        using var body = new BodyWriter(request.ExtraParameters);
        Utf8JsonWriter w = body.Writer;
        if (!string.IsNullOrWhiteSpace(request.Model) && body.Field("model"))
        {
            w.WriteStringValue(request.Model);
        }

        if (body.Field("input"))
        {
            w.WriteStartArray();
            if (request.Images.Count == 0)
            {
                // One verdict per text, in order.
                foreach (string text in request.Inputs)
                {
                    w.WriteStringValue(text);
                }
            }
            else
            {
                // The multi-modal form is an array of typed items (moderations reference, "Image and text").
                foreach (string text in request.Inputs)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", text);
                    w.WriteEndObject();
                }

                foreach (ImageInput image in request.Images)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "image_url");
                    w.WriteStartObject("image_url");
                    w.WriteString("url", DataUrl(image));
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
        }

        return body.Finish();
    }

    public ModerationResponse ParseModeration(ReadOnlyMemory<byte> bytes, ModerationRequest request)
    {
        using JsonDocument doc = ParseDocument(bytes);
        JsonElement root = doc.RootElement;
        ThrowIfError(root);
        var results = new List<ModerationResult>();
        if (root.TryGetProperty("results", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in list.EnumerateArray())
            {
                var categories = new Dictionary<string, bool>(StringComparer.Ordinal);
                if (r.TryGetProperty("categories", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in c.EnumerateObject())
                    {
                        // illicit and illicit/violent can be null on older models: not classified, so not flagged.
                        categories[p.Name] = p.Value.ValueKind == JsonValueKind.True;
                    }
                }

                var scores = new Dictionary<string, double>(StringComparer.Ordinal);
                if (r.TryGetProperty("category_scores", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in s.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Number)
                        {
                            scores[p.Name] = p.Value.GetDouble();
                        }
                    }
                }

                Dictionary<string, IReadOnlyList<string>>? types = null;
                if (r.TryGetProperty("category_applied_input_types", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
                {
                    types = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    foreach (JsonProperty p in t.EnumerateObject())
                    {
                        types[p.Name] = [.. p.Value.EnumerateArray().Select(x => x.GetString() ?? "")];
                    }
                }

                results.Add(new ModerationResult
                {
                    Flagged = r.TryGetProperty("flagged", out JsonElement f) && f.ValueKind == JsonValueKind.True,
                    Categories = categories,
                    Scores = scores,
                    CategoryInputTypes = types,
                });
            }
        }

        string model = Str(root, "model") ?? request.Model;
        var warnings = new List<ResponseWarning>();
        // The endpoint reports no usage; the models are free, so pricing an empty usage gives a zero cost, and a model the catalog does not know gives none.
        (Usage usage, Cost? cost) = MediaCosting.Price(catalog, wire.CatalogScope, model, new Usage { Model = model }, time, warnings);
        return new ModerationResponse { Results = results, Model = model, Usage = usage, Cost = cost, Warnings = warnings };
    }

    // ---- plumbing ----

    private Task<HttpResponseMessage> PostJsonAsync(string path, string? model, ReadOnlyMemory<byte> body, bool sse, CancellationToken cancellationToken, string? accept = null) =>
        wire.Http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, wire.Endpoint.GetUri(path, model)) { Content = new ReadOnlyMemoryContent(body) };
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                if (sse)
                {
                    message.Headers.Accept.ParseAdd("text/event-stream");
                }
                else if (accept is not null)
                {
                    message.Headers.Accept.ParseAdd(accept);
                }

                wire.Endpoint.Authenticate(message);
                return message;
            },
            cancellationToken);

    private Task<HttpResponseMessage> PostBytesAsync(string path, string? model, byte[] body, string contentType, bool sse, CancellationToken cancellationToken) =>
        wire.Http.SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, wire.Endpoint.GetUri(path, model)) { Content = new ByteArrayContent(body) };
                message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                if (sse)
                {
                    message.Headers.Accept.ParseAdd("text/event-stream");
                }

                wire.Endpoint.Authenticate(message);
                return message;
            },
            cancellationToken);

    private JsonDocument ParseDocument(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new MalformedResponseException(wire.Provider, $"the response is not valid JSON (at byte {e.BytePositionInLine}).");
        }
    }

    private void ThrowIfError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            throw OpenAIErrors.FromStreamError(wire.Provider, Str(error, "code"), Str(error, "message"), Str(error, "type"));
        }
    }

    private byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new MalformedResponseException(wire.Provider, "the image data was not valid base64.");
        }
    }

    private static string DataUrl(ImageInput image) =>
        "data:" + (image.ContentType.Length > 0 ? image.ContentType : "image/png") + ";base64," + Convert.ToBase64String(image.Data.Span);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
}
