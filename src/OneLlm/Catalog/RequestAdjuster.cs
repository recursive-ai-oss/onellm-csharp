using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Catalog;

/// <summary>
/// A request asks for something the model's catalog entry says it cannot do, and dropping the parameter would change what the caller asked for
/// (a forced tool on a model without tools, a JSON schema on a model without structured output, an image for a text-only model).
/// </summary>
public sealed class UnsupportedParameterException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="model">The model's id, <c>provider:name</c>.</param>
    /// <param name="parameter">The request parameter or input that is not supported.</param>
    /// <param name="reason">Why, in a sentence.</param>
    public UnsupportedParameterException(string model, string parameter, string reason)
        : base($"{model} does not support {parameter}: {reason}")
    {
        Model = model;
        Parameter = parameter;
        Reason = reason;
    }

    /// <summary>The model's id, <c>provider:name</c>.</summary>
    public string Model { get; }

    /// <summary>The request parameter or input that is not supported.</summary>
    public string Parameter { get; }

    /// <summary>Why it is not supported.</summary>
    public string Reason { get; }
}

/// <summary>A request after the adjuster ran, with what it changed.</summary>
/// <param name="Request">The request to send. The same object as the input when nothing changed.</param>
/// <param name="Warnings">A notice for every change, in the order made. Empty when nothing changed. Providers put these on <see cref="ChatResponse.Warnings"/>.</param>
public sealed record AdjustedRequest(ChatRequest Request, IReadOnlyList<ResponseWarning> Warnings);

