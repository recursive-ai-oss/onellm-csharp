using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Counting;

/// <summary>
/// Tier 2 of token counting: a tokenizer that runs in the process, built from a vendor's published vocabulary data. The core package defines the
/// interface; an adapter package supplies implementations.
/// </summary>
public interface ILocalTokenizer
{
    /// <summary>Says whether this tokenizer matches the model's own, so its counts can stand in for the provider's.</summary>
    /// <param name="model">The resolved model.</param>
    /// <returns>True when <see cref="CountTokens"/> may be used for the model.</returns>
    bool Supports(ResolvedModel model);

    /// <summary>Counts the tokens of a text, without any message framing.</summary>
    /// <param name="model">The resolved model that <see cref="Supports"/> accepted.</param>
    /// <param name="text">The text.</param>
    /// <returns>The number of tokens.</returns>
    long CountTokens(ResolvedModel model, string text);
}

/// <summary>
/// Counts the input tokens of a request, trying three tiers in order and reporting which one answered: the provider's own count endpoint
/// (<see cref="TokenCountTier.Provider"/>), a local tokenizer (<see cref="TokenCountTier.Tokenizer"/>), then the calibrated estimate
/// (<see cref="TokenCountTier.Estimate"/>).
/// </summary>
/// <remarks>
/// A tier that cannot answer, or fails with a provider error, hands over to the next one; the failure is passed to the <c>log</c> callback.
/// Cancellation is never swallowed. Tier 2 counts every text segment with the tokenizer and adds the same message, tool and file allowances the
/// estimate uses, so it is exact for the text and approximate for the framing.
/// </remarks>
public sealed class TokenCounter
{
    private readonly ILocalTokenizer? _tokenizer;
    private readonly Action<string>? _log;

    /// <summary>Creates a counter.</summary>
    /// <param name="tokenizer">The local tokenizer for tier 2, or null to skip the tier.</param>
    /// <param name="log">Receives a message when a tier is skipped because of a failure.</param>
    public TokenCounter(ILocalTokenizer? tokenizer = null, Action<string>? log = null)
    {
        _tokenizer = tokenizer;
        _log = log;
    }

    /// <summary>Counts the input tokens of a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="model">The resolved model; null skips tier 2 and uses the generic estimate.</param>
    /// <param name="provider">A provider that can count tokens, or null to skip tier 1. A model whose catalog entry says the provider cannot count its tokens (<c>capabilities.tokenCounting: false</c>) also skips it.</param>
    /// <param name="cancellationToken">Cancels the provider call.</param>
    /// <returns>The count and its tier.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<TokenCount> CountAsync(ChatRequest request, ResolvedModel? model = null, ITokenCountingProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (provider is not null && model?.Provider.Capabilities?.TokenCounting != false)
        {
            try
            {
                TokenCount counted = await provider.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
                return new TokenCount(counted.InputTokens, TokenCountTier.Provider);
            }
            catch (ProviderException e)
            {
                _log?.Invoke($"The token count endpoint of {provider.Name} failed ({e.GetType().Name}); using a local count.");
            }
        }

        return Count(request, model);
    }

    /// <summary>Counts without calling a provider: tier 2 when a local tokenizer matches the model, otherwise tier 3.</summary>
    /// <param name="request">The request.</param>
    /// <param name="model">The resolved model, or null.</param>
    /// <returns>The count and its tier.</returns>
    public TokenCount Count(ChatRequest request, ResolvedModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_tokenizer is not null && model is not null && _tokenizer.Supports(model))
        {
            try
            {
                RequestContent content = RequestContent.Collect(request);
                long tokens = 0;
                foreach (string text in content.Texts)
                {
                    tokens += _tokenizer.CountTokens(model, text);
                }

                return new TokenCount(tokens + TokenEstimator.Overhead(content, model), TokenCountTier.Tokenizer);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _log?.Invoke($"The local tokenizer failed ({e.GetType().Name}); using the estimate.");
            }
        }

        return TokenEstimator.Estimate(request, model);
    }
}

/// <summary>How full a model's context window is.</summary>
public static class ContextUsage
{
    /// <summary>
    /// The share of the context window the last turn's input used: all input tokens of that turn (uncached, cache writes and cache reads, which all
    /// sit in the window) divided by the model's <c>contextWindow</c>.
    /// </summary>
    /// <param name="lastTurn">The usage of the last inference call.</param>
    /// <param name="model">The model entry.</param>
    /// <returns>A fraction, 0 for an empty window and 1 for a full one (it can exceed 1 for an overflowing request); null when the catalog has no context window.</returns>
    public static decimal? Fraction(Usage lastTurn, ModelSpec model)
    {
        ArgumentNullException.ThrowIfNull(lastTurn);
        ArgumentNullException.ThrowIfNull(model);
        return model.ContextWindow is > 0 ? (decimal)lastTurn.TotalInputTokens / model.ContextWindow.Value : null;
    }
}
