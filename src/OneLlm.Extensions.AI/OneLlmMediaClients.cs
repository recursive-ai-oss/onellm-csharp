using System.Drawing;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;

// The speech, text-to-speech and image abstractions are marked experimental by Microsoft.Extensions.AI (MEAI001: subject to change or
// removal). Bridging them is the purpose of this file, so the opt-in is made here and nowhere else; a change to those types in a later
// Microsoft.Extensions.AI version shows up as a compile error in this file only.
#pragma warning disable MEAI001

namespace OneLlm.Extensions.AI;

/// <summary>A OneLLM speech-to-text model as a Microsoft.Extensions.AI <see cref="ISpeechToTextClient"/>.</summary>
/// <remarks>
/// The audio arrives as a stream with no content type, so the bridge reads it from the first bytes (WAV, MP3, FLAC, Ogg, MP4/M4A and WebM are
/// recognised) and otherwise sends <c>application/octet-stream</c>; set <see cref="OneLlmProperties.ContentType"/> in the options to say it.
/// The whole stream is read into memory: providers take a file upload, not a stream. Streaming returns the one finished transcript as a single update.
/// </remarks>
public sealed class OneLlmSpeechToTextClient : ISpeechToTextClient
{
    private readonly ISpeechToTextProvider _provider;
    private readonly ResolvedModel? _resolved;
    private readonly string _model;
    private readonly TimeProvider _time;

    /// <summary>A client over <paramref name="provider"/> that sends <paramref name="model"/> as the model name and prices nothing.</summary>
    /// <param name="provider">The transcription provider. Not owned.</param>
    /// <param name="model">The model name the provider expects.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmSpeechToTextClient(ISpeechToTextProvider provider, string model, TimeProvider time)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A client for a catalog entry; every call is priced.</summary>
    /// <param name="provider">The transcription provider that serves <paramref name="model"/>. Not owned.</param>
    /// <param name="model">The resolved catalog entry.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmSpeechToTextClient(ISpeechToTextProvider provider, ResolvedModel model, TimeProvider time)
        : this(provider, model?.Name ?? throw new ArgumentNullException(nameof(model)), time) => _resolved = model;

