using System.Diagnostics;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Messages;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Telemetry;

namespace OneLlm.Runners;

/// <summary>
/// One model call inside a run: checks the request against the model's catalog entry and the spend caps, sends it, then prices the
/// answer, stamps the message and reports to telemetry and the record sinks. Streaming and non-streaming calls share it.
/// </summary>
internal sealed class InferenceCall
{
    private readonly Runtime _runtime;
    private readonly RunScope _scope;
    private readonly ResolvedModel _model;
    private readonly IChatProvider _provider;
    private readonly Activity? _activity;
    private readonly long _started;
    private readonly decimal _runtimeReserved;
    private readonly decimal _instanceReserved;
    private readonly List<ResponseWarning> _warnings;

    private InferenceCall(Runtime runtime, RunScope scope, ResolvedModel model, IChatProvider provider, ChatRequest request, List<ResponseWarning> warnings, decimal runtimeReserved, decimal instanceReserved)
    {
        _runtime = runtime;
        _scope = scope;
        _model = model;
        _provider = provider;
        Request = request;
        _warnings = warnings;
        _runtimeReserved = runtimeReserved;
        _instanceReserved = instanceReserved;
        _activity = GenAiTelemetry.StartOperation("chat", provider.Name, model.Name);
        _started = runtime.TimeProvider.GetTimestamp();
    }

    /// <summary>The request after the catalog adjusted it for the model.</summary>
    public ChatRequest Request { get; }

    /// <summary>The provider to send <see cref="Request"/> to.</summary>
    public IChatProvider Provider => _provider;

    /// <summary>
    /// Adjusts the request and reserves its worst-case cost. Throws <see cref="BudgetExceededException"/> before anything is sent
    /// when a cap would be passed.
    /// </summary>
    public static InferenceCall Begin(Runtime runtime, RunScope scope, ResolvedModel model, IChatProvider provider, ChatRequest request)
    {
        AdjustedRequest adjusted = RequestAdjuster.Adjust(model, request with { Model = model.Name });
        var warnings = new List<ResponseWarning>(adjusted.Warnings);
        decimal runtimeReserved = 0m;
        decimal instanceReserved = 0m;
        SpendGuard runtimeGuard = runtime.RuntimeGuard;
        SpendGuard instanceGuard = scope.Guard;
        if (runtimeGuard.SpendCap is not null || instanceGuard.SpendCap is not null || instanceGuard.MaxCostPerCall is not null)
        {
            // The local count is enough for a bound: the estimate uses the model's maximum output, which dominates.
            TokenCount count = runtime.TokenCounter.Count(adjusted.Request, model);
            CostEstimate estimate = CostEstimator.Estimate(model, adjusted.Request, count, Runtime.PriceContextFor(adjusted.Request));
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

        return new InferenceCall(runtime, scope, model, provider, adjusted.Request, warnings, runtimeReserved, instanceReserved);
    }

    /// <summary>Prices the answer, stamps it and reports it. Returns the stamped message and the cost.</summary>
    public async ValueTask<(Message Message, Cost Cost, FinishReason Finish, IReadOnlyList<ResponseWarning> Warnings)> CompleteAsync(
        Message message, FinishReason finish, string? finishRaw, string? responseId, CancellationToken cancellationToken, IReadOnlyList<ResponseWarning>? providerWarnings = null)
    {
        if (providerWarnings is { Count: > 0 })
        {
            _warnings.AddRange(providerWarnings);
        }

        DateTimeOffset now = _runtime.TimeProvider.GetUtcNow();
        Usage? usage = message.Usage;
        Cost cost = Cost.Zero;
        if (usage is not null)
        {
            var priceNotes = new List<string>();
            PricingContext context = Runtime.PriceContextFor(Request) with { Today = DateOnly.FromDateTime(now.UtcDateTime) };
            Cost? priced = CostCalculator.Price(_model, usage, context, priceNotes);
            foreach (string note in priceNotes)
            {
                _warnings.Add(new ResponseWarning("pricing", note));
            }

            if (priced is null)
            {
                _warnings.Add(new ResponseWarning("pricing", $"The catalog has no price for '{_model.Id}', so this call's cost is not known."));
            }
            else
            {
                cost = priced;
                usage = CostCalculator.WithCost(usage, priced);
            }
        }

        _runtime.RuntimeGuard.Settle(_runtimeReserved, cost.Total);
        _scope.Guard.Settle(_instanceReserved, cost.Total);

        TimeSpan elapsed = _runtime.TimeProvider.GetElapsedTime(_started);
        GenAiTelemetry.Complete(_activity, "chat", _provider.Name, _model.Name, usage, elapsed, finishRaw ?? finish.ToString(), responseId);
        _activity?.Dispose();

        Message stamped = new()
        {
            Id = _runtime.NewId("m"),
            User = message.User,
            Assistant = message.Assistant,
            ToolCalls = message.ToolCalls,
            DateTime = now,
            Source = message.Source,
            Cache = message.Cache,
            Usage = usage,
            When = message.When,
            ExtensionData = message.ExtensionData,
        };

        await _runtime.WriteRecordAsync(
            new Records.InferenceRecord
            {
                InstanceId = _scope.Instance.InstanceId,
                Time = now,
                MessageId = stamped.Id,
                Provider = _provider.Name,
                Usage = usage,
                Duration = elapsed,
                FinishReason = finishRaw ?? finish.ToString(),
            },
            cancellationToken).ConfigureAwait(false);
        return (stamped, cost, finish, _warnings);
    }

    /// <summary>Releases the reservation and reports the failure.</summary>
    public void Fail(Exception error)
    {
        _runtime.RuntimeGuard.Release(_runtimeReserved);
        _scope.Guard.Release(_instanceReserved);
        GenAiTelemetry.Fail(_activity, "chat", _provider.Name, _model.Name, error, _runtime.TimeProvider.GetElapsedTime(_started));
        _activity?.Dispose();
    }
}
