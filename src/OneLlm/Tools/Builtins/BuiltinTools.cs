namespace OneLlm.Tools.Builtins;

/// <summary>The built-in tools that need only the base class library, and the names existing definitions know them by.</summary>
/// <remarks>
/// Every tool checks the grants of the <see cref="ToolContext"/> it is called with: files under the roots, shell by the shell policy, network by
/// the allow-list. A tool that is registered but not granted returns an error result saying what to grant; it never acts.
/// <c>fetch_webpage_markdown</c> is in the Web package.
/// </remarks>
public static class BuiltinTools
{
    private static readonly string[] s_fileGroup = ["read_file", "write_file", "replace_string", "list_files", "search_files_regex", "execute_terminal_command"];
    private static readonly string[] s_webGroup = ["fetch_webpage_markdown", "search_web"];

    /// <summary>The names of the tools <see cref="Create"/> returns when a search provider is set.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["read_file", "write_file", "replace_string", "list_files", "search_files_regex", "apply_patch", "execute_terminal_command", "http_request", "search_web", "todo", "memory", "ask_user", "run_decider"];

    /// <summary>Makes the built-in tools. <c>search_web</c> is included only when <see cref="BuiltinOptions.SearchProvider"/> is set.</summary>
    /// <param name="options">Limits and services, or null for the defaults.</param>
    /// <returns>The tools, in a stable order.</returns>
    public static IReadOnlyList<ITool> Create(BuiltinOptions? options = null)
    {
        BuiltinOptions o = options ?? new BuiltinOptions();
        var tools = new List<ITool>
        {
            new ReadFileTool(o),
            new WriteFileTool(),
            new ReplaceStringTool(),
            new ListFilesTool(),
            new SearchFilesRegexTool(),
            new ApplyPatchTool(),
            new ShellTool(o),
            new HttpRequestTool(o),
        };
        if (o.SearchProvider is { } provider)
        {
            tools.Add(new SearchWebTool(provider));
        }

        tools.Add(new TodoTool(o));
        tools.Add(new MemoryTool(o));
        tools.Add(new AskUserTool());
        tools.Add(new RunDeciderTool());
        return tools;
    }

    /// <summary>Maps a routine path or name used by existing definitions to the tool names it stands for.</summary>
    /// <param name="routine">For example <c>/agent-tools/os/read-file</c>, <c>/agent-tools/os</c> or <c>read_file</c>.</param>
    /// <returns>The tool names, or null when the routine is not one of the built-in ones.</returns>
    public static IReadOnlyList<string>? ToolsForRoutine(string routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        string r = routine.Trim().Trim('/');
        if (r is "agent-tools/os")
        {
            return s_fileGroup;
        }

        if (r is "agent-tools/web")
        {
            return s_webGroup;
        }

        string last = r[(r.LastIndexOf('/') + 1)..];
        string name = last.Replace('-', '_');
        if (name == "regex_file_search")
        {
            name = "search_files_regex";
        }

        if (r.StartsWith("agent-tools/", StringComparison.Ordinal) || !r.Contains('/', StringComparison.Ordinal))
        {
            string[] known = [.. Names, "fetch_webpage_markdown"];
            return known.Contains(name, StringComparer.Ordinal) ? [name] : null;
        }

        return null;
    }
}
