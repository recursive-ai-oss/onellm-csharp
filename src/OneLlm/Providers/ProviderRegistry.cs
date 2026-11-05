using System.Collections.Concurrent;
using OneLlm.Catalog;

namespace OneLlm.Providers;

/// <summary>What a provider factory is given when the registry first needs its provider.</summary>
/// <param name="Model">The resolved model the provider is needed for: its catalog provider, preset and base URL.</param>
/// <param name="ApiKey">The API key read from the environment variable the catalog names, or null when the catalog names none or the variable is empty.</param>
/// <param name="TimeProvider">The runtime's clock, for retries and timeouts.</param>
public sealed record ProviderFactoryContext(ResolvedModel Model, Secret? ApiKey, TimeProvider TimeProvider);

/// <summary>Creates the provider for a catalog provider or preset.</summary>
/// <param name="context">What the provider needs to be created.</param>
/// <returns>The provider.</returns>
public delegate IProvider ProviderFactory(ProviderFactoryContext context);

/// <summary>
/// Maps catalog provider names to providers. A provider is created lazily, the first time a model of that provider is used, from the
/// API key in the environment variable the catalog names (<c>apiKeyEnvVar</c>), and then reused. Thread-safe.
/// </summary>
/// <remarks>
/// The registry looks a model up by its preset name (for example <c>ollama</c>), then by the preset's protocol, then by the provider
/// name. A provider can also be set directly with <see cref="Set"/>, which is how tests and hosts with their own credentials plug in.
/// </remarks>
public sealed class ProviderRegistry
{
    private static readonly object s_defaultsGate = new();
    private static ImmutableRegistrars s_defaults = new([]);

    private readonly ConcurrentDictionary<string, ProviderFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _getEnvironmentVariable;

    /// <summary>Creates an empty registry.</summary>
    /// <param name="getEnvironmentVariable">Reads environment variables for API keys; null reads the process environment.</param>
    public ProviderRegistry(Func<string, string?>? getEnvironmentVariable = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? (static name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// Adds a function that registers the built-in providers on every registry made by <see cref="CreateWithDefaults"/>. The provider
    /// classes of the core package are added once, in <see cref="BuiltInProviders"/>; a host can add more.
    /// </summary>
    /// <param name="registrar">Registers factories on the registry it is given.</param>
    public static void AddDefaults(Action<ProviderRegistry> registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        lock (s_defaultsGate)
        {
            s_defaults = new ImmutableRegistrars([.. s_defaults.Items, registrar]);
        }
    }

    /// <summary>Creates a registry holding the built-in provider factories.</summary>
    /// <param name="getEnvironmentVariable">Reads environment variables for API keys; null reads the process environment.</param>
    /// <returns>The registry.</returns>
    public static ProviderRegistry CreateWithDefaults(Func<string, string?>? getEnvironmentVariable = null)
    {
        var registry = new ProviderRegistry(getEnvironmentVariable);
        BuiltInProviders.Register(registry);
        ImmutableRegistrars defaults;
        lock (s_defaultsGate)
        {
            defaults = s_defaults;
        }

        foreach (Action<ProviderRegistry> registrar in defaults.Items)
        {
            registrar(registry);
        }

        return registry;
    }

    /// <summary>Registers a factory for a provider name, a preset name or a preset protocol. A later registration of the same name replaces the earlier one.</summary>
    /// <param name="name">The catalog provider name, preset name or preset protocol (case-insensitive).</param>
    /// <param name="factory">Creates the provider on first use.</param>
    public void Register(string name, ProviderFactory factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[name] = factory;
        _providers.TryRemove(name, out _);
    }

    /// <summary>Uses a ready provider for a name instead of a factory, with no API key check.</summary>
    /// <param name="name">The catalog provider name or preset name (case-insensitive).</param>
    /// <param name="provider">The provider.</param>
    public void Set(string name, IProvider provider)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(provider);
        _providers[name] = provider;
    }

    /// <summary>Returns the provider for a resolved model, creating it on first use.</summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="timeProvider">The clock handed to a factory.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="CapabilityMissingException">No provider is registered for the model's provider, preset or protocol.</exception>
    /// <exception cref="AuthException">The catalog names an API key variable that is not set (and the endpoint is not a local server).</exception>
    public IProvider Get(ResolvedModel model, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        string? preset = model.PresetName;
        string? protocol = model.Preset?.Protocol;
        string provider = model.Provider.Name;

        foreach (string? key in new[] { preset, protocol, provider })
        {
            if (key is not null && _providers.TryGetValue(key, out IProvider? ready))
            {
                return ready;
            }
        }

        foreach (string? key in new[] { preset, protocol, provider })
        {
            if (key is not null && _factories.TryGetValue(key, out ProviderFactory? factory))
            {
                string cacheKey = key + "|" + (preset ?? "");
                return _providers.GetOrAdd(cacheKey, _ => factory(new ProviderFactoryContext(model, ReadKey(model), timeProvider)));
            }
        }

        throw new CapabilityMissingException(
            $"A provider for '{model.Id}'",
            $"a provider named '{preset ?? provider}' with ProviderRegistry.Register or ProviderRegistry.Set (the built-in providers register themselves in the core package)");
    }

    private Secret? ReadKey(ResolvedModel model)
    {
        string? variable = model.Preset?.ApiKeyEnvVar ?? model.Provider.ApiKeyEnvVar;
        if (string.IsNullOrEmpty(variable))
        {
            return null;
        }

        string? value = _getEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return new Secret(value.Trim());
        }

        // A server on this machine usually needs no key; every other endpoint does.
        if (model.Preset?.BaseUrl is { } text && Uri.TryCreate(text, UriKind.Absolute, out Uri? address) && address.IsLoopback)
        {
            return null;
        }

        throw new AuthException(model.PresetName ?? model.Provider.Name, $"No API key. Set the environment variable {variable}.", null);
    }

    private sealed record ImmutableRegistrars(Action<ProviderRegistry>[] Items);
}
