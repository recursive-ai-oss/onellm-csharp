namespace OneLlm.Providers.Subscriptions;

/// <summary>Transport settings for opt-in subscription providers. Credentials always come from the explicitly supplied directory, never the environment.</summary>
public sealed record SubscriptionOptions
{
    /// <summary>A custom client, for example a mock transport. The caller owns it and must disable redirects. Null uses the shared non-redirecting client.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Overrides the inference URL for testing. HTTPS or loopback HTTP only. Null uses the vendor's consumer endpoint.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Overrides the OAuth refresh URL for testing. HTTPS or loopback HTTP only. Null uses the vendor's OAuth endpoint.</summary>
    public Uri? TokenEndpoint { get; init; }
}
