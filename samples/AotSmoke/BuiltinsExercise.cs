using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Tools;
using OneLlm.Tools.Builtins;

namespace OneLlm.AotSmoke;

/// <summary>Runs two built-in tools under a granted root and checks that an ungranted path is refused: the capability model and the tools stay trim and AOT safe.</summary>
internal static class BuiltinsExercise
{
    public static async Task<bool> RunAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onellm-aot-builtins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "sub", "a.txt"), "alpha\nbeta\n");
            Capabilities caps = Capabilities.Files(dir) + Capabilities.ShellCommands("echo");
            IReadOnlyList<ITool> tools = BuiltinTools.Create();
            ITool read = tools.Single(t => t.Definition.Name == "read_file");
            ITool list = tools.Single(t => t.Definition.Name == "list_files");
            var ctx = new ToolContext("aot", "1", TimeProvider.System) { Capabilities = caps };

            ToolResult text = await ToolInvoker.InvokeAsync(read, (JsonObject)JsonNode.Parse("""{"path":"sub/a.txt","includeLineNumbers":true}""")!, ctx, CancellationToken.None);
            ToolResult tree = await ToolInvoker.InvokeAsync(list, new JsonObject(), ctx, CancellationToken.None);
            ToolResult denied = await ToolInvoker.InvokeAsync(read, (JsonObject)JsonNode.Parse("""{"path":"../outside.txt"}""")!, ctx, CancellationToken.None);

            return Text(text) == "   1 | alpha\n   2 | beta\n"
                && Text(tree).Contains("a.txt", StringComparison.Ordinal)
                && denied.IsError
                && !Capabilities.Default(dir).ShellEnabled
                && Capabilities.Files(dir).Intersect(Capabilities.Files(Path.Combine(dir, "sub"))).ReadRoots.Count == 1;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Text(ToolResult r) => string.Concat(r.Parts.OfType<TextPart>().Select(p => p.Text));
}
