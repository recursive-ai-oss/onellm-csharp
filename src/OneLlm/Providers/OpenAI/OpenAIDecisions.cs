using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Providers.OpenAI;

/// <summary>
/// The OpenAI Decisions API (<c>POST /v1/decisions</c>): the state becomes one user message (<c>input_json</c> for an object or a list of
/// objects, <c>input_text</c> otherwise), each question a <c>predicate</c>, <c>choice</c> or <c>score</c>, and each answer is read back into the
/// common answer shape so decider logic sees one form whatever engine answered.
/// </summary>
internal sealed class OpenAIDecisions
{
    private readonly OpenAIWire _wire;

    public OpenAIDecisions(OpenAIWire wire) => _wire = wire;

    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Questions.Count == 0)
        {
            throw new ArgumentException("A decision request needs at least one question.", nameof(request));
        }

        ReadOnlyMemory<byte> body = WriteBody(request);
        using HttpResponseMessage response = await _wire.PostAsync("decisions", request.Model, body, stream: false, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await _wire.Http.ReadBodyAsync(response, 64 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, request);
    }

    // ---- request ----

    internal static ReadOnlyMemory<byte> WriteBody(DecisionRequest request)
    {
        var images = new ImageRefs(request.Images);
        var questions = new JsonArray();
        foreach (WireQuestion q in request.Questions)
        {
            questions.Add((JsonNode)WriteQuestion(q, images));
        }

        JsonArray content = StateParts(request.State, images);
        if (request.ExplicitCacheBreakpoint)
        {
            // The reference lets only text and image parts carry a breakpoint; a trailing newline part marks the end of the state.
            if (content.Count == 0 || content[^1] is not JsonObject last || (string?)last["type"] == "input_json")
            {
                content.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = "\n" });
            }

            ((JsonObject)content[^1]!)["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
        }

        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["questions"] = questions,
        };
        if (!string.IsNullOrEmpty(request.PromptCacheKey))
        {
            root["prompt_cache_key"] = request.PromptCacheKey.Length > 64 ? request.PromptCacheKey[..64] : request.PromptCacheKey;
        }

        if (request.ExplicitCacheBreakpoint)
        {
            root["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
        }

        if (request.ExtraParameters is { } extra)
        {
            foreach ((string key, JsonNode? value) in extra)
            {
                root[key] = value?.DeepClone();
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static JsonArray StateParts(ReadOnlyMemory<byte> state, ImageRefs images)
    {
        JsonNode? node = JsonNode.Parse(state.Span);
        var parts = new JsonArray();
        if (images.Any)
        {
            node = images.ReplaceInTree(node);
        }

        switch (node)
        {
            case JsonObject obj:
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_json", ["data"] = obj });
                break;
            case JsonArray arr when arr.All(static n => n is JsonObject):
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_json", ["data"] = arr });
                break;
            case JsonValue v when v.TryGetValue(out string? text):
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text });
                break;
            default:
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = node?.ToJsonString() ?? "null" });
                break;
        }

        images.AppendUsedImages(parts);
        return parts;
    }

    private static JsonObject WriteQuestion(WireQuestion q, ImageRefs images)
    {
        using JsonDocument doc = JsonDocument.Parse(q.Json);
        JsonElement e = doc.RootElement;
        e.TryGetProperty("instructions", out JsonElement instructions);
        e.TryGetProperty("criteria", out JsonElement criteria);
        JsonNode? instr = instructions.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(instructions.GetRawText());
        var result = new JsonObject();
        switch (q.Type)
        {
            case "noul":
                {
                    result["type"] = "predicate";
                    result["name"] = q.Id;
                    string? clarification = null;
                    if (criteria.ValueKind == JsonValueKind.Object)
                    {
                        var lines = new StringBuilder();
                        if (criteria.TryGetProperty("true", out JsonElement yes))
                        {
                            lines.Append("- yes means: ").Append(Text(yes)).Append('\n');
                        }

                        if (criteria.TryGetProperty("false", out JsonElement no))
                        {
                            lines.Append("- no means: ").Append(Text(no)).Append('\n');
                        }

                        // Decisions has no field for clarifications, so they are appended to the instructions.
                        clarification = lines.Length == 0 ? null : "\n\nClarifications:\n" + lines.ToString().TrimEnd('\n');
                    }

                    result["instructions"] = Instructions(instr, clarification, images);
                    break;
                }

            case "choice":
                {
                    result["type"] = "choice";
                    result["name"] = q.Id;
                    result["instructions"] = Instructions(instr, null, images);
                    var choices = new JsonArray();
                    foreach (JsonProperty option in criteria.EnumerateObject())
                    {
                        var choice = new JsonObject { ["value"] = option.Name };
                        string description = Text(option.Value);
                        if (description.Length > 0)
                        {
                            choice["description"] = description;
                        }

                        choices.Add((JsonNode)choice);
                    }

                    result["choices"] = choices;
                    break;
                }

            default:
                {
                    result["type"] = "score";
                    result["name"] = q.Id;
                    result["instructions"] = Instructions(instr, null, images);
                    var levels = new JsonArray();
                    if (criteria.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty level in criteria.EnumerateObject())
                        {
                            levels.Add((JsonNode)new JsonObject { ["label"] = level.Name, ["description"] = Text(level.Value) });
                        }
                    }
                    else
                    {
                        foreach (JsonElement level in criteria.EnumerateArray())
                        {
                            // A plain string is its own label and its own description.
                            string text = Text(level);
                            levels.Add((JsonNode)new JsonObject { ["label"] = text, ["description"] = text });
                        }
                    }

                    result["levels"] = levels;
                    break;
                }
        }

        return result;
    }

    // Instructions are a string, or parts when they carry an image, structure or a clarification.
    private static JsonNode Instructions(JsonNode? instructions, string? clarification, ImageRefs images)
    {
        var parts = new JsonArray();
        switch (instructions)
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue(out string? text):
                if (images.Any && images.Split(text, parts))
                {
                    break;
                }

                if (clarification is null)
                {
                    return JsonValue.Create(text)!;
                }

                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text });
                break;
            case JsonObject obj:
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_json", ["data"] = obj.DeepClone() });
                break;
            case JsonArray arr when arr.All(static n => n is JsonObject):
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_json", ["data"] = arr.DeepClone() });
                break;
            default:
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = instructions.ToJsonString() });
                break;
        }

        if (clarification is not null)
        {
            parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = clarification });
        }

        if (parts.Count == 0)
        {
            return JsonValue.Create("")!;
        }

        return parts;
    }

    // A criterion's text: a string as it is, structure as compact JSON, nothing as the empty string.
    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    // ---- response ----

    internal static DecisionResponse Parse(byte[] bytes, DecisionRequest request)
    {
        using JsonDocument doc = ParseDocument(bytes);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("answers", out JsonElement answers) || answers.ValueKind != JsonValueKind.Array)
        {
            throw new MalformedResponseException("openai", "the decisions response has no answers.");
        }

        if (answers.GetArrayLength() != request.Questions.Count)
        {
            throw new MalformedResponseException("openai", $"the decisions response has {answers.GetArrayLength()} answers for {request.Questions.Count} questions.");
        }

        var result = new JsonObject();
        int i = 0;
        foreach (JsonElement answer in answers.EnumerateArray())
        {
            // Answers come back in question order; the name is a convenience, so position decides.
            WireQuestion q = request.Questions[i++];
            result[q.Id] = ReadAnswer(answer, q);
        }

        string model = root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : request.Model;
        long input = 0, cached = 0, written = 0, output = 0;
        if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Long(usage, "input_tokens");
            output = Long(usage, "output_tokens");
            if (usage.TryGetProperty("input_tokens_details", out JsonElement d) && d.ValueKind == JsonValueKind.Object)
            {
                cached = Long(d, "cached_tokens");
                written = Long(d, "cache_write_tokens");
            }
        }

        // Decisions API: usage.input_tokens already includes cached tokens and cache writes, so they come out of the uncached count
        // here and are reported in their own counters, never added twice.
        return new DecisionResponse
        {
            Answers = result,
            Model = model,
            Usage = new Usage
            {
                Model = model,
                InputTokens = Math.Max(0, input - cached - written),
                CacheReadInputTokens = cached,
                CacheCreationInputTokens = written,
                OutputTokens = output,
            },
        };
    }

    private static JsonDocument ParseDocument(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new MalformedResponseException("openai", "the decisions response is not JSON: " + e.Message);
        }
    }

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out long n) ? n : 0;

    private static JsonObject ReadAnswer(JsonElement answer, WireQuestion q)
    {
        string type = answer.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "refusal":
                return new JsonObject { ["type"] = "refusal" };
            case "predicate":
                return new JsonObject { ["type"] = "noul", ["noul"] = Number(answer, "probability") };
            case "choice":
                {
                    var probabilities = new JsonObject();
                    foreach (JsonElement p in answer.GetProperty("probabilities").EnumerateArray())
                    {
                        probabilities[ValueText(p.GetProperty("value"))] = Number(p, "probability");
                    }

                    return new JsonObject
                    {
                        ["type"] = "choice",
                        ["choice"] = ValueText(answer.GetProperty("choice")),
                        ["confidence"] = Number(answer, "confidence"),
                        ["probabilities"] = probabilities,
                    };
                }

            case "score":
                {
                    var probabilities = new JsonObject();
                    foreach (JsonElement p in answer.GetProperty("probabilities").EnumerateArray())
                    {
                        probabilities[p.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture)] = Number(p, "probability");
                    }

                    // The legend maps each level to the criterion the decider wrote for it.
                    var legend = new JsonObject();
                    using JsonDocument question = JsonDocument.Parse(q.Json);
                    if (question.RootElement.TryGetProperty("criteria", out JsonElement criteria))
                    {
                        int n = 0;
                        foreach (JsonElement level in criteria.ValueKind == JsonValueKind.Object ? criteria.EnumerateObject().Select(static p => p.Value) : criteria.EnumerateArray())
                        {
                            legend[n++.ToString(CultureInfo.InvariantCulture)] = JsonNode.Parse(level.GetRawText());
                        }
                    }

                    return new JsonObject
                    {
                        ["type"] = "score",
                        ["score"] = Number(answer, "score"),
                        ["confidence"] = Number(answer, "confidence"),
                        ["legend"] = legend,
                        ["probabilities"] = probabilities,
                    };
                }

            default:
                throw new MalformedResponseException("openai", $"the answer to '{q.Id}' has the unknown type '{type}'.");
        }
    }

    private static string ValueText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetString() ?? "",
    };

    private static JsonNode Number(JsonElement e, string name) => JsonNode.Parse(e.GetProperty(name).GetRawText())!;

    /// <summary>Finds image placeholders in text and swaps them for the image parts the Decisions API takes.</summary>
    private sealed class ImageRefs
    {
        private readonly IReadOnlyList<DecisionImage> _images;
        private readonly List<int> _used = [];

        public ImageRefs(IReadOnlyList<DecisionImage> images) => _images = images;

        public bool Any => _images.Count > 0;

        // Splits text at placeholders into text and image parts. Returns false when the text holds none.
        public bool Split(string text, JsonArray parts)
        {
            int at = 0;
            bool found = false;
            while (at <= text.Length)
            {
                int best = -1, bestIndex = -1;
                for (int k = 0; k < _images.Count; k++)
                {
                    int p = text.IndexOf(_images[k].Reference, at, StringComparison.Ordinal);
                    if (p >= 0 && (best < 0 || p < best))
                    {
                        best = p;
                        bestIndex = k;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                found = true;
                if (best > at)
                {
                    parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text[at..best] });
                }

                parts.Add((JsonNode)Image(_images[bestIndex]));
                at = best + _images[bestIndex].Reference.Length;
            }

            if (!found)
            {
                return false;
            }

            if (at < text.Length)
            {
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text[at..] });
            }

            return true;
        }

        // In the state an image cannot sit inside input_json, so its placeholder becomes a numbered mention and the image follows the data.
        public JsonNode? ReplaceInTree(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (string key in obj.Select(static p => p.Key).ToList())
                    {
                        obj[key] = ReplaceInTree(obj[key]);
                    }

                    return obj;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        arr[i] = ReplaceInTree(arr[i]);
                    }

                    return arr;
                case JsonValue v when v.TryGetValue(out string? text):
                    for (int k = 0; k < _images.Count; k++)
                    {
                        if (text.Contains(_images[k].Reference, StringComparison.Ordinal))
                        {
                            if (!_used.Contains(k))
                            {
                                _used.Add(k);
                            }

                            text = text.Replace(_images[k].Reference, $"(image {(_used.IndexOf(k) + 1).ToString(CultureInfo.InvariantCulture)}, attached below)", StringComparison.Ordinal);
                        }
                    }

                    return JsonValue.Create(text);
                default:
                    return node;
            }
        }

        public void AppendUsedImages(JsonArray parts)
        {
            for (int n = 0; n < _used.Count; n++)
            {
                parts.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = $"\nImage {(n + 1).ToString(CultureInfo.InvariantCulture)}:" });
                parts.Add((JsonNode)Image(_images[_used[n]]));
            }
        }

        private static JsonObject Image(DecisionImage image) => new()
        {
            ["type"] = "input_image",
            ["image_url"] = "data:" + image.MediaType + ";base64," + Convert.ToBase64String(image.Data.Span),
        };
    }
}
