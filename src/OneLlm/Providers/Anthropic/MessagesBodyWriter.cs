using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.Anthropic;

/// <summary>Which request body <see cref="MessagesBodyWriter"/> writes. They share a shape and differ in a few members.</summary>
internal enum BodyMode
{
    /// <summary>A message request, answered whole.</summary>
    Message,

    /// <summary>A message request with <c>"stream": true</c>.</summary>
    Stream,

    /// <summary>A token count request: no output limit, sampling or stop settings (API reference, Count tokens).</summary>
    Count,

    /// <summary>The <c>params</c> of one batch request: a message request without the <c>stream</c> member.</summary>
    BatchItem,
}

/// <summary>How the bytes of one asset reach the request.</summary>
internal enum AssetRoute
{
    /// <summary>The asset carries its bytes as base64.</summary>
    Inline,

    /// <summary>A public URL the API fetches.</summary>
    Url,

    /// <summary>The bytes come from <see cref="AnthropicOptions.AssetResolver"/>.</summary>
    Resolve,
}

/// <summary>
/// Writes a <see cref="ChatRequest"/> as a Messages API request body (API reference: Create a message). The JSON is written directly with
/// <see cref="Utf8JsonWriter"/>, so key order is fixed and nothing depends on reflection.
/// </summary>
/// <remarks>
/// Turns become API messages like this: a user message is its parts as content blocks; an assistant message is its parts and tool calls, in
/// the order the model produced them, followed by a user message holding one <c>tool_result</c> block per tool call that has a result
/// (tool results must come first in that user message and directly after the tool use, tool use guide). Neighbouring messages of the same
/// role are joined, as the API does anyway.
/// </remarks>
internal sealed class MessagesBodyWriter
{
    private const string User = "user";
    private const string Assistant = "assistant";

    private readonly ChatRequest _request;
    private readonly string _model;
    private readonly ModelSpec? _spec;
    private readonly BodyMode _mode;
    private readonly AnthropicOptions _options;
    private readonly TimeProvider _time;
    private readonly List<string>? _warnings;
    private readonly IReadOnlyDictionary<AssetRef, ReadOnlyMemory<byte>> _assets;

    public MessagesBodyWriter(
        ChatRequest request,
        string model,
        ModelSpec? spec,
        BodyMode mode,
        AnthropicOptions options,
        TimeProvider time,
        IReadOnlyDictionary<AssetRef, ReadOnlyMemory<byte>> assets,
        List<string>? warnings = null)
    {
        _warnings = warnings;
        _request = request;
        _model = model;
        _spec = spec;
        _mode = mode;
        _options = options;
        _time = time;
        _assets = assets;
    }

    private enum K
    {
        Text,
        Asset,
        FileRef,
        Thinking,
        ToolUse,
        ToolResult,
        Hosted,
        Raw,
    }

    private sealed class OutBlock(K kind)
    {
        public K Kind { get; } = kind;

        public string? Text { get; init; }

        public IReadOnlyList<Annotation>? Citations { get; init; }

        public Part? Part { get; init; }

        public ToolCall? Call { get; init; }

        public JsonNode? Raw { get; init; }

        public bool Cache { get; set; }

        // Thinking blocks cannot carry cache_control (prompt caching guide); they are cached as part of the content around them.
        public bool CanCache => Kind != K.Thinking;
    }

    private sealed class OutMessage(string role)
    {
        public string Role { get; } = role;

        public List<OutBlock> Blocks { get; } = [];
    }

    private sealed record Thinking(string? Type, int Budget, string? Display, string? Effort);

    /// <summary>Which route an asset takes, and the asset (or its resized copy) that supplies it. Shared with the asset resolution pass.</summary>
    public static (AssetRoute Route, AssetRef Target, string? Url) Route(AssetPart part, TimeProvider time)
    {
        string ct = part.Asset?.ContentType ?? "";
        if (part.ResizedImage is { } resized && IsImage(ct))
        {
            if (resized.Asset is { } ra && !string.IsNullOrEmpty(ra.Data))
            {
                return (AssetRoute.Inline, ra, null);
            }

            if (resized.PublicUrl is { Length: > 0 } rurl && (resized.PublicUrlExpires is not { } rexp || rexp > time.GetUtcNow()))
            {
                return (AssetRoute.Url, resized.Asset ?? part.Asset ?? new AssetRef(), rurl);
            }

            if (resized.Asset is { } rs)
            {
                return (AssetRoute.Resolve, rs, null);
            }
        }

        AssetRef? asset = part.Asset;
        if (asset is not null && !string.IsNullOrEmpty(asset.Data))
        {
            return (AssetRoute.Inline, asset, null);
        }

        if (part.PublicUrl is { Length: > 0 } url && (part.PublicUrlExpires is not { } exp || exp > time.GetUtcNow()))
        {
            return (AssetRoute.Url, asset ?? new AssetRef(), url);
        }

        if (asset is not null)
        {
            return (AssetRoute.Resolve, asset, null);
        }

        throw new BadRequestException(AnthropicErrors.Provider, "an asset part carries no file, so it cannot be sent (the request was not sent).");
    }

