using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Tools.Builtins;

namespace OneLlm.Tools.Media;

// The media tools: generate_image, speak, transcribe and moderate. Each calls one provider client the host chose and passes the cost of the call
// to the tool context as line items.
//
// The network use of these tools is the provider call itself, which the host enabled by giving the tool a client. It is not "tool network egress":
// the egress grant (ICapabilities.IsNetworkAllowed) governs tools that open connections of their own, such as http_request, and is not consulted here.
// What the tools read from disk (images to edit, audio to transcribe) is limited to the granted file roots; what they make comes back as
// asset parts with inline data, and the runtime's asset store decides where it lives. They write no files.

/// <summary>Settings shared by the media tools.</summary>
public sealed record MediaToolOptions
{
    /// <summary>The most bytes the tools read from one input file. Default 25 MiB, the limit of the audio endpoints.</summary>
    public long MaxInputBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>The most images <c>generate_image</c> makes in one call. Default 4.</summary>
    public int MaxImages { get; init; } = 4;

    /// <summary>The longest text <c>speak</c> accepts, in characters. Default 4096, the limit of OpenAI's speech endpoint.</summary>
    public int MaxSpeechCharacters { get; init; } = 4096;

    /// <summary>A rough price of one call, shown to approvers. Zero when unknown.</summary>
    public decimal CostHint { get; init; }

