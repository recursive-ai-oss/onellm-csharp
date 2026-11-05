using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;
using RecursiveAI.OneHtml;

namespace OneLlm.Web;

/// <summary>
/// fetch_webpage_markdown: downloads a page from a granted host and returns it as markdown. HTML is converted with <see cref="HtmlToMarkdown"/>;
/// text, markdown and JSON are returned as they are; PDFs and other binary types are refused. Output is cut at a size limit with a note.
/// </summary>
/// <remarks>The tool reaches the network only through the runtime's grants: the host (and every redirect hop) must be allowed with
/// <see cref="Capabilities.Network(string[])"/> or <see cref="Capabilities.AnyPublicHosts"/>, and an address that is loopback, private or
/// link-local is refused unless it is granted by address.</remarks>
public sealed class FetchWebpageMarkdownTool : ICapabilityTool
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "fetch_webpage_markdown";

    private const string Description = "Fetch a webpage and convert it to markdown format. Useful for extracting readable content from web pages for analysis or processing. Returns the markdown content as plain text.";

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The URL of the webpage to fetch and convert to markdown" }
          },
          "required": ["url"]
        }
        """;

    private readonly int _maxDownloadBytes;
    private readonly int _maxMarkdownBytes;

    /// <summary>Creates the tool.</summary>
    /// <param name="maxMarkdownBytes">The most UTF-8 bytes of markdown to return; more is cut at a line break with a note. Default 100 KiB.</param>
    /// <param name="maxDownloadBytes">The most bytes of page to download. Default 5 MiB; a larger page is refused.</param>
    public FetchWebpageMarkdownTool(int maxMarkdownBytes = 100 * 1024, int maxDownloadBytes = 5 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMarkdownBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDownloadBytes, 1024);
        _maxMarkdownBytes = maxMarkdownBytes;
        _maxDownloadBytes = maxDownloadBytes;
        Definition = new ToolSpec(ToolName, Description, JsonNode.Parse(Schema)!.AsObject()) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <inheritdoc />
    public ToolSpec Definition { get; }

    /// <inheritdoc />
    public ToolCapability Requires => ToolCapability.Network;

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        if (!inputs.TryGetPropertyValue("url", out JsonNode? n) || n is not JsonValue v || !v.TryGetValue(out string? url) || string.IsNullOrWhiteSpace(url))
        {
            throw new ToolInputException("url", "is required");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return ToolResult.Error("Error: url must be an absolute http or https URL");
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), context.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using HttpResponseMessage response = await EgressClient.SendAsync(context.Capabilities, HttpMethod.Get, uri,
                [new("Accept", "text/html,application/xhtml+xml,text/markdown,text/plain;q=0.9,*/*;q=0.5")], null, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Error(string.Create(CultureInfo.InvariantCulture, $"Error: Failed to fetch webpage: the server answered {(int)response.StatusCode} {response.ReasonPhrase}"));
            }

            (byte[] bytes, bool truncated) = await EgressClient.ReadBodyAsync(response, _maxDownloadBytes, linked.Token).ConfigureAwait(false);
            if (truncated)
            {
                return ToolResult.Error($"Error: The page is larger than the {_maxDownloadBytes / 1024} KiB download limit.");
            }

            string? type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            Uri finalUri = response.RequestMessage?.RequestUri ?? uri;
            string markdown;
            if (type == "application/pdf")
            {
                return ToolResult.Error("Error: PDF documents are not supported by fetch_webpage_markdown.");
            }

            if (type is null or "text/html" or "application/xhtml+xml" || LooksLikeHtml(type, bytes))
            {
                Document doc = Document.Parse(bytes, new ParseOptions
                {
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Limits = new ParseLimits { MaxInputLength = _maxDownloadBytes },
                }, linked.Token);
                markdown = HtmlToMarkdown.Convert(doc, finalUri);
            }
            else if (type.StartsWith("text/", StringComparison.Ordinal) || type.EndsWith("json", StringComparison.Ordinal) || type.EndsWith("xml", StringComparison.Ordinal) || type.Contains("yaml", StringComparison.Ordinal))
            {
                markdown = new UTF8Encoding(false, false).GetString(bytes);
            }
            else
            {
                return ToolResult.Error($"Error: Cannot convert content of type '{type}' to markdown.");
            }

            return ToolResult.FromText(Cut(markdown));
        }
        catch (CapabilityMissingException e)
        {
            return ToolResult.Error("Error: " + e.Message);
        }
        catch (HttpRequestException e)
        {
            return ToolResult.Error("Error: Failed to fetch webpage: " + e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("Error: Failed to fetch webpage: the request timed out after 30 seconds.");
        }
    }

    // A response with a generic type that still starts like a page is treated as one.
    private static bool LooksLikeHtml(string type, byte[] bytes) =>
        type is "application/octet-stream" or "text/plain" && bytes.AsSpan(0, Math.Min(bytes.Length, 256)).IndexOf("<html"u8) >= 0;

    // Cut at a UTF-8 safe boundary, on a line break when one is near, and say how much was left out.
    private string Cut(string text)
    {
        int total = Encoding.UTF8.GetByteCount(text);
        if (total <= _maxMarkdownBytes)
        {
            return text;
        }

        byte[] all = Encoding.UTF8.GetBytes(text);
        int end = _maxMarkdownBytes;
        while (end > 0 && (all[end] & 0xC0) == 0x80)
        {
            end--;
        }

        string cut = Encoding.UTF8.GetString(all, 0, end);
        int nl = cut.LastIndexOf('\n');
        if (nl > 0 && Encoding.UTF8.GetByteCount(cut[nl..]) < 1024)
        {
            cut = cut[..nl];
        }

        return cut + string.Create(CultureInfo.InvariantCulture, $"\n\n[Content truncated — page was {total} bytes, limit is {_maxMarkdownBytes} bytes. Use search_web to find specific information from this site instead of fetching the full page.]");
    }
}
