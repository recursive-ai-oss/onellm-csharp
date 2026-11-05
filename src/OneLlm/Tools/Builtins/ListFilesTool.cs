using System.Text;
using System.Text.Json.Nodes;

namespace OneLlm.Tools.Builtins;

/// <summary>list_files: a text tree of a directory with sizes, honouring glob include/exclude patterns, .gitignore files and a depth limit.</summary>
internal sealed class ListFilesTool : BuiltinTool
{
    public ListFilesTool()
        : base(Spec("list_files", BuiltinSchemas.ListFilesDescription, BuiltinSchemas.ListFilesSchema, TimeSpan.FromSeconds(60)), ToolCapability.FileRead)
    {
    }

    protected override ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string requested = Args.OptionalString(inputs, "directory") ?? "";
        int maxDepth = Args.OptionalInt(inputs, "maxChildDepth") ?? -1;
        List<string> exclude = Args.OptionalStringList(inputs, "exclusionPatterns") ?? [];
        List<string> include = Args.OptionalStringList(inputs, "inclusionPatterns") ?? [];
        bool gitIgnore = Args.Bool(inputs, "excludeGitIgnoreMatches", true);
        bool omitEmpty = Args.Bool(inputs, "omitEmptyDirs", false);
        bool modified = Args.Bool(inputs, "includeModified", false);

        // .git/ is always left out, even when the caller gives its own exclusions.
        if (!exclude.Contains(".git/"))
        {
            exclude.Add(".git/");
        }

        string shown = requested.Length == 0 ? "." : requested;
        string real = context.Capabilities.ResolveRead(requested.Length == 0 ? "." : requested);
        if (File.Exists(real))
        {
            return ValueTask.FromResult(Fail("Path is not a directory: " + shown));
        }

        if (!Directory.Exists(real))
        {
            return ValueTask.FromResult(Fail("Directory does not exist: " + shown));
        }

        var walker = new FileWalker(real, context.Capabilities, maxDepth, exclude, include, gitIgnore);
        List<WalkEntry> files = walker.Walk(cancellationToken);
        if (omitEmpty)
        {
            var withFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (WalkEntry f in files.Where(f => !f.IsDir))
            {
                string[] parts = f.Path.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    withFiles.Add(string.Join('/', parts[..i]));
                }
            }

            files = [.. files.Where(f => !f.IsDir || withFiles.Contains(f.Path))];
        }

        return ValueTask.FromResult(ToolResult.FromText(BuildTree(files, modified)));
    }

    private sealed class Node(string name)
    {
        public string Name { get; } = name;

        public bool IsDir { get; set; }

        public long Size { get; set; }

        public string? Modified { get; set; }

        public string? Link { get; set; }

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    private static string BuildTree(List<WalkEntry> files, bool modified)
    {
        if (files.Count == 0)
        {
            return "(empty directory)";
        }

        var root = new Node("");
        foreach (WalkEntry f in files)
        {
            string[] parts = f.Path.Split('/');
            Node current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!current.Children.TryGetValue(parts[i], out Node? child))
                {
                    child = new Node(parts[i]) { IsDir = f.IsDir || i < parts.Length - 1 };
                    current.Children[parts[i]] = child;
                }

                current = child;
            }

            current.IsDir = f.IsDir;
            current.Size = f.Size;
            current.Modified = modified ? WalkFormat(f) : null;
            current.Link = f.IsSymlink ? f.LinkTarget ?? "?" : null;
        }

        var sb = new StringBuilder();
        Render(sb, root, "", isRoot: true, isLast: true);
        return sb.ToString().TrimEnd('\n');
    }

    private static string WalkFormat(WalkEntry f) => FileWalker.Format(f.ModifiedUtc);

    private static void Render(StringBuilder sb, Node node, string prefix, bool isRoot, bool isLast)
    {
        if (!isRoot)
        {
            string name = node.Name + (node.IsDir ? "/" : "") + (node.Link is not null ? " -> " + node.Link : "");
            sb.Append(prefix).Append(isLast ? "└── " : "├── ").Append(name);
            var meta = new List<string>(2);
            if (!node.IsDir)
            {
                meta.Add(TextFile.TreeSize(node.Size));
            }

            if (node.Modified is not null)
            {
                meta.Add(node.Modified);
            }

            if (meta.Count > 0)
            {
                sb.Append("  (").Append(string.Join(", ", meta)).Append(')');
            }

            sb.Append('\n');
        }

        // Directories first, then by name.
        List<Node> children = [.. node.Children.Values.OrderByDescending(c => c.IsDir).ThenBy(c => c.Name, StringComparer.Ordinal)];
        string childPrefix = isRoot ? prefix : prefix + (isLast ? "    " : "│   ");
        for (int i = 0; i < children.Count; i++)
        {
            Render(sb, children[i], childPrefix, isRoot: false, isLast: i == children.Count - 1);
        }
    }
}
