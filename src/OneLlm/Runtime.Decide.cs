using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Deciders;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Templating;

namespace OneLlm;

public sealed partial class Runtime
{
    /// <summary>Runs a decider from its directory (or <c>decider.yaml</c>) with inputs. The run is an instance: saved in <see cref="Store"/>, reported to <see cref="Sinks"/> and telemetry, priced from the catalog and checked against the spend caps.</summary>
    /// <param name="path">A directory holding <c>decider.yaml</c>, or the file.</param>
    /// <param name="inputs">The inputs: plain values (strings, numbers, booleans, lists, dictionaries, <see cref="JsonElement"/>), and <see cref="AssetPart"/> images for engines that take them.</param>
    /// <param name="cancellationToken">Cancels the run; the instance is kept as <c>stopped</c> with <c>cancelled: true</c>.</param>
    /// <returns>The answers and outputs, with cost and the instance.</returns>
    /// <exception cref="BadDefinitionException">The decider is invalid.</exception>
    /// <exception cref="DeciderRenderException">A template does not render with these inputs (a missing name is an error).</exception>
    /// <exception cref="DeciderAnswerException">The engine's answers do not fit the questions.</exception>
    /// <exception cref="RefusedException">The engine declined to answer a question.</exception>
    /// <exception cref="ProviderException">The engine failed after retries.</exception>
    public Task<DecisionResult> DecideAsync(string path, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default) =>
        DecideAsync(Decider.Load(path), inputs, cancellationToken);

    /// <summary>
    /// Runs a decider with inputs. The engine is chosen from the decider's <c>model</c> by the catalog: a model of kind <c>decision</c>
    /// (TypeSafe Jev, OpenAI Decisions) is asked all its questions in as few requests as fit; any other model is asked each question on
    /// its own as a chat model.
    /// </summary>
    /// <param name="decider">The decider.</param>
    /// <param name="inputs">The inputs, as for <see cref="DecideAsync(string, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The answers and outputs, with cost and the instance.</returns>
    /// <exception cref="DeciderRenderException">A template does not render with these inputs.</exception>
    /// <exception cref="DeciderAnswerException">The engine's answers do not fit the questions.</exception>
    /// <exception cref="RefusedException">The engine declined to answer a question.</exception>
    /// <exception cref="CapabilityMissingException">The engine cannot take what the inputs hold (an image on Jev), or the model's provider is not a decisions provider.</exception>
    /// <exception cref="BudgetExceededException">A spend cap would be passed; nothing was sent.</exception>
    public Task<DecisionResult> DecideAsync(Decider decider, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellationToken = default) =>
        DecideCoreAsync(decider, inputs, null, "api", null, cancellationToken);

    /// <summary>Runs a decider with inputs given as JSON, for callers such as a tool that already hold JSON. <paramref name="origin"/> marks the run: <c>api</c>, <c>agent</c>, <c>eval</c>, ...</summary>
    /// <param name="decider">The decider.</param>
    /// <param name="inputs">The inputs as a JSON object.</param>
    /// <param name="origin">Where the run comes from; recorded with it. Default <c>api</c>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The result.</returns>
    public Task<DecisionResult> DecideAsync(Decider decider, JsonElement inputs, string? origin = null, CancellationToken cancellationToken = default)
    {
        var map = PlainValues.FromElement(inputs) as Dictionary<string, object?>
            ?? throw new ArgumentException("The inputs must be a JSON object.", nameof(inputs));
        return DecideCoreAsync(decider, map, null, origin ?? "api", null, cancellationToken);
    }