/// <summary>
/// Reconciles a <see cref="ChatRequest"/> with what the model's catalog entry says it supports. Parameters whose absence changes nothing the
/// caller asked for are stripped (sampling parameters on a model that takes none, reasoning settings on a model that does not reason, caching
/// on a model without it, an output limit above the model's maximum is clamped). Anything that would change the meaning of the request is refused
/// with <see cref="UnsupportedParameterException"/> (nothing is sent): tools, a JSON schema, an input kind the model cannot read, a reasoning effort or service
/// tier it does not have. Every change is returned as a warning to put on the response.
/// </summary>
/// <remarks>
/// Only what the catalog states counts: a field left out (null) means unknown and never strips or refuses. A model with no catalog entry
/// (matched by pattern only) is not adjusted. Provider clients call this once, before building the wire request.
/// </remarks>
public static class RequestAdjuster
{
    /// <summary>Adjusts a request for a resolved model.</summary>
    /// <param name="resolved">The model, from <see cref="ModelCatalog.Resolve"/>.</param>
    /// <param name="request">The request.</param>
    /// <returns>The request to send and the warnings for what was changed.</returns>
    /// <exception cref="UnsupportedParameterException">The request needs something the model does not support.</exception>
    public static AdjustedRequest Adjust(ResolvedModel resolved, ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<ResponseWarning>();
        void Note(string code, string parameter, string reason) =>
            warnings.Add(new ResponseWarning(code, $"{parameter}: {reason}", parameter));

        Exception Refuse(string parameter, string reason) => new UnsupportedParameterException(resolved.Id, parameter, reason);

        ModelSpec? model = resolved.Model;
        if (model is null)
        {
            return new AdjustedRequest(request, warnings);
        }

        ProviderCapabilitiesSpec? provider = resolved.Provider.Capabilities;
        ProviderCapabilitiesSpec? preset = resolved.Preset?.Capabilities;
        bool? Capability(bool? onModel, Func<ProviderCapabilitiesSpec, bool?> pick) =>
            onModel ?? (preset is null ? null : pick(preset)) ?? (provider is null ? null : pick(provider));

        if (Capability(null, c => c.SystemOne) == true)
        {
            throw Refuse("chat", "the provider is a decisions engine, not a chat provider (capabilities.systemOne)");
        }

        if (model.Kind is not null && !string.Equals(model.Kind, "chat", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse("chat", $"the model is a '{model.Kind}' model (kind)");
        }

        ChatRequest result = request;

        // ---- refusals: removing these would change what was asked ----
        if ((request.Tools.Count > 0 || request.ToolChoice is { Mode: ToolChoiceMode.Required or ToolChoiceMode.Specific })
            && Capability(model.SupportsTools, c => c.Tools) == false)
        {
            throw Refuse("tools", "the model does not support tool calling (supportsTools)");
        }

        if (request.ResponseFormat is { Kind: ResponseFormatKind.JsonSchema } && Capability(model.SupportsStructuredOutput, c => c.StructuredOutput) == false)
        {
            throw Refuse("responseFormat", "the model does not support structured output against a schema (supportsStructuredOutput)");
        }

        CheckInputs(request, model, Capability, Refuse);

        if (request.Reasoning is { Effort: { } effort } && model.Thinking?.Efforts is { Count: > 0 } efforts
            && !efforts.Exists(e => string.Equals(e, effort, StringComparison.OrdinalIgnoreCase)))
        {
            throw Refuse("reasoning.effort", $"'{effort}' is not one of the model's efforts: {string.Join(", ", efforts)} (thinking.efforts)");
        }

        if (request.ServiceTier is { } tier && !IsStandardTier(tier)
            && (model.ServiceTiers is null || !model.ServiceTiers.Keys.Any(k => string.Equals(k, tier, StringComparison.OrdinalIgnoreCase))))
        {
            throw Refuse("serviceTier", $"'{tier}' is not a service tier of the model (serviceTiers)");
        }

        // ---- strips and clamps: harmless to drop ----
        if (model.SupportsTemperature == false && (request.Temperature is not null || request.TopP is not null))
        {
            result = result with { Temperature = null, TopP = null };
            Note("parameter_stripped", "temperature/topP", "the model takes no sampling parameters (supportsTemperature: false)");
        }

        if (request.Reasoning is { } reasoning)
        {
            if (model.Thinking is null)
            {
                result = result with { Reasoning = null };
                Note("parameter_stripped", "reasoning", "the model has no thinking settings (thinking)");
            }
            else if (reasoning.BudgetTokens is { } budget)
            {
                if (model.Thinking.AcceptsBudget != true)
                {
                    result = result with { Reasoning = reasoning with { BudgetTokens = null } };
                    Note("parameter_stripped", "reasoning.budgetTokens", "the model takes no thinking budget (thinking.acceptsBudget)");
                }
                else
                {
                    int clamped = budget;
                    if (model.Thinking.MinBudget is { } min && clamped < min)
                    {
                        clamped = min;
                    }

                    if (model.Thinking.MaxBudget is { } max && clamped > max)
                    {
                        clamped = max;
                    }

                    if (clamped != budget)
                    {
                        result = result with { Reasoning = reasoning with { BudgetTokens = clamped } };
                        Note("parameter_clamped", "reasoning.budgetTokens", $"{budget} is outside the model's range; using {clamped} (thinking.minBudget/maxBudget)");
                    }
                }
            }
        }

        if (request.Cache is { Enabled: true } && Capability(model.SupportsCaching, c => c.PromptCaching) == false)
        {
            result = result with { Cache = CachePolicy.Off };
            Note("parameter_stripped", "cache", "the model does not support prompt caching (supportsCaching)");
        }

        if (request.MaxOutputTokens is { } maxOut && model.MaxOutputTokens is { } limit && maxOut > limit)
        {
            result = result with { MaxOutputTokens = (int)Math.Min(limit, int.MaxValue) };
            Note("parameter_clamped", "maxOutputTokens", $"{maxOut} is above the model's limit of {limit}; using the limit (maxOutputTokens)");
        }

        return new AdjustedRequest(result, warnings);
    }

    private static bool IsStandardTier(string tier) =>
        tier.Trim().ToLowerInvariant() is "" or "standard" or "default" or "auto";

    private static void CheckInputs(
        ChatRequest request,
        ModelSpec model,
        Func<bool?, Func<ProviderCapabilitiesSpec, bool?>, bool?> capability,
        Func<string, string, Exception> refuse)
    {
        bool? vision = capability(model.SupportsVision, c => c.ImageInputs);
        bool? imageResults = capability(null, c => c.ImageToolResults);
        foreach (Message message in request.Messages)
        {
            Check(message.User, vision, model, refuse, false, imageResults);
            Check(message.Assistant, vision, model, refuse, false, imageResults);
            foreach (ToolCall call in message.ToolCalls)
            {
                Check(call.Result, vision, model, refuse, true, imageResults);
            }
        }
    }

    private static void Check(IReadOnlyList<Part> parts, bool? vision, ModelSpec model, Func<string, string, Exception> refuse, bool inToolResult, bool? imageToolResults)
    {
        foreach (Part part in parts)
        {
            if (part is not AssetPart { Asset: { } asset })
            {
                continue;
            }

            string type = asset.ContentType;
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                if (vision == false)
                {
                    throw refuse("image input", "the model does not accept images (supportsVision)");
                }

                if (inToolResult && imageToolResults == false)
                {
                    throw refuse("image in a tool result", "the provider does not accept images in tool results (capabilities.imageToolResults)");
                }
            }
            else if (type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) && model.SupportsPdfInput == false)
            {
                throw refuse("PDF input", "the model does not accept PDF documents (supportsPdfInput)");
            }
            else if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && model.SupportsAudioInput == false)
            {
                throw refuse("audio input", "the model does not accept audio (supportsAudioInput)");
            }
            else if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && model.SupportsVideoInput == false)
            {
                throw refuse("video input", "the model does not accept video (supportsVideoInput)");
            }
        }
    }
}
