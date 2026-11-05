using System.Text.Json.Serialization;
using OneYaml;

namespace OneLlm.Definitions;

/// <summary>
/// A decider: a set of typed judgments (choice, score or yes/no-probability questions) about some state, answered by one model call.
/// This is the shape of a <c>decider.yaml</c>; running deciders comes later. Unknown keys are kept.
/// </summary>
public sealed class DeciderDefinition
{
    /// <summary>The model or engine to ask (<c>jev-latest</c>, a chat model, ...).</summary>
    public string Model { get; set; } = "";

    /// <summary>The state the questions are about: a template tree rendered with the run's inputs. Kept as parsed.</summary>
    public object? State { get; set; }

    /// <summary>The questions by id, in file order.</summary>
    public Dictionary<string, DeciderQuestion> Questions { get; set; } = [];

    /// <summary>Keys the file carries that OneLLM does not model (<c>samples</c>, <c>cache</c>, ...).</summary>
    public Dictionary<string, object?>? ExtensionData { get; set; }

    /// <summary>Where the decider was loaded from, or null.</summary>
    public string? SourceName { get; private set; }

    internal string? OriginalText { get; private set; }

    /// <summary>The state as the file wrote it (plain ordered values), the form request bytes are rendered from.</summary>
    internal object? RawState { get; private set; }

    /// <summary>The questions as the file wrote them, each a mapping whose keys are in file order.</summary>
    internal Dictionary<string, object?> RawQuestions { get; private set; } = [];

    /// <summary>The <c>llm</c> block: how a chat model answers the questions (samples, temperature, thinking, service tier). Defaults when the file has none.</summary>
    public DeciderLlmSettings Llm => DeciderLlmSettings.From(ExtensionData is not null && ExtensionData.TryGetValue("llm", out object? llm) ? llm : null, SourceName);

    /// <summary>The <c>cache</c> block: prompt caching hints for engines that support them. Defaults when the file has none.</summary>
    public DeciderCacheSettings Cache => DeciderCacheSettings.From(ExtensionData is not null && ExtensionData.TryGetValue("cache", out object? cache) ? cache : null, SourceName);

    internal string? LoadedSerialization { get; private set; }

    /// <summary>Parses a <c>decider.yaml</c> and checks its shape.</summary>
    /// <param name="yaml">The file contents.</param>
    /// <param name="sourceName">The name used in error positions, usually the file path.</param>
    /// <returns>The decider.</returns>
    /// <exception cref="BadDefinitionException">The YAML is malformed, or the shape is wrong (no model, no questions, an unknown question type, fewer than 2 choices, a score scale outside 2 to 10 levels), with file, line and column.</exception>
    public static DeciderDefinition Parse(string yaml, string? sourceName = null)
    {
        DeciderFileWire wire;
        try
        {
            wire = YamlSerializer.Deserialize(yaml, DefinitionsYamlContext.Default.DeciderFileWire)
                ?? throw new BadDefinitionException("The file is empty.", sourceName, 1, 1);
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, sourceName, e.Line, e.Column, e);
        }

