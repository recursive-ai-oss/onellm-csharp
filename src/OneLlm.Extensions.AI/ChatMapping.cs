using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Extensions.AI;

/// <summary>Conversions between OneLLM chat requests, responses and usage and Microsoft.Extensions.AI options, responses and usage.</summary>
internal static class ChatMapping
{
    private static readonly JsonObject s_emptySchema = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    // ---- options and messages -> ChatRequest ----

    public static ChatRequest ToRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, string model, List<string> warnings)
    {
        (string? system, List<Message> turns) = ContentMapper.ToMessages(messages, options?.Instructions);
        if (options is null)
        {
            return new ChatRequest { Model = model, System = system, Messages = turns };
        }

        var tools = new List<ToolDefinition>();
        var hosted = new List<HostedToolDefinition>();
        foreach (AITool tool in options.Tools ?? [])
        {
            switch (tool)
            {
                case AIFunctionDeclaration function:
                    tools.Add(ToDefinition(function));
                    break;
                case HostedWebSearchTool:
                    hosted.Add(new HostedToolDefinition("web_search"));
                    break;
                case HostedCodeInterpreterTool:
                    hosted.Add(new HostedToolDefinition("code_interpreter"));
                    break;
                default:
                    warnings.Add($"The tool '{tool.Name}' ({tool.GetType().Name}) has no OneLLM equivalent and was not sent.");
                    break;
            }
        }

        if (options.Seed is not null || options.TopK is not null || options.FrequencyPenalty is not null || options.PresencePenalty is not null)
        {
            warnings.Add("Seed, TopK, FrequencyPenalty and PresencePenalty have no field in a OneLLM chat request; pass provider parameters with the onellm.extraParameters option.");
        }

        AdditionalPropertiesDictionary? props = options.AdditionalProperties;
        return new ChatRequest
        {
            Model = model,
            System = system,
            Messages = turns,
            Tools = tools,
            HostedTools = hosted,
            ToolChoice = ToChoice(options.ToolMode),
            ParallelToolCalls = options.AllowMultipleToolCalls,
            ResponseFormat = ToFormat(options.ResponseFormat),
            Reasoning = ToReasoning(options.Reasoning),
            Cache = props?.TryGetValue(OneLlmProperties.Cache, out object? cache) == true && cache is true ? new CachePolicy() : null,
            MaxOutputTokens = options.MaxOutputTokens,
            Temperature = Widen(options.Temperature),
            TopP = Widen(options.TopP),
            StopSequences = options.StopSequences is { Count: > 0 } stops ? [.. stops] : [],
            ServiceTier = props?.TryGetValue(OneLlmProperties.ServiceTier, out object? tier) == true ? tier as string : null,
            UserId = props?.TryGetValue(OneLlmProperties.UserId, out object? user) == true ? user as string : null,
            ExtraParameters = props?.TryGetValue(OneLlmProperties.ExtraParameters, out object? extra) == true ? JsonBridge.AsObject(extra) : null,
        };
    }

    // A float 0.2 widens to 0.20000000298...; going through its shortest text keeps the value the caller wrote, which is what the wire shows.
    private static double? Widen(float? value) =>
        value is { } f ? double.Parse(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) : null;

    public static ToolDefinition ToDefinition(AIFunctionDeclaration function)
    {
        JsonObject schema = JsonBridge.ToNode(function.JsonSchema) as JsonObject ?? (JsonObject)s_emptySchema.DeepClone();
        bool strict = function.AdditionalProperties?.TryGetValue("strictJsonSchema", out object? s) == true && s is true;
        return new ToolDefinition(function.Name, function.Description ?? "", schema) { Strict = strict };
    }

    private static ToolChoice? ToChoice(ChatToolMode? mode) => mode switch
    {
        null => null,
        NoneChatToolMode => ToolChoice.None,
        RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } name } => ToolChoice.Tool(name),
        RequiredChatToolMode => ToolChoice.Required,
        _ => ToolChoice.Auto,
    };

    private static ResponseFormat? ToFormat(ChatResponseFormat? format)
    {
        if (format is not ChatResponseFormatJson json)
        {
            return null;
        }

        return json.Schema is { ValueKind: JsonValueKind.Object } schema && JsonBridge.ToNode(schema) is JsonObject node
            ? ResponseFormat.FromSchema(json.SchemaName is { Length: > 0 } name ? name : "response", node)
            : ResponseFormat.AnyJson;
    }

    private static OneLlm.Providers.ReasoningOptions? ToReasoning(Microsoft.Extensions.AI.ReasoningOptions? reasoning)
    {
        if (reasoning is null)
        {
            return null;
        }

        string? effort = reasoning.Effort switch
        {
            null => null,
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => null,
        };

        // The summary words are the providers' own ("auto" is the OpenAI word); a provider that spells it differently needs the
        // onellm.extraParameters option instead.
        string? summary = reasoning.Output switch
        {
            ReasoningOutput.Summary => "auto",
            ReasoningOutput.Full => "detailed",
            _ => null,
        };
        return new OneLlm.Providers.ReasoningOptions { Effort = effort, Summary = summary };
    }

    // ---- ChatRequest -> options and messages (the provider direction) ----

    public static (List<ChatMessage> Messages, ChatOptions Options) ToMeai(ChatRequest request)
    {
        var messages = new List<ChatMessage>();
        if (request.System is { Length: > 0 } system)
        {
            messages.Add(new ChatMessage(ChatRole.System, system));
        }

        foreach (Message m in request.Messages)
        {
            ContentMapper.AppendMessages(m, messages);
        }

        var options = new ChatOptions
        {
            ModelId = request.Model.Length > 0 ? request.Model : null,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature is { } t ? (float)t : null,
            TopP = request.TopP is { } p ? (float)p : null,
            StopSequences = request.StopSequences.Count > 0 ? [.. request.StopSequences] : null,
            AllowMultipleToolCalls = request.ParallelToolCalls,
        };

        List<AITool> tools = [];
        foreach (ToolDefinition d in request.Tools)
        {
            AIFunctionDeclaration declaration = new ToolDeclaration(d.Name, d.Description, JsonBridge.ToElement(d.InputSchema), d.Strict);
            tools.Add(declaration);
        }

        foreach (HostedToolDefinition h in request.HostedTools)
        {
            switch (h.Type)
            {
                case "web_search" or "google_search":
                    tools.Add(new HostedWebSearchTool());
                    break;
                case "code_interpreter" or "code_execution":
                    tools.Add(new HostedCodeInterpreterTool());
                    break;
            }
        }

        if (tools.Count > 0)
        {
            options.Tools = tools;
        }

        options.ToolMode = request.ToolChoice?.Mode switch
        {
            ToolChoiceMode.None => ChatToolMode.None,
            ToolChoiceMode.Required => ChatToolMode.RequireAny,
            ToolChoiceMode.Specific => ChatToolMode.RequireSpecific(request.ToolChoice.ToolName!),
            ToolChoiceMode.Auto => ChatToolMode.Auto,
            _ => null,
        };

        if (request.ResponseFormat is { } format)
        {
            options.ResponseFormat = format.Kind switch
            {
                ResponseFormatKind.JsonObject => ChatResponseFormat.Json,
                ResponseFormatKind.JsonSchema => ChatResponseFormat.ForJsonSchema(JsonBridge.ToElement(format.Schema!), format.SchemaName),
                _ => ChatResponseFormat.Text,
            };
        }

        if (request.Reasoning is { } reasoning)
        {
            options.Reasoning = new Microsoft.Extensions.AI.ReasoningOptions
            {
                Effort = reasoning.Effort?.ToLowerInvariant() switch
                {
                    "none" or "minimal" => ReasoningEffort.None,
                    "low" => ReasoningEffort.Low,
                    "medium" => ReasoningEffort.Medium,
                    "high" => ReasoningEffort.High,
                    "xhigh" or "max" => ReasoningEffort.ExtraHigh,
                    _ => null,
                },
                Output = reasoning.Summary is null ? null : ReasoningOutput.Summary,
            };
        }

        AdditionalPropertiesDictionary props = options.AdditionalProperties ??= [];
        if (request.Cache is { Enabled: true })
        {
            props[OneLlmProperties.Cache] = true;
        }

        if (request.ServiceTier is not null)
        {
            props[OneLlmProperties.ServiceTier] = request.ServiceTier;
        }

        if (request.UserId is not null)
        {
            props[OneLlmProperties.UserId] = request.UserId;
        }

        if (request.ExtraParameters is not null)
        {
            props[OneLlmProperties.ExtraParameters] = request.ExtraParameters.DeepClone();
        }

        return (messages, options);
    }

    // ---- finish reasons ----

    public static ChatFinishReason? ToFinish(FinishReason finish, string? raw) => finish switch
    {
        FinishReason.Stop => ChatFinishReason.Stop,
        FinishReason.Length => ChatFinishReason.Length,
        FinishReason.ToolCalls => ChatFinishReason.ToolCalls,
        FinishReason.ContentFilter => ChatFinishReason.ContentFilter,
        FinishReason.Refusal => new ChatFinishReason("refusal"),
        FinishReason.PauseTurn => new ChatFinishReason("pause_turn"),
        FinishReason.Other => new ChatFinishReason(raw ?? "other"),
        _ => null,
    };

    public static (FinishReason Finish, string? Raw) FromFinish(ChatFinishReason? reason)
    {
        if (reason is not { } r)
        {
            return (FinishReason.Unknown, null);
        }

        if (r == ChatFinishReason.Stop)
        {
            return (FinishReason.Stop, r.Value);
        }

        if (r == ChatFinishReason.Length)
        {
            return (FinishReason.Length, r.Value);
        }

        if (r == ChatFinishReason.ToolCalls)
        {
            return (FinishReason.ToolCalls, r.Value);
        }

        if (r == ChatFinishReason.ContentFilter)
        {
            return (FinishReason.ContentFilter, r.Value);
        }

        return r.Value switch
        {
            "refusal" => (FinishReason.Refusal, r.Value),
            "pause_turn" => (FinishReason.PauseTurn, r.Value),
            _ => (FinishReason.Other, r.Value),
        };
    }

    // ---- usage ----

    /// <summary>
    /// OneLLM usage as the details Microsoft.Extensions.AI carries. <c>InputTokenCount</c> is the whole input (new, cache-read and cache-write
    /// tokens together), which is how the OpenAI Responses usage reports it; <c>CachedInputTokenCount</c> is the cache-read part.
    /// </summary>
    public static UsageDetails ToUsage(Usage usage)
    {
        var details = new UsageDetails
        {
            InputTokenCount = usage.TotalInputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalInputTokens + usage.OutputTokens,
            CachedInputTokenCount = usage.CacheReadInputTokens,
        };
        if (usage.Counters is { } counters && counters.TryGetValue("reasoningTokens", out long reasoning))
        {
            details.ReasoningTokenCount = reasoning;
        }

        if (usage.CacheCreationInputTokens != 0)
        {
            (details.AdditionalCounts ??= [])[OneLlmProperties.CacheCreationInputTokens] = usage.CacheCreationInputTokens;
        }

        return details;
    }

    public static Usage FromUsage(UsageDetails details, string model)
    {
        long cached = details.CachedInputTokenCount ?? 0;
        long created = 0;
        if (details.AdditionalCounts?.TryGetValue(OneLlmProperties.CacheCreationInputTokens, out long c) == true)
        {
            created = c;
        }

        long input = details.InputTokenCount ?? 0;
        Dictionary<string, long>? counters = details.ReasoningTokenCount is { } r ? new Dictionary<string, long> { ["reasoningTokens"] = r } : null;
        return new Usage
        {
            Model = model,
            InputTokens = Math.Max(0, input - cached - created),
            CacheReadInputTokens = cached,
            CacheCreationInputTokens = created,
            OutputTokens = details.OutputTokenCount ?? 0,
            Counters = counters,
        };
    }
}
