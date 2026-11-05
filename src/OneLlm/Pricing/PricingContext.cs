namespace OneLlm.Pricing;

/// <summary>
/// Facts about a call that change its price but are not token counts: the service tier, whether it ran as a batch, the region, and
/// qualifiers such as an image's size and quality.
/// </summary>
public sealed record PricingContext
{
    /// <summary>A context with nothing set: standard tier, not a batch, no region, no qualifiers.</summary>
    public static PricingContext Default { get; } = new();

    /// <summary>
    /// The service tier the call ran on. When null, the usage's own <see cref="Messages.Usage.ServiceTier"/> is used. <c>standard</c>,
    /// <c>default</c> and <c>auto</c> (any case) are the standard tier and carry no factor; any other name needs a <c>serviceTiers</c> entry
    /// on the model, otherwise the call is priced at the standard rate and a warning says so.
    /// </summary>
    public string? ServiceTier { get; init; }

    /// <summary>The call ran through the provider's batch API. Applies the pricing multiplier named <c>batch</c>.</summary>
    public bool Batch { get; init; }

    /// <summary>The region or residency the call ran in, for example <c>eu</c>. Applies the multiplier named <c>region:eu</c> when the model has one.</summary>
    public string? Region { get; init; }

    /// <summary>Other named multipliers to apply (a name from the model's <c>multipliers</c>); a name the model lacks gives a warning.</summary>
    public IReadOnlyList<string>? Multipliers { get; init; }

    /// <summary>
    /// Qualifiers of the call, such as <c>size</c> = <c>1024x1024</c> and <c>quality</c> = <c>high</c>. A price item with a <c>when</c> applies only
    /// when every key in it is present here with the same value (ignoring case); of several items for one counter the one naming the most keys wins.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Qualifiers { get; init; }

    /// <summary>Today's date, from the runtime's clock. When set, prices older than 180 days past their <c>asOf</c> add a warning.</summary>
    public DateOnly? Today { get; init; }

    /// <summary>
    /// The server address the call went to. A loopback address with no price on the model prices the call at zero. The address of the resolved
    /// preset is used when this is null.
    /// </summary>
    public Uri? BaseUrl { get; init; }
}
