using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Access;
using OneLlm.Messages;

namespace OneLlm.Tools.Builtins;

/// <summary>One web result.</summary>
/// <param name="Title">The page title.</param>
/// <param name="Url">The page URL.</param>
/// <param name="Description">A snippet.</param>
/// <param name="Age">How old the page is, as the service states it, or null.</param>
/// <param name="ExtraSnippets">More snippets from the page.</param>
public sealed record SearchResult(string Title, string Url, string Description, string? Age = null, IReadOnlyList<string>? ExtraSnippets = null);

/// <summary>One video result.</summary>
/// <param name="Title">The video title.</param>
/// <param name="Url">The video URL.</param>
/// <param name="Description">A snippet.</param>
public sealed record SearchVideo(string Title, string Url, string Description);

/// <summary>What a search returned.</summary>
/// <param name="Query">The query as the service understood it.</param>
/// <param name="Results">Web results, best first.</param>
/// <param name="Videos">Video results, possibly none.</param>
public sealed record SearchResponse(string Query, IReadOnlyList<SearchResult> Results, IReadOnlyList<SearchVideo> Videos);

/// <summary>What one search costs. The price is data: change it when the service changes its price list.</summary>
/// <param name="PricePerQuery">USD per query.</param>
/// <param name="Source">The URL the price was read from.</param>
/// <param name="AsOf">The date the price was checked, <c>yyyy-MM-dd</c>.</param>
/// <param name="Label">A short description for the cost line.</param>
public sealed record SearchPricing(decimal PricePerQuery, string Source, string AsOf, string Label)
{
    /// <summary>The cost line for one query, in the counter form the catalog uses for per-call prices.</summary>
    /// <returns>A line with counter <c>toolCalls.search_web</c>, quantity 1.</returns>
    public CostLine ToCostLine() => CostLine.Create("toolCalls.search_web", 1m, 1m, PricePerQuery, 1m, Label, Source, AsOf);
}

/// <summary>A web search service the <c>search_web</c> tool can use.</summary>
public interface ISearchProvider
{
    /// <summary>A short name for messages, for example <c>Brave</c>.</summary>
    string Name { get; }

    /// <summary>The address the provider contacts. The tool asks the grants whether it may be reached before searching.</summary>
    Uri Endpoint { get; }

    /// <summary>The price of one query, or null when the provider is free.</summary>
    SearchPricing? Pricing { get; }

