using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Records;
using OneLlm.Runners;

namespace OneLlm;

/// <summary>
/// The happy path: verbs that take a model name and what to ask, and return the answer. Each call is one line over the
/// <see cref="Runtime"/> in <see cref="Default"/>, and runs as an instance with cost, records and telemetry like any other run.
/// Models are catalog names or aliases (<c>claude-sonnet</c>), or <c>provider:model</c> to name the provider.
/// </summary>
/// <remarks>
/// API keys come from the environment variables the catalog names (<c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>,
/// <c>GEMINI_API_KEY</c>, ...). Set <c>ONELLM_INSTANCES</c> to a directory to keep instances on disk and <c>ONELLM_RECORDS</c> to a
/// directory for JSONL records; by default instances live in memory. Set <c>ONELLM_CATALOG</c> to add catalog files. For anything
/// else, build a <see cref="Runtime"/>.
/// </remarks>
public static class Llm
{
    private static readonly object s_gate = new();
    private static Runtime? s_default;

    /// <summary>
    /// The runtime the statics use. Created on first use from the environment and the catalog. Assign your own to configure every
    /// static call at once, or to replace its providers in tests (register a scripted provider on its
    /// <see cref="Runtime.Providers"/>); tests that do so must not run in parallel with others that use the statics.
    /// </summary>
    public static Runtime Default
    {
        get
        {
            lock (s_gate)
            {
                return s_default ??= CreateDefault();
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (s_gate)
            {
                s_default = value;
            }
        }
    }

    /// <summary>Asks a model and returns its text.</summary>
    /// <param name="model">The model: a catalog name, alias or <c>provider:model</c>.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer's text.</returns>
    /// <exception cref="ModelNotFoundException">The catalog does not know the model.</exception>
    /// <exception cref="AuthException">The provider's API key variable is not set, or the key is refused.</exception>
    /// <exception cref="ProviderException">The provider failed after retries.</exception>
    public static async Task<string> AskAsync(string model, string prompt, CancellationToken cancellationToken = default) =>
        (await GenerateAsync(model, prompt, [], cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Asks a model about an asset (an image, a PDF) and returns its text.</summary>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="attachment">The asset, from <see cref="Asset"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer's text.</returns>
    public static async Task<string> AskAsync(string model, string prompt, Part attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return (await GenerateAsync(model, prompt, [attachment], cancellationToken).ConfigureAwait(false)).Text;
    }

    /// <summary>Asks a model and returns the whole result: text, usage, cost, warnings and the instance.</summary>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="attachments">Assets sent with the question; empty for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public static Task<AskResult> GenerateAsync(string model, string prompt, IReadOnlyList<Part> attachments, CancellationToken cancellationToken = default) =>
        Default.AskAsync(new AskRequest { Model = model, Prompt = prompt, Attachments = attachments }, cancellationToken);

    /// <summary>Asks a model for JSON that fits a type and returns the value. The schema is sent through the provider's native structured output.</summary>
    /// <typeparam name="T">The result type; it must serialize as a JSON object.</typeparam>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/> from a source-generated context (trim and AOT safe).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value.</returns>
    /// <exception cref="StructuredOutputException">The answer is not valid JSON for the type.</exception>
    /// <exception cref="RefusedException">The model refused.</exception>
    public static async Task<T> AskAsync<T>(string model, string prompt, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await GenerateAsync(model, prompt, [], typeInfo, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Asks a model about an asset for JSON that fits a type.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="attachment">The asset, from <see cref="Asset"/>.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value.</returns>
    public static async Task<T> AskAsync<T>(string model, string prompt, Part attachment, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return (await GenerateAsync(model, prompt, [attachment], typeInfo, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Asks for JSON that fits a type, finding the type's serialization metadata by reflection. Prefer the overload that takes a
    /// <see cref="JsonTypeInfo{T}"/>, which is trim and AOT safe.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode("Reads the type by reflection. Pass a JsonTypeInfo from a source-generated context instead.")]
    [RequiresDynamicCode("Reads the type by reflection. Pass a JsonTypeInfo from a source-generated context instead.")]
    public static async Task<T> AskAsync<T>(string model, string prompt, CancellationToken cancellationToken = default) =>
        (await GenerateAsync(model, prompt, [], ReflectionTypeInfo<T>(), cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Asks for JSON that fits a type and returns the whole result with the value, usage, cost and instance.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="attachments">Assets sent with the question; empty for none.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public static Task<AskResult<T>> GenerateAsync<T>(string model, string prompt, IReadOnlyList<Part> attachments, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        Default.AskAsync(new AskRequest { Model = model, Prompt = prompt, Attachments = attachments }, typeInfo, cancellationToken);

    /// <summary>Asks a model and returns the answer as events as it arrives. Enumerate to the end, then read <see cref="StreamingAnswer.Result"/> for usage and cost.</summary>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="attachments">Assets sent with the question; null for none.</param>
    /// <returns>The stream. Nothing is sent until it is enumerated.</returns>
    public static StreamingAnswer StreamAsync(string model, string prompt, IReadOnlyList<Part>? attachments = null) =>
        Default.StreamAsync(new AskRequest { Model = model, Prompt = prompt, Attachments = attachments ?? [] });

    /// <summary>Asks a model and returns just the pieces of its text as they arrive.</summary>
    /// <param name="model">The model.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="cancellationToken">Cancels the stream; what arrived is kept in the instance.</param>
    /// <returns>The text pieces, in order.</returns>
    public static IAsyncEnumerable<string> StreamTextAsync(string model, string prompt, CancellationToken cancellationToken = default) =>
        StreamAsync(model, prompt).TextAsync(cancellationToken);

    /// <summary>Embeds a text.</summary>
    /// <param name="model">An embedding model.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The vector.</returns>
    public static async Task<float[]> EmbedAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        EmbedResult result = await Default.EmbedAsync(model, [text], null, cancellationToken).ConfigureAwait(false);
        return result.Embeddings[0].ToArray();
    }

    /// <summary>Embeds several texts in one call.</summary>
    /// <param name="model">An embedding model.</param>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One vector per text, in order.</returns>
    public static async Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        EmbedResult result = await Default.EmbedAsync(model, texts, null, cancellationToken).ConfigureAwait(false);
        return [.. result.Embeddings.Select(static v => v.ToArray())];
    }

    /// <summary>Starts a conversation. Each <c>AskAsync</c> on the chat adds a turn to one instance.</summary>
    /// <param name="model">The model.</param>
    /// <param name="system">A system prompt, sent as written.</param>
    /// <returns>The chat.</returns>
    public static ChatSession Chat(string model, string? system = null) =>
        Default.Chat(new AskRequest { Model = model, System = system });

    /// <summary>Runs a <c>.flow</c> or agent definition as a script: its prompts are rendered with the inputs and sent in order, one model call per user prompt.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c>; plain values only (strings, numbers, booleans, lists, dictionaries, <see cref="JsonElement"/>).</param>
    /// <param name="cancellationToken">Cancels the run; the instance keeps the turns finished so far.</param>
    /// <returns>The last answer, every turn, cost and the instance.</returns>
    public static Task<ScriptResult> RunScriptAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default) =>
        Default.RunScriptAsync(path, inputs, cancellationToken);

    /// <summary>Runs a definition as a script with inputs from a typed value, read through its serialization metadata (trim and AOT safe).</summary>
    /// <typeparam name="TInputs">The inputs type.</typeparam>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="TInputs"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The script's result.</returns>
    public static Task<ScriptResult> RunScriptAsync<TInputs>(string path, TInputs inputs, JsonTypeInfo<TInputs> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        JsonElement element = JsonSerializer.SerializeToElement(inputs, typeInfo);
        return Default.RunScriptAsync(path, ToDictionary(element), cancellationToken);
    }

    /// <summary>Runs a definition as a script with inputs from any object, such as an anonymous object, read by reflection.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">The inputs; its public properties become <c>.inputs</c>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The script's result.</returns>
    [RequiresUnreferencedCode("Reads the object's properties by reflection. Pass a dictionary, or a value with a JsonTypeInfo, instead.")]
    [RequiresDynamicCode("Reads the object's properties by reflection. Pass a dictionary, or a value with a JsonTypeInfo, instead.")]
    public static Task<ScriptResult> RunScriptAsync(string path, object inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        JsonElement element = JsonSerializer.SerializeToElement(inputs, inputs.GetType(), ReflectionOptions());
        return Default.RunScriptAsync(path, ToDictionary(element), cancellationToken);
    }

    /// <summary>
    /// Runs an agent definition to the end of its run: the prompts are rendered, then the model and its tools loop until a loop-ending tool
    /// such as <c>task_complete</c> runs, the model answers without a tool, a call needs approval, or <c>maxIterations</c> is reached.
    /// The tools the definition references must be registered on <see cref="Runtime.Tools"/>; nothing is granted by the definition.
    /// </summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c>; plain values only.</param>
    /// <param name="cancellationToken">Cancels the run; the instance is kept as <c>stopped</c> with <c>cancelled: true</c> and its partial output.</param>
    /// <returns>The answer, usage, cost and the instance. <see cref="Agents.AgentResult.StopReason"/> says why it stopped.</returns>
    /// <exception cref="BadDefinitionException">The file is invalid or references a tool the runtime does not offer.</exception>
    /// <exception cref="RunCancelledException">The token was cancelled.</exception>
    public static Task<Agents.AgentResult> RunAgentAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default) =>
        Default.RunAgentAsync(path, inputs, null, cancellationToken);

    /// <summary>Runs an agent definition with inputs from a typed value, read through its serialization metadata (trim and AOT safe).</summary>
    /// <typeparam name="TInputs">The inputs type.</typeparam>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="TInputs"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The agent's result.</returns>
    public static Task<Agents.AgentResult> RunAgentAsync<TInputs>(string path, TInputs inputs, JsonTypeInfo<TInputs> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        JsonElement element = JsonSerializer.SerializeToElement(inputs, typeInfo);
        return Default.RunAgentAsync(path, ToDictionary(element), null, cancellationToken);
    }

    /// <summary>Starts an agent and returns at once with a handle: follow its events, send it messages, approve or deny held tool calls, and await its result.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c>; plain values only.</param>
    /// <param name="cancellationToken">Cancels the start, not the running agent; use <see cref="Agents.AgentRun.CancelAsync"/> for that.</param>
    /// <returns>The running agent.</returns>
    public static Task<Agents.AgentRun> StartAgentAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default) =>
        Default.StartAgentAsync(path, inputs, null, cancellationToken);

    /// <summary>Starts an agent with inputs from a typed value, read through its serialization metadata (trim and AOT safe).</summary>
    /// <typeparam name="TInputs">The inputs type.</typeparam>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="TInputs"/>.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running agent.</returns>
    public static Task<Agents.AgentRun> StartAgentAsync<TInputs>(string path, TInputs inputs, JsonTypeInfo<TInputs> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        JsonElement element = JsonSerializer.SerializeToElement(inputs, typeInfo);
        return Default.StartAgentAsync(path, ToDictionary(element), null, cancellationToken);
    }

    private static JsonSerializerOptions? s_reflectionOptions;

    [RequiresUnreferencedCode("Reads types by reflection.")]
    [RequiresDynamicCode("Reads types by reflection.")]
    private static JsonSerializerOptions ReflectionOptions() => s_reflectionOptions ??= new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [RequiresUnreferencedCode("Reads the type by reflection.")]
    [RequiresDynamicCode("Reads the type by reflection.")]
    private static JsonTypeInfo<T> ReflectionTypeInfo<T>() => (JsonTypeInfo<T>)ReflectionOptions().GetTypeInfo(typeof(T));

    private static Dictionary<string, object?> ToDictionary(JsonElement element) =>
        PlainValues.FromElement(element) as Dictionary<string, object?>
            ?? throw new ArgumentException("The inputs must serialize as a JSON object.", nameof(element));

    private static Runtime CreateDefault()
    {
        var runtime = new Runtime();
        if (runtime.GetEnvironmentVariable("ONELLM_INSTANCES") is { Length: > 0 } instances)
        {
            runtime.Store = new FileInstanceStore(instances);
        }

        if (runtime.GetEnvironmentVariable("ONELLM_RECORDS") is { Length: > 0 } records)
        {
            runtime.Sinks.Add(new JsonlRecordSink(records));
        }

        return runtime;
    }
}
