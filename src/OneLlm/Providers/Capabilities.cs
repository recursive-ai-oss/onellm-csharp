using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers;

// A provider is a class that implements the capability interfaces it supports. Callers ask with `is`:
//     if (provider is IEmbeddingProvider embeddings) { ... }
// Every I/O method takes a CancellationToken and throws the typed errors in OneLlm (ProviderException and its subclasses).

/// <summary>A provider or a deployment of one: a name and the capability interfaces it implements.</summary>
public interface IProvider
{
    /// <summary>The provider's name as the catalog knows it, for example <c>anthropic</c>.</summary>
    string Name { get; }

    /// <summary>Where the provider runs: <c>direct</c> for its own API, or <c>azure</c>, <c>bedrock</c>, <c>vertex</c>.</summary>
    string Deployment { get; }
}

/// <summary>Chat: one request, answered whole or as a stream of events.</summary>
public interface IChatProvider : IProvider
{
    /// <summary>Sends a request and waits for the whole answer.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ProviderException">The provider failed; the subclass says why.</exception>
    Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and returns the answer as events as they arrive. Feeding them to a <see cref="MessageAccumulator"/> gives the message <see cref="SendAsync"/> would have returned.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The events. A <see cref="StreamEvent"/>'s <c>Utf8</c> is valid only until the next event is requested.</returns>
    /// <exception cref="ProviderException">The provider failed, before the first event or in the middle of the stream.</exception>
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What kind of input an embedding is for, for models that treat them differently.</summary>
public enum EmbeddingInputType
{
    /// <summary>Not stated.</summary>
    Unspecified = 0,

    /// <summary>A search query.</summary>
    Query = 1,

    /// <summary>A document to be searched.</summary>
    Document = 2,
}

/// <summary>A request for embeddings.</summary>
public sealed record EmbeddingRequest
{
    /// <summary>The embedding model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>The texts to embed, one vector each.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>The vector length to ask for, for models that can shorten theirs.</summary>
    public int? Dimensions { get; init; }

    /// <summary>What the texts are for, for models with query and document modes.</summary>
    public EmbeddingInputType InputType { get; init; }

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>Embeddings for the inputs of a request, in the same order.</summary>
public sealed record EmbeddingResponse
{
    /// <summary>One vector per input.</summary>
    public IReadOnlyList<ReadOnlyMemory<float>> Embeddings { get; init; } = [];

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>Embeddings.</summary>
public interface IEmbeddingProvider : IProvider
{
    /// <summary>Embeds texts.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The vectors.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Where a token count came from. Lower is more exact.</summary>
public enum TokenCountTier
{
    /// <summary>The provider's own count endpoint: exact for the provider's tokenizer.</summary>
    Provider = 1,

    /// <summary>A local tokenizer built from the vendor's published vocabulary data: exact where the data is complete, close otherwise.</summary>
    Tokenizer = 2,

    /// <summary>A calibrated estimate from character counts.</summary>
    Estimate = 3,
}

/// <summary>A count of input tokens and where it came from.</summary>
/// <param name="InputTokens">The number of input tokens the request would use.</param>
/// <param name="Tier">How the count was made.</param>
public readonly record struct TokenCount(long InputTokens, TokenCountTier Tier);

/// <summary>Counting the input tokens of a request.</summary>
public interface ITokenCountingProvider : IProvider
{
    /// <summary>Counts the input tokens a chat request would use. A provider that implements this reports <see cref="TokenCountTier.Provider"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<TokenCount> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One request inside a batch.</summary>
public sealed record BatchItem
{
    /// <summary>An id chosen by the caller that comes back with the result. Unique within the batch.</summary>
    public string CustomId { get; init; } = "";

    /// <summary>A chat request, for chat batches.</summary>
    public ChatRequest? Chat { get; init; }

