using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Messages;

namespace OneLlm.Tools.Builtins;

/// <summary>read_file: a text file (optionally with line numbers), a one-level directory listing, an image or video as an asset, or an http(s) URL.</summary>
internal sealed class ReadFileTool : BuiltinTool
{
    private static readonly Dictionary<string, string> s_media = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".tiff"] = "image/tiff", [".tif"] = "image/tiff",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime", [".m4v"] = "video/x-m4v",
        [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska",
    };

    private readonly BuiltinOptions _options;

    public ReadFileTool(BuiltinOptions options)
        : base(Spec("read_file", BuiltinSchemas.ReadFileDescription, BuiltinSchemas.ReadFileSchema, TimeSpan.FromSeconds(30)), ToolCapability.FileRead)
    {
        _options = options;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = Args.RequiredString(inputs, "path");
        bool numbers = Args.Bool(inputs, "includeLineNumbers", false);
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadUrlAsync(path, numbers, context, cancellationToken).ConfigureAwait(false);
        }

        string real = context.Capabilities.ResolveRead(path);
        if (Directory.Exists(real))
        {
            return List(path, real);
        }

        if (!File.Exists(real))
        {
            return Fail($"Failed to read file: '{path}' does not exist.");
        }

        byte[] data;
        await using (FileStream stream = GuardedFile.OpenRead(context.Capabilities, path))
        {
            if (stream.Length > _options.MaxReadBytes)
            {
                return Fail($"The file is {TextFile.HumanSize(stream.Length)}, over the {TextFile.HumanSize(_options.MaxReadBytes)} limit. Use search_files_regex to find the part you need.");
            }

            data = new byte[stream.Length];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }

        if (s_media.TryGetValue(Path.GetExtension(real), out string? mime))
        {
            return TextFile.Asset(data, mime, Path.GetFileName(real));
        }

        return Render(data, numbers);
    }

    private static ToolResult Render(byte[] data, bool numbers)
    {
        if (!TextFile.TryDecode(data, out string text, out _, out _))
        {
            return TextFile.Asset(data, "application/octet-stream");
        }

        string normalized = TextFile.NormalizeNewlines(text);
        return ToolResult.FromText(numbers ? AddLineNumbers(normalized) : normalized);
    }

    // A gutter at least 4 wide, grown to the widest line number, then " | ". A trailing newline is kept without numbering a phantom line.
    internal static string AddLineNumbers(string s)
    {
        if (s.Length == 0)
        {
            return s;
        }

        bool trailing = s.EndsWith('\n');
        string[] lines = (trailing ? s[..^1] : s).Split('\n');
        int width = Math.Max(4, lines.Length.ToString(CultureInfo.InvariantCulture).Length);
        var sb = new StringBuilder(s.Length + lines.Length * (width + 3));
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append(" | ").Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        if (trailing)
        {
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private ToolResult List(string requested, string real)
    {
        var dir = new DirectoryInfo(real);
        FileSystemInfo[] all = [.. dir.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal)];
        int shown = Math.Min(all.Length, _options.MaxDirectoryEntries);
        string clean = requested.Length > 1 ? requested.TrimEnd('/', '\\') : requested;
        var sb = new StringBuilder();
        sb.Append("**Directory Listing: ").Append(clean).Append("**\n");
        if (shown == 0)
        {
            sb.Append("\n*(empty)*");
        }
        else
        {
            sb.Append('\n');
            for (int i = 0; i < shown; i++)
            {
                FileSystemInfo e = all[i];
                string name = e.Name.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
                // Like the file system's own listing, a link is shown as a file, not followed.
                bool isDir = e is DirectoryInfo && e.LinkTarget is null;
                if (isDir)
                {
                    sb.Append("- ").Append(name).Append("/\n");
                }
                else
                {
                    long size = e is FileInfo f && e.LinkTarget is null ? f.Length : e.LinkTarget?.Length ?? 0;
                    sb.Append("- ").Append(name).Append(" - ").Append(TextFile.HumanSize(size)).Append('\n');
                }
            }

            if (all.Length > shown)
            {
                sb.Append("... and ").Append((all.Length - shown).ToString(CultureInfo.InvariantCulture)).Append(" more");
            }
        }

        return ToolResult.FromText(sb.ToString().TrimEnd('\n'));
    }

    private async ValueTask<ToolResult> ReadUrlAsync(string url, bool numbers, ToolContext context, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return Fail($"Failed to create URL request: '{url}' is not a valid URL.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30), context.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using HttpResponseMessage response = await EgressClient.SendAsync(context.Capabilities, HttpMethod.Get, uri, [], null, linked.Token).ConfigureAwait(false);
        (byte[] bytes, bool truncated) = await EgressClient.ReadBodyAsync(response, (int)Math.Min(_options.MaxReadBytes, int.MaxValue - 1), linked.Token).ConfigureAwait(false);
        if (truncated)
        {
            return Fail($"The response is larger than the {TextFile.HumanSize(_options.MaxReadBytes)} limit.");
        }

        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && s_media.ContainsValue(contentType))
        {
            return TextFile.Asset(bytes, contentType);
        }

        string ext = Path.GetExtension(uri.AbsolutePath);
        if (s_media.TryGetValue(ext, out string? mime))
        {
            return TextFile.Asset(bytes, mime);
        }

        return Render(bytes, numbers);
    }
}
