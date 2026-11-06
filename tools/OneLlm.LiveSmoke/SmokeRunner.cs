using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;

namespace OneLlm.LiveSmoke;

/// <summary>How one smoke case ended.</summary>
public enum SmokeStatus
{
    /// <summary>The call worked and its answer passed the check.</summary>
    Pass,

    /// <summary>The call failed or its answer failed the check.</summary>
    Fail,

    /// <summary>The call was not sent (a spend cap, or the provider has no such feature).</summary>
    Skip,
}

/// <summary>The line printed for one case.</summary>
public sealed record SmokeResult(string Provider, string Feature, string Model, SmokeStatus Status, string Detail, long InputTokens, long OutputTokens, decimal Cost);

/// <summary>One keyed provider under test: its clients and the catalog models to use for each feature.</summary>
public sealed class SmokeTarget
{
    /// <summary>The provider's name, as printed.</summary>
    public required string Name { get; init; }

    /// <summary>The chat client.</summary>
    public required IChatProvider Chat { get; init; }

    /// <summary>The embeddings client, if the provider has one.</summary>
    public IEmbeddingProvider? Embeddings { get; init; }

    /// <summary>The token-count client, if the provider has one.</summary>
    public ITokenCountingProvider? Counter { get; init; }

    /// <summary>The Decisions client, if the provider has one.</summary>
    public IDecisionProvider? Decisions { get; init; }

    /// <summary>The cheap chat model.</summary>
    public required string ChatModel { get; init; }

    /// <summary>The model used for the reasoning case.</summary>
    public string? ReasoningModel { get; init; }

    /// <summary>The embedding model.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>The Decisions model.</summary>
    public string? DecisionModel { get; init; }
}

/// <summary>
/// Runs the smoke cases for one provider under its budget. Every call goes through <see cref="SmokeBudget"/> first; a refused call is
/// reported as skipped and never sent. Everything printed is scrubbed.
/// </summary>
public sealed class SmokeRunner
{
    private const long InputMargin = 300;
    private readonly SmokeTarget _target;
    private readonly SmokeBudget _budget;
    private readonly ModelCatalog _catalog;
    private readonly Scrubber _scrub;
    private readonly Action<string> _write;

    /// <summary>Creates a runner.</summary>
    /// <param name="target">The provider and models.</param>
    /// <param name="budget">The provider's budget.</param>
    /// <param name="catalog">The catalog that prices the calls.</param>
    /// <param name="scrub">Scrubs every line before it is written.</param>
    /// <param name="write">Receives each scrubbed line.</param>
    public SmokeRunner(SmokeTarget target, SmokeBudget budget, ModelCatalog catalog, Scrubber scrub, Action<string> write)
    {
        _target = target;
        _budget = budget;
        _catalog = catalog;
        _scrub = scrub;
        _write = write;
    }

    /// <summary>Every result so far.</summary>
    public List<SmokeResult> Results { get; } = [];

    /// <summary>Runs every case the provider supports.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the cases are done.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string chat = _target.ChatModel;
        await Case("chat", chat, (c, ct) => c.ChatAsync("Reply with the single word: pong", 300, text => text.Contains("pong", StringComparison.OrdinalIgnoreCase) ? null : "the answer does not contain 'pong'", ct), cancellationToken);
        await Case("stream", chat, StreamCase, cancellationToken);
        await Case("tool round trip", chat, ToolCase, cancellationToken);
        await Case("structured output", chat, StructuredCase, cancellationToken);
        if (_target.ReasoningModel is { } reasoning)
        {
            await Case("reasoning", reasoning, ReasoningCase, cancellationToken);
        }

        if (_target.EmbeddingModel is { } embedding && _target.Embeddings is not null)
        {
            await Case("embeddings", embedding, EmbedCase, cancellationToken);
        }

        if (_target.Counter is not null)
        {
            await Case("token counting", chat, CountCase, cancellationToken);
        }

