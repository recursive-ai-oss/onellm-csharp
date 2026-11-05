namespace OneLlm.Http;

/// <summary>The process-wide <see cref="HttpClient"/> that provider clients share unless they are given their own.</summary>
public static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> s_instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// One client over one <see cref="SocketsHttpHandler"/> so connections are pooled across providers. Redirects are never followed:
    /// a redirect would carry the API key header to another host. Connections are recycled every 5 minutes so DNS changes are picked up.
    /// Its own <see cref="HttpClient.Timeout"/> is infinite; time limits are applied per request by <see cref="ProviderHttpClient"/>.
    /// </summary>
    public static HttpClient Instance => s_instance.Value;

    private static HttpClient Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
