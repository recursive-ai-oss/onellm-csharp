using System.Globalization;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Counting;

/// <summary>
/// The pieces of a request that cost input tokens, collected once so every counting tier looks at the same things: text segments (which a local
/// tokenizer can count exactly), the number of messages, the attached files, and the tool definitions.
/// </summary>
internal sealed class RequestContent
{
    public List<string> Texts { get; } = [];

    public int MessageCount { get; private set; }

    public List<AssetPart> Assets { get; } = [];

    public int ToolCount { get; private set; }

    public bool HasTools => ToolCount > 0;

    public static RequestContent Collect(ChatRequest request)
    {
        var content = new RequestContent();
        if (!string.IsNullOrEmpty(request.System))
        {
            content.Texts.Add(request.System);
        }

        foreach (Message message in request.Messages)
        {
            content.MessageCount++;
            content.AddParts(message.User);
            content.AddParts(message.Assistant);
            foreach (ToolCall call in message.ToolCalls)
            {
                content.Texts.Add(call.Tool);
                if (call.Inputs is not null)
                {
                    content.Texts.Add(call.Inputs.ToJsonString());
                }

                content.AddParts(call.Result);
            }
        }

        foreach (ToolDefinition tool in request.Tools)
        {
            content.ToolCount++;
            content.Texts.Add(tool.Name);
            content.Texts.Add(tool.Description);
            content.Texts.Add(tool.InputSchema.ToJsonString());
        }

        content.ToolCount += request.HostedTools.Count;
        if (request.ResponseFormat is { Schema: { } schema })
        {
            content.Texts.Add(schema.ToJsonString());
        }

        return content;
    }

    private void AddParts(IReadOnlyList<Part> parts)
    {
        foreach (Part part in parts)
        {
            switch (part)
            {
                case TextPart t:
                    Texts.Add(t.Text);
                    break;
                case RefusalPart r:
                    Texts.Add(r.Text);
                    break;
                case JsonPart j:
                    Texts.Add(j.Value.ToJsonString());
                    break;
                case ReasoningPart r:
                    // Replayed reasoning is sent back as opaque data or readable text; count whichever is there.
                    if (r.Display is not null)
                    {
                        Texts.Add(r.Display);
                    }
                    else if (r.Data is JsonNode data)
                    {
                        Texts.Add(data.ToJsonString());
                    }

                    break;
                case HostedToolCallPart h:
                    if (h.Result is { } result)
                    {
                        Texts.Add(result.ToJsonString());
                    }

                    break;
                case ProviderPart p:
                    Texts.Add(p.Raw.ToJsonString());
                    break;
                case AssetPart { Asset: not null } a:
                    Assets.Add(a);
                    break;
            }
        }
    }
}

/// <summary>
/// Tier 3 of token counting: a calibrated estimate from character counts and per-file allowances. It needs no network and no vocabulary, is
/// the same everywhere, and errs slightly high so a budget check built on it is not optimistic.
/// </summary>
/// <remarks>
/// <para><b>Text.</b> Characters per token come from the model family: OpenAI-style and Gemini models 4.0 (both vendors state that a token is roughly
/// four characters of English text: OpenAI's tokenizer help page and Google's Gemini token guide); Anthropic models 3.5, which is
/// a conservative choice because Anthropic publishes no ratio and its newer tokenizers produce more tokens for the same text; any other family
/// 3.5. A model entry can calibrate its own ratio with the property <c>charsPerToken</c>. ASCII characters count at that ratio; other Latin, Greek,
/// Cyrillic, Arabic and Hebrew characters (two UTF-8 bytes) count double; CJK and other characters count one token each; a surrogate pair (emoji,
/// rare scripts) counts one token.</para>
/// <para><b>Allowances</b> (agent defaults, chosen to be at or above typical): 4 tokens per message and 3 to prime the reply; per tool 8 tokens
/// beyond its definition text, and for Anthropic 346 tokens once when any tool is present (the size of its tool-use system prompt, from
/// Anthropic's tool use documentation).</para>
/// <para><b>Images</b> use each vendor's documented formula when the dimensions are known (from the message's resized copy) and a typical size
/// otherwise: Anthropic width × height ÷ 750, capped at 1,600; OpenAI 85 plus 170 per 512-pixel tile after scaling the image to fit 2048 pixels and
/// its short side to 768; Gemini 258 up to 384 × 384 and 258 per 768 × 768 tile above. <b>PDFs</b>: one page per 60 KB (at least one), at 2,250
/// tokens a page for Anthropic (the middle of the 1,500 to 3,000 it documents), 258 for Gemini (as documented), 1,500 otherwise. <b>Audio</b>:
/// one second per 16 KB, at 32 tokens a second for Gemini (as documented) and 10 otherwise. <b>Video</b>: one second per 250 KB at 263 tokens a second
/// (Gemini's documented rate). Other files count a quarter of their byte size.</para>
/// </remarks>
public static class TokenEstimator
{
    private const int DefaultCharsPerTokenTenths = 35;
    private const int MessageAllowance = 4;
    private const int ReplyPriming = 3;
    private const int ToolAllowance = 8;
    private const int AnthropicToolPrompt = 346;

