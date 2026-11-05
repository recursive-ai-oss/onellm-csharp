using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Media;

// Streaming for the media clients is a separate capability from the one-shot calls in OneLlm.Providers, because not every provider streams
// every kind of media. A provider that streams implements the matching interface beside the plain one.

/// <summary>What an <see cref="ImageStreamEvent"/> carries.</summary>
public enum ImageStreamEventKind
{
    /// <summary>An in-progress image, rougher than the final one.</summary>
    Partial = 0,

    /// <summary>A finished image. The event also carries the usage and cost of the call.</summary>
    Completed = 1,
}

/// <summary>One event of a streamed image generation or edit.</summary>
public sealed record ImageStreamEvent
{
    /// <summary>Whether this is an in-progress image or the finished one.</summary>
    public ImageStreamEventKind Kind { get; init; }

    /// <summary>For a partial image, its position in the sequence, from 0. Zero for the finished image.</summary>
    public int Index { get; init; }

    /// <summary>The image bytes.</summary>
    public GeneratedImage Image { get; init; }

    /// <summary>What the call consumed. Set on the finished image only.</summary>
    public Usage? Usage { get; init; }

    /// <summary>What the call cost. Set on the finished image only, and only when the catalog can price the usage.</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call. Set on the finished image only.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];
}

/// <summary>Image generation and editing that can send in-progress images as they are made.</summary>
public interface IImageStreamingProvider : IProvider
{
    /// <summary>Generates or edits images and returns events as the provider sends them: up to <see cref="ImageRequest.PartialImages"/> partial images, then the finished one.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The events. The <see cref="GeneratedImage.Data"/> of each event is its own copy and stays valid.</returns>
    /// <exception cref="ProviderException">The provider failed, before the first event or in the middle of the stream.</exception>
    IAsyncEnumerable<ImageStreamEvent> StreamImageAsync(ImageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Speech audio that is read while the provider is still making it.</summary>
public sealed class SpeechAudio : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    /// <summary>Creates a stream over a response body.</summary>
    /// <param name="audio">The audio bytes as they arrive.</param>
    /// <param name="contentType">The audio's media type.</param>
    /// <param name="owner">What to dispose with the stream (the HTTP response).</param>
    public SpeechAudio(Stream audio, string contentType, IDisposable? owner = null)
    {
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        _owner = owner;
    }

    /// <summary>The audio bytes as they arrive. Read it to the end, then dispose the stream.</summary>
    public Stream Audio { get; }

    /// <summary>The audio's media type.</summary>
    public string ContentType { get; init; }

    /// <summary>
    /// What the call will cost, when that is known before the audio arrives (a price per character is). Null when the provider tells the usage
    /// only at the end and a stream does not carry it.
    /// </summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Audio.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
    }
}

/// <summary>Text to speech that can hand the audio over as it is made.</summary>
public interface ISpeechStreamingProvider : IProvider
{
    /// <summary>Starts a speech request and returns once the provider has begun to answer.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call, and the read of the audio.</param>
    /// <returns>The audio stream. The caller disposes it.</returns>
    /// <exception cref="ProviderException">The provider failed before the audio began.</exception>
    Task<SpeechAudio> StreamSynthesisAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One event of a streamed transcription.</summary>
public sealed record TranscriptionStreamEvent
{
    /// <summary>A piece of the transcript, in order. Null on the last event.</summary>
    public string? Delta { get; init; }

    /// <summary>The whole transcript with its usage and cost. Set on the last event only.</summary>
    public TranscriptionResponse? Completed { get; init; }
}

/// <summary>Speech to text that sends the transcript as it is made.</summary>
public interface ISpeechToTextStreamingProvider : IProvider
{
    /// <summary>Transcribes audio and returns the transcript in pieces, then the whole.</summary>
    /// <param name="request">The request. Models that cannot stream are refused by the provider.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The events: zero or more deltas, then one event with <see cref="TranscriptionStreamEvent.Completed"/>.</returns>
    /// <exception cref="ProviderException">The provider failed, before the first event or in the middle of the stream.</exception>
    IAsyncEnumerable<TranscriptionStreamEvent> StreamTranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}
