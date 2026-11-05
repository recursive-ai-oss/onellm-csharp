using System.Text.Json;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Runners;
using OneLlm.Templating;

namespace OneLlm;

public sealed partial class Runtime
{
    /// <summary>Reads a definition file (an agent <c>.yaml</c> or a <c>.flow</c>) and runs it as a script. References to tools are ignored with a warning.</summary>
    /// <param name="path">The definition file.</param>
    /// <param name="inputs">Values for <c>.inputs</c> in the templates, over the definition's own defaults. Values must be plain (strings, numbers, booleans, lists, dictionaries, <see cref="JsonElement"/>).</param>
    /// <param name="cancellationToken">Cancels the run; the instance keeps the turns finished so far.</param>
    /// <returns>The result of the last turn, with every turn, cost and the instance.</returns>
    /// <exception cref="BadDefinitionException">The file is invalid or names no model.</exception>
    /// <exception cref="TemplateException">A prompt does not render.</exception>
    public Task<ScriptResult> RunScriptAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunScriptAsync(AgentDefinition.Load(path, DefinitionLoadOptions.Lenient), inputs, cancellationToken);
    }

    /// <summary>
    /// Runs a definition as a script: the system prompt and each initial prompt are rendered as templates, and each user prompt gets
    /// one model call, in order, with the earlier turns in the history. No tools are offered. Templates can read earlier answers as
    /// <c>.turns[i].text</c> and, when the answer is JSON, <c>.turns[i].json</c>.
    /// </summary>
    /// <param name="definition">The definition. It needs a model.</param>
    /// <param name="inputs">Values for <c>.inputs</c>, over the definition's defaults.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The script's result.</returns>
    /// <remarks>
    /// A prompt with a <c>when</c> template runs only if it renders to something other than empty, <c>false</c>, <c>0</c>, <c>no</c> or
    /// <c>off</c>; a <c>when</c> that fails to render counts as true. A definition with an <c>outputSchema</c> (a JSON Schema in the
    /// definition's extra fields) asks for that shape on its last user prompt, and <see cref="ScriptResult.Output"/> holds the JSON.
    /// </remarks>
    /// <exception cref="BadDefinitionException">The definition names no model.</exception>
    /// <exception cref="TemplateException">A prompt does not render.</exception>
    public async Task<ScriptResult> RunScriptAsync(AgentDefinition definition, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Model))
        {
            throw new BadDefinitionException("The definition names no model.", definition.SourceName);
        }

        ResolvedModel model = Resolve(definition.Model);
        IChatProvider provider = GetChatProvider(model);
        var warnings = new List<string>(definition.Warnings);
        if (definition.References.Count > 0)
        {
            warnings.Add("The definition lists tool references; a scripted run offers no tools, so they are ignored.");
        }

        Dictionary<string, object?> merged = definition.Inputs is null ? [] : PlainValues.NormalizeInputs(definition.Inputs);
        foreach ((string key, object? value) in PlainValues.NormalizeInputs(inputs))
        {
            merged[key] = value;
        }

        Instance instance = Instance.FromDefinition(definition, NewId("i"));
        instance.Inputs = merged;
        var turns = new List<object?>();
        Dictionary<string, object?> scope = TemplateScope.Build(this, definition, merged, null, turns);
        TemplateRenderOptions render = RenderOptions(definition, cancellationToken);
        string sourceName = definition.SourceName ?? definition.Name;

        string Render(string text, string where) =>
            Template.Render(text, scope, new TemplateParseOptions { Name = sourceName + ":" + where, Functions = Functions }, render);

        RunScope run = await RunScope.StartAsync(this, instance, cancellationToken).ConfigureAwait(false);
        await using (run.ConfigureAwait(false))
        {
            var answers = new List<Message>();
            try
            {
                instance.RenderedSystemPrompt = Render(definition.SystemPrompt, "systemPrompt");
                await Store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                var settings = new AskRequest
                {
                    Model = definition.Model,
                    Temperature = definition.Temperature,
                    MaxOutputTokens = definition.MaxTokens,
                    ServiceTier = definition.ServiceTier,
                    Reasoning = definition.Thinking is { } t ? new ReasoningOptions { Effort = t.Effort, BudgetTokens = t.Budget } : null,
                };
                ResponseFormat? outputFormat = OutputFormat(definition);
                int lastUserPrompt = definition.InitialPrompts.FindLastIndex(static p => p.User.Count > 0);

                for (int i = 0; i < definition.InitialPrompts.Count; i++)
                {
                    Message prompt = definition.InitialPrompts[i];
                    string where = $"initialPrompts[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
                    if (!EvaluateWhen(prompt.When, Render, where + ".when"))
                    {
                        warnings.Add($"{where} was skipped: its 'when' is false.");
                        continue;
                    }

                    if (prompt.ToolCalls.Count > 0)
                    {
                        warnings.Add($"{where} has tool calls; a scripted run does not run tools, so they are dropped.");
                    }

                    Message rendered = RenderPrompt(prompt, Render, where);
                    await run.AppendAsync(rendered, cancellationToken).ConfigureAwait(false);
                    if (rendered.User.Count == 0)
                    {
                        turns.Add(TurnValue(rendered.GetAssistantText()));
                        continue;
                    }

                    ChatRequest request = BuildChat(instance, model, settings, i == lastUserPrompt ? outputFormat : null);
                    AskResult answer = await SendTurnAsync(new Turn(run, model, provider, request), cancellationToken).ConfigureAwait(false);
                    answers.Add(answer.Message);
                    warnings.AddRange(answer.Warnings.Select(static w => w.Message));
                    turns.Add(TurnValue(answer.Text));
                }

                JsonElement? output = null;
                if (outputFormat is not null && answers.Count > 0)
                {
                    output = ParseOutput(answers[^1].GetAssistantText());
                }

                await run.CompleteAsync(InstanceStates.Stopped, cancellationToken).ConfigureAwait(false);
                return new ScriptResult(instance, answers, Cost.Sum(answers.Select(static m => m.Usage?.ToCost())), SumUsage(answers, model.Id), output, warnings);
            }
            catch (Exception ex)
            {
                Exception e = await run.FailAsync(ex, null, cancellationToken).ConfigureAwait(false);
                Rethrow(e, ex);
                throw;
            }
        }
    }

    internal TemplateRenderOptions RenderOptions(AgentDefinition definition, CancellationToken cancellationToken)
    {
        var sources = new List<IEmbedSource>();
        string? rootKey = null;
        if (definition.SourceName is { } file && File.Exists(file))
        {
            string full = Path.GetFullPath(file);
            // The definition's own directory is the one place embed may read without the host listing it.
            sources.Add(new FileEmbedSource(Path.GetDirectoryName(full) ?? "."));
            rootKey = "file:" + full;
        }

        sources.AddRange(EmbedSources);
        return new TemplateRenderOptions
        {
            MissingKey = string.Equals(definition.MissingKey, "error", StringComparison.OrdinalIgnoreCase) ? MissingKeyMode.Error : MissingKeyMode.Default,
            EmbedSources = sources,
            RootKey = rootKey,
            TimeProvider = TimeProvider,
            CancellationToken = cancellationToken,
        };
    }

    internal static bool EvaluateWhen(string? when, Func<string, string, string> render, string where)
    {
        if (string.IsNullOrEmpty(when))
        {
            return true;
        }

        string text;
        try
        {
            text = render(when, where);
        }
        catch (TemplateException)
        {
            // A condition that cannot be evaluated does not silently drop the prompt.
            return true;
        }

        return text.Trim().ToLowerInvariant() is not ("" or "false" or "0" or "no" or "off");
    }

    internal Message RenderPrompt(Message prompt, Func<string, string, string> render, string where)
    {
        List<Part> Map(IReadOnlyList<Part> parts, string kind)
        {
            var result = new List<Part>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                result.Add(parts[i] is TextPart text
                    ? new TextPart(render(text.Text, $"{where}.{kind}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]")) { Annotations = text.Annotations, ExtensionData = text.ExtensionData }
                    : parts[i]);
            }

            return result;
        }

        return new Message
        {
            Id = string.IsNullOrEmpty(prompt.Id) ? NewId("m") : prompt.Id,
            User = Map(prompt.User, "user"),
            Assistant = Map(prompt.Assistant, "assistant"),
            DateTime = prompt.DateTime ?? TimeProvider.GetUtcNow(),
            Source = prompt.Source,
            Cache = prompt.Cache,
            When = prompt.When,
            ExtensionData = prompt.ExtensionData,
        };
    }

    // What `.turns[i]` holds: the answer's text, and its JSON when the text parses as JSON.
    private static Dictionary<string, object?> TurnValue(string text)
    {
        object? json = PlainValues.TryParseJson(text, out bool ok);
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = text, ["json"] = ok ? json : null };
    }

    private static ResponseFormat? OutputFormat(AgentDefinition definition) =>
        definition.ExtensionData is { } extra && extra.TryGetValue("outputSchema", out object? schema) && schema is IDictionary<string, object?> map
            ? StructuredOutput.FormatFor(map, "output")
            : null;

    private static JsonElement ParseOutput(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new StructuredOutputException("The last answer is not valid JSON, but the definition declares an outputSchema.", text, ex);
        }
    }

    private static Usage? SumUsage(List<Message> answers, string model)
    {
        if (answers.Count == 0)
        {
            return null;
        }

        long input = 0, output = 0, write = 0, read = 0;
        decimal cost = 0m;
        foreach (Usage u in answers.Select(static m => m.Usage).OfType<Usage>())
        {
            input += u.InputTokens;
            output += u.OutputTokens;
            write += u.CacheCreationInputTokens;
            read += u.CacheReadInputTokens;
            cost += u.Cost;
        }

        return new Usage { Model = model, InputTokens = input, OutputTokens = output, CacheCreationInputTokens = write, CacheReadInputTokens = read, Cost = cost };
    }
}
