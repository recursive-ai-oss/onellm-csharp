using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Messages;

/// <summary>
/// Builds an assistant <see cref="Message"/> from stream events. Streaming codecs feed it what they parse, and non-streaming codecs feed it the
/// same events made from a whole response, so both paths produce the same message by construction.
/// </summary>
/// <remarks>
/// Rules that make the two paths agree: blocks keep the order of their first event; text and tool arguments are collected as UTF-8 bytes and
/// decoded once, so a multi-byte character split across deltas is safe; a text block that stays empty is dropped; a tool call's arguments
/// that are not a JSON object leave <see cref="ToolCall.Inputs"/> null and the raw text in <see cref="ToolCall.InvalidInputs"/>; a call that
/// never sees its end event is still completed. Not thread-safe.
/// </remarks>
public sealed class MessageAccumulator
{
    private enum BlockKind
    {
        Text,
        Reasoning,
        Refusal,
        ToolCall,
        Hosted,
        Whole,
    }

    private sealed class Block(BlockKind kind)
    {
        public BlockKind Kind { get; } = kind;

        public ArrayBufferWriter<byte>? Bytes { get; set; }

        public List<Annotation>? Annotations { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public JsonObject? Data { get; set; }

        public Part? Part { get; set; }

        public Dictionary<string, object?>? Extension { get; set; }
    }

    private readonly List<Block> _blocks = [];
    private readonly Dictionary<int, Block> _byIndex = [];
    private Usage? _usage;
    private Dictionary<string, object?>? _messageExtension;

    private readonly List<Providers.ResponseWarning> _warnings = [];

    /// <summary>The provider's response id, once <see cref="StreamEventKind.MessageStart"/> was seen.</summary>
    public string? ResponseId { get; private set; }

    /// <summary>The model that answered, once known.</summary>
    public string? Model { get; private set; }

    /// <summary>Why the response stopped. <see cref="FinishReason.Unknown"/> until <see cref="StreamEventKind.Done"/> arrives.</summary>
    public FinishReason Finish { get; private set; }

    /// <summary>The provider's own word for the finish reason.</summary>
    public string? FinishReasonRaw { get; private set; }

    /// <summary>The notices from <see cref="StreamEventKind.Warning"/> events, in arrival order.</summary>
    public IReadOnlyList<Providers.ResponseWarning> Warnings => _warnings;

    /// <summary>True once a <see cref="StreamEventKind.Done"/> event was added.</summary>
    public bool IsDone { get; private set; }

