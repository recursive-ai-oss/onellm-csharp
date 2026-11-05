using System.Reflection;

namespace OneLlm.Catalog;

/// <summary>
/// Builds a <see cref="ModelCatalog"/> from its three layers: files shipped inside packages (<c>shipped</c>), the user's catalog files
/// (<c>user</c>), and overrides made in code (<c>code</c>); each layer overrides the one below it field by field.
/// </summary>
/// <remarks>
/// A package that ships models registers its embedded <c>llm-provider.yaml</c> text with <see cref="RegisterShipped(string, Func{string})"/>
/// (or <see cref="RegisterShipped(Assembly, string)"/> for an embedded resource). Registration is by name and repeated registration of a name
/// is ignored, so it is safe to call from several places. User files come from the path given to <see cref="Load"/> or, when none is given, from
/// the <c>ONELLM_CATALOG</c> environment variable: a file, a directory (every <c>.yaml</c>/<c>.yml</c> under it), or several of these separated by the
/// platform's path separator.
/// </remarks>
public static class CatalogLoader
{
    /// <summary>The environment variable that names user catalog files or directories.</summary>
    public const string EnvironmentVariable = "ONELLM_CATALOG";

    private static readonly object Gate = new();
    private static readonly List<(string Name, Func<string> Yaml)> Shipped = [];

    /// <summary>Registers catalog YAML that ships with a package. Later registrations sit above earlier ones in the shipped layer.</summary>
    /// <param name="sourceName">A unique name for the text, shown in errors, for example <c>anthropic/llm-provider.yaml</c>. A name already registered is ignored.</param>
    /// <param name="yaml">Returns the YAML text. It is called each time a catalog is loaded.</param>
    public static void RegisterShipped(string sourceName, Func<string> yaml)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(yaml);
        lock (Gate)
        {
            if (!Shipped.Exists(s => s.Name == sourceName))
            {
                Shipped.Add((sourceName, yaml));
            }
        }
    }

    /// <summary>Registers catalog YAML embedded in an assembly as a manifest resource.</summary>
    /// <param name="assembly">The assembly that holds the resource.</param>
    /// <param name="resourceName">The full manifest resource name.</param>
    /// <exception cref="ArgumentException">The assembly has no such resource.</exception>
    public static void RegisterShipped(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourceName);
        if (assembly.GetManifestResourceInfo(resourceName) is null)
        {
            throw new ArgumentException($"The assembly has no embedded resource '{resourceName}'.", nameof(resourceName));
        }

        RegisterShipped(resourceName, () =>
        {
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The embedded resource '{resourceName}' disappeared.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    /// <summary>Reads the layers of everything registered with <c>RegisterShipped</c>, as one <c>shipped</c> layer per registration.</summary>
    /// <returns>The layers, in registration order.</returns>
    /// <exception cref="BadDefinitionException">Registered text is not a valid catalog file.</exception>
    public static IReadOnlyList<CatalogLayer> ShippedLayers()
    {
        (string Name, Func<string> Yaml)[] sources;
        lock (Gate)
        {
            sources = [.. Shipped];
        }

        return [.. sources.Select(s => CatalogLayer.FromYaml(CatalogLayer.Shipped, s.Yaml(), s.Name))];
    }

    /// <summary>Reads a user layer from a file or a directory.</summary>
    /// <param name="layerName">The layer's name.</param>
    /// <param name="path">A YAML file, or a directory searched for <c>.yaml</c> and <c>.yml</c> files.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    /// <exception cref="BadDefinitionException">A file is not a valid catalog file; it names the file, line and column.</exception>
    public static CatalogLayer FromPath(string layerName, string path)
    {
        ArgumentNullException.ThrowIfNull(layerName);
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
        {
            return CatalogLayer.FromDirectory(layerName, path);
        }

        if (File.Exists(path))
        {
            return CatalogLayer.FromFile(layerName, path);
        }

        throw new FileNotFoundException($"The catalog path '{path}' does not exist.", path);
    }

    /// <summary>Builds the catalog: shipped layers, then the user layer, then the overrides.</summary>
    /// <param name="userPath">User files or directories, separated by the platform's path separator. When null, <c>ONELLM_CATALOG</c> is used if set.</param>
    /// <param name="overrides">Layers made in code, lowest first; they sit above the user files. Name them <see cref="CatalogLayer.Code"/> so provenance reads well.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable. The default reads the process environment; pass your own in tests or to isolate a host.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="FileNotFoundException">A user path does not exist.</exception>
    /// <exception cref="BadDefinitionException">A catalog file is invalid.</exception>
    public static ModelCatalog Load(string? userPath = null, IEnumerable<CatalogLayer>? overrides = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        var layers = new List<CatalogLayer>(ShippedLayers());
        // The process environment is read here, the one place the catalog's configuration comes from it.
        string? configured = userPath ?? (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (string part in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                layers.Add(FromPath(CatalogLayer.User, part));
            }
        }

        if (overrides is not null)
        {
            layers.AddRange(overrides);
        }

        return ModelCatalog.Create(layers);
    }
}
