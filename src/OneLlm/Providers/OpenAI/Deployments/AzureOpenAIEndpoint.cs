using System.Net.Http.Headers;
using OneLlm.Auth;
using OneLlm.Catalog;
using OneLlm.Http;

namespace OneLlm.Providers.OpenAI;

/// <summary>Settings of an <see cref="AzureOpenAIEndpoint"/>.</summary>
public sealed record AzureOpenAIOptions
{
    /// <summary>
    /// The <c>api-version</c> query value. Null (the default) uses the v1 API at <c>/openai/v1/</c>, which takes no api-version and names the
    /// deployment in the request's <c>model</c> member. A dated value (such as <c>2024-10-21</c>) uses the older deployment-path form,
    /// <c>/openai/deployments/{deployment}/chat/completions?api-version=...</c>.
    /// </summary>
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Deployment names by model name, for the deployment-path form: a request for <c>gpt-4.1</c> goes to the deployment this maps it to. A model
    /// that is not listed is used as the deployment name. Ignored by the v1 form, where the <c>model</c> member is already the deployment name.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Deployments { get; init; }
}

/// <summary>
/// Azure OpenAI and Microsoft Foundry as an endpoint of the OpenAI wire: a resource address plus an <c>api-key</c> header or a Microsoft Entra
/// ID bearer token, over the same Responses and Chat Completions codecs as OpenAI itself. Both documented address forms are supported
/// (<see cref="AzureOpenAIOptions.ApiVersion"/>). No Azure SDK is involved: Entra tokens come from a delegate you supply (for example one
/// that calls your credential library, with the scope <c>https://cognitiveservices.azure.com/.default</c>).
/// </summary>
public sealed class AzureOpenAIEndpoint : OpenAIEndpoint
{
    private readonly Uri _resource;
    private readonly AzureOpenAIOptions _options;
    private readonly Secret? _apiKey;
    private readonly AccessTokenCache? _tokens;

    /// <summary>Creates an endpoint that authenticates with the resource's API key, sent in the <c>api-key</c> header.</summary>
    /// <param name="resource">The resource address, such as <c>https://myresource.openai.azure.com</c> or <c>https://myresource.services.ai.azure.com</c>. https, or http for loopback test servers.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="options">Settings; the defaults (v1 API) when null.</param>
    /// <exception cref="ArgumentException">The address is not https, or the key is empty.</exception>
    public AzureOpenAIEndpoint(Uri resource, Secret apiKey, AzureOpenAIOptions? options = null)
        : this(resource, options)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (apiKey.IsEmpty)
        {
            throw new ArgumentException("The Azure OpenAI API key is empty.", nameof(apiKey));
        }

        _apiKey = apiKey;
    }

    /// <summary>Creates an endpoint that authenticates with Microsoft Entra ID tokens, sent as <c>Authorization: Bearer</c>.</summary>
    /// <param name="resource">The resource address.</param>
    /// <param name="tokenSource">Fetches a token and its expiry; called again shortly before the token expires.</param>
    /// <param name="time">The clock that decides when a token is stale. Required.</param>
    /// <param name="options">Settings; the defaults (v1 API) when null.</param>
    /// <exception cref="ArgumentException">The address is not https.</exception>
    public AzureOpenAIEndpoint(Uri resource, Func<CancellationToken, ValueTask<AccessToken>> tokenSource, TimeProvider time, AzureOpenAIOptions? options = null)
        : this(resource, options)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        _tokens = new AccessTokenCache(tokenSource, time);
    }

    private AzureOpenAIEndpoint(Uri resource, AzureOpenAIOptions? options)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Scheme != Uri.UriSchemeHttps && !(resource.Scheme == Uri.UriSchemeHttp && resource.IsLoopback))
        {
            throw new ArgumentException("The Azure OpenAI address must be https (http is allowed only for loopback addresses).", nameof(resource));
        }

        _resource = new Uri(resource.GetLeftPart(UriPartial.Authority) + "/");
        _options = options ?? new AzureOpenAIOptions();
    }

    /// <inheritdoc />
    public override string Deployment => "azure";

    /// <inheritdoc />
    public override IEnumerable<Secret> Secrets => _apiKey is not null ? [_apiKey] : _tokens!.Secrets;

    /// <inheritdoc />
    public override bool IsLoopback => _resource.IsLoopback;

    /// <inheritdoc />
    public override Uri GetUri(string path, string? model)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_options.ApiVersion is not { Length: > 0 } version)
        {
            // v1 API: {resource}/openai/v1/{path}; no api-version (Azure OpenAI API version lifecycle guide).
            return new Uri(_resource, "openai/v1/" + path);
        }

        string query = Uri.EscapeDataString(version);
        int q = path.IndexOf('?', StringComparison.Ordinal);
        string route = q < 0 ? path : path[..q];
        string existing = q < 0 ? "" : path[(q + 1)..] + "&";

        // Routes that act on a model sit under the deployment; files, batches and the Responses API sit directly under /openai.
        bool underDeployment = model is not null && route is "chat/completions" or "completions" or "embeddings";
        if (underDeployment && model is not null)
        {
            string deployment = _options.Deployments is { } map && map.TryGetValue(model, out string? mapped) ? mapped : model;
            return new Uri(_resource, $"openai/deployments/{Uri.EscapeDataString(deployment)}/{route}?{existing}api-version={query}");
        }

        return new Uri(_resource, $"openai/{route}?{existing}api-version={query}");
    }

    /// <inheritdoc />
    /// <remarks>The key is sent as <c>api-key</c>; a token as a bearer. A token that has expired mid-retry fails with <see cref="AuthException"/>.</remarks>
    public override void Authenticate(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("api-key", _apiKey.Value);
            return;
        }

        Secret token = _tokens!.Current ?? throw new AuthException("openai", "the Microsoft Entra token expired while the request was being retried.", null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }

    /// <inheritdoc />
    public override ValueTask PrepareAsync(CancellationToken cancellationToken) =>
        _tokens is null ? ValueTask.CompletedTask : DiscardAsync(_tokens.GetAsync(cancellationToken));

    private static async ValueTask DiscardAsync(ValueTask<Secret> pending) => _ = await pending.ConfigureAwait(false);

    /// <summary>
    /// A catalog layer that lets deployment names stand for models: each deployment becomes an alias of its model, so a request that names
    /// the deployment (as the v1 API requires) is priced and adjusted as the model it serves.
    /// </summary>
    /// <param name="deploymentToModel">Model names by deployment name, for example <c>my-gpt-deployment</c> to <c>gpt-4.1</c>.</param>
    /// <returns>A user-level layer to add to the catalog.</returns>
    public static CatalogLayer CatalogAliases(IReadOnlyDictionary<string, string> deploymentToModel)
    {
        ArgumentNullException.ThrowIfNull(deploymentToModel);
        var yaml = new System.Text.StringBuilder("name: openai\naliases:\n");
        foreach ((string deployment, string model) in deploymentToModel)
        {
            // Quoted scalars, so a deployment name never changes the document's structure.
            yaml.Append("  \"").Append(Escape(deployment)).Append("\": \"").Append(Escape(model)).Append("\"\n");
        }

        return CatalogLayer.FromYaml(CatalogLayer.User, yaml.ToString(), "azure deployments");
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
