using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Access;

namespace OneLlm.Tools.Builtins;

/// <summary>write_file: writes UTF-8 text, creating parent directories, either replacing the file or appending.</summary>
internal sealed class WriteFileTool : BuiltinTool
{
    public WriteFileTool()
        : base(Spec("write_file", BuiltinSchemas.WriteFileDescription, BuiltinSchemas.WriteFileSchema, TimeSpan.FromSeconds(30)), ToolCapability.FileWrite)
    {
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = Args.RequiredString(inputs, "path");
        string content = Args.OptionalString(inputs, "content") ?? throw new ToolInputException("content", "is required");
        bool append = Args.Bool(inputs, "append", false);
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        await using (FileStream stream = GuardedFile.OpenWrite(context.Capabilities, path, append))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        string shown = Path.GetFullPath(path, context.Capabilities.WorkingDirectory ?? Directory.GetCurrentDirectory());
        return ToolResult.FromText(string.Create(CultureInfo.InvariantCulture, $"Successfully wrote {bytes.Length} bytes to {shown}"));
    }
}