    /// <summary>An embeddings request, for providers whose batches carry embeddings.</summary>
    public EmbeddingRequest? Embedding { get; init; }
}

/// <summary>A set of requests to run asynchronously at the provider's batch price.</summary>
public sealed record BatchRequest
{
    /// <summary>The requests.</summary>
    public IReadOnlyList<BatchItem> Items { get; init; } = [];

    /// <summary>Parameters written into the batch creation request as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>Where a batch stands.</summary>
public enum BatchStatus
{
    /// <summary>Accepted and being checked.</summary>
    Validating = 0,

    /// <summary>Running.</summary>
    InProgress = 1,

    /// <summary>Finished; results are ready.</summary>
    Completed = 2,

    /// <summary>Rejected or failed as a whole.</summary>
    Failed = 3,

    /// <summary>Being cancelled.</summary>
    Cancelling = 4,

    /// <summary>Cancelled.</summary>
    Cancelled = 5,

    /// <summary>Not finished within the provider's window.</summary>
    Expired = 6,
}

/// <summary>A batch at a provider.</summary>
public sealed record BatchJob
{
    /// <summary>The provider's id for the batch.</summary>
    public string Id { get; init; } = "";

    /// <summary>Where the batch stands.</summary>
    public BatchStatus Status { get; init; }

    /// <summary>How many requests it holds, when known.</summary>
    public int? Total { get; init; }

    /// <summary>How many requests have succeeded, when known.</summary>
    public int? Succeeded { get; init; }

    /// <summary>How many requests have failed, when known.</summary>
    public int? Failed { get; init; }

    /// <summary>When the batch was created, when known.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the batch expires, when known.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>The outcome of one request in a batch.</summary>
public sealed record BatchResultItem
{
    /// <summary>The id the caller gave the request.</summary>
    public string CustomId { get; init; } = "";

    /// <summary>The chat response, for a chat request that succeeded.</summary>
    public ChatResponse? Chat { get; init; }

    /// <summary>The embeddings, for an embeddings request that succeeded.</summary>
    public EmbeddingResponse? Embedding { get; init; }

    /// <summary>Why the request failed, when it did.</summary>
    public ProviderException? Error { get; init; }
}

/// <summary>Batch processing: submit many requests, poll, read results.</summary>
public interface IBatchProvider : IProvider
{
    /// <summary>Submits a batch.</summary>
    /// <param name="request">The requests.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The created batch.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<BatchJob> SubmitBatchAsync(BatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a batch's state.</summary>
    /// <param name="batchId">The provider's batch id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The batch.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<BatchJob> GetBatchAsync(string batchId, CancellationToken cancellationToken = default);

    /// <summary>Asks the provider to cancel a batch.</summary>
    /// <param name="batchId">The provider's batch id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The batch after the request.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<BatchJob> CancelBatchAsync(string batchId, CancellationToken cancellationToken = default);

    /// <summary>Streams the results of a finished batch.</summary>
    /// <param name="batchId">The provider's batch id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One item per request, in the provider's order.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    IAsyncEnumerable<BatchResultItem> GetBatchResultsAsync(string batchId, CancellationToken cancellationToken = default);
}

// ---- Declared now, implemented by the media and decision clients. Each request type has init-only members only, so fields can be added later. ----

/// <summary>A request to generate or edit images.</summary>
public sealed record ImageRequest
{
    /// <summary>The image model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>What to draw or change.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Images to edit or use as references. Empty for plain generation.</summary>
    public IReadOnlyList<ImageInput> Inputs { get; init; } = [];

    /// <summary>How many images to make.</summary>
    public int Count { get; init; } = 1;

    /// <summary>The size, in the provider's terms, for example <c>1024x1024</c>.</summary>
    public string? Size { get; init; }

    /// <summary>The quality tier, in the provider's terms.</summary>
    public string? Quality { get; init; }

    /// <summary>The file format to return, for example <c>png</c>, <c>jpeg</c> or <c>webp</c>. The provider's default when null.</summary>
    public string? Format { get; init; }

