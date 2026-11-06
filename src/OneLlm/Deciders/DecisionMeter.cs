using System.Globalization;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Runners;
using OneLlm.Telemetry;

namespace OneLlm.Deciders;

/// <summary>One call an engine made while answering a decision: a Jev or Decisions request, or one chat call for one question.</summary>
public sealed class DecisionCall
{
    /// <summary>Which engine made the call: <c>jev</c>, <c>decisions</c> or <c>llm</c>.</summary>
    public string Engine { get; init; } = "";

    /// <summary>For a chat engine: the question the call answered (its wire id). Null for decisions engines, whose requests carry many.</summary>
    public string? Question { get; init; }

    /// <summary>For a split decisions run: the 1-based request number (planned requests first, then halves made after an overflow). 0 when the run was not split.</summary>
    public int Chunk { get; init; }

    /// <summary>For a decisions engine: how many questions the request carried.</summary>
    public int Questions { get; init; }

    /// <summary>For a chat engine with <c>samples</c>: which sample, from 1. 0 otherwise.</summary>
    public int Sample { get; init; }

    /// <summary>The attempt for this question and sample, from 1; a chat call is repeated when its answer is malformed.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The model that answered.</summary>
    public string Model { get; init; } = "";

    /// <summary><c>ok</c>, <c>invalid_output</c> (a chat answer that did not fit the required form and was asked again), <c>too_large</c> (a request that did not fit and was split) or <c>error</c>.</summary>
    public string Status { get; init; } = "ok";

    /// <summary>What went wrong, for a call that was not <c>ok</c>.</summary>
    public string? Error { get; init; }

    /// <summary>How long the call took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>What the call used and cost, or null for a call that failed before usage.</summary>
    public Usage? Usage { get; init; }
}

/// <summary>What an engine returns for a decision: the answers by wire question id, in the common answer shape, and what it took.</summary>
internal sealed record EngineOutput(JsonObject Answers, string Model, IReadOnlyList<DecisionCall> Calls, IReadOnlyList<ResponseWarning> Warnings);

/// <summary>
/// Accounting for the engine calls of one decision run: spend limits before a call, pricing, telemetry and an inference record
/// after it. Used by the decisions engines; the chat engine goes through <see cref="InferenceCall"/> like any chat call.
/// </summary>
internal sealed class DecisionMeter
{
    private readonly Runtime _runtime;
    private readonly RunScope _scope;
    private readonly ResolvedModel _model;
    private readonly IDecisionProvider _provider;
    private readonly object _gate = new();
    private readonly List<DecisionCall> _calls = [];
    private readonly List<ResponseWarning> _warnings = [];

    public DecisionMeter(Runtime runtime, RunScope scope, ResolvedModel model, IDecisionProvider provider)
    {
        _runtime = runtime;
        _scope = scope;
        _model = model;
        _provider = provider;
    }

    public IReadOnlyList<DecisionCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<ResponseWarning> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _warnings];
            }
        }
    }

    /// <summary>Sends one request. Throws what the provider throws; a request that did not fit is recorded as <c>too_large</c>.</summary>
    public async Task<DecisionResponse> SendAsync(DecisionRequest request, int chunk, CancellationToken cancellationToken)
    {
        decimal runtimeReserved = 0m, instanceReserved = 0m;
        SpendGuard runtimeGuard = _runtime.RuntimeGuard;
        SpendGuard instanceGuard = _scope.Guard;
        if (runtimeGuard.SpendCap is not null || instanceGuard.SpendCap is not null || instanceGuard.MaxCostPerCall is not null)
        {
            // Worst case: every byte of state and questions is input, at about three bytes a token. Output is free for decisions engines.
            long bytes = request.State.Length + request.Questions.Sum(static q => (long)q.Json.Length + q.Id.Length + 4);
            long tokens = (bytes + 2) / 3;
            Cost? worst = CostCalculator.Price(_model, new Usage { Model = _model.Name, InputTokens = tokens }, Runtime.PriceContextFor(new ChatRequest()));
            var estimate = new CostEstimate(tokens, Providers.TokenCountTier.Estimate, 0, worst?.Total, []);
            runtimeReserved = runtimeGuard.Reserve(estimate);
            try
            {
                instanceReserved = instanceGuard.Reserve(estimate);
            }
            catch
            {
                runtimeGuard.Release(runtimeReserved);
                throw;
            }
        }

        using System.Diagnostics.Activity? activity = GenAiTelemetry.StartOperation("decide", _provider.Name, _model.Name);
        long started = _runtime.TimeProvider.GetTimestamp();
        DecisionResponse response;
        try
        {
            response = await _provider.DecideAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runtimeGuard.Release(runtimeReserved);
            instanceGuard.Release(instanceReserved);
            TimeSpan failedAfter = _runtime.TimeProvider.GetElapsedTime(started);
            GenAiTelemetry.Fail(activity, "decide", _provider.Name, _model.Name, ex, failedAfter);
            bool tooLarge = ex is ContextOverflowException;
            Add(new DecisionCall
            {
                Engine = EngineName,
                Chunk = chunk,
                Questions = request.Questions.Count,
                Model = _model.Name,
                Status = tooLarge ? "too_large" : "error",
                Error = RecordText.Bound(ex.Message),
                Duration = failedAfter,
            });
            throw;
        }

        DateTimeOffset now = _runtime.TimeProvider.GetUtcNow();
        Usage? usage = response.Usage;
        if (usage is not null)
        {
            var notes = new List<string>();
            Cost? priced = CostCalculator.Price(_model, usage, Runtime.PriceContextFor(new ChatRequest()) with { Today = DateOnly.FromDateTime(now.UtcDateTime) }, notes);
            lock (_gate)
            {
                _warnings.AddRange(notes.Select(static n => new ResponseWarning("pricing", n)));
                if (priced is null)
                {
                    _warnings.Add(new ResponseWarning("pricing", $"The catalog has no price for '{_model.Id}', so this call's cost is not known."));
                }
            }

            if (priced is not null)
            {
                usage = CostCalculator.WithCost(usage, priced);
            }
        }

        decimal actual = usage?.Cost ?? 0m;
        runtimeGuard.Settle(runtimeReserved, actual);
        instanceGuard.Settle(instanceReserved, actual);
        TimeSpan elapsed = _runtime.TimeProvider.GetElapsedTime(started);
        if (usage?.ToCost() is { } decided)
        {
            await _scope.CostAsync(decided, cancellationToken).ConfigureAwait(false);
        }

        GenAiTelemetry.Complete(activity, "decide", _provider.Name, _model.Name, usage, elapsed);
        await _runtime.WriteRecordAsync(
            new InferenceRecord { InstanceId = _scope.Instance.InstanceId, Time = now, Provider = _provider.Name, Operation = "decide", Usage = usage, Duration = elapsed },
            cancellationToken).ConfigureAwait(false);
        Add(new DecisionCall { Engine = EngineName, Chunk = chunk, Questions = request.Questions.Count, Model = response.Model ?? _model.Name, Usage = usage, Duration = elapsed });
        return response with { Usage = usage };
    }

    private string EngineName => _provider.Name == "typesafe" ? "jev" : "decisions";

    private void Add(DecisionCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    public static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
