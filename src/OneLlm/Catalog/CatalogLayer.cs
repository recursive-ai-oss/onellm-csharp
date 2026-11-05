using OneLlm.Messages;
using OneYaml;

namespace OneLlm.Catalog;

/// <summary>
/// One layer of the catalog: a named set of provider entries. Layers are stacked lowest first: the shipped files, then user files, then code
/// overrides. A layer states only what it changes.
/// </summary>
public sealed class CatalogLayer
{
    /// <summary>The conventional name of the layer of files shipped with a package.</summary>
    public const string Shipped = "shipped";

    /// <summary>The conventional name of the layer of files a user keeps.</summary>
    public const string User = "user";

    /// <summary>The conventional name of the layer of overrides made in code.</summary>
    public const string Code = "code";

    /// <summary>Creates a layer from provider entries, for example overrides made in code.</summary>
    /// <param name="name">The layer's name, shown wherever the catalog says where a value came from.</param>
    /// <param name="providers">The entries. Each is matched to the provider of the same name in the layers below; a name seen there first becomes a new provider.</param>
    public CatalogLayer(string name, IEnumerable<ProviderSpec> providers)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Providers = [.. providers ?? throw new ArgumentNullException(nameof(providers))];
    }

    /// <summary>The layer's name.</summary>
    public string Name { get; }

    /// <summary>The provider entries of the layer.</summary>
    public IReadOnlyList<ProviderSpec> Providers { get; }

    /// <summary>Reads a layer from YAML text: one provider's fields at the top level, or several under <c>providers</c>.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="sourceName">What to call the text in errors, usually a file name.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="BadDefinitionException">The text is not valid YAML or does not fit the catalog shape. It names the line and column.</exception>
    public static CatalogLayer FromYaml(string name, string yaml, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        try
        {
            var options = new YamlSerializerOptions(CatalogYamlContext.Default.Options) { SourceName = sourceName };
            var context = new CatalogYamlContext(options);

            ProviderSpec? provider = YamlSerializer.Deserialize(yaml, context.ProviderSpec);
            if (provider is null)
            {
                return new CatalogLayer(name, []);
            }

            if (provider.Name.Length > 0)
            {
                return new CatalogLayer(name, [provider]);
            }

            // No name at the top level: the file lists its providers under a providers key.
            CatalogFileSpec? file = YamlSerializer.Deserialize(yaml, context.CatalogFileSpec);
            if (file?.Providers is null)
            {
                throw new BadDefinitionException("a catalog file needs a provider name or a providers list.", sourceName, 1, 1);
            }

            return new CatalogLayer(name, file.Providers);
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, sourceName, e.Line, e.Column, e);
        }
    }

    /// <summary>Reads a layer from one YAML file.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="path">The file.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="BadDefinitionException">The file does not fit the catalog shape.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static CatalogLayer FromFile(string name, string path) =>
        FromYaml(name, File.ReadAllText(path), path);

    /// <summary>Reads a layer from every <c>.yaml</c> and <c>.yml</c> file under a directory, in path order, so a later file in the order overrides an earlier one.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="directory">The directory, searched including its subdirectories.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="BadDefinitionException">A file does not fit the catalog shape.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static CatalogLayer FromDirectory(string name, string directory)
    {
        var providers = new List<ProviderSpec>();
        var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (string file in files)
        {
            providers.AddRange(FromFile(name, file).Providers);
        }

        return new CatalogLayer(name, providers);
    }
}
