using OneLlm.Media;

namespace OneLlm.Providers.OpenAI;

// The media endpoints. The request and response handling is in OpenAIMedia; this file only gives the provider the capability interfaces.
public sealed partial class OpenAIProvider : IImageProvider, IImageStreamingProvider, ITextToSpeechProvider, ISpeechStreamingProvider, ISpeechToTextProvider, ISpeechToTextStreamingProvider, IModerationProvider
{
    /// <summary>
    /// Generates images (<c>/images/generations</c>), or edits the images in <see cref="ImageRequest.Inputs"/> (<c>/images/edits</c>, with the optional
    /// <see cref="ImageRequest.Mask"/>). GPT image models return base64 images; the content type follows the requested <see cref="ImageRequest.Format"/>
    /// (PNG when the API reports none). The cost is priced from the token usage the API reports, with <c>size</c> and <c>quality</c> as price qualifiers.
    /// </summary>
    /// <param name="request">The request. <see cref="ImageRequest.Count"/> is 1 to 10; an edit takes up to 16 input images.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The images, usage and cost.</returns>
    /// <exception cref="ArgumentException">The request is missing its model or prompt, or has too many images.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default) =>
        _media.GenerateImageAsync(request, cancellationToken);

    /// <summary>Generates or edits an image as a stream: up to <see cref="ImageRequest.PartialImages"/> partial images, then the finished one with its usage and cost.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The events.</returns>
    /// <exception cref="ProviderException">The API failed.</exception>
    public IAsyncEnumerable<ImageStreamEvent> StreamImageAsync(ImageRequest request, CancellationToken cancellationToken = default) =>
        _media.StreamImageAsync(request, cancellationToken);

    /// <summary>
    /// Turns text into speech with <c>/audio/speech</c>. The voice defaults to <c>alloy</c> and the format to the API's own default (mp3). The API reports no
    /// usage: <c>tts-1</c> and <c>tts-1-hd</c> are priced per input character, so their cost is known; for a model priced per token the cost is null and a warning says why.
    /// </summary>
    /// <param name="request">The request. <see cref="SpeechRequest.Speakers"/> is not supported.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The audio.</returns>
    /// <exception cref="ArgumentException">The request is missing its model or text, or asks for several speakers.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default) =>
        _media.SynthesizeAsync(request, cancellationToken);

    /// <summary>Starts speech synthesis and returns the audio as it arrives, using the API's chunked audio body.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call and the read.</param>
    /// <returns>The audio stream; the caller disposes it.</returns>
    /// <exception cref="ProviderException">The API failed before the audio began.</exception>
    public Task<SpeechAudio> StreamSynthesisAsync(SpeechRequest request, CancellationToken cancellationToken = default) =>
        _media.StreamSynthesisAsync(request, cancellationToken);

    /// <summary>
    /// Transcribes audio with <c>/audio/transcriptions</c>, or translates it into English with <c>/audio/translations</c> when
    /// <see cref="TranscriptionRequest.Translate"/> is set. The audio is sent as <c>multipart/form-data</c>. Usage is tokens or audio seconds, whichever the model reports.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transcript with its timed details (for <c>verbose_json</c> and <c>diarized_json</c>), usage and cost.</returns>
    /// <exception cref="ArgumentException">The request has no model or no audio.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
        _media.TranscribeAsync(request, cancellationToken);

    /// <summary>Transcribes audio as a stream of text deltas followed by the whole transcript. Models that do not stream (<c>whisper-1</c>) answer whole; the API ignores the flag for them and this call then fails with a malformed-response error.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The events.</returns>
    /// <exception cref="ProviderException">The API failed.</exception>
    public IAsyncEnumerable<TranscriptionStreamEvent> StreamTranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
        _media.StreamTranscribeAsync(request, cancellationToken);

    /// <summary>Classifies texts, and images for models that take them, with <c>/moderations</c>. The endpoint is free; the cost is zero.</summary>
    /// <param name="request">The request. With <see cref="ModerationRequest.Images"/> set, texts and images are checked together and one verdict comes back.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The verdicts.</returns>
    /// <exception cref="ArgumentException">The request has nothing to check.</exception>
    /// <exception cref="ProviderException">The API failed.</exception>
    public Task<ModerationResponse> ModerateAsync(ModerationRequest request, CancellationToken cancellationToken = default) =>
        _media.ModerateAsync(request, cancellationToken);
}
