namespace OneLlm.Extensions.AI;

/// <summary>
/// The keys OneLLM reads and writes in the <c>AdditionalProperties</c> dictionaries of Microsoft.Extensions.AI types. Everything the
/// two models cannot share directly travels under one of these keys, so a round trip through the bridge keeps it.
/// </summary>
/// <remarks>
/// Keys on a <c>ChatResponse</c> (and on the <c>UsageContent</c> of a streamed response), written by the bridge:
/// <list type="bullet">
/// <item><see cref="Cost"/>: the call's <see cref="OneLlm.Messages.Cost"/> (line items and total), present when the model is priced in the catalog.</item>
/// <item><see cref="CostTotal"/>: the same total as a <see cref="decimal"/>, for callers that only want the number.</item>
/// <item><see cref="Warnings"/>: a <c>string[]</c> of notices (a parameter the model does not take was stripped, the catalog has no price).</item>
/// </list>
/// Keys on a <c>ChatMessage</c> and its contents, written when the OneLLM value is set and read back when the message returns:
/// <see cref="When"/>, <see cref="Injected"/>, <see cref="InjectedBy"/>, <see cref="Routine"/>, <see cref="InvalidInputs"/>,
/// <see cref="IsError"/>, <see cref="ResultParts"/>, <see cref="Provider"/>.
/// Keys on <c>ChatOptions</c>, read by the bridge: <see cref="ExtraParameters"/>, <see cref="Cache"/>, <see cref="ServiceTier"/>, <see cref="UserId"/>.
/// </remarks>
public static class OneLlmProperties
{
    /// <summary>The call's <see cref="OneLlm.Messages.Cost"/>: line items and total, each line naming its counter and unit price.</summary>
    public const string Cost = "onellm.cost";

    /// <summary>The call's total cost as a <see cref="decimal"/> in US dollars.</summary>
    public const string CostTotal = "onellm.cost.total";

    /// <summary>A <c>string[]</c> of notices about the call: stripped parameters, a missing price, an option the bridge could not carry.</summary>
    public const string Warnings = "onellm.warnings";

    /// <summary>On a message or a function call: the <c>when</c> expression (a template condition) the entry carries.</summary>
    public const string When = "onellm.when";

    /// <summary>On a <c>FunctionCallContent</c>: <see langword="true"/> when the runtime injected the call before the first turn rather than the model asking for it.</summary>
    public const string Injected = "onellm.injected";

    /// <summary>On a <c>FunctionCallContent</c>: what injected the call (a definition entry or a tool call id).</summary>
    public const string InjectedBy = "onellm.injectedBy";

    /// <summary>On a <c>FunctionCallContent</c>: the routine (a tool source reference) the call resolved to.</summary>
    public const string Routine = "onellm.routine";

    /// <summary>On a <c>FunctionCallContent</c>: the raw argument text when the model produced arguments that were not a JSON object.</summary>
    public const string InvalidInputs = "onellm.invalidInputs";

    /// <summary>On a <c>FunctionResultContent</c>: <see langword="true"/> when the tool reported an error.</summary>
    public const string IsError = "onellm.isError";

    /// <summary>On a <c>FunctionResultContent</c>: the original <c>IReadOnlyList&lt;Part&gt;</c> result, kept in process so images and typed parts survive a round trip.</summary>
    public const string ResultParts = "onellm.resultParts";

    /// <summary>On a <c>TextReasoningContent</c>: the provider that produced the reasoning, so its opaque <c>ProtectedData</c> is replayed to the same provider.</summary>
    public const string Provider = "onellm.provider";

    /// <summary>On a <c>TextContent</c>: <see langword="true"/> when the text is a model refusal rather than an answer.</summary>
    public const string Refusal = "onellm.refusal";

    /// <summary>On <c>UsageDetails.AdditionalCounts</c>: input tokens written to the provider's prompt cache.</summary>
    public const string CacheCreationInputTokens = "onellm.cacheCreationInputTokens";

    /// <summary>On <c>ChatOptions.AdditionalProperties</c>: a JSON object (<c>JsonObject</c> or <c>JsonElement</c>) of provider parameters passed through unchanged.</summary>
    public const string ExtraParameters = "onellm.extraParameters";

    /// <summary>On <c>ChatOptions.AdditionalProperties</c>: <see langword="true"/> asks for prompt caching with the provider's defaults.</summary>
    public const string Cache = "onellm.cache";

    /// <summary>On <c>ChatOptions.AdditionalProperties</c>: the service tier to request (for example <c>flex</c> or <c>priority</c>).</summary>
    public const string ServiceTier = "onellm.serviceTier";

    /// <summary>On <c>ChatOptions.AdditionalProperties</c>: an opaque end-user identifier for the provider's abuse monitoring.</summary>
    public const string UserId = "onellm.userId";

    /// <summary>On speech-to-text options: the audio content type, when the bridge cannot tell it from the first bytes of the stream.</summary>
    public const string ContentType = "onellm.contentType";
}