    /// <summary>Runs a search.</summary>
    /// <param name="query">The query.</param>
    /// <param name="count">How many web results to ask for, 1 to 20.</param>
    /// <param name="context">The call's context; use its capabilities and clock.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results.</returns>
    Task<SearchResponse> SearchAsync(string query, int count, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Brave Search (the web search API). The key is held as a <see cref="Secret"/> and sent as the <c>X-Subscription-Token</c> header; a 429 is
/// retried up to three times, waiting as long as <c>Retry-After</c> says (at most 30 seconds).
/// </summary>
public sealed class BraveSearchProvider : ISearchProvider
{
    /// <summary>The environment variable the key is read from by <see cref="FromEnvironment"/>.</summary>
    public const string KeyVariable = "BRAVE_API_KEY";

    /// <summary>The list price per query on the Search plan, as published on the vendor's pricing page.</summary>
    public static SearchPricing DefaultPricing { get; } = new(0.005m, "https://api-dashboard.search.brave.com/documentation/pricing", "2026-11-05", "Brave Search query");

    private readonly Secret _key;

    /// <summary>Creates the provider.</summary>
    /// <param name="apiKey">The subscription token.</param>
    /// <param name="endpoint">The web search endpoint. Default <c>https://api.search.brave.com/res/v1/web/search</c>.</param>
    /// <param name="pricing">The price per query. Default <see cref="DefaultPricing"/>; pass a different value after a price change.</param>
    public BraveSearchProvider(Secret apiKey, Uri? endpoint = null, SearchPricing? pricing = null)
    {
        _key = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        Endpoint = endpoint ?? new Uri("https://api.search.brave.com/res/v1/web/search");
        Pricing = pricing ?? DefaultPricing;
    }

    /// <inheritdoc />
    public string Name => "Brave";

    /// <inheritdoc />
    public Uri Endpoint { get; }

    /// <inheritdoc />
    public SearchPricing? Pricing { get; }

    /// <summary>Makes a provider from the <c>BRAVE_API_KEY</c> environment variable.</summary>
    /// <param name="variable">The variable to read.</param>
    /// <returns>The provider, or null when the variable is not set.</returns>
    public static BraveSearchProvider? FromEnvironment(string variable = KeyVariable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : new BraveSearchProvider(new Secret(value));
    }

    /// <inheritdoc />
    public async Task<SearchResponse> SearchAsync(string query, int count, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var uri = new Uri(Endpoint + (string.IsNullOrEmpty(Endpoint.Query) ? "?" : "&") + "q=" + Uri.EscapeDataString(query) + "&count=" + count.ToString(CultureInfo.InvariantCulture));
        List<KeyValuePair<string, string>> headers = [new("Accept", "application/json"), new("X-Subscription-Token", _key.Value)];
        for (int attempt = 0; ; attempt++)
        {
            using HttpResponseMessage response = await EgressClient.SendAsync(context.Capabilities, HttpMethod.Get, uri, headers, null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, context.TimeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            (byte[] bytes, _) = await EgressClient.ReadBodyAsync(response, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(bytes);
            if (!response.IsSuccessStatusCode)
            {
                string shown = _key.ScrubAll(text);
                throw new HttpRequestException($"Brave API error (status {(int)response.StatusCode}): {(shown.Length > 500 ? shown[..500] + "..." : shown)}");
            }

            return Parse(text, query);
        }
    }

    internal static SearchResponse Parse(string json, string fallbackQuery)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string query = root.TryGetProperty("query", out JsonElement q) && q.TryGetProperty("original", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : fallbackQuery;
        var results = new List<SearchResult>();
        if (root.TryGetProperty("web", out JsonElement web) && web.TryGetProperty("results", out JsonElement wr) && wr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in wr.EnumerateArray())
            {
                List<string> extra = r.TryGetProperty("extra_snippets", out JsonElement es) && es.ValueKind == JsonValueKind.Array
                    ? [.. es.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                    : [];
                results.Add(new SearchResult(Str(r, "title"), Str(r, "url"), Str(r, "description"), Str(r, "age") is { Length: > 0 } age ? age : null, extra));
            }
        }

        var videos = new List<SearchVideo>();
        if (root.TryGetProperty("videos", out JsonElement vids) && vids.TryGetProperty("results", out JsonElement vr) && vr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement v in vr.EnumerateArray())
            {
                videos.Add(new SearchVideo(Str(v, "title"), Str(v, "url"), Str(v, "description")));
            }
        }

        return new SearchResponse(query, results, videos);
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

/// <summary>search_web: searches the web through the configured <see cref="ISearchProvider"/> and prints numbered results. Each query adds its price as a cost line.</summary>
internal sealed class SearchWebTool : BuiltinTool
{
    private readonly ISearchProvider _provider;

    public SearchWebTool(ISearchProvider provider)
        : base(Spec("search_web", BuiltinSchemas.SearchWebDescription, BuiltinSchemas.SearchWebSchema, TimeSpan.FromSeconds(60)), ToolCapability.Network)
    {
        _provider = provider;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonObject inputs, ToolContext context, CancellationToken cancellationToken)
    {
        string query = Args.RequiredString(inputs, "query");
        int count = Args.OptionalInt(inputs, "count") ?? 10;
        count = count <= 0 ? 10 : Math.Min(count, 20);

        // The destination is the host's choice, but it is still network egress from a tool, so it needs the same grant.
        EgressClient.Require(context.Capabilities, _provider.Endpoint);
        SearchResponse response = await _provider.SearchAsync(query, count, context, cancellationToken).ConfigureAwait(false);
        if (_provider.Pricing is SearchPricing pricing)
        {
            context.AddCost(pricing.ToCostLine());
        }

        return ToolResult.FromText(Format(response));
    }

    internal static string Format(SearchResponse r)
    {
        var sb = new StringBuilder();
        sb.Append("Web Search Results for: ").Append(r.Query).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"Found {r.Results.Count} results\n\n");
        for (int i = 0; i < r.Results.Count; i++)
        {
            SearchResult x = r.Results[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. {x.Title}\n   URL: {x.Url}\n");
            if (!string.IsNullOrEmpty(x.Age))
            {
                sb.Append("   Age: ").Append(x.Age).Append('\n');
            }

            sb.Append("   ").Append(x.Description).Append('\n');
            foreach (string snippet in x.ExtraSnippets ?? [])
            {
                sb.Append("   - ").Append(snippet).Append('\n');
            }

            sb.Append('\n');
        }

        if (r.Videos.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nVideo Results: {r.Videos.Count}\n\n");
            for (int i = 0; i < r.Videos.Count; i++)
            {
                SearchVideo v = r.Videos[i];
                sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. {v.Title}\n   URL: {v.Url}\n   {v.Description}\n\n");
            }
        }

        return sb.ToString();
    }
}
