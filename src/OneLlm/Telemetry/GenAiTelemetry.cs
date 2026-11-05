using System.Diagnostics;
using System.Diagnostics.Metrics;
using OneLlm.Messages;

namespace OneLlm.Telemetry;

/// <summary>
/// OpenTelemetry output for model calls: an <see cref="ActivitySource"/> for spans and a <see cref="Meter"/> for metrics, named
/// <see cref="SourceName"/>. Attribute and instrument names follow the OpenTelemetry GenAI semantic conventions (see <c>docs/telemetry.md</c>).
/// Nothing is allocated and almost nothing is done when no listener is subscribed.
/// </summary>
public static class GenAiTelemetry
{
    /// <summary>The name of the activity source and the meter. Subscribe to it with an OpenTelemetry SDK or a listener.</summary>
    public const string SourceName = "OneLlm";

    /// <summary>The activity source that spans come from.</summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>The meter that metrics come from.</summary>
    public static Meter Meter { get; } = new(SourceName);

    // gen_ai.client.token.usage: tokens per call, tagged with gen_ai.token.type = input | output.
    private static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", "{token}", "Number of input and output tokens used.");

    // gen_ai.client.operation.duration: seconds per call.
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s", "GenAI operation duration.");

    /// <summary>Starts the span of one model call, named <c>{operation} {model}</c>.</summary>
    /// <param name="operation">The <c>gen_ai.operation.name</c>: <c>chat</c>, <c>embeddings</c>, <c>text_completion</c>, <c>execute_tool</c>, <c>invoke_agent</c>, ...</param>
    /// <param name="provider">The <c>gen_ai.provider.name</c>, for example <c>anthropic</c>, <c>openai</c>, <c>gcp.gemini</c>.</param>
    /// <param name="requestModel">The <c>gen_ai.request.model</c>.</param>
    /// <returns>The started activity, or null when nothing listens (then there is nothing to dispose or tag).</returns>
    public static Activity? StartOperation(string operation, string provider, string requestModel)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        Activity? a = ActivitySource.StartActivity(operation + " " + requestModel, ActivityKind.Client);
        if (a is { IsAllDataRequested: true })
        {
            a.SetTag("gen_ai.operation.name", operation);
            a.SetTag("gen_ai.provider.name", provider);
            a.SetTag("gen_ai.request.model", requestModel);
        }

        return a;
    }

    /// <summary>Tags a finished call's span with the response model, id, finish reason and token counts, then records the metrics.</summary>
    /// <param name="activity">The span from <see cref="StartOperation"/>; may be null.</param>
    /// <param name="operation">The operation name given to <see cref="StartOperation"/>.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="requestModel">The requested model.</param>
    /// <param name="usage">The call's usage, or null when it has none.</param>
    /// <param name="duration">How long the call took.</param>
    /// <param name="finishReason">Why the model stopped, or null.</param>
    /// <param name="responseId">The provider's response id, or null.</param>
    public static void Complete(Activity? activity, string operation, string provider, string requestModel, Usage? usage, TimeSpan duration, string? finishReason = null, string? responseId = null)
    {
        if (activity is { IsAllDataRequested: true })
        {
            if (usage is not null)
            {
                activity.SetTag("gen_ai.response.model", usage.Model);
                activity.SetTag("gen_ai.usage.input_tokens", usage.TotalInputTokens);
                activity.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
                if (usage.CacheReadInputTokens != 0)
                {
                    activity.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CacheReadInputTokens);
                }

                if (usage.CacheCreationInputTokens != 0)
                {
                    activity.SetTag("gen_ai.usage.cache_creation.input_tokens", usage.CacheCreationInputTokens);
                }

                long reasoning = usage.GetCounter("reasoningTokens");
                if (reasoning != 0)
                {
                    activity.SetTag("gen_ai.usage.reasoning.output_tokens", reasoning);
                }
            }

            if (finishReason is not null)
            {
                activity.SetTag("gen_ai.response.finish_reasons", new[] { finishReason });
            }

            if (responseId is not null)
            {
                activity.SetTag("gen_ai.response.id", responseId);
            }
        }

        RecordMetrics(operation, provider, requestModel, usage, duration, errorType: null);
    }

    /// <summary>Marks a span as failed and records the duration metric with an <c>error.type</c>.</summary>
    /// <param name="activity">The span from <see cref="StartOperation"/>; may be null.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="requestModel">The requested model.</param>
    /// <param name="error">The failure. Only its type name and message go to the span; OneLLM's errors never contain secrets.</param>
    /// <param name="duration">How long the call took before failing.</param>
    public static void Fail(Activity? activity, string operation, string provider, string requestModel, Exception error, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(error);
        string type = error.GetType().FullName ?? error.GetType().Name;
        if (activity is not null)
        {
            activity.SetTag("error.type", type);
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
        }

        RecordMetrics(operation, provider, requestModel, null, duration, type);
    }

    /// <summary>Records only the metrics for a call, for callers that keep no span.</summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="requestModel">The requested model.</param>
    /// <param name="usage">The usage, or null.</param>
    /// <param name="duration">How long the call took.</param>
    /// <param name="errorType">The <c>error.type</c> when the call failed, else null.</param>
    public static void RecordMetrics(string operation, string provider, string requestModel, Usage? usage, TimeSpan duration, string? errorType)
    {
        bool tokens = usage is not null && TokenUsage.Enabled;
        bool time = OperationDuration.Enabled;
        if (!tokens && !time)
        {
            return;
        }

        var tags = new TagList
        {
            { "gen_ai.operation.name", operation },
            { "gen_ai.provider.name", provider },
            { "gen_ai.request.model", requestModel },
        };
        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        if (time)
        {
            OperationDuration.Record(duration.TotalSeconds, tags);
        }

        if (tokens)
        {
            var input = tags;
            input.Add("gen_ai.token.type", "input");
            TokenUsage.Record(usage!.TotalInputTokens, input);
            var output = tags;
            output.Add("gen_ai.token.type", "output");
            TokenUsage.Record(usage.OutputTokens, output);
        }
    }
}
