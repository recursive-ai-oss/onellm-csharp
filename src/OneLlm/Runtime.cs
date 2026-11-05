using OneLlm.Caching;
using OneLlm.Catalog;
using OneLlm.Counting;
using OneLlm.Instances;
using OneLlm.Pricing;
using OneLlm.Providers;
using OneLlm.Records;
using OneLlm.Templating;

namespace OneLlm;

/// <summary>
/// The low level: owns the model catalog, the provider registry, the instance store, record sinks, template functions, spend caps and
/// the clock, and runs simple asks, scripted runs and chats as instances. The <see cref="Llm"/> statics are one line over a default
/// runtime; build your own to choose a store, sinks or caps.
/// </summary>
/// <remarks>
/// Set the properties before the first run; they are read without locks afterwards. Running is thread-safe: any number of runs may
/// be in flight, and one instance has one live runner at a time (the store's lease; a second runner gets
/// <see cref="InstanceLockedException"/>).
/// </remarks>
public sealed partial class Runtime
{
    private readonly object _gate = new();
    private ModelCatalog? _catalog;
    private ProviderRegistry? _providers;
    private SpendGuard? _runtimeGuard;
    private IInstanceStore? _store;

    /// <summary>The model catalog. Defaults to the shipped layers plus the files named by <c>ONELLM_CATALOG</c>.</summary>
    public ModelCatalog Catalog
    {
        get
        {
            lock (_gate)
            {
                BuiltInProviders.RegisterCatalogs();
                return _catalog ??= CatalogLoader.Load(getEnvironmentVariable: GetEnvironmentVariable);
            }
        }

        set => _catalog = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The providers by catalog name. Defaults to the built-in providers, created on first use from their environment keys.</summary>
    public ProviderRegistry Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers ??= ProviderRegistry.CreateWithDefaults(GetEnvironmentVariable);
            }
        }

        set => _providers = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Where instances are saved. Defaults to an in-memory store, so a run leaves nothing on disk unless a store is chosen.</summary>
    public IInstanceStore Store
    {
        get
        {
            lock (_gate)
            {
                return _store ??= new MemoryInstanceStore(TimeProvider);
            }
        }

        set => _store = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Append-only audit records (runs, messages, inference calls, errors). Failures in a sink never fail a run.</summary>
    public RecordSinks Sinks { get; } = new();

    /// <summary>The functions templates can call. Defaults to the standard set; add host functions here.</summary>
    public TemplateFunctions Functions { get; set; } = new();

    /// <summary>Where <c>embed</c> in a template looks, after the definition's own directory. Empty by default.</summary>
    public IList<IEmbedSource> EmbedSources { get; } = [];

    /// <summary>
    /// Environment variable names a template may read as <c>.env</c>. Empty by default, so <c>.env</c> is empty: a prompt
    /// cannot read a credential unless the host lists its name.
    /// </summary>
    public ISet<string> AllowedEnvironment { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Extra top-level names for the template scope, such as <c>git</c>. A name here replaces the built-in one.</summary>
    public IDictionary<string, object?> ScopeValues { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Reads an environment variable for API keys and the allow-listed <c>.env</c>. Defaults to the process environment.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; set; } = static name => Environment.GetEnvironmentVariable(name);

    /// <summary>The clock for stamps, timeouts, retries and <c>.time</c>. Defaults to the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Makes the unique part of ids. Defaults to a random GUID; tests set a counter.</summary>
    public Func<string> IdSource { get; set; } = static () => Guid.NewGuid().ToString("N");

    /// <summary>The most this runtime may spend, in USD, over its lifetime. Null for no cap. Checked before each call against its worst case and after against the real cost.</summary>
    public decimal? SpendCap { get; set; }

    /// <summary>The most one instance may spend, in USD, including what earlier runs of it spent. Null for no cap.</summary>
    public decimal? InstanceSpendCap { get; set; }

    /// <summary>The most one call may cost in the worst case, in USD. Null for no limit.</summary>
    public decimal? MaxCostPerCall { get; set; }

    /// <summary>An opt-in cache of whole answers keyed by request. Null (the default) turns it off.</summary>
    public ResultCache? Cache { get; set; }

    /// <summary>Counts tokens for spend-cap estimates. Defaults to local estimates, which never call the network.</summary>
    public TokenCounter TokenCounter { get; set; } = new();

    /// <summary>What this runtime has spent in USD since it was created, over all runs.</summary>
    public decimal Spent => RuntimeGuard.Spent;

    internal SpendGuard RuntimeGuard
    {
        get
        {
            lock (_gate)
            {
                return _runtimeGuard ??= new SpendGuard(null, SpendCap);
            }
        }
    }

    /// <summary>Resolves a model name against the catalog.</summary>
    /// <param name="model">A catalog name, an alias or <c>provider:model</c>.</param>
    /// <returns>The resolved model, with its provider, preset and catalog entry.</returns>
    /// <exception cref="ModelNotFoundException">The catalog does not know the name, or it names more than one provider.</exception>
    public ResolvedModel Resolve(string model)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        return Catalog.Resolve(model);
    }

    /// <summary>Gets the chat provider for a resolved model, with the result cache in front when one is set.</summary>
    /// <param name="model">The resolved model.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="CapabilityMissingException">No provider is registered, or it does not chat.</exception>
    /// <exception cref="AuthException">The API key variable is not set.</exception>
    public IChatProvider GetChatProvider(ResolvedModel model)
    {
        IProvider provider = Providers.Get(model, TimeProvider);
        if (provider is not IChatProvider chat)
        {
            throw new CapabilityMissingException($"Chat on provider '{provider.Name}'", $"a chat provider for '{model.Id}'");
        }

        return Cache is { } cache ? new CachingChatProvider(chat, cache) : chat;
    }

    /// <summary>Copies an instance (up to a message, or all of it) into a new instance and saves it. The copy is <c>stopped</c> and records where it came from.</summary>
    /// <param name="instanceId">The instance to copy.</param>
    /// <param name="upToMessageId">The last message to keep; null keeps every message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new instance.</returns>
    /// <exception cref="KeyNotFoundException">There is no such instance.</exception>
    public async Task<Instance> ForkAsync(string instanceId, string? upToMessageId = null, CancellationToken cancellationToken = default)
    {
        Instance source = await Store.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"There is no instance '{instanceId}'.");
        Instance copy = source.Fork(NewId("i"), upToMessageId);
        await Store.SaveAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }

    /// <summary>Copies a whole instance into a new one. Same as <see cref="ForkAsync"/> with no cut.</summary>
    /// <param name="instanceId">The instance to copy.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new instance.</returns>
    public Task<Instance> DuplicateAsync(string instanceId, CancellationToken cancellationToken = default) => ForkAsync(instanceId, null, cancellationToken);

    internal string NewId(string prefix) => prefix + "-" + IdSource();

    internal static PricingContext PriceContextFor(ChatRequest request) => new() { ServiceTier = request.ServiceTier };

    internal ValueTask WriteRecordAsync(AuditRecord record, CancellationToken cancellationToken) =>
        Sinks.IsEmpty ? ValueTask.CompletedTask : Sinks.WriteAsync(record, cancellationToken);
}
