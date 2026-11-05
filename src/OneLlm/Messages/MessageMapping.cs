using System.Text.Json.Nodes;

namespace OneLlm.Messages;

/// <summary>Maps between the public message model and the wire classes of the file format.</summary>
internal static class MessageMapping
{
    // ---- Part ----

    public static Part FromWire(PartWire w)
    {
        Dictionary<string, object?>? ext = w.ExtensionData;
        if (w.Asset is not null || w.PublicUrl is not null || w.ResizedImage is not null || HasLegacyInlineKey(ext))
        {
            // A text field next to an asset has no home on AssetPart; it stays in extension data so nothing is lost.
            if (w.Text is not null)
            {
                ext = new Dictionary<string, object?>(ext ?? []) { ["text"] = w.Text };
            }

            return new AssetPart(w.Asset) { PublicUrl = w.PublicUrl, PublicUrlExpires = w.PublicUrlExpires, ResizedImage = w.ResizedImage, ExtensionData = ext };
        }

        if (w.Refusal is not null)
        {
            return new RefusalPart(w.Refusal) { ExtensionData = ext };
        }

        if (w.Json is not null)
        {
            return new JsonPart(w.Json) { ExtensionData = ext };
        }

        if (w.FileRef is not null)
        {
            return new FileRefPart(w.Provider ?? "", w.FileRef) { ContentType = w.ContentType, Name = w.Name, Uri = w.Uri, ExtensionData = ext };
        }

        if (w.HostedToolCall is not null)
        {
            return new HostedToolCallPart(w.HostedToolCall)
            {
                Provider = w.Provider,
                Id = w.Id,
                Status = w.Status,
                Inputs = w.Inputs,
                Result = w.Result,
                Cost = w.Cost,
                Data = w.Data,
                ExtensionData = ext,
            };
        }

        if (w.ProviderPart is not null)
        {
            return new ProviderPart(w.ProviderPart, w.Raw ?? new JsonObject()) { Type = w.Type, ExtensionData = ext };
        }

        if (w.Reasoning is not null)
        {
            return new ReasoningPart(w.Reasoning) { Display = w.Display, Data = w.Data, ExtensionData = ext };
        }

        return new TextPart(w.Text ?? "") { Annotations = w.Annotations, ExtensionData = ext };
    }

    public static PartWire ToWire(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var w = new PartWire { ExtensionData = part.ExtensionData };
        switch (part)
        {
            case TextPart t:
                w.Text = t.Text.Length == 0 ? null : t.Text;
                w.Annotations = t.Annotations is { Count: > 0 } notes ? [.. notes] : null;
                break;
            case AssetPart a:
                w.Asset = a.Asset;
                w.PublicUrl = a.PublicUrl;
                w.PublicUrlExpires = a.PublicUrlExpires;
                w.ResizedImage = a.ResizedImage;
                break;
            case RefusalPart r:
                w.Refusal = r.Text;
                break;
            case JsonPart j:
                w.Json = j.Value;
                break;
            case FileRefPart f:
                w.FileRef = f.FileId;
                w.Provider = f.Provider;
                w.ContentType = f.ContentType;
                w.Name = f.Name;
                w.Uri = f.Uri;
                break;
            case HostedToolCallPart h:
                w.HostedToolCall = h.Tool;
                w.Provider = h.Provider;
                w.Id = h.Id;
                w.Status = h.Status;
                w.Inputs = h.Inputs;
                w.Result = h.Result;
                w.Cost = h.Cost;
                w.Data = h.Data;
                break;
            case ProviderPart p:
                w.ProviderPart = p.Provider;
                w.Type = p.Type;
                w.Raw = p.Raw;
                break;
            case ReasoningPart rs:
                w.Reasoning = rs.Provider;
                w.Display = rs.Display;
                w.Data = rs.Data;
                break;
            default:
                throw new InvalidOperationException($"Unknown part type {part.GetType().Name}.");
        }

        return w;
    }

    private static bool HasLegacyInlineKey(Dictionary<string, object?>? ext) =>
        ext is not null && (ext.ContainsKey("image") || ext.ContainsKey("audio") || ext.ContainsKey("video") || ext.ContainsKey("pdf"));

    // ---- Message ----

    // Order index tokens: a<i> = assistant[i], r<i> = reasoning[i], x<i> = extraParts[i], t<i> = toolCalls[i].
    public static Message FromWire(MessageWire w)
    {
        var assistantItems = w.Assistant ?? [];
        var reasoning = w.Reasoning ?? [];
        var extra = w.ExtraParts ?? [];
        var wireCalls = w.ToolCalls ?? [];

        var calls = new ToolCall[wireCalls.Count];
        var parts = new List<Part>(assistantItems.Count + reasoning.Count + extra.Count);
        var afterOfCall = new int[wireCalls.Count];
        Array.Fill(afterOfCall, int.MaxValue);

        var usedA = new bool[assistantItems.Count];
        var usedR = new bool[reasoning.Count];
        var usedX = new bool[extra.Count];
        var usedT = new bool[wireCalls.Count];

        if (w.PartOrder is { } order)
        {
            foreach (string token in order)
            {
                if (token.Length < 2 || !int.TryParse(token.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i))
                {
                    continue;
                }

                switch (token[0])
                {
                    case 'a' when i < assistantItems.Count && !usedA[i]:
                        usedA[i] = true;
                        parts.Add(assistantItems[i]);
                        break;
                    case 'r' when i < reasoning.Count && !usedR[i]:
                        usedR[i] = true;
                        parts.Add(ReasoningFromWire(reasoning[i]));
                        break;
                    case 'x' when i < extra.Count && !usedX[i]:
                        usedX[i] = true;
                        parts.Add(extra[i]);
                        break;
                    case 't' when i < wireCalls.Count && !usedT[i]:
                        usedT[i] = true;
                        afterOfCall[i] = parts.Count;
                        break;
                }
            }
        }

        // Anything the index did not place (no index at all, or a hand-edited one) follows in the default order:
        // reasoning, then assistant items, then extra parts, then tool calls.
        for (int i = 0; i < reasoning.Count; i++)
        {
            if (!usedR[i])
            {
                parts.Add(ReasoningFromWire(reasoning[i]));
            }
        }

        for (int i = 0; i < assistantItems.Count; i++)
        {
            if (!usedA[i])
            {
                parts.Add(assistantItems[i]);
            }
        }

        for (int i = 0; i < extra.Count; i++)
        {
            if (!usedX[i])
            {
                parts.Add(extra[i]);
            }
        }

        for (int i = 0; i < wireCalls.Count; i++)
        {
            calls[i] = ToolCallFromWire(wireCalls[i], afterOfCall[i]);
        }

        return new Message
        {
            Id = w.Id,
            User = w.User ?? [],
            Assistant = parts,
            ToolCalls = calls,
            DateTime = w.DateTime,
            Source = w.Source,
            Cache = w.Cache,
            Usage = w.Usage,
            When = w.When,
            ExtensionData = w.ExtensionData,
        };
    }