    /// <summary>The aspect ratio for providers that take one instead of a pixel size, for example <c>16:9</c>.</summary>
    public string? AspectRatio { get; init; }

    /// <summary>For an edit: an image whose transparent areas mark where to change the first input. Null for no mask.</summary>
    public ImageInput? Mask { get; init; }

    /// <summary>How many in-progress images to stream before the final one (0 to 3), for providers that stream them. Used only by <see cref="Media.IImageStreamingProvider"/>.</summary>
    public int? PartialImages { get; init; }

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>An image given to an edit request.</summary>
/// <param name="Data">The image bytes.</param>
/// <param name="ContentType">The media type, for example <c>image/png</c>.</param>
public readonly record struct ImageInput(ReadOnlyMemory<byte> Data, string ContentType);

/// <summary>A generated image.</summary>
/// <param name="Data">The image bytes.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="RevisedPrompt">The prompt the model actually used, when it rewrote it.</param>
public readonly record struct GeneratedImage(ReadOnlyMemory<byte> Data, string ContentType, string? RevisedPrompt);

/// <summary>The images a request made.</summary>
public sealed record ImageResponse
{
    /// <summary>The images.</summary>
    public IReadOnlyList<GeneratedImage> Images { get; init; } = [];

    /// <summary>Text the model wrote beside the images, for providers whose image models answer in prose too. Null when there is none.</summary>
    public string? Text { get; init; }

    /// <summary>The model that answered, when the provider says.</summary>
    public string? Model { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>What the call cost, priced from the catalog. Null when the catalog has no price for the model or the call reported no usage to price.</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call: parameters the model ignores, costs that could not be fully priced.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];
}

/// <summary>Image generation and editing.</summary>
public interface IImageProvider : IProvider
{
    /// <summary>Generates or edits images.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The images.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<ImageResponse> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request to turn text into speech.</summary>
public sealed record SpeechRequest
{
    /// <summary>The model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>The text to speak.</summary>
    public string Input { get; init; } = "";

    /// <summary>The voice, in the provider's terms.</summary>
    public string? Voice { get; init; }

    /// <summary>The audio format to return, for example <c>mp3</c> or <c>wav</c>.</summary>
    public string? Format { get; init; }

    /// <summary>Style instructions for models that take them.</summary>
    public string? Instructions { get; init; }

    /// <summary>The speed of speech, 1.0 being normal, for providers that take one.</summary>
    public double? Speed { get; init; }

    /// <summary>For a dialogue: the voice of each speaker named in <see cref="Input"/>. When set, <see cref="Voice"/> is not used.</summary>
    public IReadOnlyList<SpeakerVoice>? Speakers { get; init; }

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>The voice that reads one speaker's lines in a dialogue.</summary>
/// <param name="Speaker">The speaker's name as the input text uses it.</param>
/// <param name="Voice">The voice, in the provider's terms.</param>
public readonly record struct SpeakerVoice(string Speaker, string Voice);

/// <summary>Synthesized speech.</summary>
public sealed record SpeechResponse
{
    /// <summary>The audio bytes.</summary>
    public ReadOnlyMemory<byte> Audio { get; init; }

    /// <summary>The audio's media type.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>What the call cost. Null when the provider reports no usage for the model and the catalog prices by something the response does not tell (see the warnings).</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];
}

/// <summary>Text to speech.</summary>
public interface ITextToSpeechProvider : IProvider
{
    /// <summary>Turns text into speech.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The audio.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request to transcribe audio.</summary>
public sealed record TranscriptionRequest
{
    /// <summary>The model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>The audio bytes.</summary>
    public ReadOnlyMemory<byte> Audio { get; init; }

    /// <summary>The audio's media type, for example <c>audio/mpeg</c>.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>The file name to send, when the provider uses its extension.</summary>
    public string? FileName { get; init; }

    /// <summary>The spoken language as an ISO 639-1 code, when known.</summary>
    public string? Language { get; init; }

    /// <summary>Text that primes the model with names or style.</summary>
    public string? Prompt { get; init; }

