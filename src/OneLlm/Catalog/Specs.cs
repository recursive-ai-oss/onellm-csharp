using System.Text.Json.Serialization;

namespace OneLlm.Catalog;

// The shapes of an llm-provider.yaml file. Every field that can be left out of a layer is nullable: "absent" means "keep the
// value from the layer below", so a user file or a code override states only what it changes. Fields a reader does not know are
// kept in ExtensionData and merge like any other field.

/// <summary>One provider in a catalog file: its name, model name patterns, aliases, capabilities and models.</summary>
public sealed class ProviderSpec
{
    /// <summary>The provider's short name, for example <c>anthropic</c>. Layers are matched by it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name for people, for example <c>Anthropic</c>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Glob patterns (<c>*</c> and <c>?</c>) for model names this provider serves, for example <c>claude-*</c>. A layer that gives patterns replaces the list below it.</summary>
    public List<string>? ModelPatterns { get; set; }

    /// <summary>Short or logical names mapped to model names, for example <c>claude-sonnet</c> to <c>claude-sonnet-5-5</c>. Layers merge key by key.</summary>
    public Dictionary<string, string>? Aliases { get; set; }

    /// <summary>The environment variable that holds the API key.</summary>
    public string? ApiKeyEnvVar { get; set; }

    /// <summary>What the provider can do.</summary>
    public ProviderCapabilitiesSpec? Capabilities { get; set; }

    /// <summary>The models. Layers merge by model name; a layer can add a model or change fields of one.</summary>
    public List<ModelSpec>? Models { get; set; }

    /// <summary>Base URLs and settings for servers that speak the provider's protocol, by preset name (for example <c>ollama</c>, <c>groq</c>).</summary>
    public Dictionary<string, PresetSpec>? Presets { get; set; }

    /// <summary>Free-form settings for provider code and for people, by key. Layers merge key by key.</summary>
    public Dictionary<string, object?>? Properties { get; set; }

    /// <summary>Fields not modelled here, kept as read (for example the runtime-specific <c>inferenceRoutine</c>).</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>What a provider supports as a whole. A model can narrow this with its own <c>supports*</c> fields.</summary>
public sealed class ProviderCapabilitiesSpec
{
    /// <summary>Server-sent event streaming for chat.</summary>
    public bool? Streaming { get; set; }

    /// <summary>Prompt caching.</summary>
    public bool? PromptCaching { get; set; }

    /// <summary>Images in user messages.</summary>
    public bool? ImageInputs { get; set; }

    /// <summary>Images in tool results.</summary>
    public bool? ImageToolResults { get; set; }

    /// <summary>The provider is a decisions engine, not a chat provider (for example TypeSafe Jev): chat requests to its models are refused.</summary>
    public bool? SystemOne { get; set; }

    /// <summary>Client-side tool calling.</summary>
    public bool? Tools { get; set; }

    /// <summary>Structured output against a JSON schema.</summary>
    public bool? StructuredOutput { get; set; }

    /// <summary>A batch API.</summary>
    public bool? Batch { get; set; }

    /// <summary>Embeddings.</summary>
    public bool? Embeddings { get; set; }

    /// <summary>A token count endpoint.</summary>
    public bool? TokenCounting { get; set; }

    /// <summary>Image generation or editing.</summary>
    public bool? ImageGeneration { get; set; }

    /// <summary>Text to speech.</summary>
    public bool? TextToSpeech { get; set; }

    /// <summary>Speech to text.</summary>
    public bool? SpeechToText { get; set; }

    /// <summary>Content moderation.</summary>
    public bool? Moderation { get; set; }

