using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>How a Chat Completions server differs from OpenAI's own, as far as the request goes.</summary>
internal sealed record ChatDialect
{
    /// <summary>OpenAI's current name for the output limit is <c>max_completion_tokens</c>; most compatible servers still read <c>max_tokens</c>.</summary>
    public string MaxTokensField { get; init; } = "max_tokens";

    /// <summary>Whether <c>stream_options.include_usage</c> is sent. Servers that do not know it can reject unknown fields.</summary>
    public bool StreamUsage { get; init; } = true;

    /// <summary>Whether <c>reasoning_effort</c> is sent when the request asks for an effort.</summary>
    public bool ReasoningEffort { get; init; } = true;

    /// <summary>The word for "a tool call is required". Mistral calls it <c>any</c>.</summary>
    public string RequiredWord { get; init; } = "required";

    /// <summary>Whether strict structured output (<c>json_schema</c> with <c>strict</c>) is sent; otherwise <c>json_object</c> mode with the schema in the system prompt is not attempted and the request is refused.</summary>
    public bool JsonSchema { get; init; } = true;

    /// <summary>The OpenAI dialect.</summary>
    public static ChatDialect OpenAI { get; } = new() { MaxTokensField = "max_completion_tokens" };
}

/// <summary>
/// Writes a Chat Completions request (POST /v1/chat/completions) from a <see cref="ChatRequest"/>. A turn becomes the messages Chat Completions
/// knows: a user message, an assistant message carrying text and tool calls, then one tool message per call result.
/// </summary>
internal static class ChatCompletionsRequestWriter
{
    public static ReadOnlyMemory<byte> Write(ChatRequest r, RequestPolicy policy, ChatDialect dialect, bool stream)
    {
        ModelSpec? spec = policy.Spec;
        if (spec?.SupportsTools == false && (r.Tools.Count > 0))
        {
            throw policy.Refuse($"model '{r.Model}' does not support tools.");
        }

        if (r.HostedTools.Count > 0)
        {
            throw policy.Refuse("Chat Completions has no hosted tools; use the Responses protocol.");
        }

        if (r.ResponseFormat is { Kind: ResponseFormatKind.JsonSchema } && (spec?.SupportsStructuredOutput == false || !dialect.JsonSchema))
        {
            throw policy.Refuse($"model '{r.Model}' does not support structured output.");
        }

        using var body = new BodyWriter(r.ExtraParameters, BodyWriter.EstimateSize(r));
        Utf8JsonWriter w = body.Writer;
        if (body.Field("model"))
        {
            w.WriteStringValue(r.Model);
        }

        if (body.Field("messages"))
        {
            WriteMessages(w, r);
        }

        if (r.Tools.Count > 0 && body.Field("tools"))
        {
            w.WriteStartArray();
            foreach (ToolDefinition t in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WritePropertyName("function");
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("parameters");
                t.InputSchema.WriteTo(w);
                if (t.Strict)
                {
                    w.WriteBoolean("strict", true);
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (r.ToolChoice is { } choice && r.Tools.Count > 0 && body.Field("tool_choice"))
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
                    w.WriteStringValue(dialect.RequiredWord);
                    break;
                default:
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", choice.ToolName);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    break;
            }
        }

        if (r.ParallelToolCalls is { } parallel && r.Tools.Count > 0 && body.Field("parallel_tool_calls"))
        {
            w.WriteBooleanValue(parallel);
        }

        if (r.ResponseFormat is { Kind: not ResponseFormatKind.Text } format && body.Field("response_format"))
        {
            w.WriteStartObject();
            if (format.Kind == ResponseFormatKind.JsonObject)
            {
                w.WriteString("type", "json_object");
            }
            else
            {
                w.WriteString("type", "json_schema");
                w.WritePropertyName("json_schema");
                w.WriteStartObject();
                w.WriteString("name", format.SchemaName);
                w.WritePropertyName("schema");
                format.Schema!.WriteTo(w);
                w.WriteBoolean("strict", format.Strict);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        if (r.Reasoning is { } reasoning)
        {
            if (policy.Spec is not null && policy.Spec.Thinking is null)
            {
                policy.Dropped("reasoning", $"model '{r.Model}' is not a reasoning model");
            }
            else if (reasoning.Effort is { } effort && dialect.ReasoningEffort)
            {
                if (policy.Spec?.Thinking?.Efforts is { Count: > 0 } efforts && !efforts.Contains(effort))
                {
                    throw policy.Refuse($"model '{r.Model}' does not accept reasoning effort '{effort}'; it takes {string.Join(", ", efforts)}.");
                }

                if (body.Field("reasoning_effort"))
                {
                    w.WriteStringValue(effort);
                }
            }

            if (reasoning.BudgetTokens is not null || reasoning.Summary is not null)
            {
                policy.Dropped("reasoning budget or summary", "Chat Completions sets reasoning by effort only");
            }
        }

        if (stream)
        {
            if (body.Field("stream"))
            {
                w.WriteBooleanValue(true);
            }

            if (dialect.StreamUsage && body.Field("stream_options"))
            {
                w.WriteStartObject();
                w.WriteBoolean("include_usage", true);
                w.WriteEndObject();
            }
        }

        if (r.MaxOutputTokens is { } max && body.Field(dialect.MaxTokensField))
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

        if (r.StopSequences.Count > 0 && body.Field("stop"))
        {
            w.WriteStartArray();
            foreach (string s in r.StopSequences)
            {
                w.WriteStringValue(s);
            }

            w.WriteEndArray();
        }

        if (r.ServiceTier is { Length: > 0 } tier && body.Field("service_tier"))
        {
            w.WriteStringValue(tier);
        }

        if (r.UserId is { Length: > 0 } user && body.Field("user"))
        {
            w.WriteStringValue(user);
        }

        if (r.Cache is { Enabled: true, Key: { Length: > 0 } key } && body.Field("prompt_cache_key"))
        {
            w.WriteStringValue(key.Length <= 64 ? key : key[..64]);
        }

        return body.Finish();
    }

    private static void WriteMessages(Utf8JsonWriter w, ChatRequest r)
    {
        w.WriteStartArray();
        if (r.System is { Length: > 0 } system)
        {
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", system);
            w.WriteEndObject();
        }

        foreach (Message m in r.Messages)
        {
            if (m.User.Count > 0)
            {
                WriteUser(w, m.User);
            }

            if (m.Assistant.Count == 0 && m.ToolCalls.Count == 0)
            {
                continue;
            }

            w.WriteStartObject();
            w.WriteString("role", "assistant");
            string text = AssistantText(m.Assistant);
            if (text.Length > 0 || m.ToolCalls.Count == 0)
            {
                w.WriteString("content", text);
            }
            else
            {
                w.WriteNull("content");
            }

            if (m.ToolCalls.Count > 0)
            {
                w.WritePropertyName("tool_calls");
                w.WriteStartArray();
                foreach (ToolCall call in m.ToolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", call.Id);
                    w.WriteString("type", "function");
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", call.Tool);
                    w.WriteString("arguments", call.Inputs?.ToJsonString() ?? call.InvalidInputs ?? "{}");
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();

            List<Part>? images = null;
            foreach (ToolCall call in m.ToolCalls)
            {
                w.WriteStartObject();
                w.WriteString("role", "tool");
                w.WriteString("tool_call_id", call.Id);
                w.WriteString("content", ResponsesRequestWriter.ResultText(call.Result));
                w.WriteEndObject();
                foreach (Part p in call.Result)
                {
                    if (p is AssetPart)
                    {
                        (images ??= []).Add(p);
                    }
                }
            }

            // A tool message can only carry text, so images a tool returned follow as a user message.
            if (images is { Count: > 0 })
            {
                WriteUser(w, images);
            }
        }

        w.WriteEndArray();
    }

    private static string AssistantText(IReadOnlyList<Part> parts)
    {
        // The usual assistant turn is one text part: use its string as it is.
        if (parts.Count == 1 && parts[0] is TextPart single)
        {
            return single.Text;
        }

        var sb = new System.Text.StringBuilder();
        foreach (Part p in parts)
        {
            switch (p)
            {
                case TextPart t:
                    sb.Append(t.Text);
                    break;
                case RefusalPart rf:
                    sb.Append(rf.Text);
                    break;
                case JsonPart j:
                    sb.Append(j.Value.ToJsonString());
                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }

    private static void WriteUser(Utf8JsonWriter w, IReadOnlyList<Part> parts)
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
                WriteContent(w, p);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter w, Part part)
    {
        switch (part)
        {
            case TextPart t:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", t.Text);
                w.WriteEndObject();
                break;
            case JsonPart j:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", j.Value.ToJsonString());
                w.WriteEndObject();
                break;
            case AssetPart a:
                string contentType = a.Asset?.ContentType ?? "";
                string? data = a.Asset?.Data;
                string? url = data is { Length: > 0 } ? $"data:{contentType};base64,{data}" : a.PublicUrl;
                if (url is null)
                {
                    throw new BadRequestException("openai", "an asset part has neither inline data nor a public URL; load the asset's bytes before sending.", null);
                }

                w.WriteStartObject();
                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    w.WriteString("type", "image_url");
                    w.WritePropertyName("image_url");
                    w.WriteStartObject();
                    w.WriteString("url", url);
                    w.WriteEndObject();
                }
                else if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && data is { Length: > 0 })
                {
                    w.WriteString("type", "input_audio");
                    w.WritePropertyName("input_audio");
                    w.WriteStartObject();
                    w.WriteString("data", data);
                    w.WriteString("format", contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "wav" : "mp3");
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString("type", "file");
                    w.WritePropertyName("file");
                    w.WriteStartObject();
                    w.WriteString("filename", a.Asset?.Name ?? "file");
                    w.WriteString("file_data", url);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
                break;
            case FileRefPart f when f.Provider == "openai":
                w.WriteStartObject();
                w.WriteString("type", "file");
                w.WritePropertyName("file");
                w.WriteStartObject();
                w.WriteString("file_id", f.FileId);
                w.WriteEndObject();
                w.WriteEndObject();
                break;
            default:
                break;
        }
    }
}
