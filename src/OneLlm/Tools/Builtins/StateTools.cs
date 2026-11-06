using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Messages;

namespace OneLlm.Tools.Builtins;

/// <summary>Where the working-state tools keep their files: under <see cref="BuiltinOptions.StateDirectory"/>, checked against the write grants like any other file.</summary>
internal static class StateFiles
{
    internal static string Relative(BuiltinOptions options, string name) => Path.Join(options.StateDirectory, name);

    internal static async Task<string?> ReadAsync(ICapabilities caps, string path, CancellationToken ct)
    {
        string real = PathGuard.Resolve(caps.ReadRoots, caps.WorkingDirectory, path, write: false);
        if (!File.Exists(real))
        {
            return null;
        }

        await using FileStream s = GuardedFile.OpenRead(caps, path, state: true);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(ICapabilities caps, string path, string text, CancellationToken ct)
    {
        await using FileStream s = GuardedFile.OpenWrite(caps, path, append: false, state: true);
        await s.WriteAsync(new UTF8Encoding(false).GetBytes(text), ct).ConfigureAwait(false);
    }
}

/// <summary>todo: the agent's own task list for the current run, kept in a file so it survives a restart.</summary>
internal sealed class TodoTool : BuiltinTool
{
    internal const string Description = "Keep a short task list for this run. Call with `todos` to replace the whole list; call with no arguments to read it. Mark an item `in_progress` when you start it and `completed` when it is done. Keep at most one item in progress.";

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "The complete new list. Omit to read the current list.",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "What to do, in a few words." },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["content", "status"]
              }
            }
          }
        }
        """;

    private const int MaxItems = 100;
    private const int MaxContent = 500;

    private readonly BuiltinOptions _options;

    public TodoTool(BuiltinOptions options)
        : base(Spec("todo", Description, Schema, TimeSpan.FromSeconds(30)), ToolCapability.FileRead | ToolCapability.FileWrite)
    {
        _options = options;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string path = StateFiles.Relative(_options, "todo-" + Safe(context.InstanceId) + ".json");
        if (inputs.TryGetPropertyValue("todos", out JsonNode? node) && node is not null)
        {
            if (node is not JsonArray array)
            {
                throw new ToolInputException("todos", "must be an array");
            }

            if (array.Count > MaxItems)
            {
                throw new ToolInputException("todos", $"can hold at most {MaxItems} items");
            }

            var items = new List<(string Content, string Status)>();
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject o)
                {
                    throw new ToolInputException($"todos[{i}]", "must be an object");
                }

                string content = Args.OptionalString(o, "content") is { Length: > 0 } c ? c : throw new ToolInputException($"todos[{i}].content", "is required");
                string status = Args.OptionalString(o, "status") ?? "";
                if (status is not ("pending" or "in_progress" or "completed"))
                {
                    throw new ToolInputException($"todos[{i}].status", "must be pending, in_progress or completed");
                }

                items.Add((content.Length > MaxContent ? content[..MaxContent] : content, status));
            }

            await StateFiles.WriteAsync(context.Capabilities, path, Serialize(items), cancellationToken).ConfigureAwait(false);
            return ToolResult.FromText(Render(items));
        }

        string? existing = await StateFiles.ReadAsync(context.Capabilities, path, cancellationToken).ConfigureAwait(false);
        return ToolResult.FromText(Render(existing is null ? [] : Deserialize(existing)));
    }

    internal static string Safe(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.Length == 0 ? "instance" : sb.ToString();
    }

    private static string Serialize(List<(string Content, string Status)> items)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach ((string content, string status) in items)
            {
                w.WriteStartObject();
                w.WriteString("content", content);
                w.WriteString("status", status);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<(string Content, string Status)> Deserialize(string json)
    {
        var items = new List<(string, string)>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                items.Add((e.GetProperty("content").GetString() ?? "", e.GetProperty("status").GetString() ?? "pending"));
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A damaged file reads as an empty list; the next write replaces it.
            items.Clear();
        }

        return items;
    }

    internal static string Render(List<(string Content, string Status)> items)
    {
        if (items.Count == 0)
        {
            return "The todo list is empty.";
        }

        var sb = new StringBuilder();
        int done = items.Count(i => i.Status == "completed");
        sb.Append(CultureInfo.InvariantCulture, $"Todo list ({done} of {items.Count} done):");
        for (int i = 0; i < items.Count; i++)
        {
            string mark = items[i].Status switch { "completed" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            sb.Append(CultureInfo.InvariantCulture, $"\n{i + 1}. {mark} {items[i].Content}");
        }

        return sb.ToString();
    }
}

/// <summary>memory: notes that outlive a run, in one file under the state directory. Every run that has the tool shares the file.</summary>
internal sealed class MemoryTool : BuiltinTool
{
    internal const string Description = "Read and write long-term notes that persist across runs. `read` returns the notes, `append` adds text to the end, `write` replaces everything, `clear` empties the notes. Keep entries short and factual.";

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["read", "append", "write", "clear"], "description": "What to do. Defaults to read.", "default": "read" },
            "content": { "type": "string", "description": "The text for append and write." }
          }
        }
        """;

    private const int MaxBytes = 64 * 1024;

    private readonly BuiltinOptions _options;

    public MemoryTool(BuiltinOptions options)
        : base(Spec("memory", Description, Schema, TimeSpan.FromSeconds(30)), ToolCapability.FileRead | ToolCapability.FileWrite)
    {
        _options = options;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string action = Args.OptionalString(inputs, "action") ?? "read";
        string? content = Args.OptionalString(inputs, "content");
        string path = StateFiles.Relative(_options, "memory.md");
        string current = await StateFiles.ReadAsync(context.Capabilities, path, cancellationToken).ConfigureAwait(false) ?? "";
        string next;
        switch (action)
        {
            case "read":
                return ToolResult.FromText(current.Length == 0 ? "(memory is empty)" : current);
            case "clear":
                next = "";
                break;
            case "write":
                next = content ?? throw new ToolInputException("content", "is required for write");
                break;
            case "append":
                string add = content ?? throw new ToolInputException("content", "is required for append");
                next = current.Length == 0 || current.EndsWith('\n') ? current + add : current + "\n" + add;
                break;
            default:
                throw new ToolInputException("action", "must be read, append, write or clear");
        }

        if (Encoding.UTF8.GetByteCount(next) > MaxBytes)
        {
            return Fail($"Memory is limited to {MaxBytes / 1024} KiB. Shorten the notes with write first.");
        }

        await StateFiles.WriteAsync(context.Capabilities, path, next, cancellationToken).ConfigureAwait(false);
        return ToolResult.FromText(string.Create(CultureInfo.InvariantCulture, $"Saved. Memory is now {Encoding.UTF8.GetByteCount(next)} bytes."));
    }
}

