using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>What the catalog says about the model of one request, and where dropped parameters are reported.</summary>
internal sealed class RequestPolicy(string provider, ModelSpec? spec, List<string>? warnings)
{
    public string Provider { get; } = provider;

    public ModelSpec? Spec { get; } = spec;

    /// <summary>Temperature and top_p are dropped for models whose entry says they take none.</summary>
    public bool TakesSampling => Spec?.SupportsTemperature != false;

    public void Dropped(string parameter, string reason) => warnings?.Add($"dropped {parameter}: {reason}");

    /// <summary>Refuses a parameter whose absence would change what the request means.</summary>
    public BadRequestException Refuse(string message) => new(Provider, message, null);
}

/// <summary>
/// Writes a Responses API request (POST /v1/responses, and /v1/responses/input_tokens for counting) from a <see cref="ChatRequest"/>. The turn-shaped
/// messages become Responses input items: one item per user turn, then for each assistant turn its reasoning, text, hosted tool and function call
/// items in their original order, then the function outputs.
/// </summary>
internal static class ResponsesRequestWriter
{
    public static ReadOnlyMemory<byte> Write(ChatRequest r, RequestPolicy policy, bool store, bool stream, bool forCount)
    {
        ModelSpec? spec = policy.Spec;
        if (spec?.SupportsTools == false && (r.Tools.Count > 0 || r.HostedTools.Count > 0))
        {
            throw policy.Refuse($"model '{r.Model}' does not support tools.");
        }

        if (r.ResponseFormat is { Kind: ResponseFormatKind.JsonSchema } && spec?.SupportsStructuredOutput == false)
        {
            throw policy.Refuse($"model '{r.Model}' does not support structured output.");
        }

        if (r.StopSequences.Count > 0)
        {
            throw policy.Refuse("the Responses API has no stop sequences; use the Chat Completions protocol for them.");
        }

        using var body = new BodyWriter(r.ExtraParameters);
        Utf8JsonWriter w = body.Writer;

        if (body.Field("model"))
        {
            w.WriteStringValue(r.Model);
        }

        if (body.Field("input"))
        {
            WriteInput(w, r);
        }

        if (r.System is { Length: > 0 } system && body.Field("instructions"))
        {
            w.WriteStringValue(system);
        }

        if (r.Tools.Count + r.HostedTools.Count > 0 && body.Field("tools"))
        {
            WriteTools(w, r);
        }

        if (r.ToolChoice is { } choice && (r.Tools.Count + r.HostedTools.Count > 0) && body.Field("tool_choice"))
        {
            WriteToolChoice(w, r, choice);
        }

        if (r.ParallelToolCalls is { } parallel && body.Field("parallel_tool_calls"))
        {
            w.WriteBooleanValue(parallel);
        }

        if (r.ResponseFormat is { Kind: not ResponseFormatKind.Text } format && body.Field("text"))
        {
            w.WriteStartObject();
            w.WritePropertyName("format");
            w.WriteStartObject();
            if (format.Kind == ResponseFormatKind.JsonObject)
            {
                w.WriteString("type", "json_object");
            }
            else
            {
                w.WriteString("type", "json_schema");
                w.WriteString("name", format.SchemaName);
                w.WritePropertyName("schema");
                format.Schema!.WriteTo(w);
                w.WriteBoolean("strict", format.Strict);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        bool reasoning = false;
        if (r.Reasoning is { } options)
        {
            reasoning = WriteReasoning(w, body, r, options, policy);
        }

        if (forCount)
        {
            return body.Finish();
        }

        // Reasoning items are sent back as encrypted content when nothing is stored, so asking for them is part of keeping a reasoning
        // model stateless (Reasoning guide, "Keeping reasoning items in context").
        if (!store && (reasoning || spec?.Thinking is not null) && body.Field("include"))
        {
            w.WriteStartArray();
            w.WriteStringValue("reasoning.encrypted_content");
            w.WriteEndArray();
        }

        if (body.Field("store"))
        {
            w.WriteBooleanValue(store);
        }

        if (stream && body.Field("stream"))
        {
            w.WriteBooleanValue(true);
        }

        if (r.MaxOutputTokens is { } max && body.Field("max_output_tokens"))
        {
            w.WriteNumberValue(max);
        }

        if (r.Temperature is { } temperature)
        {
            if (!policy.TakesSampling)
            {
                policy.Dropped("temperature", $"model '{r.Model}' takes none");
            }
            else if (body.Field("temperature"))
            {
                w.WriteNumberValue(temperature);
            }
        }

        if (r.TopP is { } topP)
        {
            if (!policy.TakesSampling)
            {
                policy.Dropped("top_p", $"model '{r.Model}' takes none");
            }
            else if (body.Field("top_p"))
            {
                w.WriteNumberValue(topP);
            }
        }

        if (r.ServiceTier is { Length: > 0 } tier && body.Field("service_tier"))
        {
            w.WriteStringValue(tier);
        }

        if (r.UserId is { Length: > 0 } user && body.Field("safety_identifier"))
        {
            w.WriteStringValue(user.Length <= 128 ? user : user[..128]);
        }

        WriteCache(w, body, r, policy);
        return body.Finish();
    }

    // ---- input ----

    private static void WriteInput(Utf8JsonWriter w, ChatRequest r)
    {
        w.WriteStartArray();
        foreach (Message m in r.Messages)
        {
            if (m.User.Count > 0)
            {
                WriteUserMessage(w, m.User);
            }

            if (m.Assistant.Count > 0 || m.ToolCalls.Count > 0)
            {
                WriteAssistantTurn(w, r, m);
            }
        }

        w.WriteEndArray();
    }

    private static void WriteUserMessage(Utf8JsonWriter w, IReadOnlyList<Part> parts)
    {
        w.WriteStartObject();
        w.WriteString("role", "user");
        w.WritePropertyName("content");
        if (parts.Count == 1 && parts[0] is TextPart only)
        {
            w.WriteStringValue(only.Text);
        }
        else
        {
            w.WriteStartArray();
            foreach (Part p in parts)
            {
                WriteInputContent(w, p);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    internal static void WriteInputContent(Utf8JsonWriter w, Part part)
    {
        switch (part)
        {
            case TextPart t:
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", t.Text);
                w.WriteEndObject();
                break;
            case AssetPart a:
                WriteAsset(w, a);
                break;
            case JsonPart j:
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", j.Value.ToJsonString());
                w.WriteEndObject();
                break;
            case FileRefPart f when f.Provider == "openai":
                w.WriteStartObject();
                w.WriteString("type", "input_file");
                w.WriteString("file_id", f.FileId);
                w.WriteEndObject();
                break;
            case RefusalPart rf:
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", rf.Text);
                w.WriteEndObject();
                break;
            default:
                // Parts of another provider's making (its reasoning, hosted tool results) have no meaning here and are left out.
                break;
        }
    }

    private static void WriteAsset(Utf8JsonWriter w, AssetPart part)
    {
        AssetRef? asset = part.Asset;
        string contentType = asset?.ContentType ?? "";
        bool image = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        string? url = asset?.Data is { Length: > 0 } data ? $"data:{contentType};base64,{data}" : part.PublicUrl;
        if (url is null)
        {
            throw new BadRequestException("openai", "an asset part has neither inline data nor a public URL; load the asset's bytes before sending.", null);
        }

        w.WriteStartObject();
        if (image)
        {
            w.WriteString("type", "input_image");
            w.WriteString("image_url", url);
        }
        else if (asset?.Data is { Length: > 0 })
        {
            w.WriteString("type", "input_file");
            w.WriteString("filename", asset.Name ?? "file");
            w.WriteString("file_data", url);
        }
        else
        {
            w.WriteString("type", "input_file");
            w.WriteString("file_url", url);
        }

        w.WriteEndObject();
    }

    private static void WriteAssistantTurn(Utf8JsonWriter w, ChatRequest r, Message m)
    {
        IReadOnlyList<Part> parts = m.Assistant;
        ToolCall[] calls = [.. m.ToolCalls.OrderBy(c => c.After)];
        int next = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            while (next < calls.Length && calls[next].After <= i)
            {
                WriteCall(w, r, calls[next++]);
            }

            WriteAssistantPart(w, parts[i]);
        }

        while (next < calls.Length)
        {
            WriteCall(w, r, calls[next++]);
        }

        foreach (ToolCall call in m.ToolCalls)
        {
            WriteCallOutput(w, r, call);
        }
    }

    private static void WriteAssistantPart(Utf8JsonWriter w, Part part)
    {
        switch (part)
        {
            case ReasoningPart { Provider: "openai", Data: { } data }:
                // The reasoning item goes back exactly as it came, encrypted content included.
                data.WriteTo(w);
                break;
            case HostedToolCallPart { Provider: "openai", Data: { } data }:
                data.WriteTo(w);
                break;
            case ProviderPart { Provider: "openai" } raw:
                raw.Raw.WriteTo(w);
                break;
            case TextPart t:
                WriteAssistantText(w, t.Text);
                break;
            case RefusalPart rf:
                WriteAssistantText(w, rf.Text);
                break;
            case JsonPart j:
                WriteAssistantText(w, j.Value.ToJsonString());
                break;
            default:
                break;
        }
    }

    private static void WriteAssistantText(Utf8JsonWriter w, string text)
    {
        // The short message form: with nothing stored there is no item id to refer to, and the full output-message form requires one.
        w.WriteStartObject();
        w.WriteString("role", "assistant");
        w.WriteString("content", text);
        w.WriteEndObject();
    }

    private static bool IsCustom(ChatRequest r, string tool)
    {
        foreach (HostedToolDefinition h in r.HostedTools)
        {
            if (h.Type == "custom" && h.Options?["name"]?.GetValue<string>() == tool)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteCall(Utf8JsonWriter w, ChatRequest r, ToolCall call)
    {
        w.WriteStartObject();
        if (IsCustom(r, call.Tool))
        {
            w.WriteString("type", "custom_tool_call");
            w.WriteString("call_id", call.Id);
            w.WriteString("name", call.Tool);
            w.WriteString("input", call.Inputs?["input"]?.GetValue<string>() ?? "");
        }
        else
        {
            w.WriteString("type", "function_call");
            w.WriteString("call_id", call.Id);
            w.WriteString("name", call.Tool);
            w.WriteString("arguments", call.Inputs?.ToJsonString() ?? call.InvalidInputs ?? "{}");
        }

        w.WriteEndObject();
    }

    private static void WriteCallOutput(Utf8JsonWriter w, ChatRequest r, ToolCall call)
    {
        w.WriteStartObject();
        w.WriteString("type", IsCustom(r, call.Tool) ? "custom_tool_call_output" : "function_call_output");
        w.WriteString("call_id", call.Id);
        w.WritePropertyName("output");
        bool plain = true;
        foreach (Part p in call.Result)
        {
            if (p is AssetPart)
            {
                plain = false;
            }
        }

        if (plain)
        {
            w.WriteStringValue(ResultText(call.Result));
        }
        else
        {
            w.WriteStartArray();
            foreach (Part p in call.Result)
            {
                WriteInputContent(w, p);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    internal static string ResultText(IReadOnlyList<Part> parts)
    {
        if (parts.Count == 1 && parts[0] is TextPart only)
        {
            return only.Text;
        }

        var sb = new System.Text.StringBuilder();
        foreach (Part p in parts)
        {
            switch (p)
            {
                case TextPart t:
                    sb.Append(t.Text);
                    break;
                case JsonPart j:
                    sb.Append(j.Value.ToJsonString());
                    break;
                case RefusalPart rf:
                    sb.Append(rf.Text);
                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }

    // ---- tools ----

    private static void WriteTools(Utf8JsonWriter w, ChatRequest r)
    {
        w.WriteStartArray();
        foreach (ToolDefinition t in r.Tools)
        {
            w.WriteStartObject();
            w.WriteString("type", "function");
            w.WriteString("name", t.Name);
            w.WriteString("description", t.Description);
            w.WritePropertyName("parameters");
            t.InputSchema.WriteTo(w);
            w.WriteBoolean("strict", t.Strict);
            w.WriteEndObject();
        }

        foreach (HostedToolDefinition h in r.HostedTools)
        {
            w.WriteStartObject();
            w.WriteString("type", h.Type);
            bool container = false;
            if (h.Options is not null)
            {
                foreach ((string key, JsonNode? value) in h.Options)
                {
                    if (key == "type")
                    {
                        continue;
                    }

                    container |= key == "container";
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

            if (h.Type == "code_interpreter" && !container)
            {
                // The interpreter needs a container; an automatic one is the documented default for a caller who names none.
                w.WritePropertyName("container");
                w.WriteStartObject();
                w.WriteString("type", "auto");
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteToolChoice(Utf8JsonWriter w, ChatRequest r, ToolChoice choice)
    {
        switch (choice.Mode)
        {
            case ToolChoiceMode.Auto:
                w.WriteStringValue("auto");
                break;
            case ToolChoiceMode.None:
                w.WriteStringValue("none");
                break;
            case ToolChoiceMode.Required:
                w.WriteStringValue("required");
                break;
            default:
                string name = choice.ToolName!;
                w.WriteStartObject();
                if (r.HostedTools.Any(h => h.Type == name && h.Type != "custom"))
                {
                    w.WriteString("type", name);
                }
                else if (IsCustom(r, name))
                {
                    w.WriteString("type", "custom");
                    w.WriteString("name", name);
                }
                else
                {
                    w.WriteString("type", "function");
                    w.WriteString("name", name);
                }

                w.WriteEndObject();
                break;
        }
    }

    // ---- reasoning and caching ----

    private static bool WriteReasoning(Utf8JsonWriter w, BodyWriter body, ChatRequest r, ReasoningOptions options, RequestPolicy policy)
    {
        ThinkingSpec? thinking = policy.Spec?.Thinking;
        if (policy.Spec is not null && thinking is null)
        {
            policy.Dropped("reasoning", $"model '{r.Model}' is not a reasoning model");
            return false;
        }

        if (options.BudgetTokens is not null)
        {
            policy.Dropped("reasoning budget", "OpenAI reasoning is set by effort, not by a token budget");
        }

        string? effort = options.Effort;
        if (effort is not null && thinking?.Efforts is { Count: > 0 } efforts && !efforts.Contains(effort))
        {
            throw policy.Refuse($"model '{r.Model}' does not accept reasoning effort '{effort}'; it takes {string.Join(", ", efforts)}.");
        }

        if (effort is null && options.Summary is null)
        {
            return true;
        }

        if (body.Field("reasoning"))
        {
            w.WriteStartObject();
            if (effort is not null)
            {
                w.WriteString("effort", effort);
            }

            if (options.Summary is not null)
            {
                w.WriteString("summary", options.Summary);
            }

            w.WriteEndObject();
        }

        return true;
    }

    private static void WriteCache(Utf8JsonWriter w, BodyWriter body, ChatRequest r, RequestPolicy policy)
    {
        if (r.Cache is not { } cache)
        {
            return;
        }

        if (!cache.Enabled)
        {
            policy.Dropped("cache", "OpenAI prompt caching cannot be turned off");
            return;
        }

        if (cache.Key is { Length: > 0 } key && body.Field("prompt_cache_key"))
        {
            w.WriteStringValue(key.Length <= 64 ? key : key[..64]);
        }

        if (cache.Ttl is not { } ttl)
        {
            return;
        }

        // Newer models take prompt_cache_options with a minimum lifetime of 30 minutes or 12 hours; older ones take the retention switch.
        bool options = policy.Spec?.Properties?.TryGetValue("promptCacheOptions", out object? flag) == true && flag is true;
        if (options)
        {
            if (body.Field("prompt_cache_options"))
            {
                w.WriteStartObject();
                w.WriteString("ttl", ttl > TimeSpan.FromMinutes(30) ? "12h" : "30m");
                w.WriteEndObject();
            }
        }
        else if (ttl >= TimeSpan.FromHours(1) && body.Field("prompt_cache_retention"))
        {
            w.WriteStringValue("24h");
        }
    }
}
