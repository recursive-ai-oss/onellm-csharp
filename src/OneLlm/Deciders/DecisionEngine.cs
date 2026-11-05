using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Providers;

namespace OneLlm.Deciders;

/// <summary>
/// Answers a decision with a decisions engine (TypeSafe Jev, OpenAI Decisions). Engines evaluate every question independently against
/// the state, so a question set too large for one request is split across requests that each carry the whole state, and the answers
/// are merged. The rules:
/// <list type="bullet">
/// <item>The budget for one request is the model's context window less 10%; sizes are estimated at three bytes a token (deliberately
/// high: over-splitting only repeats the state, under-splitting fails).</item>
/// <item>When the state and every question fit the estimate, or no window is declared, one request goes out as it is.</item>
/// <item>If the engine still says the request is too long, that request is halved and both halves are tried again; one question that
/// cannot fit alone is an error and nothing is cut.</item>
/// <item>At most 8 requests are in flight.</item>
/// <item>An engine with a question cap per request (the catalog's <c>maxQuestions</c>) never gets more.</item>
/// </list>
/// </summary>
internal static class DecisionEngine
{
    private const int BytesPerToken = 3;
    private const double BudgetMargin = 0.10;
    private const int QuestionOverheadTokens = 8;
    private const int RequestOverheadTokens = 64;
    private const int MaxParallel = 8;

    public static int EstimateTokens(int bytes) => (bytes + BytesPerToken - 1) / BytesPerToken;

    /// <summary>The proactive split budget in tokens, or 0 when the model declares no context window.</summary>
    public static int Budget(ModelSpec? model) =>
        model?.ContextWindow is > 0 and long window ? (int)(window * (1 - BudgetMargin)) : 0;