    /// <summary>Writes the request body as one JSON object.</summary>
    public void Write(Utf8JsonWriter w)
    {
        ChatRequest r = _request;
        JsonObject? extra = r.ExtraParameters;
        List<OutMessage> messages = BuildMessages();
        (bool topLevelCache, string? ttl) = ApplyCachePolicy(messages);
        int maxTokens = ResolveMaxTokens();
        Thinking? thinking = ResolveThinking(maxTokens);
        string? effort = ResolveEffort();

        w.WriteStartObject();
        w.WriteString("model", _model);
        if (_mode != BodyMode.Count && !Has(extra, "max_tokens"))
        {
            w.WriteNumber("max_tokens", maxTokens);
        }

        if (!string.IsNullOrEmpty(r.System) && !Has(extra, "system"))
        {
            w.WriteString("system", r.System);
        }

        if (!Has(extra, "messages"))
        {
            w.WriteStartArray("messages");
            foreach (OutMessage m in messages)
            {
                WriteMessage(w, m, ttl);
            }

            w.WriteEndArray();
        }

        bool hasTools = r.Tools.Count > 0 || r.HostedTools.Count > 0;
        if (hasTools && !Has(extra, "tools"))
        {
            w.WriteStartArray("tools");
            foreach (HostedToolDefinition t in r.HostedTools)
            {
                WriteHostedTool(w, t);
            }

            foreach (ToolDefinition t in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("input_schema");
                t.InputSchema.WriteTo(w);
                if (t.Strict)
                {
                    w.WriteBoolean("strict", true);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (hasTools && !Has(extra, "tool_choice"))
        {
            WriteToolChoice(w, thinking);
        }

        if (thinking is not null && !Has(extra, "thinking"))
        {
            w.WriteStartObject("thinking");
            w.WriteString("type", thinking.Type);
            if (thinking.Type == "enabled")
            {
                w.WriteNumber("budget_tokens", thinking.Budget);
            }

            if (thinking.Display is not null)
            {
                w.WriteString("display", thinking.Display);
            }

            w.WriteEndObject();
        }

        WriteOutputConfig(w, effort, extra);

        if (topLevelCache && !Has(extra, "cache_control"))
        {
            w.WriteStartObject("cache_control");
            w.WriteString("type", "ephemeral");
            if (ttl is not null)
            {
                w.WriteString("ttl", ttl);
            }

            w.WriteEndObject();
        }

        if (_mode != BodyMode.Count)
        {
            WriteSampling(w, extra);
        }

        string? serviceTier = r.ServiceTier;
        if (serviceTier is not null)
        {
            WriteServiceTier(w, serviceTier, extra);
        }

        if (_mode == BodyMode.Stream && !Has(extra, "stream"))
        {
            w.WriteBoolean("stream", true);
        }

        if (extra is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> p in extra)
            {
                w.WritePropertyName(p.Key);
                if (p.Value is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    p.Value.WriteTo(w);
                }
            }
        }

        w.WriteEndObject();
    }

    private static bool Has(JsonObject? extra, string key) => extra is not null && extra.ContainsKey(key);

    private static bool IsImage(string contentType) => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private void Log(string message) => _warnings?.Add(message);

    private int ResolveMaxTokens()
    {
        if (_request.MaxOutputTokens is { } given)
        {
            return given;
        }

        if (_spec?.MaxOutputTokens is { } max)
        {
            return (int)Math.Min(max, _options.DefaultMaxTokensCap);
        }

        return _options.UnknownModelMaxTokens;
    }

    // ---- sampling and small members ----

    private void WriteSampling(Utf8JsonWriter w, JsonObject? extra)
    {
        ChatRequest r = _request;
        if (r.StopSequences.Count > 0 && !Has(extra, "stop_sequences"))
        {
            w.WriteStartArray("stop_sequences");
            foreach (string s in r.StopSequences)
            {
                w.WriteStringValue(s);
            }

            w.WriteEndArray();
        }

        // From API version 2026-10-21 the API answers any temperature or top_p with a 400, on every model; some earlier models accept only
        // the default (API reference, Versions; migration guide). The catalog's supportsTemperature says which models take the parameter.
        bool samplingRemoved = string.CompareOrdinal(_options.ApiVersion, AnthropicOptions.SamplingRemovedFrom) >= 0;
        bool modelTakes = _spec?.SupportsTemperature != false;
        if (r.Temperature is { } temperature)
        {
            if (samplingRemoved || !modelTakes)
            {
                Log($"anthropic: dropped temperature for {_model}: " + (samplingRemoved ? $"API version {_options.ApiVersion} does not accept it." : "the model does not take it."));
            }
            else if (!Has(extra, "temperature"))
            {
                w.WriteNumber("temperature", temperature);
            }
        }

        if (r.TopP is { } topP)
        {
            if (samplingRemoved || !modelTakes)
            {
                Log($"anthropic: dropped top_p for {_model}: " + (samplingRemoved ? $"API version {_options.ApiVersion} does not accept it." : "the model does not take it."));
            }
            else if (!Has(extra, "top_p"))
            {
                w.WriteNumber("top_p", topP);
            }
        }

        if (r.UserId is { Length: > 0 } user && !Has(extra, "metadata"))
        {
            w.WriteStartObject("metadata");
            w.WriteString("user_id", user);
            w.WriteEndObject();
        }
    }

    // ChatRequest.ServiceTier -> the API's two members: service_tier (auto | standard_only) and speed (standard | fast).
    private void WriteServiceTier(Utf8JsonWriter w, string tier, JsonObject? extra)
    {
        switch (tier.ToLowerInvariant())
        {
            case "auto":
            case "priority":
                if (_mode != BodyMode.Count && !Has(extra, "service_tier"))
                {
                    w.WriteString("service_tier", "auto");
                }

                break;
            case "standard":
            case "standard_only":
                if (_mode != BodyMode.Count && !Has(extra, "service_tier"))
                {
                    w.WriteString("service_tier", "standard_only");
                }

                break;
            case "fast":
                if (!Has(extra, "speed"))
                {
                    w.WriteString("speed", "fast");
                }

                break;
            default:
                Log($"anthropic: dropped service tier '{tier}': Anthropic knows auto, standard_only and fast.");
                break;
        }
    }

    private void WriteToolChoice(Utf8JsonWriter w, Thinking? thinking)
    {
        ToolChoice? choice = _request.ToolChoice;
        bool? parallel = _request.ParallelToolCalls;
        if (choice is null && parallel is null)
        {
            return;
        }

        ToolChoiceMode mode = choice?.Mode ?? ToolChoiceMode.Auto;
        if (mode == ToolChoiceMode.Auto && parallel is null)
        {
            return;
        }

        if (thinking?.Type == "enabled" && mode is ToolChoiceMode.Required or ToolChoiceMode.Specific)
        {
            // Manual extended thinking allows only auto and none (extended thinking guide). Forcing a tool changes meaning, so it is refused.
            throw new BadRequestException(AnthropicErrors.Provider, "a forced tool choice cannot be combined with thinking that has a token budget; use adaptive thinking or leave the tool choice to the model (the request was not sent).");
        }

        w.WriteStartObject("tool_choice");
        switch (mode)
        {
            case ToolChoiceMode.None:
                w.WriteString("type", "none");
                break;
            case ToolChoiceMode.Required:
                w.WriteString("type", "any");
                break;
            case ToolChoiceMode.Specific:
                w.WriteString("type", "tool");
                w.WriteString("name", choice!.ToolName);
                break;
            default:
                w.WriteString("type", "auto");
                break;
        }

        if (parallel == false && mode != ToolChoiceMode.None)
        {
            w.WriteBoolean("disable_parallel_tool_use", true);
        }

        w.WriteEndObject();
    }

    // The tool's name is its type without the date suffix, except the text editor (tool use guides); toolsets carry no name.
    internal static string? HostedToolName(string type)
    {
        if (type.Contains("_toolset_", StringComparison.Ordinal))
        {
            return null;
        }

        if (type.StartsWith("text_editor_", StringComparison.Ordinal))
        {
            return type == "text_editor_20250124" ? "str_replace_editor" : "str_replace_based_edit_tool";
        }

        int i = type.LastIndexOf('_');
        if (i > 0 && type.Length - i - 1 == 8 && type.AsSpan(i + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return type[..i];
        }

        return type;
    }

    private static void WriteHostedTool(Utf8JsonWriter w, HostedToolDefinition tool)
    {
        w.WriteStartObject();
        w.WriteString("type", tool.Type);
        string? name = tool.Options is not null && AnthropicJson.GetString(tool.Options, "name") is { } given ? given : HostedToolName(tool.Type);
        if (name is not null)
        {
            w.WriteString("name", name);
        }

        if (tool.Options is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> p in tool.Options)
            {
                if (p.Key is "type" or "name")
                {
                    continue;
                }

                w.WritePropertyName(p.Key);
                if (p.Value is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    p.Value.WriteTo(w);
                }
            }
        }

        w.WriteEndObject();
    }

    // ---- thinking, effort, structured output ----

    private string? ResolveEffort()
    {
        string? effort = _request.Reasoning?.Effort;
        if (effort is null)
        {
            return null;
        }

        ThinkingSpec? spec = _spec?.Thinking;
        if (_spec is not null && (spec?.Efforts is not { Count: > 0 } efforts || !efforts.Contains(effort, StringComparer.OrdinalIgnoreCase)))
        {
            Log($"anthropic: dropped effort '{effort}' for {_model}: the model does not take that level.");
            return null;
        }

        return effort.ToLowerInvariant();
    }

    // Which thinking configuration a request gets (thinking guide, "Configuring thinking"): adaptive on models that decide for themselves,
    // enabled with a token budget on models that take one, nothing on models that do not think.
    private Thinking? ResolveThinking(int maxTokens)
    {
        ReasoningOptions? reasoning = _request.Reasoning;
        if (reasoning is null)
        {
            return null;
        }

        string? display = reasoning.Summary?.ToLowerInvariant() switch
        {
            null => null,
            "none" or "off" or "omitted" or "hidden" => "omitted",
            _ => "summarized",
        };

        ThinkingSpec? spec = _spec?.Thinking;
        if (_spec is not null && spec is null)
        {
            if (reasoning.Effort is not null || reasoning.BudgetTokens is not null || reasoning.Summary is not null)
            {
                Log($"anthropic: dropped reasoning settings for {_model}: the model does not think.");
            }

            return null;
        }

        bool acceptsBudget = _spec is null || spec?.AcceptsBudget == true;
        bool acceptsAdaptive = _spec is null
            || (_spec.Properties is not null && _spec.Properties.TryGetValue("adaptiveThinking", out object? flag) ? flag is true : !acceptsBudget);

        if (reasoning.BudgetTokens is { } budget)
        {
            if (acceptsBudget)
            {
                return EnabledThinking(budget, spec, maxTokens, display, reasoning.Effort);
            }

            if (acceptsAdaptive)
            {
                Log($"anthropic: dropped the thinking budget for {_model}: the model decides how much to think; use an effort level to steer it.");
                return new Thinking("adaptive", 0, display, reasoning.Effort);
            }

            Log($"anthropic: dropped reasoning settings for {_model}: the model takes no thinking configuration.");
            return null;
        }

        if (acceptsAdaptive)
        {
            return new Thinking("adaptive", 0, display, reasoning.Effort);
        }

        if (acceptsBudget)
        {
            // Effort only, on a model with a manual budget: the catalog says which budget each effort level stands for.
            int fallback = spec?.MinBudget ?? 1024;
            int chosen = reasoning.Effort is not null && spec?.DefaultEffortBudgets is { } map && map.TryGetValue(reasoning.Effort, out int b) ? b : fallback;
            return EnabledThinking(chosen, spec, maxTokens, display, reasoning.Effort);
        }

        return null;
    }

    private Thinking? EnabledThinking(int budget, ThinkingSpec? spec, int maxTokens, string? display, string? effort)
    {
        int min = spec?.MinBudget ?? 1024;
        int clamped = Math.Max(budget, min);
        if (spec?.MaxBudget is { } maxBudget)
        {
            clamped = Math.Min(clamped, maxBudget);
        }

        // The budget must be below max_tokens, which also counts the answer (API reference, thinking).
        if (clamped >= maxTokens)
        {
            clamped = maxTokens - 1;
        }

        if (clamped < min)
        {
            Log($"anthropic: dropped thinking for {_model}: max_tokens {maxTokens} leaves no room for the smallest budget ({min}).");
            return null;
        }

        if (clamped != budget)
        {
            Log($"anthropic: changed the thinking budget for {_model} from {budget} to {clamped} to fit the model's limits.");
        }

        return new Thinking("enabled", clamped, display, effort);
    }

    private void WriteOutputConfig(Utf8JsonWriter w, string? effort, JsonObject? extra)
    {
        ResponseFormat? format = _request.ResponseFormat;
        bool hasFormat = false;
        if (format is { Kind: ResponseFormatKind.JsonObject })
        {
            // Anthropic constrains output through a schema only (structured outputs guide); a bare "any JSON" request has no equivalent.
            throw new BadRequestException(AnthropicErrors.Provider, "Anthropic has no free-form JSON mode; ask for a JSON schema instead (the request was not sent).");
        }

        if (format is { Kind: ResponseFormatKind.JsonSchema })
        {
            if (_spec?.SupportsStructuredOutput == false)
            {
                throw new BadRequestException(AnthropicErrors.Provider, $"{_model} does not support structured output (the request was not sent).");
            }

            hasFormat = true;
        }

        if ((effort is null && !hasFormat) || Has(extra, "output_config"))
        {
            return;
        }

        w.WriteStartObject("output_config");
        if (effort is not null)
        {
            w.WriteString("effort", effort);
        }

        if (hasFormat)
        {
            w.WriteStartObject("format");
            w.WriteString("type", "json_schema");
            w.WritePropertyName("schema");
            format!.Schema!.WriteTo(w);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    // ---- caching ----

    private (bool TopLevel, string? Ttl) ApplyCachePolicy(List<OutMessage> messages)
    {
        CachePolicy? policy = _request.Cache;
        bool anyMark = messages.Any(m => m.Blocks.Any(b => b.Cache));
        bool policyOn = policy is null || policy.Enabled;
        bool modelCaches = _spec?.SupportsCaching != false;

        if (anyMark && (!policyOn || !modelCaches))
        {
            Log(policyOn ? $"anthropic: dropped cache breakpoints for {_model}: the model does not support prompt caching." : "anthropic: dropped cache breakpoints because caching is switched off for the request.");
            foreach (OutMessage m in messages)
            {
                foreach (OutBlock b in m.Blocks)
                {
                    b.Cache = false;
                }
            }

            anyMark = false;
        }

        // At most four explicit breakpoints per request (prompt caching guide); the latest ones are kept because they cover the most.
        List<OutBlock> marked = [.. messages.SelectMany(m => m.Blocks).Where(b => b.Cache)];
        if (marked.Count > 4)
        {
            Log($"anthropic: kept the last 4 of {marked.Count} cache breakpoints; the API allows 4.");
            foreach (OutBlock b in marked.Take(marked.Count - 4))
            {
                b.Cache = false;
            }
        }

        string? ttl = null;
        if (policy?.Ttl is { } wanted)
        {
            // The API offers 5 minutes and 1 hour; the nearer one is used (the midpoint is 32.5 minutes).
            ttl = wanted > TimeSpan.FromMinutes(32.5) ? "1h" : null;
        }

        bool topLevel = policy is { Enabled: true } && modelCaches && !anyMark;
        return (topLevel, ttl);
    }

    // ---- messages ----

    private List<OutMessage> BuildMessages()
    {
        var result = new List<OutMessage>();
        foreach (Message m in _request.Messages)
        {
            int blocksBefore = result.Sum(x => x.Blocks.Count);
            if (m.Assistant.Count > 0 || m.ToolCalls.Count > 0)
            {
                AddAssistantTurn(result, m);
            }
            else if (m.User.Count > 0)
            {
                List<OutBlock> blocks = UserBlocks(m.User);
                if (blocks.Count > 0)
                {
                    Append(result, User, blocks);
                }
            }

            if (m.Cache && result.Count > 0)
            {
                // The breakpoint follows the last block this message contributed.
                OutMessage last = result[^1];
                OutBlock? target = last.Blocks.LastOrDefault(b => b.CanCache);
                if (target is not null && result.Sum(x => x.Blocks.Count) > blocksBefore)
                {
                    target.Cache = true;
                }
            }
        }

        return result;
    }

    private static void Append(List<OutMessage> messages, string role, List<OutBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        if (messages.Count > 0 && messages[^1].Role == role)
        {
            messages[^1].Blocks.AddRange(blocks);
        }
        else
        {
            var m = new OutMessage(role);
            m.Blocks.AddRange(blocks);
            messages.Add(m);
        }
    }

    private static void AddAssistantTurn(List<OutMessage> result, Message m)
    {
        var blocks = new List<OutBlock>();
        int callIndex = 0;
        IReadOnlyList<ToolCall> calls = m.ToolCalls;

        void FlushCallsBefore(int position)
        {
            // Calls are placed by the number of parts before them; calls with the same position keep their order.
            while (callIndex < calls.Count && calls[callIndex].After <= position)
            {
                blocks.Add(new OutBlock(K.ToolUse) { Call = calls[callIndex] });
                callIndex++;
            }
        }

        // ToolCall.After is not necessarily sorted across calls; sort stably by it so position order wins.
        if (calls.Count > 1 && !IsSortedByAfter(calls))
        {
            calls = [.. calls.Select((c, i) => (c, i)).OrderBy(t => t.c.After).ThenBy(t => t.i).Select(t => t.c)];
        }

        for (int i = 0; i < m.Assistant.Count; i++)
        {
            FlushCallsBefore(i);
            AddAssistantPart(blocks, m.Assistant[i]);
        }

        FlushCallsBefore(int.MaxValue);
        Append(result, Assistant, blocks);

        var results = new List<OutBlock>();
        foreach (ToolCall call in m.ToolCalls)
        {
            if (call.Result.Count > 0 || call.IsError)
            {
                results.Add(new OutBlock(K.ToolResult) { Call = call });
            }
        }

        Append(result, User, results);
    }

    private static bool IsSortedByAfter(IReadOnlyList<ToolCall> calls)
    {
        for (int i = 1; i < calls.Count; i++)
        {
            if (calls[i].After < calls[i - 1].After)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddAssistantPart(List<OutBlock> blocks, Part part)
    {
        switch (part)
        {
            case TextPart t when t.Text.Length > 0:
                blocks.Add(new OutBlock(K.Text) { Text = t.Text, Citations = t.Annotations });
                break;
            case ReasoningPart r when r.Provider == AnthropicErrors.Provider && r.Data is not null:
                blocks.Add(new OutBlock(K.Thinking) { Part = r });
                break;
            case HostedToolCallPart h when h.Provider is null or AnthropicErrors.Provider:
                blocks.Add(new OutBlock(K.Hosted) { Part = h });
                break;
            case JsonPart j:
                blocks.Add(new OutBlock(K.Text) { Text = AnthropicJson.ToJson(j.Value) });
                break;
            case ProviderPart p when p.Provider == AnthropicErrors.Provider:
                blocks.Add(new OutBlock(K.Raw) { Raw = p.Raw });
                break;
            default:
                // Other providers' reasoning and hosted calls cannot be replayed here, a refusal is not something the model wrote as an
                // answer, and an assistant turn cannot hold files.
                break;
        }
    }

    private static List<OutBlock> UserBlocks(IReadOnlyList<Part> parts)
    {
        var blocks = new List<OutBlock>(parts.Count);
        foreach (Part part in parts)
        {
            OutBlock? b = ContentBlock(part);
            if (b is not null)
            {
                blocks.Add(b);
            }
        }

        return blocks;
    }

    private static OutBlock? ContentBlock(Part part) => part switch
    {
        TextPart t when t.Text.Length > 0 => new OutBlock(K.Text) { Text = t.Text },
        AssetPart a => new OutBlock(K.Asset) { Part = a },
        FileRefPart f => FileBlock(f),
        JsonPart j => new OutBlock(K.Text) { Text = AnthropicJson.ToJson(j.Value) },
        RefusalPart r when r.Text.Length > 0 => new OutBlock(K.Text) { Text = r.Text },
        ProviderPart p when p.Provider == AnthropicErrors.Provider => new OutBlock(K.Raw) { Raw = p.Raw },
        _ => null,
    };

    private static OutBlock FileBlock(FileRefPart f)
    {
        if (f.Provider != AnthropicErrors.Provider)
        {
            throw new BadRequestException(AnthropicErrors.Provider, $"a file reference belongs to {f.Provider}, not to Anthropic (the request was not sent).");
        }

        return new OutBlock(K.FileRef) { Part = f };
    }

    // ---- writing ----

    private void WriteMessage(Utf8JsonWriter w, OutMessage m, string? ttl)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role);
        w.WriteStartArray("content");
        foreach (OutBlock b in m.Blocks)
        {
            WriteBlock(w, b, ttl);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCacheControl(Utf8JsonWriter w, bool cache, string? ttl)
    {
        if (!cache)
        {
            return;
        }

        w.WriteStartObject("cache_control");
        w.WriteString("type", "ephemeral");
        if (ttl is not null)
        {
            w.WriteString("ttl", ttl);
        }

        w.WriteEndObject();
    }

    private void WriteBlock(Utf8JsonWriter w, OutBlock b, string? ttl)
    {
        switch (b.Kind)
        {
            case K.Text:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", b.Text);
                if (b.Citations is { Count: > 0 } citations)
                {
                    w.WriteStartArray("citations");
                    foreach (Annotation a in citations)
                    {
                        WriteCitation(w, a);
                    }

                    w.WriteEndArray();
                }

                WriteCacheControl(w, b.Cache, ttl);
                w.WriteEndObject();
                break;
            case K.Asset:
                WriteAsset(w, (AssetPart)b.Part!, b.Cache, ttl);
                break;
            case K.FileRef:
                WriteFileRef(w, (FileRefPart)b.Part!, b.Cache, ttl);
                break;
            case K.Thinking:
                WriteThinking(w, (ReasoningPart)b.Part!);
                break;
            case K.ToolUse:
                WriteToolUse(w, b.Call!, b.Cache, ttl);
                break;
            case K.ToolResult:
                WriteToolResult(w, b.Call!, b.Cache, ttl);
                break;
            case K.Hosted:
                WriteHosted(w, (HostedToolCallPart)b.Part!, b.Cache, ttl);
                break;
            case K.Raw:
                WriteRaw(w, b.Raw!, b.Cache, ttl);
                break;
        }
    }

    // A raw block is written as received; a cache breakpoint is added only when the block does not already carry one.
    private static void WriteRaw(Utf8JsonWriter w, JsonNode raw, bool cache, string? ttl)
    {
        if (!cache || raw is not JsonObject o || o.ContainsKey("cache_control"))
        {
            raw.WriteTo(w);
            return;
        }

        w.WriteStartObject();
        foreach (KeyValuePair<string, JsonNode?> p in o)
        {
            w.WritePropertyName(p.Key);
            if (p.Value is null)
            {
                w.WriteNullValue();
            }
            else
            {
                p.Value.WriteTo(w);
            }
        }

        WriteCacheControl(w, true, ttl);
        w.WriteEndObject();
    }

    // A citation is written back as it was read: its type, the cited text, and every other field kept in extension data.
    private static void WriteCitation(Utf8JsonWriter w, Annotation a)
    {
        w.WriteStartObject();
        w.WriteString("type", a.Type);
        if (a.CitedText is not null)
        {
            w.WriteString("cited_text", a.CitedText);
        }

        if (a.ExtensionData is not null)
        {
            foreach (KeyValuePair<string, object?> p in a.ExtensionData)
            {
                w.WritePropertyName(p.Key);
                AnthropicJson.WriteNatural(w, p.Value);
            }
        }

        w.WriteEndObject();
    }

    private static void WriteThinking(Utf8JsonWriter w, ReasoningPart r)
    {
        // Thinking blocks go back exactly as received: the signature covers the text, and redacted blocks are opaque (extended thinking guide).
        JsonObject data = r.Data!;
        string type = AnthropicJson.GetString(data, "type") ?? "thinking";
        w.WriteStartObject();
        w.WriteString("type", type);
        if (type == "thinking")
        {
            w.WriteString("thinking", r.Display ?? "");
        }

        foreach (KeyValuePair<string, JsonNode?> p in data)
        {
            if (p.Key == "type")
            {
                continue;
            }

            w.WritePropertyName(p.Key);
            if (p.Value is null)
            {
                w.WriteNullValue();
            }
            else
            {
                p.Value.WriteTo(w);
            }
        }

        w.WriteEndObject();
    }

    private static void WriteToolUse(Utf8JsonWriter w, ToolCall call, bool cache, string? ttl)
    {
        w.WriteStartObject();
        w.WriteString("type", "tool_use");
        w.WriteString("id", call.Id);
        w.WriteString("name", call.Tool);
        w.WritePropertyName("input");
        if (call.Inputs is null)
        {
            w.WriteStartObject();
            w.WriteEndObject();
        }
        else
        {
            call.Inputs.WriteTo(w);
        }

        if (call.ExtensionData is not null)
        {
            // Fields the API added to the block (a caller for programmatic calls, a toolset name) are sent back with it.
            foreach (KeyValuePair<string, object?> p in call.ExtensionData)
            {
                w.WritePropertyName(p.Key);
                AnthropicJson.WriteNatural(w, p.Value);
            }
        }

        WriteCacheControl(w, cache, ttl);
        w.WriteEndObject();
    }

    private void WriteToolResult(Utf8JsonWriter w, ToolCall call, bool cache, string? ttl)
    {
        w.WriteStartObject();
        w.WriteString("type", "tool_result");
        w.WriteString("tool_use_id", call.Id);
        if (call.ExtensionData is not null && call.ExtensionData.TryGetValue("toolset_name", out object? toolset) && toolset is string toolsetName)
        {
            // A result for a member of a client toolset (computer or browser use) must echo the toolset name (tool use guide).
            w.WriteString("toolset_name", toolsetName);
        }

        if (call.Result.Count == 1 && call.Result[0] is TextPart only)
        {
            w.WriteString("content", only.Text);
        }
        else if (call.Result.Count > 0)
        {
            var any = false;
            foreach (Part p in call.Result)
            {
                OutBlock? inner = ContentBlock(p);
                if (inner is null)
                {
                    continue;
                }

                if (!any)
                {
                    w.WriteStartArray("content");
                    any = true;
                }

                WriteBlock(w, inner, null);
            }

            if (any)
            {
                w.WriteEndArray();
            }
        }

        if (call.IsError)
        {
            w.WriteBoolean("is_error", true);
        }

        WriteCacheControl(w, cache, ttl);
        w.WriteEndObject();
    }

    // A hosted call is two blocks in the API: the model's call and the provider's result for it. Both are rebuilt from the part and its replay data.
    private static void WriteHosted(Utf8JsonWriter w, HostedToolCallPart h, bool cache, string? ttl)
    {
        JsonObject? data = h.Data;
        string callType = data is null ? "server_tool_use" : AnthropicJson.GetString(data, "callType") ?? "server_tool_use";
        w.WriteStartObject();
        w.WriteString("type", callType);
        w.WriteString("id", h.Id ?? "");
        w.WriteString("name", h.Tool);
        w.WritePropertyName("input");
        if (h.Inputs is null)
        {
            w.WriteStartObject();
            w.WriteEndObject();
        }
        else
        {
            h.Inputs.WriteTo(w);
        }

        if (data?["callExtra"] is JsonObject callExtra)
        {
            foreach (KeyValuePair<string, JsonNode?> p in callExtra)
            {
                w.WritePropertyName(p.Key);
                if (p.Value is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    p.Value.WriteTo(w);
                }
            }
        }

        bool hasResult = h.Result is not null;
        w.WriteEndObject();
        if (!hasResult)
        {
            return;
        }

        string resultType = (data is null ? null : AnthropicJson.GetString(data, "resultType")) ?? (callType == "mcp_tool_use" ? "mcp_tool_result" : h.Tool + "_tool_result");
        w.WriteStartObject();
        w.WriteString("type", resultType);
        w.WriteString("tool_use_id", h.Id ?? "");
        w.WritePropertyName("content");
        h.Result!.WriteTo(w);
        if (data?["resultExtra"] is JsonObject resultExtra)
        {
            foreach (KeyValuePair<string, JsonNode?> p in resultExtra)
            {
                w.WritePropertyName(p.Key);
                if (p.Value is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    p.Value.WriteTo(w);
                }
            }
        }

        WriteCacheControl(w, cache, ttl);
        w.WriteEndObject();
    }

    private void WriteAsset(Utf8JsonWriter w, AssetPart part, bool cache, string? ttl)
    {
        (AssetRoute route, AssetRef target, string? url) = Route(part, _time);
        string contentType = target.ContentType.Length > 0 ? target.ContentType : part.Asset?.ContentType ?? "";
        bool image = IsImage(contentType);
        bool pdf = contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
        bool text = contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);
        if (!image && !pdf && !text)
        {
            throw new BadRequestException(AnthropicErrors.Provider, $"files of type '{contentType}' are not supported: Anthropic takes images (JPEG, PNG, GIF, WebP), PDFs and plain text (the request was not sent).");
        }

        w.WriteStartObject();
        w.WriteString("type", image ? "image" : "document");
        w.WritePropertyName("source");
        w.WriteStartObject();
        if (route == AssetRoute.Url)
        {
            w.WriteString("type", "url");
            w.WriteString("url", url);
        }
        else if (text)
        {
            // A text document is sent as text, not base64 (PDF support guide).
            byte[] bytes = route == AssetRoute.Inline ? Convert.FromBase64String(target.Data!) : _assets[target].ToArray();
            w.WriteString("type", "text");
            w.WriteString("media_type", "text/plain");
            w.WriteString("data", Encoding.UTF8.GetString(bytes));
        }
        else
        {
            w.WriteString("type", "base64");
            w.WriteString("media_type", contentType);
            if (route == AssetRoute.Inline)
            {
                w.WriteString("data", target.Data);
            }
            else
            {
                w.WriteBase64String("data", _assets[target].Span);
            }
        }

        w.WriteEndObject();
        if (!image)
        {
            WriteDocumentExtras(w, part);
        }

        WriteCacheControl(w, cache, ttl);
        w.WriteEndObject();
    }

    // A document may carry a title, a context string and a request for citations (citations guide). The asset's extension data holds them.
    private static void WriteDocumentExtras(Utf8JsonWriter w, AssetPart part)
    {
        Dictionary<string, object?>? ext = part.ExtensionData;
        string? title = ext is not null && ext.TryGetValue("title", out object? t) ? t as string : part.Asset?.Name;
        if (title is { Length: > 0 })
        {
            w.WriteString("title", title);
        }

        if (ext is not null && ext.TryGetValue("context", out object? c) && c is string context && context.Length > 0)
        {
            w.WriteString("context", context);
        }

        if (ext is not null && ext.TryGetValue("citations", out object? enabled) && enabled is true)
        {
            w.WriteStartObject("citations");
            w.WriteBoolean("enabled", true);
            w.WriteEndObject();
        }
    }

    private static void WriteFileRef(Utf8JsonWriter w, FileRefPart file, bool cache, string? ttl)
    {
        bool image = file.ContentType is { } ct && IsImage(ct);
        w.WriteStartObject();
        w.WriteString("type", image ? "image" : "document");
        w.WriteStartObject("source");
        w.WriteString("type", "file");
        w.WriteString("file_id", file.FileId);
        w.WriteEndObject();
        if (!image && file.Name is { Length: > 0 } name)
        {
            w.WriteString("title", name);
        }

        WriteCacheControl(w, cache, ttl);
        w.WriteEndObject();
    }
}