    /// <summary>Estimates the input tokens of a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="model">The resolved model, used to pick the family and the model's own <c>charsPerToken</c>; null for the generic ratio.</param>
    /// <returns>The estimate, reported as <see cref="TokenCountTier.Estimate"/>.</returns>
    public static TokenCount Estimate(ChatRequest request, ResolvedModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestContent content = RequestContent.Collect(request);
        long milli = 0;
        int tenths = CharsPerTokenTenths(model);
        foreach (string text in content.Texts)
        {
            milli += TextMilliTokens(text, tenths);
        }

        long tokens = (milli + 999) / 1000;
        return new TokenCount(tokens + Overhead(content, model), TokenCountTier.Estimate);
    }

    /// <summary>Estimates the tokens of one piece of text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="model">The resolved model, or null for the generic ratio.</param>
    /// <returns>The estimated tokens, rounded up.</returns>
    public static long EstimateText(string text, ResolvedModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (TextMilliTokens(text, CharsPerTokenTenths(model)) + 999) / 1000;
    }

    /// <summary>The tokens a request costs beyond the text that a local tokenizer can count: message and tool allowances, images and other files.</summary>
    internal static long Overhead(RequestContent content, ResolvedModel? model)
    {
        string family = Family(model);
        long total = (long)content.MessageCount * MessageAllowance + (content.MessageCount > 0 ? ReplyPriming : 0);
        total += (long)content.ToolCount * ToolAllowance;
        if (family == "anthropic" && content.HasTools)
        {
            total += AnthropicToolPrompt;
        }

        foreach (AssetPart part in content.Assets)
        {
            total += AssetTokens(part, family);
        }

        return total;
    }

    internal static string Family(ResolvedModel? model)
    {
        string name = (model?.Provider.Name ?? string.Empty).ToLowerInvariant();
        string deployed = name.Length == 0 ? (model?.Name ?? string.Empty).ToLowerInvariant() : name;
        if (deployed.Contains("anthropic", StringComparison.Ordinal) || deployed.Contains("claude", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (deployed.Contains("gemini", StringComparison.Ordinal) || deployed.Contains("google", StringComparison.Ordinal))
        {
            return "gemini";
        }

        if (deployed.Contains("openai", StringComparison.Ordinal) || deployed.Contains("chatgpt", StringComparison.Ordinal))
        {
            return "openai";
        }

        return "other";
    }

    private static int CharsPerTokenTenths(ResolvedModel? model)
    {
        if (model?.Model?.Properties is { } properties && properties.TryGetValue("charsPerToken", out object? value) && value is not null)
        {
            try
            {
                decimal chars = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (chars > 0m)
                {
                    return (int)Math.Clamp(Math.Round(chars * 10m), 5m, 100m);
                }
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                // An unreadable calibration falls back to the family ratio below.
            }
        }

        return Family(model) is "openai" or "gemini" ? 40 : DefaultCharsPerTokenTenths;
    }

    // Thousandths of a token, so a long text is summed exactly and rounded once.
    private static long TextMilliTokens(string text, int charsPerTokenTenths)
    {
        long ascii = 10_000 / charsPerTokenTenths;
        long milli = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
            {
                milli += ascii;
            }
            else if (c < 0x800)
            {
                milli += ascii * 2;
            }
            else if (char.IsSurrogate(c))
            {
                milli += 500;
            }
            else
            {
                milli += 1000;
            }
        }

        return milli;
    }

    private static long AssetTokens(AssetPart part, string family)
    {
        AssetRef asset = part.Asset!;
        string type = asset.ContentType;
        long size = Math.Max(asset.Size, 0);
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return part.ResizedImage is { Width: > 0, Height: > 0 } r ? ImageTokens(r.Width, r.Height, family) : DefaultImageTokens(family);
        }

        if (type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            long pages = Math.Max(1, (size + 59_999) / 60_000);
            return pages * family switch { "anthropic" => 2250, "gemini" => 258, _ => 1500 };
        }

        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            long seconds = Math.Max(1, (size + 15_999) / 16_000);
            return seconds * (family == "gemini" ? 32 : 10);
        }

        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            long seconds = Math.Max(1, (size + 249_999) / 250_000);
            return seconds * 263;
        }

        return (size + 3) / 4;
    }

    private static long DefaultImageTokens(string family) => family switch
    {
        "anthropic" => 1600,
        "gemini" => 258 * 4,
        _ => 85 + (170 * 4),
    };

    /// <summary>The documented image token formula of a vendor family for an image of known size.</summary>
    internal static long ImageTokens(int width, int height, string family)
    {
        switch (family)
        {
            case "anthropic":
                return Math.Min(1600, ((long)width * height / 750) + 1);
            case "gemini":
                if (width <= 384 && height <= 384)
                {
                    return 258;
                }

                return 258L * (((width + 767) / 768) * ((height + 767) / 768));
            default:
                {
                    // Fit within 2048 x 2048, then scale the short side down to 768, then count 512-pixel tiles.
                    decimal w = width;
                    decimal h = height;
                    decimal fit = Math.Min(1m, 2048m / Math.Max(w, h));
                    w *= fit;
                    h *= fit;
                    decimal shrink = Math.Min(1m, 768m / Math.Min(w, h));
                    w *= shrink;
                    h *= shrink;
                    long tiles = (long)Math.Ceiling(w / 512m) * (long)Math.Ceiling(h / 512m);
                    return 85 + (170 * Math.Max(1, tiles));
                }
        }
    }
}
