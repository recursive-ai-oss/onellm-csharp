using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

/// <summary>
/// Writes Gemini request bodies (GenerateContentRequest and the bodies of its sibling methods) from the provider-neutral <see cref="ChatRequest"/>.
/// Field order is fixed: system instruction, contents, tools, tool config, generation config, because models read in order and a stable
/// prefix is what makes implicit caching hit.
/// </summary>
internal sealed class GeminiRequestWriter
{
    /// <summary>The prefix of tool call ids made here for calls the API gave no id; such ids are never sent back.</summary>
    public const string SyntheticCallIdPrefix = "gemini-call-";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = false };

    private readonly string _provider;
    private readonly Action<string>? _dropped;
    private readonly Func<string, string> _fileUri;

    public GeminiRequestWriter(string provider, Action<string>? dropped, Func<string, string> fileUri)
    {
        _provider = provider;
        _dropped = dropped;
        _fileUri = fileUri;
    }

    /// <summary>The body of <c>generateContent</c> and <c>streamGenerateContent</c>.</summary>
    public byte[] GenerateContent(ChatRequest request, ModelSpec? spec) =>
        Write(w => Body(w, request, spec, modelResource: null));

    /// <summary>The body of <c>countTokens</c>: the whole generate request, so the system prompt and tools are counted too.</summary>
    public byte[] CountTokens(ChatRequest request, ModelSpec? spec, string modelResource, bool vertex = false)
    {
        if (vertex)
        {
            // Vertex countTokens takes the generation members at the top level and knows only these (Vertex AI REST reference, publishers.models.countTokens).
            JsonObject whole = JsonNode.Parse(Write(w => Body(w, request, spec, modelResource)))!.AsObject();
            var flat = new JsonObject();
            foreach (string key in new[] { "model", "systemInstruction", "contents", "tools", "generationConfig" })
            {
                if (whole.Remove(key, out JsonNode? value))
                {
                    flat[key] = value;
                }
            }

            return System.Text.Encoding.UTF8.GetBytes(flat.ToJsonString());
        }

        return CountTokensWrapped(request, spec, modelResource);
    }

    private byte[] CountTokensWrapped(ChatRequest request, ModelSpec? spec, string modelResource) =>
        Write(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("generateContentRequest");
            Body(w, request, spec, modelResource);
            w.WriteEndObject();
        });

    /// <summary>The body of <c>cachedContents.create</c>: the prefix to cache (system prompt, tools and the messages so far) and its lifetime.</summary>
    public byte[] CachedContent(ChatRequest request, ModelSpec? spec, string modelResource, TimeSpan ttl, string? displayName) =>
        Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("model", modelResource);
            if (displayName is { Length: > 0 })
            {
                w.WriteString("displayName", displayName);
            }

            if (!string.IsNullOrEmpty(request.System))
            {
                SystemInstruction(w, request.System);
            }

            w.WritePropertyName("contents");
            Contents(w, request.Messages);
            Tools(w, request, spec);
            ToolConfig(w, request, spec);
            // A protobuf duration in JSON: seconds with an "s".
            w.WriteString("ttl", FormattableString.Invariant($"{(long)Math.Ceiling(ttl.TotalSeconds)}s"));
            w.WriteEndObject();
        });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            body(w);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void Drop(string what) => _dropped?.Invoke(what);

    private void Body(Utf8JsonWriter w, ChatRequest r, ModelSpec? spec, string? modelResource)
    {
        JsonObject? extra = r.ExtraParameters;
        bool Own(string key) => extra is null || !extra.ContainsKey(key);
        string? cachedName = r.Cache is { Enabled: true, CachedContent: { Length: > 0 } cc } ? cc : null;

        w.WriteStartObject();
        if (modelResource is not null)
        {
            w.WriteString("model", modelResource);
        }

        if (cachedName is not null && Own("cachedContent"))
        {
            w.WriteString("cachedContent", cachedName);
        }

        // With a cached prefix the system prompt, tools and tool config live in the cache; sending them again is an error.
        if (cachedName is null && !string.IsNullOrEmpty(r.System) && Own("systemInstruction"))
        {
            SystemInstruction(w, r.System);
        }

        if (Own("contents"))
        {
            w.WritePropertyName("contents");
            Contents(w, r.Messages);
        }

        if (cachedName is null)
        {
            if (Own("tools"))
            {
                Tools(w, r, spec);
            }

            if (Own("toolConfig"))
            {
                ToolConfig(w, r, spec);
            }
        }

        if (Own("generationConfig"))
        {
            GenerationConfig(w, r, spec);
        }

        if (r.ServiceTier is { Length: > 0 } tier && Own("serviceTier"))
        {
            if (spec?.ServiceTiers is { } tiers && !tiers.ContainsKey(tier))
            {
                Drop($"service tier '{tier}' dropped: the model has no such tier.");
            }
            else
            {
                w.WriteString("serviceTier", tier);
            }
        }

        if (r.UserId is not null)
        {
            Drop("user id dropped: the Gemini API has no end-user identifier on generateContent.");
        }

        if (extra is not null)
        {
            foreach ((string key, JsonNode? value) in extra)
            {
                w.WritePropertyName(key);
                if (value is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    value.WriteTo(w);
                }
            }
        }

        w.WriteEndObject();
    }

    private static void SystemInstruction(Utf8JsonWriter w, string text)
    {
        w.WriteStartObject("systemInstruction");
        w.WriteStartArray("parts");
        w.WriteStartObject();
        w.WriteString("text", text);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ---- contents ----

    private void Contents(Utf8JsonWriter w, IReadOnlyList<Message> messages)
    {
        w.WriteStartArray();
        foreach (Message m in messages)
        {
            if (m.User.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteStartArray("parts");
                foreach (Part p in m.User)
                {
                    UserPart(w, p);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            if (m.Assistant.Count > 0 || m.ToolCalls.Count > 0)
            {
                ModelTurn(w, m);
                FunctionResponses(w, m);
            }
        }

        w.WriteEndArray();
    }

    private void UserPart(Utf8JsonWriter w, Part p)
    {
        switch (p)
        {
            case TextPart t:
                if (t.Text.Length > 0)
                {
                    w.WriteStartObject();
                    w.WriteString("text", t.Text);
                    w.WriteEndObject();
                }

                break;
            case AssetPart a:
                Asset(w, a, null);
                break;
            case FileRefPart f:
                FileRef(w, f);
                break;
            case JsonPart j:
                w.WriteStartObject();
                w.WriteString("text", j.Value.ToJsonString());
                w.WriteEndObject();
                break;
            case ProviderPart pp when pp.Provider == _provider:
                pp.Raw.WriteTo(w);
                break;
            default:
                // Reasoning, hosted tool calls and refusals only exist in assistant turns.
                break;
        }
    }

    private void Asset(Utf8JsonWriter w, AssetPart a, string? displayName, PendingSignature? sig = null)
    {
        AssetRef? asset = a.ResizedImage?.Asset ?? a.Asset;
        string? publicUrl = a.ResizedImage?.PublicUrl ?? a.PublicUrl;
        w.WriteStartObject();
        if (asset is { Data: { Length: > 0 } data })
        {
            w.WriteStartObject("inlineData");
            w.WriteString("mimeType", asset.ContentType);
            w.WriteString("data", data);
            if (displayName is not null)
            {
                w.WriteString("displayName", displayName);
            }

            w.WriteEndObject();
        }
        else if (publicUrl is { Length: > 0 })
        {
            w.WriteStartObject("fileData");
            w.WriteString("mimeType", asset?.ContentType ?? "");
            w.WriteString("fileUri", publicUrl);
            if (displayName is not null)
            {
                w.WriteString("displayName", displayName);
            }

            w.WriteEndObject();
        }
        else
        {
            throw new BadRequestException(_provider, "an asset has no bytes and no public URL to send. Load the asset's bytes (or upload it with UploadFileAsync and send the file reference) before sending.", null);
        }

        sig?.AttachIfAny(w, "inlineData");
        w.WriteEndObject();
    }

    private void FileRef(Utf8JsonWriter w, FileRefPart f)
    {
        if (f.Provider != _provider)
        {
            throw new BadRequestException(_provider, $"a file uploaded to '{f.Provider}' cannot be used with this provider.", null);
        }

        w.WriteStartObject();
        w.WriteStartObject("fileData");
        if (f.ContentType is { Length: > 0 } mime)
        {
            w.WriteString("mimeType", mime);
        }

        w.WriteString("fileUri", f.Uri ?? _fileUri(f.FileId));
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private void ModelTurn(Utf8JsonWriter w, Message m)
    {
        w.WriteStartObject();
        w.WriteString("role", "model");
        w.WriteStartArray("parts");

        // Calls go where the model made them: after the stated number of parts, in the order given.
        ToolCall[] calls = [.. m.ToolCalls.Select((c, i) => (c, i)).OrderBy(x => x.c.After).ThenBy(x => x.i).Select(x => x.c)];
        int next = 0;
        var signature = new PendingSignature();
        for (int i = 0; i < m.Assistant.Count; i++)
        {
            while (next < calls.Length && calls[next].After <= i)
            {
                FunctionCall(w, calls[next++], signature);
            }

            AssistantPart(w, m.Assistant[i], signature);
        }

        while (next < calls.Length)
        {
            FunctionCall(w, calls[next++], signature);
        }

        signature.FlushStandalone(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // A thought signature is replayed on the part it came with. It is kept as a reasoning part just before that part, so it waits here for the next part.
    private sealed class PendingSignature
    {
        public string? Value { get; set; }

        public string? On { get; set; }

        public void Set(string value, string on) => (Value, On) = (value, on);

        // Call after FlushIfMismatch: a waiting signature then belongs to this part.
        public void AttachIfAny(Utf8JsonWriter w, string hostKind)
        {
            if (Value is not null && On == hostKind)
            {
                w.WriteString("thoughtSignature", Value);
                Value = null;
                On = null;
            }
        }

        public void FlushIfMismatch(string hostKind, Utf8JsonWriter w)
        {
            if (Value is not null && On != hostKind)
            {
                FlushStandalone(w);
            }
        }

        // A signature whose host part is gone (it was empty, or the history was edited) travels on an empty text part, which the API accepts.
        public void FlushStandalone(Utf8JsonWriter w)
        {
            if (Value is null)
            {
                return;
            }

            w.WriteStartObject();
            w.WriteString("text", "");
            w.WriteString("thoughtSignature", Value);
            w.WriteEndObject();
            Value = null;
            On = null;
        }
    }

    private void AssistantPart(Utf8JsonWriter w, Part p, PendingSignature sig)
    {
        switch (p)
        {
            case TextPart t:
                if (t.Text.Length == 0)
                {
                    break;
                }

                sig.FlushIfMismatch("text", w);
                w.WriteStartObject();
                w.WriteString("text", t.Text);
                sig.AttachIfAny(w, "text");
                w.WriteEndObject();
                break;
            case ReasoningPart r when r.Provider == _provider:
                Reasoning(w, r, sig);
                break;
            case AssetPart a:
                sig.FlushIfMismatch("inlineData", w);
                Asset(w, a, null, sig);
                break;
            case FileRefPart f:
                FileRef(w, f);
                break;
            case JsonPart j:
                w.WriteStartObject();
                w.WriteString("text", j.Value.ToJsonString());
                w.WriteEndObject();
                break;
            case HostedToolCallPart { Data: { } data } h when h.Provider == _provider && data["replay"] is JsonArray replay:
                foreach (JsonNode? raw in replay)
                {
                    raw?.WriteTo(w);
                }

                break;
            case ProviderPart pp when pp.Provider == _provider:
                pp.Raw.WriteTo(w);
                break;
            default:
                // Other providers' reasoning, refusals, and hosted calls made up from grounding metadata are not part of what Gemini sent.
                break;
        }
    }

    private static void Reasoning(Utf8JsonWriter w, ReasoningPart r, PendingSignature sig)
    {
        string? signature = r.Data?["signature"]?.GetValue<string>();
        string on = r.Data?["on"]?.GetValue<string>() ?? "thought";
        if (on == "thought")
        {
            // A thought summary, replayed as received. With no summary text only the signature remains.
            sig.FlushStandalone(w);
            if (string.IsNullOrEmpty(r.Display))
            {
                if (signature is not null)
                {
                    sig.Set(signature, "none");
                }

                return;
            }

            w.WriteStartObject();
            w.WriteString("text", r.Display);
            w.WriteBoolean("thought", true);
            if (signature is not null)
            {
                w.WriteString("thoughtSignature", signature);
            }

            w.WriteEndObject();
            return;
        }

        sig.FlushStandalone(w);
        if (signature is not null)
        {
            sig.Set(signature, on);
        }
    }

    private static void FunctionCall(Utf8JsonWriter w, ToolCall c, PendingSignature sig)
    {
        sig.FlushIfMismatch("functionCall", w);
        w.WriteStartObject();
        w.WriteStartObject("functionCall");
        if (!c.Id.StartsWith(SyntheticCallIdPrefix, StringComparison.Ordinal) && c.Id.Length > 0)
        {
            w.WriteString("id", c.Id);
        }

        w.WriteString("name", c.Tool);
        w.WritePropertyName("args");
        if (c.Inputs is not null)
        {
            c.Inputs.WriteTo(w);
        }
        else
        {
            w.WriteStartObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        sig.AttachIfAny(w, "functionCall");
        w.WriteEndObject();
    }

    // Results of the turn's calls travel together in one user turn, in call order.
    private void FunctionResponses(Utf8JsonWriter w, Message m)
    {
        ToolCall[] done = [.. m.ToolCalls.Where(c => c.Result.Count > 0 || c.IsError)];
        if (done.Length == 0)
        {
            return;
        }

        w.WriteStartObject();
        w.WriteString("role", "user");
        w.WriteStartArray("parts");
        foreach (ToolCall c in done)
        {
            w.WriteStartObject();
            w.WriteStartObject("functionResponse");
            if (!c.Id.StartsWith(SyntheticCallIdPrefix, StringComparison.Ordinal) && c.Id.Length > 0)
            {
                w.WriteString("id", c.Id);
            }

            w.WriteString("name", c.Tool);
            w.WritePropertyName("response");
            AssetPart[] media = [.. c.Result.OfType<AssetPart>()];
            ResponseObject(w, c, media);
            if (media.Length > 0)
            {
                w.WriteStartArray("parts");
                for (int i = 0; i < media.Length; i++)
                {
                    Asset(w, media[i], MediaName(i));
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string MediaName(int i) => FormattableString.Invariant($"result-{i}");

    // functionResponse.response must be an object. A single JSON object result is sent as is; text goes under "result", failures under "error"
    // (the keys the function calling guide uses), and media is referenced by display name with {"$ref": name}.
    private static void ResponseObject(Utf8JsonWriter w, ToolCall c, AssetPart[] media)
    {
        if (!c.IsError && media.Length == 0 && c.Result.Count == 1 && c.Result[0] is JsonPart { Value: JsonObject only })
        {
            only.WriteTo(w);
            return;
        }

        var text = new System.Text.StringBuilder();
        foreach (Part p in c.Result)
        {
            switch (p)
            {
                case TextPart t:
                    text.Append(t.Text);
                    break;
                case JsonPart j:
                    text.Append(j.Value.ToJsonString());
                    break;
            }
        }

        w.WriteStartObject();
        w.WriteString(c.IsError ? "error" : "result", text.ToString());
        if (media.Length > 0)
        {
            w.WriteStartArray("media");
            for (int i = 0; i < media.Length; i++)
            {
                w.WriteStartObject();
                w.WriteString("$ref", MediaName(i));
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    // ---- tools ----

    private void Tools(Utf8JsonWriter w, ChatRequest r, ModelSpec? spec)
    {
        if (r.Tools.Count == 0 && r.HostedTools.Count == 0)
        {
            return;
        }

        if (spec?.SupportsTools == false)
        {
            throw new BadRequestException(_provider, $"the model '{spec.Name}' does not support tools.", null);
        }

        w.WriteStartArray("tools");
        if (r.Tools.Count > 0)
        {
            w.WriteStartObject();
            w.WriteStartArray("functionDeclarations");
            foreach (ToolDefinition t in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                // parametersJsonSchema takes the JSON Schema as written, so no conversion to the OpenAPI subset is needed.
                w.WritePropertyName("parametersJsonSchema");
                t.InputSchema.WriteTo(w);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        foreach (HostedToolDefinition h in r.HostedTools)
        {
            w.WriteStartObject();
            w.WritePropertyName(HostedToolKey(h.Type));
            if (h.Options is null)
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
            else
            {
                h.Options.WriteTo(w);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>The request field that switches on a hosted tool. A name that is already a wire name is used as it is.</summary>
    internal static string HostedToolKey(string type) => type switch
    {
        "google_search" or "web_search" => "googleSearch",
        "google_search_retrieval" => "googleSearchRetrieval",
        "code_execution" => "codeExecution",
        "url_context" => "urlContext",
        "google_maps" => "googleMaps",
        "file_search" => "fileSearch",
        "computer_use" => "computerUse",
        _ => type,
    };

    private void ToolConfig(Utf8JsonWriter w, ChatRequest r, ModelSpec? spec)
    {
        bool hasFunctions = r.Tools.Count > 0;
        if (r.ParallelToolCalls == false && hasFunctions)
        {
            Drop("parallelToolCalls=false dropped: Gemini has no switch for parallel function calls.");
        }

        ToolChoice? choice = r.ToolChoice;
        if (choice is null || choice.Mode == ToolChoiceMode.Auto)
        {
            return;
        }

        if (choice.Mode == ToolChoiceMode.None)
        {
            if (hasFunctions)
            {
                WriteFunctionCallingConfig(w, "NONE", null);
            }

            return;
        }

        // Forcing a call changes what the request means, so a request that cannot honour it is refused instead of quietly weakened.
        if (!hasFunctions || spec?.SupportsTools == false)
        {
            throw new BadRequestException(_provider, "a forced tool choice needs function tools on a model that supports them.", null);
        }

        if (choice.Mode == ToolChoiceMode.Specific && r.Tools.All(t => t.Name != choice.ToolName))
        {
            throw new BadRequestException(_provider, $"the forced tool '{choice.ToolName}' is not among the tools sent.", null);
        }

        WriteFunctionCallingConfig(w, "ANY", choice.Mode == ToolChoiceMode.Specific ? choice.ToolName : null);
    }

    private static void WriteFunctionCallingConfig(Utf8JsonWriter w, string mode, string? allowed)
    {
        w.WriteStartObject("toolConfig");
        w.WriteStartObject("functionCallingConfig");
        w.WriteString("mode", mode);
        if (allowed is not null)
        {
            w.WriteStartArray("allowedFunctionNames");
            w.WriteStringValue(allowed);
            w.WriteEndArray();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    // ---- generation config ----

    private void GenerationConfig(Utf8JsonWriter w, ChatRequest r, ModelSpec? spec)
    {
        bool takesSampling = spec?.SupportsTemperature != false;
        if (!takesSampling && (r.Temperature is not null || r.TopP is not null))
        {
            Drop($"temperature and topP dropped: the model '{spec!.Name}' ignores sampling parameters.");
        }

        Thinking(r.Reasoning, spec, out string? level, out int? budget, out bool includeThoughts);
        bool structured = r.ResponseFormat is { Kind: not ResponseFormatKind.Text };
        bool any = r.StopSequences.Count > 0 || r.MaxOutputTokens is not null || (takesSampling && (r.Temperature is not null || r.TopP is not null))
            || structured || level is not null || budget is not null || includeThoughts;
        if (!any)
        {
            return;
        }

        w.WriteStartObject("generationConfig");
        if (r.StopSequences.Count > 0)
        {
            w.WriteStartArray("stopSequences");
            foreach (string s in r.StopSequences)
            {
                w.WriteStringValue(s);
            }

            w.WriteEndArray();
        }

        if (r.MaxOutputTokens is { } max)
        {
            w.WriteNumber("maxOutputTokens", max);
        }

        if (takesSampling && r.Temperature is { } t)
        {
            w.WriteNumber("temperature", t);
        }

        if (takesSampling && r.TopP is { } p)
        {
            w.WriteNumber("topP", p);
        }

        if (structured)
        {
            ResponseFormat f = r.ResponseFormat!;
            w.WriteString("responseMimeType", "application/json");
            if (f.Kind == ResponseFormatKind.JsonSchema && f.Schema is { } schema)
            {
                if (spec is not null && spec.SupportsStructuredOutput == false)
                {
                    throw new BadRequestException(_provider, $"the model '{spec.Name}' does not support structured output.", null);
                }

                if (SchemaForm(spec) == "responseSchema")
                {
                    w.WritePropertyName("responseSchema");
                    GeminiSchema.ToOpenApiSubset(schema).WriteTo(w);
                }
                else
                {
                    w.WritePropertyName("responseJsonSchema");
                    schema.WriteTo(w);
                }
            }
        }

        if (level is not null || budget is not null || includeThoughts)
        {
            w.WriteStartObject("thinkingConfig");
            if (includeThoughts)
            {
                w.WriteBoolean("includeThoughts", true);
            }

            if (budget is { } b)
            {
                w.WriteNumber("thinkingBudget", b);
            }
            else if (level is not null)
            {
                w.WriteString("thinkingLevel", level);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    /// <summary>Which structured output field a model takes: <c>responseJsonSchema</c> (default) or the OpenAPI-subset <c>responseSchema</c>, by the model's <c>schemaForm</c> property.</summary>
    internal static string SchemaForm(ModelSpec? spec) =>
        spec?.Properties is { } props && props.TryGetValue("schemaForm", out object? v) && v is string s && s == "responseSchema" ? "responseSchema" : "responseJsonSchema";

    // Works out the thinking settings the model accepts. A model with no thinking entry takes none; a level the model lacks becomes a budget where
    // the model takes budgets, otherwise the setting is dropped and reported.
    private void Thinking(ReasoningOptions? options, ModelSpec? spec, out string? level, out int? budget, out bool includeThoughts)
    {
        level = null;
        budget = null;
        includeThoughts = false;
        if (options is null)
        {
            return;
        }

        ThinkingSpec? thinking = spec?.Thinking;
        if (spec is not null && thinking is null)
        {
            Drop($"reasoning settings dropped: the model '{spec.Name}' does not think.");
            return;
        }

        includeThoughts = options.Summary is { Length: > 0 };
        if (options.BudgetTokens is { } requested)
        {
            if (thinking is null || thinking.AcceptsBudget == true)
            {
                // A budget of 0 (off) and -1 (the model decides) are values of their own; only real budgets are held to the model's range.
                budget = requested <= 0 ? requested : Math.Clamp(requested, thinking?.MinBudget ?? int.MinValue, thinking?.MaxBudget ?? int.MaxValue);
            }
            else
            {
                Drop($"thinking budget dropped: the model '{spec!.Name}' takes levels, not budgets.");
            }
        }
        else if (options.Effort is { Length: > 0 } effort)
        {
            if (thinking?.AcceptsBudget == true)
            {
                // Budget models take no level: the effort stands for a budget (disabled = 0, dynamic = -1, the others from the catalog).
                if (effort.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    budget = 0;
                }
                else if (effort.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
                {
                    budget = -1;
                }
                else if (thinking.DefaultEffortBudgets is { } budgets && budgets.TryGetValue(effort.ToLowerInvariant(), out int mapped))
                {
                    budget = mapped;
                }
                else
                {
                    Drop($"thinking effort '{effort}' dropped: the model '{spec!.Name}' has no budget for it.");
                }
            }
            else if (thinking?.Efforts is null || thinking.Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            {
                level = effort.ToUpperInvariant();
            }
            else
            {
                Drop($"thinking effort '{effort}' dropped: the model '{spec!.Name}' accepts {string.Join(", ", thinking.Efforts)}.");
            }
        }
    }
}