    /// <summary>Applies one event.</summary>
    /// <param name="e">The event. Its <see cref="StreamEvent.Utf8"/> is copied before this method returns.</param>
    public void Add(in StreamEvent e)
    {
        switch (e.Kind)
        {
            case StreamEventKind.MessageStart:
                ResponseId = e.Id ?? ResponseId;
                Model = e.Name ?? Model;
                break;
            case StreamEventKind.TextDelta:
                Append(GetOrAdd(e.Index, BlockKind.Text), e.Utf8.Span);
                break;
            case StreamEventKind.ReasoningDelta:
                Append(GetOrAdd(e.Index, BlockKind.Reasoning), e.Utf8.Span);
                break;
            case StreamEventKind.ReasoningEnd:
                Block r = GetOrAdd(e.Index, BlockKind.Reasoning);
                r.Name = e.Name;
                r.Data = e.Data;
                break;
            case StreamEventKind.RefusalDelta:
                Append(GetOrAdd(e.Index, BlockKind.Refusal), e.Utf8.Span);
                break;
            case StreamEventKind.ToolCallStart:
                Block t = GetOrAdd(e.Index, BlockKind.ToolCall);
                t.Id = e.Id;
                t.Name = e.Name;
                if (!e.Extension.IsEmpty && NaturalObjectJsonConverter.Parse(e.Extension.Span) is Dictionary<string, object?> toolFields)
                {
                    t.Extension = toolFields;
                }

                break;
            case StreamEventKind.MessageExtension:
                if (e.Name is { Length: > 0 } key && !e.Extension.IsEmpty)
                {
                    (_messageExtension ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = NaturalObjectJsonConverter.Parse(e.Extension.Span);
                }

                break;
            case StreamEventKind.ToolCallArgumentsDelta:
                Append(GetOrAdd(e.Index, BlockKind.ToolCall), e.Utf8.Span);
                break;
            case StreamEventKind.ToolCallEnd:
                break;
            case StreamEventKind.HostedToolCallStart:
                GetOrAdd(e.Index, BlockKind.Hosted).Part = e.Part;
                break;
            case StreamEventKind.HostedToolCallArgumentsDelta:
                Append(GetOrAdd(e.Index, BlockKind.Hosted), e.Utf8.Span);
                break;
            case StreamEventKind.HostedToolCallEnd:
                GetOrAdd(e.Index, BlockKind.Hosted).Part = e.Part;
                break;
            case StreamEventKind.Annotation:
                if (e.Annotation is { } a)
                {
                    (GetOrAdd(e.Index, BlockKind.Text).Annotations ??= []).Add(a);
                }

                break;
            case StreamEventKind.PartAdded:
                if (e.Part is { } p)
                {
                    GetOrAdd(e.Index, BlockKind.Whole).Part = p;
                }

                break;
            case StreamEventKind.Usage:
                if (e.Usage is { } u)
                {
                    _usage = _usage is null ? u : _usage.MergeLater(u);
                }

                break;
            case StreamEventKind.Warning:
                if (e.Notice is { } notice)
                {
                    _warnings.Add(notice);
                }

                break;
            case StreamEventKind.Done:
                IsDone = true;
                Finish = e.Finish;
                FinishReasonRaw = e.Name;
                break;
        }
    }

    /// <summary>Adds text to a block. Use this from non-streaming codecs instead of building a <see cref="StreamEvent"/>.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="text">The text.</param>
    public void AddText(int index, string text) => Append(GetOrAdd(index, BlockKind.Text), Encoding.UTF8.GetBytes(text));

    /// <summary>Adds a whole tool call from a non-streaming response.</summary>
    /// <param name="index">The block index.</param>
    /// <param name="id">The call id.</param>
    /// <param name="tool">The tool's name.</param>
    /// <param name="argumentsJson">The arguments as JSON text.</param>
    public void AddToolCall(int index, string id, string tool, string argumentsJson)
    {
        Block b = GetOrAdd(index, BlockKind.ToolCall);
        b.Id = id;
        b.Name = tool;
        Append(b, Encoding.UTF8.GetBytes(argumentsJson));
    }

    /// <summary>Builds the message from everything added so far. May be called more than once; the accumulator is not reset.</summary>
    /// <returns>The assistant message. Its id and time are left for the runtime to stamp.</returns>
    public Message ToMessage()
    {
        var parts = new List<Part>(_blocks.Count);
        var calls = new List<ToolCall>();
        foreach (Block b in _blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Text:
                    string text = Decode(b);
                    if (text.Length > 0)
                    {
                        parts.Add(new TextPart(text) { Annotations = b.Annotations is { Count: > 0 } ? [.. b.Annotations] : null });
                    }

                    break;
                case BlockKind.Reasoning:
                    string display = Decode(b);
                    parts.Add(new ReasoningPart(b.Name ?? "") { Display = display.Length > 0 ? display : null, Data = b.Data });
                    break;
                case BlockKind.Refusal:
                    parts.Add(new RefusalPart(Decode(b)));
                    break;
                case BlockKind.ToolCall:
                    calls.Add(BuildCall(b, parts.Count));
                    break;
                case BlockKind.Hosted:
                    if (BuildHosted(b) is { } hosted)
                    {
                        parts.Add(hosted);
                    }

                    break;
                case BlockKind.Whole:
                    if (b.Part is { } whole)
                    {
                        parts.Add(whole);
                    }

                    break;
            }
        }

        Usage? usage = _usage;
        if (usage is not null && usage.Model.Length == 0 && Model is { Length: > 0 })
        {
            usage = usage.MergeLater(new Usage { Model = Model });
        }

        return new Message { Assistant = parts, ToolCalls = calls, Usage = usage, ExtensionData = _messageExtension is null ? null : new Dictionary<string, object?>(_messageExtension) };
    }

    private Block GetOrAdd(int index, BlockKind kind)
    {
        if (_byIndex.TryGetValue(index, out Block? existing))
        {
            return existing;
        }

        var block = new Block(kind);
        _byIndex[index] = block;
        _blocks.Add(block);
        return block;
    }

    private static void Append(Block block, ReadOnlySpan<byte> utf8)
    {
        block.Bytes ??= new ArrayBufferWriter<byte>(Math.Max(utf8.Length, 64));
        block.Bytes.Write(utf8);
    }

    private static string Decode(Block block) => block.Bytes is null ? "" : Encoding.UTF8.GetString(block.Bytes.WrittenSpan);

    private static ToolCall BuildCall(Block b, int after)
    {
        JsonObject? inputs = null;
        string? invalid = null;
        ReadOnlySpan<byte> raw = b.Bytes is null ? default : b.Bytes.WrittenSpan;
        if (!raw.IsEmpty && !IsWhitespace(raw))
        {
            try
            {
                inputs = JsonNode.Parse(raw) as JsonObject;
            }
            catch (JsonException)
            {
                inputs = null;
            }

            if (inputs is null)
            {
                invalid = Encoding.UTF8.GetString(raw);
            }
        }

        return new ToolCall(b.Id ?? "", b.Name ?? "") { Inputs = inputs, InvalidInputs = invalid, After = after, ExtensionData = b.Extension };
    }

    private static HostedToolCallPart? BuildHosted(Block b)
    {
        if (b.Part is not HostedToolCallPart skeleton)
        {
            return null;
        }

        if (skeleton.Inputs is not null || b.Bytes is null || b.Bytes.WrittenCount == 0)
        {
            return skeleton;
        }

        JsonObject? inputs;
        try
        {
            inputs = JsonNode.Parse(b.Bytes.WrittenSpan) as JsonObject;
        }
        catch (JsonException)
        {
            inputs = null;
        }

        return new HostedToolCallPart(skeleton.Tool)
        {
            Provider = skeleton.Provider,
            Id = skeleton.Id,
            Status = skeleton.Status,
            Inputs = inputs,
            Result = skeleton.Result,
            Cost = skeleton.Cost,
            Data = skeleton.Data,
            ExtensionData = skeleton.ExtensionData,
        };
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (byte c in bytes)
        {
            if (c is not (0x20 or 0x09 or 0x0A or 0x0D))
            {
                return false;
            }
        }

        return true;
    }
}
