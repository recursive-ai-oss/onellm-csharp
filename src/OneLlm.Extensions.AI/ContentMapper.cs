using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OneLlm.Messages;

namespace OneLlm.Extensions.AI;

/// <summary>Conversions between OneLLM parts, tool calls and messages and Microsoft.Extensions.AI content and chat messages.</summary>
internal static class ContentMapper
{
    // ---- OneLLM -> Microsoft.Extensions.AI ----

    public static AIContent ToContent(Part part)
    {
        switch (part)
        {
            case TextPart text:
                return TextOf(text);
            case RefusalPart refusal:
                var refused = new TextContent(refusal.Text);
                (refused.AdditionalProperties ??= [])[OneLlmProperties.Refusal] = true;
                return refused;
            case ReasoningPart reasoning:
                var thought = new TextReasoningContent(reasoning.Display ?? "") { ProtectedData = reasoning.Data?.ToJsonString() };
                (thought.AdditionalProperties ??= [])[OneLlmProperties.Provider] = reasoning.Provider;
                return thought;
            case AssetPart asset:
                return AssetContent(asset);
            case JsonPart json:
                return new TextContent(json.Value.ToJsonString());
            case FileRefPart file:
                return new HostedFileContent(file.FileId) { MediaType = file.ContentType, Name = file.Name };
            default:
                // Hosted tool calls and provider parts: carried whole.
                return new OneLlmPartContent(part);
        }
    }

    private static TextContent TextOf(TextPart text)
    {
        var content = new TextContent(text.Text);
        if (text.Annotations is { Count: > 0 } annotations)
        {
            content.Annotations = [.. annotations.Select(ToAnnotation)];
        }

        return content;
    }

    private static AIAnnotation ToAnnotation(Annotation a)
    {
        var citation = new CitationAnnotation
        {
            Title = a.Title,
            Snippet = a.CitedText,
            FileId = a.SourceId,
            Url = a.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri : null,
            RawRepresentation = a,
        };
        if (a.Start is { } start && a.End is { } end)
        {
            citation.AnnotatedRegions = [new TextSpanAnnotatedRegion { StartIndex = start, EndIndex = end }];
        }

        return citation;
    }

    private static AIContent AssetContent(AssetPart asset)
    {
        AssetRef? r = asset.Asset;
        if (r is { Data: { Length: > 0 } data })
        {
            return new DataContent(Convert.FromBase64String(data), r.ContentType) { Name = r.Name };
        }

        if (asset.PublicUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return new UriContent(uri, r?.ContentType is { Length: > 0 } type ? type : "application/octet-stream");
        }

        // A stored asset with no inline bytes and no public link: only a OneLLM asset store can open it.
        return new OneLlmPartContent(asset);
    }

    public static FunctionCallContent ToCall(ToolCall call)
    {
        var content = new FunctionCallContent(call.Id, call.Tool, JsonBridge.ToArguments(call.Inputs) ?? (call.InvalidInputs is null ? new Dictionary<string, object?>() : null));
        AdditionalPropertiesDictionary props = content.AdditionalProperties ??= [];
        if (call.When is not null)
        {
            props[OneLlmProperties.When] = call.When;
        }

        if (call.Injected)
        {
            props[OneLlmProperties.Injected] = true;
        }

        if (call.InjectedBy is not null)
        {
            props[OneLlmProperties.InjectedBy] = call.InjectedBy;
        }

        if (call.Routine is not null)
        {
            props[OneLlmProperties.Routine] = call.Routine;
        }

        if (call.InvalidInputs is not null)
        {
            props[OneLlmProperties.InvalidInputs] = call.InvalidInputs;
        }

        return content;
    }

    public static FunctionResultContent ToResult(ToolCall call)
    {
        object? value = ResultValue(call.Result);
        var content = new FunctionResultContent(call.Id, value);
        AdditionalPropertiesDictionary props = content.AdditionalProperties ??= [];
        if (call.IsError)
        {
            props[OneLlmProperties.IsError] = true;
        }

        props[OneLlmProperties.ResultParts] = call.Result;
        return content;
    }

    /// <summary>A result as the value a function result carries: the text of a single text part, the JSON value of a single JSON part, otherwise the content list.</summary>
    private static object? ResultValue(IReadOnlyList<Part> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count == 1 && parts[0] is JsonPart only)
        {
            return JsonBridge.ToElement(only.Value);
        }