    /// <summary>A decisions endpoint.</summary>
    public bool? Decisions { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A server that speaks a provider's protocol at its own address, such as a local Ollama or a hosted Groq.</summary>
public sealed class PresetSpec
{
    /// <summary>The name for people.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The server's base URL, for example <c>http://localhost:11434/v1</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The environment variable that holds the API key; absent for servers that need none.</summary>
    public string? ApiKeyEnvVar { get; set; }

    /// <summary>Which of the provider's wire protocols the server speaks, for example <c>chat-completions</c> or <c>responses</c>.</summary>
    public string? Protocol { get; set; }

    /// <summary>Glob patterns for the model names this server serves. A preset's patterns, aliases and models work like a provider's.</summary>
    public List<string>? ModelPatterns { get; set; }

    /// <summary>Aliases for this server's models.</summary>
    public Dictionary<string, string>? Aliases { get; set; }

    /// <summary>What this server supports; fields left out fall back to the provider's.</summary>
    public ProviderCapabilitiesSpec? Capabilities { get; set; }

    /// <summary>The models the preset knows about, with limits and prices where they are known.</summary>
    public List<ModelSpec>? Models { get; set; }

    /// <summary>Free-form settings, by key.</summary>
    public Dictionary<string, object?>? Properties { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>One model: limits, what it supports, how it thinks, and what it costs.</summary>
public sealed class ModelSpec
{
    /// <summary>The model's name as the provider's API takes it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name for people.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The model family, for grouping.</summary>
    public string? Family { get; set; }

    /// <summary>What the model does: <c>chat</c> (the default when absent), <c>embedding</c>, <c>image</c>, <c>speech</c>, <c>transcription</c>, <c>moderation</c> or <c>decision</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>The context window in tokens.</summary>
    public long? ContextWindow { get; set; }

    /// <summary>The most output tokens one response can have.</summary>
    public long? MaxOutputTokens { get; set; }

    /// <summary>For embedding models: the vector length.</summary>
    public int? Dimensions { get; set; }

    /// <summary>The model supports prompt caching.</summary>
    public bool? SupportsCaching { get; set; }

    /// <summary>The model accepts images.</summary>
    public bool? SupportsVision { get; set; }

    /// <summary>The model supports tool calling.</summary>
    public bool? SupportsTools { get; set; }

    /// <summary>The model accepts a temperature. <c>false</c> means the parameter is stripped, and logged, before the request is sent.</summary>
    public bool? SupportsTemperature { get; set; }

    /// <summary>The model supports structured output against a JSON schema.</summary>
    public bool? SupportsStructuredOutput { get; set; }

    /// <summary>The model accepts audio input.</summary>
    public bool? SupportsAudioInput { get; set; }

    /// <summary>The model accepts PDF documents.</summary>
    public bool? SupportsPdfInput { get; set; }

    /// <summary>The model accepts video input.</summary>
    public bool? SupportsVideoInput { get; set; }

    /// <summary>Above this many images in a request the provider applies <see cref="ManyImageMaxDimension"/>.</summary>
    public int? ManyImageThreshold { get; set; }

    /// <summary>The longest side in pixels the provider accepts once a request has more than <see cref="ManyImageThreshold"/> images.</summary>
    public int? ManyImageMaxDimension { get; set; }

    /// <summary>Non-standard service tiers (<c>flex</c>, <c>priority</c>, ...) by name. The standard tier needs no entry.</summary>
    public Dictionary<string, ServiceTierSpec>? ServiceTiers { get; set; }

    /// <summary>How the model reasons; absent for models that do not, and any thinking setting sent to them is stripped.</summary>
    public ThinkingSpec? Thinking { get; set; }

    /// <summary>What the model costs.</summary>
    public PricingSpec? Pricing { get; set; }

    /// <summary>Free-form settings, by key.</summary>
    public Dictionary<string, object?>? Properties { get; set; }

    /// <summary>Fields not modelled here (for example <c>baseTokens</c>), kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A service tier of a model.</summary>
public sealed class ServiceTierSpec
{
    /// <summary>The factor applied to every price on this tier, for example 0.5 for flex or 2 for priority.</summary>
    public decimal? PricingMultiplier { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>How a model reasons: the efforts it accepts and whether it takes a token budget.</summary>
public sealed class ThinkingSpec
{
    /// <summary>The effort levels the model accepts, lowest first.</summary>
    public List<string>? Efforts { get; set; }

    /// <summary>The effort used when none is asked for.</summary>
    public string? DefaultEffort { get; set; }

    /// <summary>The model takes an explicit token budget for thinking.</summary>
    public bool? AcceptsBudget { get; set; }

    /// <summary>For models that take a budget: the budget each effort level stands for.</summary>
    public Dictionary<string, int>? DefaultEffortBudgets { get; set; }

    /// <summary>The smallest budget accepted.</summary>
    public int? MinBudget { get; set; }

    /// <summary>The largest budget accepted.</summary>
    public int? MaxBudget { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>
/// What a model costs. The per-million token prices keep the runtime's field names; <see cref="Items"/> prices anything else by the usage
/// counter it multiplies, and <see cref="Tiers"/> change prices above a size. All amounts are USD and exact decimals.
/// </summary>
public sealed class PricingSpec
{
    /// <summary>USD per million input tokens that were neither cache writes nor cache reads (counter <c>inputTokens</c>).</summary>
    public decimal? InputPerMillion { get; set; }

    /// <summary>USD per million output tokens (counter <c>outputTokens</c>).</summary>
    public decimal? OutputPerMillion { get; set; }

    /// <summary>USD per million tokens written to the prompt cache (counter <c>cacheCreationInputTokens</c>).</summary>
    public decimal? CacheCreationPerMillion { get; set; }

    /// <summary>USD per million tokens read from the prompt cache (counter <c>cacheReadInputTokens</c>).</summary>
    public decimal? CacheReadPerMillion { get; set; }

    /// <summary>USD per million input tokens when the whole request is above <see cref="LongContextThreshold"/> input tokens.</summary>
    public decimal? LongContextInputPerMillion { get; set; }

    /// <summary>USD per million output tokens when the whole request is above <see cref="LongContextThreshold"/> input tokens.</summary>
    public decimal? LongContextOutputPerMillion { get; set; }

    /// <summary>The total input token count above which the long-context prices apply to the whole request.</summary>
    public long? LongContextThreshold { get; set; }

    /// <summary>The date the prices were last checked against the source, as <c>yyyy-MM-dd</c>. A catalog whose prices are older than 180 days warns.</summary>
    public string? AsOf { get; set; }

    /// <summary>The page the prices come from.</summary>
    public string? Source { get; set; }

    /// <summary>Prices for everything beyond the fields above: cache writes by lifetime, audio and image tokens, images by size and quality, audio seconds, characters, tool calls, container hours.</summary>
    public List<PriceItem>? Items { get; set; }

    /// <summary>Price changes above a size, for example long-context rates expressed as multipliers.</summary>
    public List<PriceTier>? Tiers { get; set; }

    /// <summary>Named factors a caller can apply by name: <c>batch</c>, <c>region:eu</c>, ...</summary>
    public Dictionary<string, decimal>? Multipliers { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>One price: an amount for a number of units of a usage counter.</summary>
public sealed class PriceItem
{
    /// <summary>The usage counter this price multiplies: <c>inputAudioTokens</c>, <c>images</c>, <c>audioSeconds</c>, <c>characters</c>, <c>toolCalls.web_search</c>, <c>containerHours</c>, <c>cacheCreation1hInputTokens</c>, ...</summary>
    public string Counter { get; set; } = "";

    /// <summary>The price in USD for <see cref="Per"/> units.</summary>
    public decimal Price { get; set; }

    /// <summary>How many units the price covers. Absent means 1,000,000 for counters whose name ends in <c>Tokens</c> and 1 for every other counter.</summary>
    public decimal? Per { get; set; }

    /// <summary>Narrows the price to a case, for example <c>{ size: 1024x1024, quality: high }</c>. A usage report carries the matching qualifiers.</summary>
    public Dictionary<string, string>? When { get; set; }

    /// <summary>A short label for cost lines.</summary>
    public string? Label { get; set; }

    /// <summary>The date this price was last checked, as <c>yyyy-MM-dd</c>; falls back to the model's.</summary>
    public string? AsOf { get; set; }

    /// <summary>The page this price comes from; falls back to the model's.</summary>
    public string? Source { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}

/// <summary>A change to prices that applies when a size is passed.</summary>
public sealed class PriceTier
{
    /// <summary>The counter whose value is compared with <see cref="Above"/>; <c>totalInputTokens</c> (uncached plus cache writes plus cache reads) when absent.</summary>
    public string? Basis { get; set; }

    /// <summary>The tier applies to a request whose basis is strictly greater than this.</summary>
    public long Above { get; set; }

    /// <summary>Factors applied to the prices of the named counters while the tier applies, for example <c>{ inputTokens: 2, outputTokens: 1.5 }</c>.</summary>
    public Dictionary<string, decimal>? Multipliers { get; set; }

    /// <summary>A label for cost lines, for example <c>long context</c>.</summary>
    public string? Label { get; set; }

    /// <summary>Fields not modelled here, kept as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
