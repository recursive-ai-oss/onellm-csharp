using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers;

/// <summary>A client-side tool the model may call, in the provider-neutral shape: a name, a description and a JSON Schema for its inputs.</summary>
public sealed record ToolDefinition
{
    /// <summary>Creates a tool definition.</summary>
    /// <param name="name">The tool's name. Providers limit it to letters, digits, underscores and hyphens, up to 64 characters.</param>
    /// <param name="description">What the tool does and when to use it, written for the model.</param>
    /// <param name="inputSchema">A JSON Schema object for the tool's inputs. Key order is kept as written, because models read it in order.</param>
    public ToolDefinition(string name, string description, JsonObject inputSchema)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
    }

    /// <summary>The tool's name.</summary>
    public string Name { get; }

    /// <summary>What the tool does, written for the model.</summary>
    public string Description { get; }

    /// <summary>The JSON Schema for the tool's inputs.</summary>
    public JsonObject InputSchema { get; }

    /// <summary>Ask the provider to enforce the schema exactly (strict mode) where it supports that. Unsupported keywords then fail the request.</summary>
    public bool Strict { get; init; }
}

/// <summary>A tool the provider runs itself, such as web search or code execution, named in the provider's terms.</summary>
public sealed record HostedToolDefinition
{
    /// <summary>Creates a hosted tool definition.</summary>
    /// <param name="type">The provider's type name for the tool, for example <c>web_search</c> or <c>code_execution</c>.</param>
    public HostedToolDefinition(string type) => Type = type ?? throw new ArgumentNullException(nameof(type));

    /// <summary>The provider's type name for the tool.</summary>
    public string Type { get; }

    /// <summary>The tool's settings in the provider's own fields (allowed domains, user location, ...), written into the request as given.</summary>
    public JsonObject? Options { get; init; }
}

/// <summary>How the model may use tools.</summary>
public enum ToolChoiceMode
{
    /// <summary>The model decides.</summary>
    Auto = 0,

    /// <summary>The model may not call tools.</summary>
    None = 1,

    /// <summary>The model must call some tool.</summary>
    Required = 2,

    /// <summary>The model must call the named tool.</summary>
    Specific = 3,
}

/// <summary>Constrains tool use. A forced choice changes meaning, so a model that cannot honour it is refused rather than silently given <see cref="Auto"/>.</summary>
public sealed record ToolChoice
{
    private ToolChoice(ToolChoiceMode mode, string? toolName)
    {
        Mode = mode;
        ToolName = toolName;
    }

    /// <summary>The model decides.</summary>
    public static ToolChoice Auto { get; } = new(ToolChoiceMode.Auto, null);

    /// <summary>No tool calls.</summary>
    public static ToolChoice None { get; } = new(ToolChoiceMode.None, null);

    /// <summary>Some tool must be called.</summary>
    public static ToolChoice Required { get; } = new(ToolChoiceMode.Required, null);

    /// <summary>The mode.</summary>
    public ToolChoiceMode Mode { get; }

    /// <summary>For <see cref="ToolChoiceMode.Specific"/>: the tool that must be called.</summary>
    public string? ToolName { get; }

    /// <summary>The named tool must be called.</summary>
    /// <param name="toolName">The tool's name.</param>
    /// <returns>The choice.</returns>
    public static ToolChoice Tool(string toolName) => new(ToolChoiceMode.Specific, toolName ?? throw new ArgumentNullException(nameof(toolName)));
}

/// <summary>The kind of output a request asks for.</summary>
public enum ResponseFormatKind
{
    /// <summary>Plain text (the default).</summary>
    Text = 0,

    /// <summary>Any valid JSON object.</summary>
    JsonObject = 1,

    /// <summary>JSON that matches a schema, through the provider's native structured output.</summary>
    JsonSchema = 2,
}

/// <summary>Structured output: asks for JSON that matches a schema.</summary>
public sealed record ResponseFormat
{
    private ResponseFormat(ResponseFormatKind kind) => Kind = kind;

    /// <summary>Plain text.</summary>
    public static ResponseFormat Text { get; } = new(ResponseFormatKind.Text);

    /// <summary>Any valid JSON object.</summary>
    public static ResponseFormat AnyJson { get; } = new(ResponseFormatKind.JsonObject);

    /// <summary>The kind of output.</summary>
    public ResponseFormatKind Kind { get; }

    /// <summary>For <see cref="ResponseFormatKind.JsonSchema"/>: a name for the schema (letters, digits, underscores), which some providers require.</summary>
    public string? SchemaName { get; private init; }

    /// <summary>For <see cref="ResponseFormatKind.JsonSchema"/>: the JSON Schema.</summary>
    public JsonObject? Schema { get; private init; }

    /// <summary>Ask the provider to enforce the schema exactly where it supports that.</summary>
    public bool Strict { get; private init; }