        if (parts.Count == 1 && parts[0] is TextPart text)
        {
            return text.Text;
        }

        // Several parts stay several: joining the text would change what the provider is sent on the next turn.
        return parts.Select(ToContent).ToList();
    }

    /// <summary>
    /// One OneLLM turn as chat messages: the user parts as a user message; the assistant parts and tool calls as one assistant message with each
    /// call placed where the model made it; and, when any call has a result, one tool message with the results in call order.
    /// </summary>
    public static void AppendMessages(Message m, List<ChatMessage> into)
    {
        if (m.User.Count > 0)
        {
            into.Add(Stamp(new ChatMessage(ChatRole.User, [.. m.User.Select(ToContent)]), m));
        }

        if (m.Assistant.Count > 0 || m.ToolCalls.Count > 0)
        {
            var contents = new List<AIContent>(m.Assistant.Count + m.ToolCalls.Count);
            int next = 0;
            foreach (ToolCall call in m.ToolCalls.OrderBy(c => Math.Min(c.After, m.Assistant.Count)))
            {
                int at = Math.Min(call.After, m.Assistant.Count);
                while (next < at)
                {
                    contents.Add(ToContent(m.Assistant[next++]));
                }

                contents.Add(ToCall(call));
            }

            while (next < m.Assistant.Count)
            {
                contents.Add(ToContent(m.Assistant[next++]));
            }

            into.Add(Stamp(new ChatMessage(ChatRole.Assistant, contents), m));

            List<AIContent> results = [.. m.ToolCalls.Where(c => c.Result.Count > 0 || c.IsError).Select(ToResult)];
            if (results.Count > 0)
            {
                into.Add(new ChatMessage(ChatRole.Tool, results));
            }
        }
    }

    private static ChatMessage Stamp(ChatMessage message, Message m)
    {
        if (m.Id.Length > 0)
        {
            message.MessageId = m.Id;
        }

        message.CreatedAt = m.DateTime;
        if (m.When is not null)
        {
            (message.AdditionalProperties ??= [])[OneLlmProperties.When] = m.When;
        }

        return message;
    }

    // ---- Microsoft.Extensions.AI -> OneLLM ----

    public static Part ToPart(AIContent content)
    {
        switch (content)
        {
            case OneLlmPartContent carried:
                return carried.Part;
            case TextContent text when text.AdditionalProperties?.TryGetValue(OneLlmProperties.Refusal, out object? flag) == true && flag is true:
                return new RefusalPart(text.Text ?? "");
            case TextContent text:
                return new TextPart(text.Text ?? "") { Annotations = FromAnnotations(text.Annotations) };
            case TextReasoningContent thought:
                return FromReasoning(thought);
            case DataContent data when data.Data.Length > 0:
                return Asset.FromBytes(data.Data.Span, data.MediaType, data.Name);
            case DataContent data:
                return UrlAsset(data.Uri, data.MediaType);
            case UriContent uri:
                return UrlAsset(uri.Uri.ToString(), uri.MediaType);
            case HostedFileContent file:
                return new FileRefPart("meai", file.FileId) { ContentType = file.MediaType, Name = file.Name };
            case ErrorContent error:
                return new TextPart(error.Message ?? "");
            default:
                // Content this bridge has no mapping for, kept as the provider-neutral raw part so nothing is silently dropped.
                return new ProviderPart("meai", new JsonObject { ["type"] = content.GetType().Name });
        }
    }

    private static AssetPart UrlAsset(string? url, string? mediaType) =>
        new(new AssetRef { Storage = "url", Key = url ?? "", ContentType = mediaType ?? "application/octet-stream" }) { PublicUrl = url };

    private static ReasoningPart FromReasoning(TextReasoningContent thought)
    {
        string? provider = thought.AdditionalProperties?.TryGetValue(OneLlmProperties.Provider, out object? p) == true ? p as string : null;
        JsonObject? data = null;
        if (thought.ProtectedData is { Length: > 0 } blob)
        {
            if (provider is not null)
            {
                try
                {
                    data = JsonNode.Parse(blob) as JsonObject;
                }
                catch (JsonException)
                {
                    data = null;
                }
            }

            data ??= new JsonObject { ["protectedData"] = blob };
        }

        return new ReasoningPart(provider ?? "meai") { Display = thought.Text is { Length: > 0 } t ? t : null, Data = data };
    }

    private static List<Annotation>? FromAnnotations(IList<AIAnnotation>? annotations)
    {
        if (annotations is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<Annotation>(annotations.Count);
        foreach (AIAnnotation a in annotations)
        {
            if (a.RawRepresentation is Annotation original)
            {
                result.Add(original);
            }
            else if (a is CitationAnnotation c)
            {
                TextSpanAnnotatedRegion? span = c.AnnotatedRegions?.OfType<TextSpanAnnotatedRegion>().FirstOrDefault();
                result.Add(new Annotation
                {
                    Type = c.Url is null ? "citation" : "url_citation",
                    Title = c.Title,
                    Url = c.Url?.ToString(),
                    SourceId = c.FileId,
                    CitedText = c.Snippet,
                    Start = span?.StartIndex,
                    End = span?.EndIndex,
                });
            }
        }

        return result.Count == 0 ? null : result;
    }

    public static IReadOnlyList<Part> ToResultParts(FunctionResultContent result)
    {
        if (result.AdditionalProperties?.TryGetValue(OneLlmProperties.ResultParts, out object? kept) == true && kept is IReadOnlyList<Part> parts)
        {
            return parts;
        }

        if (result.Exception is { } error && result.Result is null)
        {
            return [new TextPart(error.Message)];
        }

        return ResultParts(result.Result);
    }

    public static List<Part> ResultPartsOf(object? value) => ResultParts(value);

    private static List<Part> ResultParts(object? value)
    {
        switch (value)
        {
            case null:
                return [];
            case string s:
                return [new TextPart(s)];
            case AIContent c:
                return [ToPart(c)];
            case JsonElement { ValueKind: JsonValueKind.String } e:
                return [new TextPart(e.GetString() ?? "")];
            case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                return [];
            case IEnumerable<AIContent> list:
                return [.. list.Select(ToPart)];
            default:
                return JsonBridge.ObjectToNode(value) is { } node ? [new JsonPart(node)] : [];
        }
    }

    private sealed class CallBuilder(FunctionCallContent content, int after)
    {
        public FunctionCallContent Content { get; } = content;

        public int After { get; } = after;

        public IReadOnlyList<Part> Result { get; set; } = [];

        public bool IsError { get; set; }
    }

    private sealed class TurnBuilder(ChatMessage source)
    {
        public ChatMessage Source { get; } = source;

        public List<Part> Parts { get; } = [];

        public List<CallBuilder> Calls { get; } = [];
    }

    /// <summary>
    /// Chat messages as OneLLM turns plus the system text. A tool message attaches its results to the earlier assistant turn that made the
    /// call; a result with no matching call is an error, because no provider accepts a tool result without its call.
    /// </summary>
    /// <exception cref="ArgumentException">A function result names a call that no earlier message made.</exception>
    public static (string? System, List<Message> Messages) ToMessages(IEnumerable<ChatMessage> messages, string? instructions)
    {
        var system = new StringBuilder();
        if (instructions is { Length: > 0 })
        {
            system.Append(instructions);
        }

        var turns = new List<object>();
        foreach (ChatMessage m in messages)
        {
            string role = m.Role.Value;
            if (role is "system" or "developer")
            {
                if (m.Text is { Length: > 0 } text)
                {
                    if (system.Length > 0)
                    {
                        system.Append("\n\n");
                    }

                    system.Append(text);
                }

                continue;
            }

            if (role == "assistant")
            {
                var turn = new TurnBuilder(m);
                foreach (AIContent c in m.Contents)
                {
                    if (c is FunctionCallContent call)
                    {
                        turn.Calls.Add(new CallBuilder(call, turn.Parts.Count));
                    }
                    else if (c is UsageContent)
                    {
                        continue;
                    }
                    else
                    {
                        turn.Parts.Add(ToPart(c));
                    }
                }

                turns.Add(turn);
                continue;
            }

            // User and tool messages: results go back to their calls; everything else is the user's turn.
            var userParts = new List<Part>();
            foreach (AIContent c in m.Contents)
            {
                if (c is FunctionResultContent result)
                {
                    CallBuilder target = FindCall(turns, result.CallId)
                        ?? throw new ArgumentException($"A function result names the call '{result.CallId}', but no earlier assistant message made that call.", nameof(messages));
                    target.Result = ToResultParts(result);
                    target.IsError = result.Exception is not null || result.AdditionalProperties?.TryGetValue(OneLlmProperties.IsError, out object? flag) == true && flag is true;
                }
                else if (c is not UsageContent)
                {
                    userParts.Add(ToPart(c));
                }
            }

            if (userParts.Count > 0)
            {
                turns.Add(new Message { Id = m.MessageId ?? "", User = userParts, DateTime = m.CreatedAt, When = WhenOf(m.AdditionalProperties) });
            }
        }

        var result2 = new List<Message>(turns.Count);
        foreach (object t in turns)
        {
            result2.Add(t is TurnBuilder b ? Build(b) : (Message)t);
        }

        return (system.Length > 0 ? system.ToString() : null, result2);
    }

    private static CallBuilder? FindCall(List<object> turns, string callId)
    {
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i] is TurnBuilder b)
            {
                foreach (CallBuilder c in b.Calls)
                {
                    if (c.Content.CallId == callId)
                    {
                        return c;
                    }
                }
            }
        }

        return null;
    }

    private static string? WhenOf(AdditionalPropertiesDictionary? props) =>
        props?.TryGetValue(OneLlmProperties.When, out object? v) == true ? v as string : null;

    private static Message Build(TurnBuilder b)
    {
        var calls = new List<ToolCall>(b.Calls.Count);
        foreach (CallBuilder c in b.Calls)
        {
            FunctionCallContent f = c.Content;
            AdditionalPropertiesDictionary? props = f.AdditionalProperties;
            string? invalid = props?.TryGetValue(OneLlmProperties.InvalidInputs, out object? raw) == true ? raw as string : null;
            calls.Add(new ToolCall(f.CallId, f.Name)
            {
                Inputs = invalid is null ? JsonBridge.FromArguments(f.Arguments) : null,
                InvalidInputs = invalid,
                Result = c.Result,
                IsError = c.IsError,
                After = c.After,
                When = WhenOf(props),
                Injected = props?.TryGetValue(OneLlmProperties.Injected, out object? inj) == true && inj is true,
                InjectedBy = props?.TryGetValue(OneLlmProperties.InjectedBy, out object? by) == true ? by as string : null,
                Routine = props?.TryGetValue(OneLlmProperties.Routine, out object? routine) == true ? routine as string : null,
            });
        }

        return new Message
        {
            Id = b.Source.MessageId ?? "",
            Assistant = b.Parts,
            ToolCalls = calls,
            DateTime = b.Source.CreatedAt,
            When = WhenOf(b.Source.AdditionalProperties),
        };
    }

    /// <summary>
    /// A response's assistant messages as one OneLLM turn. A client that runs functions itself returns the assistant message, the tool message
    /// and the final assistant message; their parts and calls join in order, with each call keeping its place.
    /// </summary>
    public static Message ToTurn(IEnumerable<ChatMessage> messages)
    {
        (_, List<Message> turns) = ToMessages(messages, null);
        if (turns.Count == 1)
        {
            return turns[0];
        }

        var parts = new List<Part>();
        var calls = new List<ToolCall>();
        foreach (Message turn in turns)
        {
            int offset = parts.Count;
            parts.AddRange(turn.Assistant);
            foreach (ToolCall c in turn.ToolCalls)
            {
                calls.Add(new ToolCall(c.Id, c.Tool)
                {
                    Routine = c.Routine,
                    Inputs = c.Inputs,
                    InvalidInputs = c.InvalidInputs,
                    Result = c.Result,
                    IsError = c.IsError,
                    When = c.When,
                    Injected = c.Injected,
                    InjectedBy = c.InjectedBy,
                    After = c.After == int.MaxValue ? int.MaxValue : c.After + offset,
                });
            }
        }

        return new Message { Id = turns.Count > 0 ? turns[0].Id : "", Assistant = parts, ToolCalls = calls, DateTime = turns.Count > 0 ? turns[0].DateTime : null };
    }
}