        var d = new DeciderDefinition
        {
            Model = wire.Model ?? "",
            State = wire.State,
            Questions = wire.Questions ?? [],
            ExtensionData = wire.ExtensionData,
            SourceName = sourceName,
            OriginalText = yaml,
        };
        try
        {
            DeciderRawWire raw = YamlSerializer.Deserialize(yaml, DefinitionsYamlContext.Default.DeciderRawWire) ?? new DeciderRawWire();
            d.RawState = raw.State;
            d.RawQuestions = raw.Questions ?? [];
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, sourceName, e.Line, e.Column, e);
        }

        d.Validate(yaml);
        d.LoadedSerialization = d.WriteWire();
        return d;
    }

    /// <summary>Reads a <c>decider.yaml</c> file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The decider.</returns>
    /// <exception cref="BadDefinitionException">As <see cref="Parse"/>.</exception>
    public static DeciderDefinition Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Writes the decider as YAML. A loaded decider that was not changed comes back byte for byte.</summary>
    /// <returns>The YAML text.</returns>
    public string ToYaml()
    {
        string text = WriteWire();
        return OriginalText is not null && LoadedSerialization == text ? OriginalText : text;
    }

    internal string WriteTyped() => WriteWire();

    private string WriteWire() => YamlSerializer.Serialize(
        new DeciderFileWire { Model = Model.Length == 0 ? null : Model, State = State, Questions = Questions.Count == 0 ? null : Questions, ExtensionData = ExtensionData },
        DefinitionsYamlContext.Default.DeciderFileWire);

    private void Validate(string yaml)
    {
        void Fail(string pointer, string message)
        {
            int line = 0, column = 0;
            try
            {
                using YamlDocument doc = YamlDocument.Parse(yaml);
                YamlNode node = doc.Root;
                foreach (string part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    node = node[part];
                }

                line = node.Position.Line;
                column = node.Position.Column;
            }
            catch (Exception e) when (e is YamlException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                // Position stays 0: the message still says what is wrong.
            }

            throw new BadDefinitionException(message, SourceName, line, column);
        }

        if (Model.Length == 0)
        {
            Fail("", "A decider needs a 'model'.");
        }

        if (Questions.Count == 0)
        {
            Fail("", "A decider needs at least one entry under 'questions'.");
        }

        _ = Llm;
        _ = Cache;
        foreach ((string id, DeciderQuestion q) in Questions)
        {
            if (q.ForEach is not null && !(q.ForEach is string each && Deciders.DecisionTemplate.IsWholeAction(each)))
            {
                Fail("questions/" + id + "/forEach", $"'forEach' of question '{id}' must be one {{{{ ... }}}} action that gives a list, such as "{{{{ .inputs.items }}}}".");
            }

            string at = "questions/" + id;
            if (!IsQuestionId(id))
            {
                Fail(at, $"Question id '{id}' must start with a letter or underscore and use only letters, digits and underscores.");
            }

            if (q.Instructions is null or "")
            {
                Fail(at, $"Question '{id}' needs 'instructions'.");
            }

            switch (q.Type)
            {
                case "choice":
                    int choices = CountEntries(q.Criteria);
                    if (q.Criteria is not IDictionary<string, object?> || choices < 2 || choices > 255)
                    {
                        Fail(at, $"Choice question '{id}' needs 'criteria' as a mapping of 2 to 255 options, not {choices}.");
                    }

                    break;
                case "score":
                    int levels = CountEntries(q.Criteria);
                    if (levels is < 2 or > 10)
                    {
                        Fail(at, $"Score question '{id}' needs 'criteria' with 2 to 10 levels, lowest first, not {levels}.");
                    }

                    break;
                case "noul":
                    if (q.Criteria is IDictionary<string, object?> clarifications && clarifications.Keys.Any(static k => k is not ("true" or "false")))
                    {
                        Fail(at, $"Noul question '{id}' may only clarify 'true' and 'false' in its criteria.");
                    }

                    break;
                default:
                    Fail(at, $"Question '{id}' has type '{q.Type}'; it must be 'choice', 'score' or 'noul'.");
                    break;
            }
        }
    }

    private static bool IsQuestionId(string id)
    {
        if (id.Length == 0 || !(char.IsAsciiLetter(id[0]) || id[0] == '_'))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountEntries(object? value) => value switch
    {
        Dictionary<string, object?> d => d.Count,
        System.Collections.ICollection c => c.Count,
        _ => 0,
    };
}

/// <summary>One question of a decider.</summary>
public sealed class DeciderQuestion
{
    /// <summary>The kind of judgment: <c>choice</c>, <c>score</c> or <c>noul</c> (a yes/no with a probability).</summary>
    public string Type { get; set; } = "";

    /// <summary>What to judge: a template string, or a mapping of named parts (each a template). Kept as parsed.</summary>
    public object? Instructions { get; set; }

    /// <summary>
    /// What the answer is judged by. For <c>choice</c>: the options, a mapping from value to description. For <c>score</c>: the levels
    /// from lowest to highest, a list (or a mapping from label to description). For <c>noul</c>: optional conditions. Kept as parsed.
    /// </summary>
    public object? Criteria { get; set; }

    /// <summary>For repeated questions: the template that names the items to ask about. Kept as parsed.</summary>
    public object? ForEach { get; set; }

    /// <summary>Keys the file carries that OneLLM does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