    /// <summary>Groups question indexes into requests of at most <paramref name="avail"/> estimated tokens (state excluded), balanced so parallel requests are alike. A question above the limit gets a request alone.</summary>
    public static List<List<int>> PlanChunks(IReadOnlyList<int> questionTokens, int avail)
    {
        if (avail < 1)
        {
            avail = 1;
        }

        List<List<int>> Pack(int limit)
        {
            var chunks = new List<List<int>>();
            var current = new List<int>();
            int sum = 0;
            for (int i = 0; i < questionTokens.Count; i++)
            {
                int t = questionTokens[i];
                if (current.Count > 0 && sum + t > limit)
                {
                    chunks.Add(current);
                    current = [];
                    sum = 0;
                }

                current.Add(i);
                sum += t;
            }

            if (current.Count > 0)
            {
                chunks.Add(current);
            }

            return chunks;
        }

        List<List<int>> best = Pack(avail);
        int n = best.Count;
        if (n <= 1)
        {
            return best;
        }

        // Balance: the smallest limit that still packs into the same number of requests.
        int lo = 1, hi = avail;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Pack(mid).Count <= n)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return Pack(lo);
    }

    public static async Task<EngineOutput> RunAsync(
        DecisionMeter meter,
        DecisionRequest template,
        ResolvedModel model,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WireQuestion> questions = template.Questions;
        int wireCount = questions.Count;
        int maxQuestions = MaxQuestions(model.Model);
        int budget = Budget(model.Model);
        int stateTokens = EstimateTokens(template.State.Length);
        int questionsBytes = QuestionsObjectBytes(questions);
        int need = stateTokens + EstimateTokens(questionsBytes) + wireCount * QuestionOverheadTokens + RequestOverheadTokens;
        var merged = new JsonObject();
        string answered = model.Name;
        using var gate = new SemaphoreSlim(MaxParallel);
        int sequence = 0;

        async Task<JsonObject> SendAsync(IReadOnlyList<int> idx, int chunk, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            DecisionResponse response;
            try
            {
                response = await meter.SendAsync(template with { Questions = [.. idx.Select(i => questions[i])] }, chunk, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            if (response.Model is { Length: > 0 } m)
            {
                answered = m;
            }

            return response.Answers;
        }

        // Runs the questions at idx, halving the request when the engine says it is too long.
        async Task<JsonObject> RunChunkAsync(IReadOnlyList<int> idx, int chunk, CancellationToken ct)
        {
            try
            {
                return await SendAsync(idx, chunk, ct).ConfigureAwait(false);
            }
            catch (ContextOverflowException)
            {
                if (idx.Count == 1)
                {
                    throw QuestionTooLarge(questions[idx[0]], stateTokens, model);
                }

                int mid = idx.Count / 2;
                int a = Interlocked.Increment(ref sequence), b = Interlocked.Increment(ref sequence);
                Task<JsonObject> first = RunChunkAsync([.. idx.Take(mid)], a, ct);
                Task<JsonObject> second = RunChunkAsync([.. idx.Skip(mid)], b, ct);
                JsonObject[] halves = await Task.WhenAll(first, second).ConfigureAwait(false);
                return Merge(halves[0], halves[1]);
            }
        }

        bool fits = (budget == 0 || need <= budget) && (maxQuestions == 0 || wireCount <= maxQuestions);
        if (fits)
        {
            try
            {
                JsonObject all = await SendAsync(Enumerable.Range(0, wireCount).ToArray(), 0, cancellationToken).ConfigureAwait(false);
                return new EngineOutput(all, answered, meter.Calls, meter.Warnings);
            }
            catch (ContextOverflowException) when (wireCount > 1)
            {
                // The engine counted more tokens than the estimate: split below.
            }
            catch (ContextOverflowException)
            {
                throw QuestionTooLarge(questions[0], stateTokens, model);
            }
        }

        var perQuestion = new int[wireCount];
        for (int i = 0; i < wireCount; i++)
        {
            perQuestion[i] = EstimateTokens(Encoding.UTF8.GetByteCount(questions[i].Id) + questions[i].Json.Length + 4) + QuestionOverheadTokens;
        }

        int avail = budget - stateTokens - RequestOverheadTokens;
        int half = (EstimateTokens(questionsBytes) + 1) / 2;
        if (budget == 0 || half < avail)
        {
            // Reached with a fitting estimate only when the engine already rejected the whole set: at least halve it.
            if (budget == 0 || need <= budget)
            {
                avail = half;
            }
        }

        List<List<int>> chunks = PlanChunks(perQuestion, avail);
        if (maxQuestions > 0)
        {
            chunks = [.. chunks.SelectMany(c => c.Chunk(maxQuestions).Select(p => p.ToList()))];
        }

        var numbers = chunks.Select(_ => Interlocked.Increment(ref sequence)).ToArray();
        JsonObject[] parts = await Task.WhenAll(chunks.Select((c, i) => RunChunkAsync(c, numbers[i], cancellationToken))).ConfigureAwait(false);
        foreach (JsonObject part in parts)
        {
            Merge(merged, part);
        }

        return new EngineOutput(merged, answered, meter.Calls, meter.Warnings);
    }

    private static JsonObject Merge(JsonObject into, JsonObject from)
    {
        foreach ((string key, JsonNode? value) in from.ToList())
        {
            from.Remove(key);
            into[key] = value;
        }

        return into;
    }

    private static int MaxQuestions(ModelSpec? model) =>
        model?.Properties is { } p && p.TryGetValue("maxQuestions", out object? v) && v is long n && n > 0 ? (int)n : 0;

    // The length of the questions object as it would be sent: {"id":question,...}.
    private static int QuestionsObjectBytes(IReadOnlyList<WireQuestion> questions)
    {
        int total = 2;
        for (int i = 0; i < questions.Count; i++)
        {
            var sb = new StringBuilder();
            GoJson.AppendString(sb, questions[i].Id);
            total += Encoding.UTF8.GetByteCount(sb.ToString()) + 1 + questions[i].Json.Length + (i > 0 ? 1 : 0);
        }

        return total;
    }

    private static DeciderAnswerException QuestionTooLarge(WireQuestion q, int stateTokens, ResolvedModel model) =>
        new(
            $"Question '{q.Id}' does not fit in one {model.Name} request even on its own (state about {stateTokens} tokens plus the question about {EstimateTokens(q.Json.Length)} tokens, estimated"
            + (model.Model?.ContextWindow is { } w ? $"; model limit {w} tokens" : "")
            + "). Shorten the state or the per-item data.",
            q.Id);
}