    /// <summary>Asks for JSON that matches a schema.</summary>
    /// <param name="name">A name for the schema.</param>
    /// <param name="schema">The JSON Schema.</param>
    /// <param name="strict">Whether the provider must enforce it exactly.</param>
    /// <returns>The format.</returns>
    public static ResponseFormat FromSchema(string name, JsonObject schema, bool strict = true) =>
        new(ResponseFormatKind.JsonSchema)
        {
            SchemaName = name ?? throw new ArgumentNullException(nameof(name)),
            Schema = schema ?? throw new ArgumentNullException(nameof(schema)),
            Strict = strict,
        };
}

/// <summary>How much the model should think before answering. Settings a model does not accept are stripped, and logged, based on the catalog.</summary>
public sealed record ReasoningOptions
{
    /// <summary>The effort level, one of the model's <c>thinking.efforts</c> in the catalog (<c>low</c>, <c>medium</c>, <c>high</c>, ...). Null leaves the model's default.</summary>
    public string? Effort { get; init; }

    /// <summary>A token budget for thinking, for models that take one. Null leaves the default.</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>Ask for readable reasoning summaries (<c>auto</c>, <c>concise</c> or <c>detailed</c>) where the provider offers them.</summary>
    public string? Summary { get; init; }
}

/// <summary>Prompt caching settings for a request. Messages with <see cref="Message.Cache"/> set mark where cache breakpoints go.</summary>
public sealed record CachePolicy
{
    /// <summary>A policy that turns caching off, including the caching a provider would otherwise do by itself where that can be switched off.</summary>
    public static CachePolicy Off { get; } = new() { Enabled = false };

    /// <summary>Whether to use prompt caching. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long cached prefixes should live. Providers offer fixed lifetimes (for example 5 minutes and 1 hour); the nearest is used. Null leaves the default.</summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>A key that groups requests which share a prefix, for providers that route by it (<c>prompt_cache_key</c>).</summary>
    public string? Key { get; init; }

    /// <summary>The name of an explicitly created cache to use, for providers that have such objects (Gemini <c>cachedContents/...</c>).</summary>
    public string? CachedContent { get; init; }
}

/// <summary>A chat request in the provider-neutral shape. Each provider client turns it into its own wire format.</summary>
public sealed record ChatRequest
{
    /// <summary>The model's name as the provider takes it (resolve catalog names and aliases first).</summary>
    public string Model { get; init; } = "";

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    /// <summary>The system prompt.</summary>
    public string? System { get; init; }

    /// <summary>Client-side tools the model may call.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>Tools the provider runs itself.</summary>
    public IReadOnlyList<HostedToolDefinition> HostedTools { get; init; } = [];

    /// <summary>How the model may use tools. Null leaves the provider's default (auto).</summary>
    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Whether the model may make several tool calls in one turn. Null leaves the provider's default.</summary>
    public bool? ParallelToolCalls { get; init; }

    /// <summary>Structured output. Null asks for plain text.</summary>
    public ResponseFormat? ResponseFormat { get; init; }

    /// <summary>Reasoning settings. Null leaves the model's default.</summary>
    public ReasoningOptions? Reasoning { get; init; }

    /// <summary>Prompt caching settings. Null leaves the provider's default.</summary>
    public CachePolicy? Cache { get; init; }

    /// <summary>The most tokens to generate. Null uses the model's maximum where the provider requires a value, otherwise the provider's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Sampling temperature. Dropped, and logged, for models whose catalog entry says they take none.</summary>
    public double? Temperature { get; init; }

    /// <summary>Nucleus sampling probability mass.</summary>
    public double? TopP { get; init; }

    /// <summary>Sequences that end generation.</summary>
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>Asks for a service tier (<c>flex</c>, <c>priority</c>, ...) where the provider has them.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>An identifier for the end user, passed on for the provider's abuse monitoring.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Parameters written into the request body as given, at its top level, after the ones above. This is the way to use a provider feature this
    /// type has no field for. A key that clashes with one the codec writes replaces it.
    /// </summary>
    public JsonObject? ExtraParameters { get; init; }
}

/// <summary>A chat response in the provider-neutral shape.</summary>
public sealed record ChatResponse
{
    /// <summary>The assistant's turn. Built by <see cref="MessageAccumulator"/>, so streaming and non-streaming calls give equal messages.</summary>
    public Message Message { get; init; } = new();

    /// <summary>The provider's id for the response.</summary>
    public string? Id { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>Why generation stopped.</summary>
    public FinishReason Finish { get; init; }

    /// <summary>The provider's own word for why generation stopped.</summary>
    public string? FinishReasonRaw { get; init; }

    /// <summary>What the call consumed. The same object as <see cref="Messages.Message.Usage"/>.</summary>
    public Usage? Usage => Message.Usage;

    /// <summary>What the call cost, once priced from the catalog. Null until then.</summary>
    public Cost? Cost { get; init; }

    /// <summary>Notices about request parameters the provider stripped or changed for this model (for example a temperature the model ignores). Empty when nothing was.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