    /// <summary>Runs a decider again with the inputs a recorded run had, optionally on another model, as a new instance that records what it replays.</summary>
    /// <param name="instanceId">The run to replay.</param>
    /// <param name="decider">The decider to run it with (the recorded run's decider, by name; its definition may have changed since).</param>
    /// <param name="model">Another model to ask; null keeps the decider's.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The new run's result.</returns>
    /// <exception cref="KeyNotFoundException">There is no such run.</exception>
    public async Task<DecisionResult> ReplayDecisionAsync(string instanceId, Decider decider, string? model = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decider);
        Instance source = await Store.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"There is no run '{instanceId}'.");
        if (!IsDecisionInstance(source))
        {
            throw new KeyNotFoundException($"Instance '{instanceId}' is not a decider run.");
        }

        return await DecideCoreAsync(decider, source.Inputs, model, "replay", instanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one recorded decider run.</summary>
    /// <param name="instanceId">The run's instance id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The run, or null when there is no decider run with that id.</returns>
    public async Task<DecisionRun?> GetDecisionAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        Instance? instance = await Store.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return instance is not null && IsDecisionInstance(instance) ? ToRun(instance) : null;
    }

    /// <summary>Lists recorded decider runs, newest first.</summary>
    /// <param name="decider">Only runs of the decider with this name; null for all deciders.</param>
    /// <param name="limit">The most runs to return; null for all.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The runs.</returns>
    public async IAsyncEnumerable<DecisionRun> ListDecisionsAsync(string? decider = null, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int count = 0;
        await foreach (InstanceSummary summary in Store.ListAsync(new InstanceQuery { NameContains = decider }, cancellationToken).ConfigureAwait(false))
        {
            if (decider is not null && !string.Equals(summary.Name, decider, StringComparison.Ordinal))
            {
                continue;
            }

            Instance? instance = await Store.LoadAsync(summary.InstanceId, cancellationToken).ConfigureAwait(false);
            if (instance is null || !IsDecisionInstance(instance))
            {
                continue;
            }

            yield return ToRun(instance);
            if (limit is { } max && ++count >= max)
            {
                yield break;
            }
        }
    }

    /// <summary>Counts, cost, timing and mean confidence over the recorded runs of one decider.</summary>
    /// <param name="decider">The decider's name.</param>
    /// <param name="excludeOrigins">Origins to leave out, such as <c>eval</c> and <c>replay</c>, so evaluation runs stay out of production numbers. Null counts every run.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The statistics.</returns>
    public async Task<DecisionStats> DecisionStatsAsync(string decider, IReadOnlyCollection<string>? excludeOrigins = null, CancellationToken cancellationToken = default)
    {
        int runs = 0, failed = 0;
        decimal cost = 0m;
        TimeSpan duration = TimeSpan.Zero;
        var confidenceSum = new Dictionary<string, (double Sum, int N)>(StringComparer.Ordinal);
        var probabilitySum = new Dictionary<string, (double Sum, int N)>(StringComparer.Ordinal);
        await foreach (DecisionRun run in ListDecisionsAsync(decider, null, cancellationToken).ConfigureAwait(false))
        {
            if (excludeOrigins is not null && excludeOrigins.Contains(run.Origin))
            {
                continue;
            }

            runs++;
            failed += run.State == InstanceStates.Error ? 1 : 0;
            cost += run.Cost;
            duration += run.Duration;
            if (run.Answers is { ValueKind: JsonValueKind.Object } answers)
            {
                foreach (JsonProperty q in answers.EnumerateObject())
                {
                    IEnumerable<JsonElement> items = q.Value.ValueKind == JsonValueKind.Array ? q.Value.EnumerateArray() : [q.Value];
                    foreach (JsonElement item in items)
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (item.TryGetProperty("confidence", out JsonElement c) && c.TryGetDouble(out double conf))
                        {
                            confidenceSum[q.Name] = confidenceSum.TryGetValue(q.Name, out var s) ? (s.Sum + conf, s.N + 1) : (conf, 1);
                        }
                        else if (item.TryGetProperty("noul", out JsonElement p) && p.TryGetDouble(out double prob))
                        {
                            probabilitySum[q.Name] = probabilitySum.TryGetValue(q.Name, out var s) ? (s.Sum + prob, s.N + 1) : (prob, 1);
                        }
                    }
                }
            }
        }

        return new DecisionStats(
            decider,
            runs,
            failed,
            cost,
            runs == 0 ? TimeSpan.Zero : duration / runs,
            confidenceSum.ToDictionary(static p => p.Key, static p => p.Value.Sum / p.Value.N, StringComparer.Ordinal),
            probabilitySum.ToDictionary(static p => p.Key, static p => p.Value.Sum / p.Value.N, StringComparer.Ordinal));
    }

    private static bool IsDecisionInstance(Instance instance) => instance.ExtensionData is { } e && e.TryGetValue("decider", out object? d) && d is IDictionary<string, object?>;

    private static DecisionRun ToRun(Instance instance)
    {
        var info = (IDictionary<string, object?>)instance.ExtensionData!["decider"]!;
        string? Str(string key) => info.TryGetValue(key, out object? v) ? v as string : null;
        JsonElement? Element(string key) => info.TryGetValue(key, out object? v) && v is not null ? PlainJson.ToElement(v) : null;
        decimal cost = instance.Messages.Sum(static m => m.Usage?.Cost ?? 0m);
        double ms = info.TryGetValue("durationMs", out object? d) ? Convert.ToDouble(d, CultureInfo.InvariantCulture) : 0;
        return new DecisionRun(
            instance.InstanceId,
            instance.Name,
            instance.State,
            instance.RequestedModel ?? instance.Model,
            Str("answeredBy"),
            Str("engine"),
            Str("origin") ?? "api",
            Str("replayOf"),
            TimeSpan.FromMilliseconds(ms),
            cost,
            Element("answers"),
            Element("outputs"));
    }

    private static bool IsDecisionModel(ResolvedModel model) =>
        string.Equals(model.Model?.Kind, "decision", StringComparison.OrdinalIgnoreCase)
        || (model.Model is null && model.Provider.Capabilities?.SystemOne == true);

    internal async Task<DecisionResult> DecideCoreAsync(
        Decider decider,
        IReadOnlyDictionary<string, object?>? callerInputs,
        string? modelOverride,
        string origin,
        string? replayOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decider);
        DeciderDefinition def = decider.Definition;
        string requested = modelOverride ?? def.Model;
        ResolvedModel model = Resolve(requested);
        bool decisions = IsDecisionModel(model);
        IDecisionProvider? decisionProvider = null;
        IChatProvider? chatProvider = null;
        if (decisions)
        {
            decisionProvider = Providers.Get(model, TimeProvider) as IDecisionProvider
                ?? throw new CapabilityMissingException($"Decisions on provider '{model.Provider.Name}'", $"a decisions provider for '{model.Id}'");
        }
        else
        {
            chatProvider = GetChatProvider(model);
        }

        var images = new List<DecisionImage>();
        Dictionary<string, object?> plain = PlainValues.NormalizeInputs(ReplaceAssets(callerInputs, images));
        var agent = new AgentDefinition { Name = decider.Name, Model = requested, SourceName = def.SourceName };
        Instance instance = Instance.FromDefinition(agent, NewId("i"));
        instance.Inputs = plain;
        long started = TimeProvider.GetTimestamp();
        string engine = decisions ? (decisionProvider!.Name == "typesafe" ? "jev" : "decisions") : "llm";

        RunScope run = await RunScope.StartAsync(this, instance, cancellationToken).ConfigureAwait(false);
        await using (run.ConfigureAwait(false))
        {
            try
            {
                JsonElement inputsElement = PlainJson.ToElement(plain);
                IDeciderLogic? logic = decider.Logic;
                JsonElement prepared = logic is { HasPrepare: true } ? await logic.PrepareAsync(inputsElement, cancellationToken).ConfigureAwait(false) : inputsElement;

                Dictionary<string, object?> scope = TemplateScope.Build(this, agent, [], null, []);
                scope["inputs"] = DeciderValues.FromElement(prepared);
                scope["decider"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = decider.Name, ["model"] = requested };
                // A decider is pure and its templates must not hide a typo: a missing name is always an error here, whatever the definition says.
                RenderedDecision rendered = decider.GetTemplate(Functions).Render(
                    scope,
                    new TemplateRenderOptions { MissingKey = MissingKeyMode.Error, TimeProvider = TimeProvider, CancellationToken = cancellationToken });

                images = [.. images.Where(i => Mentions(rendered, i.Reference))];
                await run.AppendAsync(StateMessage(rendered), cancellationToken).ConfigureAwait(false);

                EngineOutput output;
                if (rendered.Questions.Count == 0)
                {
                    // Every question is repeated over an empty list: the answers are all empty lists and nothing is asked.
                    output = new EngineOutput([], model.Name, [], []);
                }
                else if (decisions)
                {
                    var meter = new DecisionMeter(this, run, model, decisionProvider!);
                    string wire = model.Model?.Properties is { } props && props.TryGetValue("wireModel", out object? w) && w is string wm ? wm : (model.WasQualified ? model.Name : model.Requested);
                    string cacheKey = def.Cache.Key ?? decider.Name;
                    output = await DecisionEngine.RunAsync(
                        meter,
                        new DecisionRequest
                        {
                            Model = wire,
                            State = rendered.State,
                            Questions = rendered.Questions,
                            Images = images,
                            PromptCacheKey = cacheKey.Length > 64 ? cacheKey[..64] : cacheKey,
                            ExplicitCacheBreakpoint = def.Cache.Explicit,
                        },
                        model,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (images.Count > 0)
                    {
                        throw new CapabilityMissingException("Images in a decider answered by a chat model", "a decisions model that takes images (an OpenAI Decisions model), or inputs without images");
                    }

                    output = await ChatEngine.RunAsync(this, run, model, chatProvider!, def.Llm, rendered, cancellationToken).ConfigureAwait(false);
                }

                JsonObject answers = DeciderAnswers.Assemble(rendered, output.Answers);
                JsonElement answersElement = PlainJson.ToElement(answers);
                JsonElement outputs = logic is { HasDecide: true }
                    ? await logic.DecideAsync(answersElement, prepared, cancellationToken).ConfigureAwait(false)
                    : answersElement;

                Cost cost = Cost.Sum(output.Calls.Select(static c => c.Usage?.ToCost()));
                Usage? total = TotalUsage(output.Calls, output.Model, cost);
                TimeSpan elapsed = TimeProvider.GetElapsedTime(started);
                var assistant = new Message
                {
                    Id = NewId("m"),
                    Assistant = [new JsonPart(answers.DeepClone())],
                    DateTime = TimeProvider.GetUtcNow(),
                    Usage = total,
                };
                instance.ExtensionData ??= [];
                instance.ExtensionData["decider"] = RunInfo(engine, model, requested, output, origin, replayOf, elapsed, PlainJson.ToPlain(answersElement), PlainJson.ToPlain(outputs));
                await run.AppendAsync(assistant, cancellationToken).ConfigureAwait(false);
                await run.CompleteAsync(InstanceStates.Stopped, cancellationToken).ConfigureAwait(false);
                return new DecisionResult(answersElement, outputs, output.Model, engine, total, cost, output.Calls, instance, output.Warnings, elapsed);
            }
            catch (Exception ex)
            {
                TimeSpan elapsed = TimeProvider.GetElapsedTime(started);
                (instance.ExtensionData ??= [])["decider"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["engine"] = engine,
                    ["origin"] = origin,
                    ["replayOf"] = replayOf,
                    ["durationMs"] = elapsed.TotalMilliseconds,
                    ["error"] = RecordText.Bound(ex.Message),
                };
                Exception e = await run.FailAsync(ex, null, cancellationToken).ConfigureAwait(false);
                Rethrow(e, ex);
                throw;
            }
        }
    }

    private static Dictionary<string, object?> RunInfo(string engine, ResolvedModel model, string requested, EngineOutput output, string origin, string? replayOf, TimeSpan elapsed, object? answers, object? outputs) =>
        new(StringComparer.Ordinal)
        {
            ["engine"] = engine,
            ["answerSource"] = engine == "llm" ? "llm" : "system-one",
            ["requestedModel"] = requested,
            ["resolvedModel"] = model.Id,
            ["answeredBy"] = output.Model,
            ["origin"] = origin,
            ["replayOf"] = replayOf,
            ["durationMs"] = elapsed.TotalMilliseconds,
            ["calls"] = output.Calls.Select(static c => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["engine"] = c.Engine,
                ["question"] = c.Question,
                ["chunk"] = (long)c.Chunk,
                ["questions"] = (long)c.Questions,
                ["sample"] = (long)c.Sample,
                ["attempt"] = (long)c.Attempt,
                ["status"] = c.Status,
                ["error"] = c.Error,
                ["inputTokens"] = c.Usage?.InputTokens ?? 0,
                ["outputTokens"] = c.Usage?.OutputTokens ?? 0,
                ["cost"] = c.Usage?.Cost ?? 0m,
            }).ToList(),
            ["answers"] = answers,
            ["outputs"] = outputs,
        };

    private static Usage? TotalUsage(IReadOnlyList<DecisionCall> calls, string model, Cost cost)
    {
        long input = 0, output = 0, write = 0, read = 0;
        bool any = false;
        foreach (Usage u in calls.Select(static c => c.Usage).OfType<Usage>())
        {
            any = true;
            input += u.InputTokens;
            output += u.OutputTokens;
            write += u.CacheCreationInputTokens;
            read += u.CacheReadInputTokens;
        }

        return any
            ? new Usage { Model = model, InputTokens = input, OutputTokens = output, CacheCreationInputTokens = write, CacheReadInputTokens = read, Cost = cost.Total, CostLines = cost.Lines.Count > 0 ? cost.Lines : null }
            : null;
    }

    private Message StateMessage(RenderedDecision rendered)
    {
        Part part;
        JsonNode? state = null;
        try
        {
            state = JsonNode.Parse(rendered.State.Span);
        }
        catch (JsonException)
        {
            // Rendered state is JSON by construction; text keeps the run recordable if it somehow is not.
        }

        part = state is JsonObject or JsonArray ? new JsonPart(state) : new TextPart(Encoding.UTF8.GetString(rendered.State.Span));
        return new Message { Id = NewId("m"), User = [part], DateTime = TimeProvider.GetUtcNow(), Source = new MessageSource { Kind = "runtime", Reason = "decider-state" } };
    }

    private static bool Mentions(RenderedDecision rendered, string reference)
    {
        byte[] needle = Encoding.UTF8.GetBytes(reference);
        if (rendered.State.Span.IndexOf(needle) >= 0)
        {
            return true;
        }

        foreach (WireQuestion q in rendered.Questions)
        {
            if (q.Json.Span.IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // Image parts in the inputs become placeholder strings the templates carry, and the images travel beside the request.
    private Dictionary<string, object?>? ReplaceAssets(IReadOnlyDictionary<string, object?>? inputs, List<DecisionImage> images)
    {
        if (inputs is null)
        {
            return null;
        }

        object? Walk(object? value)
        {
            switch (value)
            {
                case AssetPart { Asset: { Data: { } data } asset } when asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    string reference = "oneimg-" + IdSource();
                    images.Add(new DecisionImage(reference, Convert.FromBase64String(data), asset.ContentType));
                    return reference;
                case AssetPart:
                    throw new ArgumentException("An image input needs its bytes (Asset.File or Asset.FromBytes); a stored reference carries none.", nameof(inputs));
                case IDictionary<string, object?> d:
                    return d.ToDictionary(static p => p.Key, p => Walk(p.Value), StringComparer.Ordinal);
                case string:
                    return value;
                case System.Collections.IDictionary:
                    return value;
                case System.Collections.IEnumerable list:
                    return list.Cast<object?>().Select(Walk).ToList();
                default:
                    return value;
            }
        }

        return inputs.ToDictionary(static p => p.Key, p => Walk(p.Value), StringComparer.Ordinal);
    }
}

/// <summary>Plain values to and from JSON, keeping key order and whole numbers.</summary>
internal static class PlainJson
{
    public static JsonElement ToElement(object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    public static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    public static object? ToPlain(JsonElement element) => PlainValues.FromElement(element);

    private static void Write(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case JsonElement e:
                e.WriteTo(w);
                break;
            case IDictionary<string, object?> map:
                w.WriteStartObject();
                foreach ((string k, object? v) in map)
                {
                    w.WritePropertyName(k);
                    Write(w, v);
                }

                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartArray();
                foreach (object? item in list)
                {
                    Write(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                throw new ArgumentException($"A {value.GetType().Name} cannot be written as JSON without reflection.", nameof(value));
        }
    }
}

/// <summary>Puts an engine's answers by wire id into the declared shape: one answer per question, a list for a repeated question.</summary>
internal static class DeciderAnswers
{
    public static JsonObject Assemble(RenderedDecision rendered, JsonObject wire)
    {
        var answers = new JsonObject();
        var refused = new List<string>();
        JsonNode Take(string id, string type)
        {
            if (!wire.TryGetPropertyValue(id, out JsonNode? node) || node is not JsonObject answer)
            {
                throw new DeciderAnswerException($"The engine gave no answer for question '{id}'.", id);
            }

            string kind = (string?)answer["type"] ?? "";
            if (kind == "refusal")
            {
                refused.Add(id);
            }
            else if (kind != type)
            {
                throw new DeciderAnswerException($"The answer to question '{id}' is a {kind}, but the question is a {type}.", id);
            }

            return answer.DeepClone();
        }

        foreach (DeclaredQuestion q in rendered.Declared)
        {
            if (q.Count is not { } count)
            {
                answers[q.Id] = Take(q.Id, q.Type);
                continue;
            }

            var list = new JsonArray();
            for (int n = 0; n < count; n++)
            {
                list.Add(Take(q.Id + "[" + n.ToString(CultureInfo.InvariantCulture) + "]", q.Type));
            }

            answers[q.Id] = list;
        }

        if (refused.Count > 0)
        {
            // OpenAI Decisions can decline single questions; the others are answered but the run does not guess at the missing ones.
            throw new RefusedException($"The engine declined to answer {(refused.Count == 1 ? "question" : "questions")} {string.Join(", ", refused.Select(static r => "'" + r + "'"))}.", string.Join(", ", refused));
        }

        return answers;
    }
}