    /// <summary>True when a person must approve each call. Default false.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>How long one call may run. Default five minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

internal static class MediaToolSupport
{
    public static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)JsonValue.Create(r)!)]);
        }

        return schema;
    }

    public static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static string RequireString(JsonObject inputs, string name)
    {
        JsonNode? node = inputs[name];
        if (node is null)
        {
            throw new ToolInputException(name, "is required");
        }

        if (node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ToolInputException(name, "must be a non-empty string");
    }

    public static string? OptionalString(JsonObject inputs, string name)
    {
        JsonNode? node = inputs[name];
        if (node is null)
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue(out string? s) ? s : throw new ToolInputException(name, "must be a string");
    }

    public static bool OptionalBool(JsonObject inputs, string name)
    {
        JsonNode? node = inputs[name];
        return node is null ? false : node is JsonValue v && v.TryGetValue(out bool b) ? b : throw new ToolInputException(name, "must be true or false");
    }

    public static IReadOnlyList<string> StringList(JsonObject inputs, string name)
    {
        JsonNode? node = inputs[name];
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new ToolInputException(name, "must be an array of strings");
        }

        var list = new List<string>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
            else
            {
                throw new ToolInputException($"{name}[{i}]", "must be a non-empty string");
            }
        }

        return list;
    }

    public static void RejectUnknown(JsonObject inputs, params string[] known)
    {
        foreach ((string key, _) in inputs)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw new ToolInputException(key, "is not an argument of this tool");
            }
        }
    }

    public static void AddCost(ToolContext context, Cost? cost)
    {
        if (cost is null)
        {
            return;
        }

        foreach (CostLine line in cost.Lines)
        {
            context.AddCost(line);
        }
    }

    public static AssetPart Asset(ReadOnlyMemory<byte> data, string contentType, string name) =>
        new(new AssetRef
        {
            // The bytes travel inline until the runtime's asset store takes them in (AssetRef.Data is accepted at ingress).
            Storage = "inline",
            Key = string.Empty,
            ContentType = contentType,
            Size = data.Length,
            Name = name,
            Data = Convert.ToBase64String(data.Span),
        });

    public static string WarningsText(IReadOnlyList<ResponseWarning> warnings)
    {
        var sb = new StringBuilder();
        foreach (ResponseWarning w in warnings)
        {
            sb.Append(w.Message).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Makes images from a prompt, or edits images the run may read, and returns them as asset parts.</summary>
public sealed class GenerateImageTool : ICapabilityTool
{
    /// <summary>Nothing: the tool reads files only when the model names input files, and then only under a granted read root.</summary>
    public ToolCapability Requires => ToolCapability.None;

    private readonly IImageProvider _provider;
    private readonly string _model;
    private readonly MediaToolOptions _options;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The client that makes the images.</param>
    /// <param name="model">The image model to use. The model is the host's choice; the tool takes none from the model.</param>
    /// <param name="options">Limits and metadata; defaults when null.</param>
    public GenerateImageTool(IImageProvider provider, string model, MediaToolOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new MediaToolOptions();
        JsonObject props = new()
        {
            ["prompt"] = MediaToolSupport.Prop("string", "What to draw, or how to change the input images."),
            ["size"] = MediaToolSupport.Prop("string", "Image size in the provider's terms, for example 1024x1024 or 2K. Leave out for the default."),
            ["quality"] = MediaToolSupport.Prop("string", "Quality tier, for example low, medium or high, where the model has them."),
            ["format"] = MediaToolSupport.Prop("string", "File format: png, jpeg or webp."),
            ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = _options.MaxImages, ["description"] = "How many images to make. Default 1." },
            ["images"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Paths of image files to edit or use as references. Leave out to make a new image.",
            },
            ["mask"] = MediaToolSupport.Prop("string", "Path of a mask image whose transparent areas mark where to change the first input image."),
        };
        Definition = new ToolSpec(
            "generate_image",
            "Makes an image from a text prompt, or edits input images given by path. Each call costs money; the images come back as files.",
            MediaToolSupport.Schema(props, "prompt"))
        {
            CostHint = _options.CostHint,
            RequiresApproval = _options.RequiresApproval,
            Timeout = _options.Timeout,
        };
    }

    /// <inheritdoc />
    public ToolSpec Definition { get; }

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        MediaToolSupport.RejectUnknown(inputs, "prompt", "size", "quality", "format", "count", "images", "mask");
        string prompt = MediaToolSupport.RequireString(inputs, "prompt");
        int count = 1;
        if (inputs["count"] is { } c)
        {
            if (c is not JsonValue cv || !cv.TryGetValue(out int n) || n < 1 || n > _options.MaxImages)
            {
                throw new ToolInputException("count", $"must be a whole number from 1 to {_options.MaxImages}");
            }

            count = n;
        }

        var edits = new List<ImageInput>();
        foreach (string path in MediaToolSupport.StringList(inputs, "images"))
        {
            (byte[] data, string type, _) = await MediaInputFile.ReadAsync(context.Capabilities, "images", path, _options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
            edits.Add(new ImageInput(data, type));
        }

        ImageInput? mask = null;
        if (MediaToolSupport.OptionalString(inputs, "mask") is { } maskPath)
        {
            if (edits.Count == 0)
            {
                throw new ToolInputException("mask", "needs at least one image to edit");
            }

            (byte[] data, string type, _) = await MediaInputFile.ReadAsync(context.Capabilities, "mask", maskPath, _options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
            mask = new ImageInput(data, type);
        }

        ImageResponse response = await _provider.GenerateImageAsync(
            new ImageRequest
            {
                Model = _model,
                Prompt = prompt,
                Inputs = edits,
                Count = count,
                Size = MediaToolSupport.OptionalString(inputs, "size"),
                Quality = MediaToolSupport.OptionalString(inputs, "quality"),
                Format = MediaToolSupport.OptionalString(inputs, "format"),
                Mask = mask,
            },
            cancellationToken).ConfigureAwait(false);

        MediaToolSupport.AddCost(context, response.Cost);
        var parts = new List<Part>();
        for (int i = 0; i < response.Images.Count; i++)
        {
            GeneratedImage image = response.Images[i];
            string extension = MediaFileTypes.ExtensionOf(image.ContentType);
            parts.Add(MediaToolSupport.Asset(image.Data, image.ContentType, $"image-{i + 1}{extension}"));
        }

        string note = string.Join(' ', new[] { response.Images.Count > 0 ? response.Images[0].RevisedPrompt : null, response.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (note.Length > 0)
        {
            parts.Add(new TextPart(note));
        }

        return new ToolResult(parts);
    }
}

/// <summary>Turns text into speech and returns the audio as an asset part.</summary>
public sealed class SpeakTool : ICapabilityTool
{
    /// <summary>Nothing: the tool reads files only when the model names input files, and then only under a granted read root.</summary>
    public ToolCapability Requires => ToolCapability.None;

    private readonly ITextToSpeechProvider _provider;
    private readonly string _model;
    private readonly MediaToolOptions _options;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The client that makes the speech.</param>
    /// <param name="model">The speech model to use.</param>
    /// <param name="options">Limits and metadata; defaults when null.</param>
    public SpeakTool(ITextToSpeechProvider provider, string model, MediaToolOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new MediaToolOptions();
        JsonObject props = new()
        {
            ["text"] = MediaToolSupport.Prop("string", "The text to speak."),
            ["voice"] = MediaToolSupport.Prop("string", "The voice, in the provider's terms (for example alloy or Kore). Leave out for the default."),
            ["format"] = MediaToolSupport.Prop("string", "Audio format, for example mp3 or wav. Leave out for the provider's default."),
            ["instructions"] = MediaToolSupport.Prop("string", "How to say it, for models that take direction, for example 'calm and warm'."),
        };
        Definition = new ToolSpec(
            "speak",
            "Reads text aloud and returns an audio file. Each call costs money.",
            MediaToolSupport.Schema(props, "text"))
        {
            CostHint = _options.CostHint,
            RequiresApproval = _options.RequiresApproval,
            Timeout = _options.Timeout,
        };
    }

    /// <inheritdoc />
    public ToolSpec Definition { get; }

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        MediaToolSupport.RejectUnknown(inputs, "text", "voice", "format", "instructions");
        string text = MediaToolSupport.RequireString(inputs, "text");
        if (text.Length > _options.MaxSpeechCharacters)
        {
            throw new ToolInputException("text", $"is {text.Length} characters; the most this tool speaks is {_options.MaxSpeechCharacters}");
        }

        SpeechResponse response = await _provider.SynthesizeAsync(
            new SpeechRequest
            {
                Model = _model,
                Input = text,
                Voice = MediaToolSupport.OptionalString(inputs, "voice"),
                Format = MediaToolSupport.OptionalString(inputs, "format"),
                Instructions = MediaToolSupport.OptionalString(inputs, "instructions"),
            },
            cancellationToken).ConfigureAwait(false);

        MediaToolSupport.AddCost(context, response.Cost);
        return new ToolResult([MediaToolSupport.Asset(response.Audio, response.ContentType, "speech" + MediaFileTypes.ExtensionOf(response.ContentType))]);
    }
}

/// <summary>Transcribes an audio file the run may read and returns the text.</summary>
public sealed class TranscribeTool : ICapabilityTool
{
    /// <summary>Reading the audio file under a granted read root.</summary>
    public ToolCapability Requires => ToolCapability.FileRead;

    private readonly ISpeechToTextProvider _provider;
    private readonly string _model;
    private readonly MediaToolOptions _options;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The client that transcribes.</param>
    /// <param name="model">The transcription model to use.</param>
    /// <param name="options">Limits and metadata; defaults when null.</param>
    public TranscribeTool(ISpeechToTextProvider provider, string model, MediaToolOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new MediaToolOptions();
        JsonObject props = new()
        {
            ["path"] = MediaToolSupport.Prop("string", "Path of the audio file (mp3, wav, flac, ogg, m4a, webm)."),
            ["language"] = MediaToolSupport.Prop("string", "The spoken language as an ISO 639-1 code, for example en. Leave out to detect it."),
            ["prompt"] = MediaToolSupport.Prop("string", "Names or terms that appear in the audio, to help the model spell them."),
            ["translate"] = MediaToolSupport.Prop("boolean", "True to translate the speech into English instead of transcribing it."),
        };
        Definition = new ToolSpec(
            "transcribe",
            "Turns speech in an audio file into text. Each call costs money.",
            MediaToolSupport.Schema(props, "path"))
        {
            CostHint = _options.CostHint,
            RequiresApproval = _options.RequiresApproval,
            Timeout = _options.Timeout,
        };
    }

    /// <inheritdoc />
    public ToolSpec Definition { get; }

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        MediaToolSupport.RejectUnknown(inputs, "path", "language", "prompt", "translate");
        string path = MediaToolSupport.RequireString(inputs, "path");
        (byte[] data, string type, string name) = await MediaInputFile.ReadAsync(context.Capabilities, "path", path, _options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        TranscriptionResponse response = await _provider.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = _model,
                Audio = data,
                ContentType = type,
                FileName = name,
                Language = MediaToolSupport.OptionalString(inputs, "language"),
                Prompt = MediaToolSupport.OptionalString(inputs, "prompt"),
                Translate = MediaToolSupport.OptionalBool(inputs, "translate"),
            },
            cancellationToken).ConfigureAwait(false);

        MediaToolSupport.AddCost(context, response.Cost);
        return ToolResult.FromText(response.Text);
    }
}

/// <summary>Checks texts, and images the run may read, for policy violations and returns the verdicts as JSON.</summary>
public sealed class ModerateTool : ICapabilityTool
{
    /// <summary>Nothing: the tool reads files only when the model names input files, and then only under a granted read root.</summary>
    public ToolCapability Requires => ToolCapability.None;

    private readonly IModerationProvider _provider;
    private readonly string _model;
    private readonly MediaToolOptions _options;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The client that classifies.</param>
    /// <param name="model">The moderation model to use.</param>
    /// <param name="options">Limits and metadata; defaults when null.</param>
    public ModerateTool(IModerationProvider provider, string model, MediaToolOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new MediaToolOptions();
        JsonObject props = new()
        {
            ["texts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Texts to check. One verdict per text, in order, unless images are given too.",
            },
            ["images"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Paths of image files to check together with the texts. Then one verdict covers everything.",
            },
        };
        Definition = new ToolSpec(
            "moderate",
            "Checks text, and optionally images, for harmful content such as hate, harassment, violence or sexual content. Returns whether each is flagged, the flagged categories and the scores.",
            MediaToolSupport.Schema(props))
        {
            CostHint = _options.CostHint,
            RequiresApproval = _options.RequiresApproval,
            Timeout = _options.Timeout,
        };
    }

    /// <inheritdoc />
    public ToolSpec Definition { get; }

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        MediaToolSupport.RejectUnknown(inputs, "texts", "images");
        IReadOnlyList<string> texts = MediaToolSupport.StringList(inputs, "texts");
        var images = new List<ImageInput>();
        foreach (string path in MediaToolSupport.StringList(inputs, "images"))
        {
            (byte[] data, string type, _) = await MediaInputFile.ReadAsync(context.Capabilities, "images", path, _options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
            images.Add(new ImageInput(data, type));
        }

        if (texts.Count == 0 && images.Count == 0)
        {
            throw new ToolInputException("texts", "needs at least one text or image");
        }

        ModerationResponse response = await _provider.ModerateAsync(new ModerationRequest { Model = _model, Inputs = texts, Images = images }, cancellationToken).ConfigureAwait(false);
        MediaToolSupport.AddCost(context, response.Cost);

        var results = new JsonArray();
        foreach (ModerationResult r in response.Results)
        {
            var flagged = new JsonArray([.. r.Categories.Where(kv => kv.Value).Select(kv => (JsonNode)JsonValue.Create(kv.Key)!)]);
            var scores = new JsonObject();
            foreach ((string name, double score) in r.Scores)
            {
                scores[name] = score;
            }

            results.Add((JsonNode)new JsonObject { ["flagged"] = r.Flagged, ["categories"] = flagged, ["scores"] = scores });
        }

        return ToolResult.FromJson(new JsonObject { ["results"] = results });
    }
}

/// <summary>Registers the media tools together.</summary>
public static class MediaTools
{
    /// <summary>The names of the tools <see cref="Create"/> makes when every model is given.</summary>
    public static IReadOnlyList<string> Names { get; } = ["generate_image", "speak", "transcribe", "moderate"];

    /// <summary>Makes the four media tools over the clients a provider implements. A tool whose capability the provider lacks, or whose model is null, is left out.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="imageModel">The image model, or null for no <c>generate_image</c>.</param>
    /// <param name="speechModel">The speech model, or null for no <c>speak</c>.</param>
    /// <param name="transcriptionModel">The transcription model, or null for no <c>transcribe</c>.</param>
    /// <param name="moderationModel">The moderation model, or null for no <c>moderate</c>.</param>
    /// <param name="options">Limits and metadata shared by the tools.</param>
    /// <returns>The tools.</returns>
    public static IReadOnlyList<ITool> Create(IProvider provider, string? imageModel = null, string? speechModel = null, string? transcriptionModel = null, string? moderationModel = null, MediaToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var tools = new List<ITool>();
        if (imageModel is not null && provider is IImageProvider image)
        {
            tools.Add(new GenerateImageTool(image, imageModel, options));
        }

        if (speechModel is not null && provider is ITextToSpeechProvider speech)
        {
            tools.Add(new SpeakTool(speech, speechModel, options));
        }

        if (transcriptionModel is not null && provider is ISpeechToTextProvider stt)
        {
            tools.Add(new TranscribeTool(stt, transcriptionModel, options));
        }

        if (moderationModel is not null && provider is IModerationProvider moderation)
        {
            tools.Add(new ModerateTool(moderation, moderationModel, options));
        }

        return tools;
    }
}

internal static class MediaFileTypes
{
    public static string ExtensionOf(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/flac" => ".flac",
        "audio/opus" => ".opus",
        "audio/aac" => ".aac",
        "audio/ogg" => ".ogg",
        var t when t.StartsWith("audio/l16", StringComparison.Ordinal) => ".pcm",
        _ => string.Empty,
    };
}