    /// <summary>True to translate the speech into English instead of transcribing it, for providers and models that do.</summary>
    public bool Translate { get; init; }

    /// <summary>The form of the answer in the provider's terms, for example <c>json</c>, <c>verbose_json</c>, <c>diarized_json</c>, <c>text</c>, <c>srt</c> or <c>vtt</c>. <c>json</c> when null.</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>Which timestamps to return with <c>verbose_json</c>: <c>segment</c>, <c>word</c> or both.</summary>
    public IReadOnlyList<string>? TimestampGranularities { get; init; }

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>A transcript.</summary>
public sealed record TranscriptionResponse
{
    /// <summary>The transcribed text.</summary>
    public string Text { get; init; } = "";

    /// <summary>The detected or given language.</summary>
    public string? Language { get; init; }

    /// <summary>The audio length, when the provider reports it.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Timed segments and words as the provider returned them, when it did.</summary>
    public JsonNode? Details { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>What the call cost, priced from the catalog. Null when the model has no price or the call reported nothing to price.</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];
}

/// <summary>Speech to text.</summary>
public interface ISpeechToTextProvider : IProvider
{
    /// <summary>Transcribes audio.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transcript.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request to classify texts for policy violations.</summary>
public sealed record ModerationRequest
{
    /// <summary>The model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>The texts to check.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>Images to check with the texts, for models that take them. When any are given, texts and images are checked together and the response holds one verdict for the lot.</summary>
    public IReadOnlyList<ImageInput> Images { get; init; } = [];

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>The verdict for one text.</summary>
public sealed record ModerationResult
{
    /// <summary>True when any category is flagged.</summary>
    public bool Flagged { get; init; }

    /// <summary>Which categories are flagged.</summary>
    public IReadOnlyDictionary<string, bool> Categories { get; init; } = new Dictionary<string, bool>();

    /// <summary>The score per category, 0 to 1.</summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();

    /// <summary>For each category, which kinds of input (<c>text</c>, <c>image</c>) the score applies to, when the provider says. Null when it does not.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CategoryInputTypes { get; init; }
}

/// <summary>Verdicts for the inputs of a request, in the same order.</summary>
public sealed record ModerationResponse
{
    /// <summary>One verdict per input.</summary>
    public IReadOnlyList<ModerationResult> Results { get; init; } = [];

    /// <summary>The model that answered, when the provider says.</summary>
    public string? Model { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>What the call cost: zero for a free model. Null when the catalog has no entry for the model.</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about the call.</summary>
    public IReadOnlyList<ResponseWarning> Warnings { get; init; } = [];
}

/// <summary>Content moderation.</summary>
public interface IModerationProvider : IProvider
{
    /// <summary>Checks texts.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The verdicts.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<ModerationResponse> ModerateAsync(ModerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request for typed judgments: answers to questions about a state, from a decisions engine.</summary>
public sealed record DecisionRequest
{
    /// <summary>The model's name.</summary>
    public string Model { get; init; } = "";

    /// <summary>Instructions that frame the questions.</summary>
    public string? Instructions { get; init; }

    /// <summary>The state the questions are about.</summary>
    public JsonNode? State { get; init; }

    /// <summary>The questions in the engine's own wire form (choice, score and noul questions).</summary>
    public JsonArray? Questions { get; init; }

    /// <summary>Parameters written into the request body as given.</summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>The answers to a decision request.</summary>
public sealed record DecisionResponse
{
    /// <summary>The answers by question id, in the engine's wire form.</summary>
    public JsonObject? Answers { get; init; }

    /// <summary>The model's explanation, when it refused to answer.</summary>
    public string? Refusal { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>A decisions engine (TypeSafe Jev, OpenAI Decisions).</summary>
public interface IDecisionProvider : IProvider
{
    /// <summary>Answers questions about a state.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answers.</returns>
    /// <exception cref="ProviderException">The provider failed.</exception>
    Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}