/// <summary>What <c>ask_user</c> asked, read back from its result by the loop.</summary>
/// <param name="Question">The question for the person.</param>
/// <param name="Choices">Suggested answers, possibly none.</param>
public sealed record AskUserRequest(string Question, IReadOnlyList<string> Choices);

/// <summary>
/// ask_user: poses a question to the person running the agent. The tool cannot wait itself; it returns a result holding one JSON part
/// <c>{"type":"ask_user","question":"...","choices":["..."]}</c>. The loop recognizes it with <see cref="TryGetRequest"/>, puts the instance in the
/// waiting state, and replaces the result with the person's answer when it comes.
/// </summary>
public static class AskUser
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "ask_user";

    /// <summary>The value of the <c>type</c> field in the result.</summary>
    public const string ResultType = "ask_user";

    private static readonly AskUserRequest None = new("", []);

    /// <summary>Reads the question out of a result made by the <c>ask_user</c> tool.</summary>
    /// <param name="result">A tool result.</param>
    /// <param name="request">The question, when the result is an ask_user result.</param>
    /// <returns>True when <paramref name="result"/> asks the user something.</returns>
    public static bool TryGetRequest(ToolResult result, out AskUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (Part p in result.Parts)
        {
            if (p is JsonPart { Value: JsonObject o } && o["type"]?.GetValue<string>() == ResultType && o["question"] is JsonValue q && q.TryGetValue(out string? text))
            {
                List<string> choices = o["choices"] is JsonArray a ? [.. a.Select(c => c?.GetValue<string>() ?? "")] : [];
                request = new AskUserRequest(text, choices);
                return true;
            }
        }

        request = None;
        return false;
    }
}

internal sealed class AskUserTool : BuiltinTool
{
    internal const string Description = "Ask the person you are working for a question and wait for the answer. Use it only when you cannot continue without their input; do not use it to confirm routine steps. Give `choices` when the answer is one of a few options.";

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question, written so it can be answered without seeing the conversation." },
            "choices": { "type": "array", "items": { "type": "string" }, "description": "Suggested answers." }
          },
          "required": ["question"]
        }
        """;

    public AskUserTool()
        : base(Spec(AskUser.ToolName, Description, Schema, TimeSpan.FromSeconds(30)), ToolCapability.None)
    {
    }

    protected override ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string question = Args.RequiredString(inputs, "question");
        List<string> choices = Args.OptionalStringList(inputs, "choices") ?? [];
        var value = new JsonObject
        {
            ["type"] = AskUser.ResultType,
            ["question"] = question,
            ["choices"] = new JsonArray([.. choices.Select(c => (JsonNode?)JsonValue.Create(c))]),
        };
        return ValueTask.FromResult(new ToolResult([new JsonPart(value)]));
    }
}