    /// <inheritdoc />
    public async Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioSpeechStream);
        using var buffer = new MemoryStream();
        await audioSpeechStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] audio = buffer.ToArray();
        string contentType = options?.AdditionalProperties?.TryGetValue(OneLlmProperties.ContentType, out object? given) == true && given is string text
            ? text
            : AudioSniffer.ContentTypeOf(audio);
        string model = options?.ModelId is { Length: > 0 } requested && _resolved is null ? requested : _model;
        TranscriptionResponse r = await _provider.TranscribeAsync(
            new TranscriptionRequest { Model = model, Audio = audio, ContentType = contentType, Language = options?.SpeechLanguage },
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        (Usage? usage, Cost? cost) = Billing.Price(_resolved, r.Usage, null, _time, warnings);
        var response = new SpeechToTextResponse(r.Text) { ModelId = model, EndTime = r.Duration };
        if (usage is not null)
        {
            response.Usage = ChatMapping.ToUsage(usage);
        }

        Billing.Annotate(response.AdditionalProperties ??= [], cost, warnings);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SpeechToTextResponse whole = await GetTextAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false);
        yield return new SpeechToTextResponseUpdate(whole.Text) { Kind = SpeechToTextResponseUpdateKind.TextUpdated, ModelId = whole.ModelId, EndTime = whole.EndTime, AdditionalProperties = whole.AdditionalProperties };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(SpeechToTextClientMetadata))
        {
            return new SpeechToTextClientMetadata(_provider.Name, null, _model);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>A OneLLM text-to-speech model as a Microsoft.Extensions.AI <see cref="ITextToSpeechClient"/>.</summary>
/// <remarks>The audio comes back as one <c>DataContent</c>. Streaming returns the finished audio as a single update, because providers return whole files.</remarks>
public sealed class OneLlmTextToSpeechClient : ITextToSpeechClient
{
    private readonly ITextToSpeechProvider _provider;
    private readonly ResolvedModel? _resolved;
    private readonly string _model;
    private readonly TimeProvider _time;

    /// <summary>A client over <paramref name="provider"/> that sends <paramref name="model"/> as the model name and prices nothing.</summary>
    /// <param name="provider">The speech provider. Not owned.</param>
    /// <param name="model">The model name the provider expects.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmTextToSpeechClient(ITextToSpeechProvider provider, string model, TimeProvider time)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A client for a catalog entry; every call is priced.</summary>
    /// <param name="provider">The speech provider that serves <paramref name="model"/>. Not owned.</param>
    /// <param name="model">The resolved catalog entry.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmTextToSpeechClient(ITextToSpeechProvider provider, ResolvedModel model, TimeProvider time)
        : this(provider, model?.Name ?? throw new ArgumentNullException(nameof(model)), time) => _resolved = model;

    /// <inheritdoc />
    public async Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        string model = options?.ModelId is { Length: > 0 } requested && _resolved is null ? requested : _model;
        SpeechResponse r = await _provider.SynthesizeAsync(
            new SpeechRequest { Model = model, Input = text, Voice = options?.VoiceId, Format = options?.AudioFormat },
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        (Usage? usage, Cost? cost) = Billing.Price(_resolved, r.Usage, null, _time, warnings);
        var response = new TextToSpeechResponse([new DataContent(r.Audio, r.ContentType)]) { ModelId = model };
        if (usage is not null)
        {
            response.Usage = ChatMapping.ToUsage(usage);
        }

        Billing.Annotate(response.AdditionalProperties ??= [], cost, warnings);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TextToSpeechResponse whole = await GetAudioAsync(text, options, cancellationToken).ConfigureAwait(false);
        yield return new TextToSpeechResponseUpdate(whole.Contents) { Kind = TextToSpeechResponseUpdateKind.AudioUpdated, ModelId = whole.ModelId, AdditionalProperties = whole.AdditionalProperties };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(TextToSpeechClientMetadata))
        {
            return new TextToSpeechClientMetadata(_provider.Name, null, _model);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>A OneLLM image model as a Microsoft.Extensions.AI <see cref="IImageGenerator"/>. Original images in the request become edit inputs.</summary>
/// <remarks><c>ImageGenerationOptions.ImageSize</c> becomes the provider's <c>WIDTHxHEIGHT</c> size string; <c>Count</c> becomes the image count.</remarks>
public sealed class OneLlmImageGenerator : IImageGenerator
{
    private readonly IImageProvider _provider;
    private readonly ResolvedModel? _resolved;
    private readonly string _model;
    private readonly TimeProvider _time;

    /// <summary>A generator over <paramref name="provider"/> that sends <paramref name="model"/> as the model name and prices nothing.</summary>
    /// <param name="provider">The image provider. Not owned.</param>
    /// <param name="model">The model name the provider expects.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmImageGenerator(IImageProvider provider, string model, TimeProvider time)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A generator for a catalog entry; every call is priced.</summary>
    /// <param name="provider">The image provider that serves <paramref name="model"/>. Not owned.</param>
    /// <param name="model">The resolved catalog entry.</param>
    /// <param name="time">The clock used to date prices.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OneLlmImageGenerator(IImageProvider provider, ResolvedModel model, TimeProvider time)
        : this(provider, model?.Name ?? throw new ArgumentNullException(nameof(model)), time) => _resolved = model;

    /// <inheritdoc />
    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string model = options?.ModelId is { Length: > 0 } requested && _resolved is null ? requested : _model;
        var inputs = new List<ImageInput>();
        foreach (AIContent original in request.OriginalImages ?? [])
        {
            if (original is DataContent data && data.Data.Length > 0)
            {
                inputs.Add(new ImageInput(data.Data, data.MediaType));
            }
        }

        Size? size = options?.ImageSize;
        ImageResponse r = await _provider.GenerateImageAsync(
            new ImageRequest
            {
                Model = model,
                Prompt = request.Prompt ?? "",
                Inputs = inputs,
                Count = options?.Count ?? 1,
                Size = size is { } s ? $"{s.Width}x{s.Height}" : null,
            },
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        (Usage? usage, _) = Billing.Price(_resolved, r.Usage, null, _time, warnings);
        List<AIContent> contents = [.. r.Images.Select(i => (AIContent)new DataContent(i.Data, i.ContentType))];
        var response = new ImageGenerationResponse(contents);
        if (usage is not null)
        {
            response.Usage = ChatMapping.ToUsage(usage);
        }

        return response;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ImageGeneratorMetadata))
        {
            return new ImageGeneratorMetadata(_provider.Name, null, _model);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Recognises common audio containers from their first bytes.</summary>
internal static class AudioSniffer
{
    public static string ContentTypeOf(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8))
        {
            return "audio/wav";
        }

        if (head.Length >= 3 && (head[..3].SequenceEqual("ID3"u8) || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)))
        {
            return "audio/mpeg";
        }

        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8))
        {
            return "audio/flac";
        }

        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8))
        {
            return "audio/ogg";
        }

        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            return "audio/mp4";
        }

        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            return "audio/webm";
        }

        return "application/octet-stream";
    }
}