    public static MessageWire ToWire(Message m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var assistantItems = new List<Part>();
        var reasoning = new List<ReasoningWire>();
        var extra = new List<Part>();
        var order = new List<string>();
        IReadOnlyList<ToolCall> calls = m.ToolCalls;

        // Tool calls sit between the parts they follow. A call's position is clamped to the part count; calls with the same
        // position keep their order in the list.
        int n = m.Assistant.Count;
        var callsAt = new List<int>[n + 1];
        for (int c = 0; c < calls.Count; c++)
        {
            int at = Math.Min(Math.Max(calls[c].After, 0), n);
            (callsAt[at] ??= []).Add(c);
        }

        for (int p = 0; p <= n; p++)
        {
            if (callsAt[p] is { } here)
            {
                foreach (int c in here)
                {
                    order.Add("t" + c.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (p == n)
            {
                break;
            }

            Part part = m.Assistant[p];
            switch (part)
            {
                case TextPart or AssetPart:
                    order.Add("a" + assistantItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    assistantItems.Add(part);
                    break;
                case ReasoningPart r:
                    order.Add("r" + reasoning.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    reasoning.Add(new ReasoningWire { Provider = r.Provider, Display = r.Display, Data = r.Data, ExtensionData = r.ExtensionData });
                    break;
                default:
                    order.Add("x" + extra.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    extra.Add(part);
                    break;
            }
        }

        return new MessageWire
        {
            Id = m.Id,
            User = m.User.Count == 0 ? null : [.. m.User],
            Assistant = assistantItems.Count == 0 ? null : assistantItems,
            ToolCalls = calls.Count == 0 ? null : [.. calls.Select(ToolCallToWire)],
            Reasoning = reasoning.Count == 0 ? null : reasoning,
            DateTime = m.DateTime,
            Source = m.Source,
            Cache = m.Cache,
            Usage = m.Usage,
            When = m.When,
            PartOrder = IsDefaultOrder(order) ? null : order,
            ExtraParts = extra.Count == 0 ? null : extra,
            ExtensionData = m.ExtensionData,
        };
    }

    // The default order is what a reader assumes without an index: every reasoning block, then the assistant items, then the tool calls.
    // Extra parts always need the index, because only the index says where they go.
    private static bool IsDefaultOrder(List<string> order)
    {
        int stage = 0;
        foreach (string token in order)
        {
            int tokenStage = token[0] switch { 'r' => 0, 'a' => 1, 't' => 3, _ => -1 };
            if (tokenStage < stage)
            {
                return false;
            }

            stage = tokenStage;
        }

        // Within each group the indexes must also run in order for the default to reproduce them.
        int lastR = -1, lastA = -1, lastT = -1;
        foreach (string token in order)
        {
            int i = int.Parse(token.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            switch (token[0])
            {
                case 'r' when i != ++lastR:
                case 'a' when i != ++lastA:
                case 't' when i != ++lastT:
                    return false;
            }
        }

        return true;
    }

    private static ReasoningPart ReasoningFromWire(ReasoningWire r) =>
        new(r.Provider) { Display = r.Display, Data = r.Data, ExtensionData = r.ExtensionData };

    private static ToolCall ToolCallFromWire(ToolCallWire t, int after) => new(t.Id, t.Tool)
    {
        Routine = t.Routine,
        Inputs = t.Inputs,
        Result = t.Result ?? [],
        IsError = t.IsError,
        Cost = t.Cost,
        CostLines = t.CostLines,
        When = t.When,
        Injected = t.Injected,
        InjectedBy = t.InjectedBy,
        After = after,
        ExtensionData = t.ExtensionData,
    };

    private static ToolCallWire ToolCallToWire(ToolCall t) => new()
    {
        Id = t.Id,
        Tool = t.Tool,
        Routine = t.Routine,
        Inputs = t.Inputs is { Count: 0 } ? null : t.Inputs,
        Result = t.Result.Count == 0 ? null : [.. t.Result],
        IsError = t.IsError,
        Cost = t.Cost,
        CostLines = t.CostLines is { Count: > 0 } ? [.. t.CostLines] : null,
        When = t.When,
        Injected = t.Injected,
        InjectedBy = t.InjectedBy,
        ExtensionData = t.ExtensionData,
    };
}