        if (_target.DecisionModel is { } decisions && _target.Decisions is not null)
        {
            await Case("decisions", decisions, DecideCase, cancellationToken);
        }
    }

    /// <summary>One line per case, then the provider's total.</summary>
    /// <returns>The summary line.</returns>
    public string Summary() =>
        string.Create(CultureInfo.InvariantCulture, $"spend {_target.Name}: ${_budget.Spent:0.000000} of ${_budget.Cap:0.00} cap ({Results.Count(r => r.Status == SmokeStatus.Pass)} passed, {Results.Count(r => r.Status == SmokeStatus.Fail)} failed, {Results.Count(r => r.Status == SmokeStatus.Skip)} skipped)");

    private async Task Case(string feature, string model, Func<CallScope, CancellationToken, Task<string>> body, CancellationToken ct)
    {
        var scope = new CallScope(this, model);
        SmokeResult result;
        try
        {
            string detail = await body(scope, ct);
            result = new SmokeResult(_target.Name, feature, model, SmokeStatus.Pass, detail, scope.Input, scope.Output, scope.Cost);
        }
        catch (SkipException e)
        {
            result = new SmokeResult(_target.Name, feature, model, SmokeStatus.Skip, e.Message, scope.Input, scope.Output, scope.Cost);
        }
        catch (CheckException e)
        {
            result = new SmokeResult(_target.Name, feature, model, SmokeStatus.Fail, e.Message, scope.Input, scope.Output, scope.Cost);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new SmokeResult(_target.Name, feature, model, SmokeStatus.Fail, e.GetType().Name + ": " + e.Message, scope.Input, scope.Output, scope.Cost);
        }

        Results.Add(result);
        string status = result.Status switch { SmokeStatus.Pass => "PASS", SmokeStatus.Fail => "FAIL", _ => "SKIP" };
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{status} {result.Provider,-9} {result.Feature,-18} {result.Model,-24} in={result.InputTokens} out={result.OutputTokens} cost=${result.Cost:0.000000}  {result.Detail}");
        _write(_scrub.Scrub(line.ReplaceLineEndings(" ")));
    }

    private static async Task<string> StreamCase(CallScope c, CancellationToken ct)
    {
        ChatRequest request = c.Request("Count from one to three in words.", 300);
        Message m = await c.StreamAsync(request, ct);
        string text = m.GetAssistantText();
        return text.Length == 0 ? throw new CheckException("the stream built no text") : "streamed " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters";
    }

    private static async Task<string> ToolCase(CallScope c, CancellationToken ct)
    {
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""")!;
        var tool = new ToolDefinition("get_weather", "Returns the current weather for a city.", schema);
        var user = Message.FromUser("What is the weather in Paris? Use the tool.");
        ChatRequest first = c.Request(user, 400) with { Tools = [tool] };
        ChatResponse r1 = await c.SendAsync(first, ct);
        ToolCall call = (r1.Message.ToolCalls.Count > 0 ? r1.Message.ToolCalls[0] : null) ?? throw new CheckException("the model made no tool call");
        if (call.Tool != "get_weather")
        {
            throw new CheckException("the model called '" + call.Tool + "'");
        }

        // Give the result back on the same call, as the loop does, and ask again.
        var answered = new ToolCall(call.Id, call.Tool) { Routine = call.Routine, Inputs = call.Inputs, Result = [new TextPart("18 degrees Celsius, clear")] };
        var assistant = new Message { Id = r1.Message.Id, Assistant = r1.Message.Assistant, ToolCalls = [answered], ExtensionData = r1.Message.ExtensionData };
        ChatRequest second = c.Request(user, 400) with { Messages = [user, assistant], Tools = [tool] };
        ChatResponse r2 = await c.SendAsync(second, ct);
        string text = r2.Message.GetAssistantText();
        return text.Length == 0 ? throw new CheckException("no final answer after the tool result") : "two calls; final answer " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters";
    }

    private static async Task<string> StructuredCase(CallScope c, CancellationToken ct)
    {
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"},"country":{"type":"string"}},"required":["city","country"],"additionalProperties":false}""")!;
        ChatRequest request = c.Request("Give the capital of France and its country.", 400) with { ResponseFormat = ResponseFormat.FromSchema("capital", schema) };
        ChatResponse r = await c.SendAsync(request, ct);
        using JsonDocument doc = JsonDocument.Parse(r.Message.GetAssistantText());
        return doc.RootElement.TryGetProperty("city", out _) && doc.RootElement.TryGetProperty("country", out _)
            ? "answer fits the schema"
            : throw new CheckException("the JSON lacks city or country");
    }

    private static async Task<string> ReasoningCase(CallScope c, CancellationToken ct)
    {
        ChatRequest request = c.Request("What is 17 times 23? Answer with just the number.", 4000) with { Reasoning = new ReasoningOptions { Effort = "low" } };
        ChatResponse r = await c.SendAsync(request, ct);
        bool hasReasoning = r.Message.Assistant.Any(static p => p is ReasoningPart);
        long thinking = r.Usage?.GetCounter("reasoningTokens") ?? 0;
        string text = r.Message.GetAssistantText();
        return text.Contains("391", StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"answered 391; reasoning part {(hasReasoning ? "present" : "absent")}, reasoning tokens {thinking}")
            : throw new CheckException("the answer does not contain 391");
    }

    private static async Task<string> EmbedCase(CallScope c, CancellationToken ct)
    {
        EmbeddingResponse r = await c.EmbedAsync("reset my password", ct);
        return r.Embeddings.Count == 1 && r.Embeddings[0].Length > 0
            ? "vector of " + r.Embeddings[0].Length.ToString(CultureInfo.InvariantCulture)
            : throw new CheckException("no vector came back");
    }

    private static async Task<string> CountCase(CallScope c, CancellationToken ct)
    {
        ChatRequest request = c.Request("How many tokens is this sentence?", 16);
        TokenCount n = await c.CountAsync(request, ct);
        return n.InputTokens > 0 ? string.Create(CultureInfo.InvariantCulture, $"{n.InputTokens} tokens, tier {n.Tier}") : throw new CheckException("the count was zero");
    }

    private static async Task<string> DecideCase(CallScope c, CancellationToken ct)
    {
        DecisionResponse r = await c.DecideAsync(ct);
        return r.Answers.Count > 0 ? "answered " + string.Join(",", r.Answers.Select(static a => a.Key)) : throw new CheckException("no answers");
    }

    private sealed class SkipException(string message) : Exception(message);

    private sealed class CheckException(string message) : Exception(message);

    /// <summary>One case's calls: each one reserves under the budget, runs, and settles with its real cost.</summary>
    private sealed class CallScope
    {
        private readonly SmokeRunner _owner;
        private readonly string _model;
        private readonly ResolvedModel _resolved;

        public CallScope(SmokeRunner owner, string model)
        {
            _owner = owner;
            _model = model;
            _resolved = owner._catalog.Resolve(model);
        }

        public long Input { get; private set; }

        public long Output { get; private set; }

        public decimal Cost { get; private set; }

        public ChatRequest Request(string prompt, int maxOutput) =>
            Request(Message.FromUser(prompt), maxOutput);

        public ChatRequest Request(Message user, int maxOutput) => new() { Model = _resolved.Name, Messages = [user], MaxOutputTokens = maxOutput };

        public async Task<string> ChatAsync(string prompt, int maxOutput, Func<string, string?> check, CancellationToken ct)
        {
            ChatResponse r = await SendAsync(Request(prompt, maxOutput), ct);
            string text = r.Message.GetAssistantText();
            return check(text) is { } problem ? throw new CheckException(problem) : "answered " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters";
        }

        public Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken ct) =>
            Paid(Estimate(request), async () =>
            {
                ChatResponse r = await _owner._target.Chat.SendAsync(request, ct);
                return (r, r.Usage);
            });

        public Task<Message> StreamAsync(ChatRequest request, CancellationToken ct) =>
            Paid(Estimate(request), async () =>
            {
                var acc = new MessageAccumulator();
                await foreach (StreamEvent e in _owner._target.Chat.StreamAsync(request, ct))
                {
                    acc.Add(e);
                }

                Message m = acc.ToMessage();
                return (m, m.Usage);
            });

        public Task<EmbeddingResponse> EmbedAsync(string text, CancellationToken ct) =>
            Paid(Worst(new Usage { InputTokens = 500 }), async () =>
            {
                EmbeddingResponse r = await _owner._target.Embeddings!.EmbedAsync(new EmbeddingRequest { Model = _resolved.Name, Inputs = [text] }, ct);
                return (r, r.Usage);
            });

        public Task<TokenCount> CountAsync(ChatRequest request, CancellationToken ct) =>
            Paid(new CostEstimate(0, TokenCountTier.Provider, 0, 0m, []), async () => (await _owner._target.Counter!.CountTokensAsync(request, ct), (Usage?)null));

        public Task<DecisionResponse> DecideAsync(CancellationToken ct)
        {
            var question = new WireQuestion(
                "area",
                "choice",
                Encoding.UTF8.GetBytes("""{"type":"choice","instructions":"Which area does this support ticket belong to?","criteria":{"billing":"Charges and refunds","orders":"Delivery and returns"}}"""));
            var request = new DecisionRequest
            {
                Model = _model,
                State = Encoding.UTF8.GetBytes("""{"ticket":"I was charged twice for my last order."}"""),
                Questions = [question],
            };
            return Paid(Worst(new Usage { InputTokens = 3000 }), async () =>
            {
                DecisionResponse r = await _owner._target.Decisions!.DecideAsync(request, ct);
                return (r, r.Usage);
            });
        }

        // The worst case of a call whose size is known without a request: this usage priced at the catalog.
        private CostEstimate Worst(Usage usage) =>
            new(usage.InputTokens, TokenCountTier.Estimate, null, CostCalculator.Price(_resolved, usage)?.Total, []);

        private CostEstimate Estimate(ChatRequest request)
        {
            TokenCount local = TokenEstimator.Estimate(request, _resolved);
            return CostEstimator.Estimate(_resolved, request, new TokenCount(local.InputTokens + InputMargin, local.Tier));
        }

        private async Task<T> Paid<T>(CostEstimate estimate, Func<Task<(T Value, Usage? Usage)>> send)
        {
            if (!_owner._budget.TryReserve(estimate, out decimal reserved, out string? reason))
            {
                throw new SkipException("not sent: " + reason);
            }

            (T Value, Usage? Usage) result;
            try
            {
                result = await send();
            }
            catch (Exception e)
            {
                // A call refused by the provider cost nothing; one that may have run (a dropped connection) is charged at its worst case.
                if (e is TransportException)
                {
                    _owner._budget.Settle(reserved, null);
                    Cost += reserved;
                }
                else
                {
                    _owner._budget.Release(reserved);
                }

                throw;
            }

            decimal? actual = result.Usage is null ? null : CostCalculator.Price(_resolved, result.Usage)?.Total;
            _owner._budget.Settle(reserved, actual);
            Cost += actual ?? reserved;
            Input += result.Usage?.TotalInputTokens ?? 0;
            Output += result.Usage?.OutputTokens ?? 0;
            return result.Value;
        }
    }
}
