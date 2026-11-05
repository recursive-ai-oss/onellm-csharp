using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Catalog;

/// <summary>
/// The model catalog: layers of provider entries merged into one view, with model name resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layers</b> are stacked lowest first (shipped files, user files, code). Providers are matched by <c>name</c>. For a provider that
/// appears in several layers, fields merge from the bottom up: a scalar set in a higher layer replaces the lower value; mappings
/// (<c>aliases</c>, <c>properties</c>, <c>presets</c>, <c>serviceTiers</c>, <c>pricing</c>, ...) merge key by key; <c>models</c> merge by model
/// name, so a layer can add a model or change single fields of one; any other list (<c>modelPatterns</c>, <c>efforts</c>, price <c>items</c>)
/// is replaced as a whole. A field a layer leaves out is unchanged. Fields the reader does not know merge the same way.
/// </para>
/// <para>
/// <b>Resolution</b> of a name, in this order: an optional <c>provider:model</c> (or <c>preset:model</c>) prefix limits the search to that
/// provider or preset; then an alias; then an exact model name; then the model name patterns, where the pattern with the most literal
/// characters wins. A name that two providers match at the same step is ambiguous: <see cref="Resolve"/> throws and names the candidates.
/// Names compare ignoring case. A colon belongs to the model name unless the text before it names a provider or preset
/// (<c>llama3:8b</c> stays one name).
/// </para>
/// </remarks>
public sealed class ModelCatalog
{
    private const int MaxAliasDepth = 8;

    private readonly CatalogLayer[] _layers;
    private readonly ProviderSpec[] _providers;
    private readonly Dictionary<string, Dictionary<string, string>> _provenance;
    private readonly Scope[] _scopes;

    private ModelCatalog(CatalogLayer[] layers, ProviderSpec[] providers, Dictionary<string, Dictionary<string, string>> provenance)
    {
        _layers = layers;
        _providers = providers;
        _provenance = provenance;
        _scopes = BuildScopes(providers);
    }

    /// <summary>A catalog with no providers.</summary>
    public static ModelCatalog Empty { get; } = Create([]);

    /// <summary>The layers, lowest first.</summary>
    public IReadOnlyList<CatalogLayer> Layers => _layers;

    /// <summary>The merged providers, in the order they first appear in the layers.</summary>
    public IReadOnlyList<ProviderSpec> Providers => _providers;

    /// <summary>Merges layers into a catalog.</summary>
    /// <param name="layers">The layers, lowest (least specific) first.</param>
    /// <returns>The catalog.</returns>
    public static ModelCatalog Create(IEnumerable<CatalogLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        CatalogLayer[] stack = [.. layers];
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        var provenance = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (CatalogLayer layer in stack)
        {
            foreach (ProviderSpec spec in layer.Providers)
            {
                var node = (JsonObject)JsonSerializer.SerializeToNode(spec, CatalogJsonContext.Default.ProviderSpec)!;
                if (!merged.TryGetValue(spec.Name, out JsonObject? target))
                {
                    order.Add(spec.Name);
                    merged[spec.Name] = node;
                    provenance[spec.Name] = [];
                    RecordTree(string.Empty, "", node, layer.Name, provenance[spec.Name]);
                    continue;
                }

                MergeInto(target, node, layer.Name, string.Empty, provenance[spec.Name]);
            }
        }

        var providers = new ProviderSpec[order.Count];
        for (int i = 0; i < providers.Length; i++)
        {
            providers[i] = merged[order[i]].Deserialize(CatalogJsonContext.Default.ProviderSpec)!;
        }

        return new ModelCatalog(stack, providers, provenance);
    }

    /// <summary>Returns a catalog with one more layer on top.</summary>
    /// <param name="layer">The layer, for example overrides made in code.</param>
    /// <returns>A new catalog; this one is unchanged.</returns>
    public ModelCatalog WithLayer(CatalogLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Create([.. _layers, layer]);
    }

    /// <summary>Finds a provider by its name.</summary>
    /// <param name="name">The provider's name, ignoring case.</param>
    /// <returns>The merged provider, or null.</returns>
    public ProviderSpec? FindProvider(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Says which layer a value came from.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="path">
    /// The value's path in the provider entry, with dots between keys and the model name in brackets, for example <c>aliases.claude-sonnet</c>,
    /// <c>models[claude-sonnet-5-5].contextWindow</c> or <c>models[gpt-6].pricing.inputPerMillion</c>.
    /// </param>
    /// <returns>The name of the highest layer that set the value, or null when no layer did.</returns>
    public string? SourceOf(string provider, string path)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(path);
        return _provenance.TryGetValue(provider, out Dictionary<string, string>? paths) && paths.TryGetValue(path, out string? layer) ? layer : null;
    }

    /// <summary>Lists where every value of a provider's entry came from.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <returns>Value paths (as in <see cref="SourceOf"/>) with the name of the highest layer that set each; empty for an unknown provider.</returns>
    public IReadOnlyDictionary<string, string> SourcesOf(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _provenance.TryGetValue(provider, out Dictionary<string, string>? paths) ? paths : new Dictionary<string, string>();
    }

    /// <summary>Looks a model name up.</summary>
    /// <param name="name">A catalog name, an alias, a pattern-matched name, or any of these as <c>provider:model</c>.</param>
    /// <returns>The match.</returns>
    /// <exception cref="ModelNotFoundException">No model matches, or more than one provider matches and the name is ambiguous.</exception>
    public ResolvedModel Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ResolveCore(name, out string? problem) ?? throw new ModelNotFoundException(name, problem!);
    }

    /// <summary>Looks a model name up without throwing.</summary>
    /// <param name="name">The name; see <see cref="Resolve"/>.</param>
    /// <param name="resolved">The match, or null.</param>
    /// <returns>True when exactly one model matches.</returns>
    public bool TryResolve(string name, out ResolvedModel? resolved)
    {
        ArgumentNullException.ThrowIfNull(name);
        resolved = ResolveCore(name, out _);
        return resolved is not null;
    }

    // ---- resolution ----

    private sealed class Scope(ProviderSpec provider, string? presetName, PresetSpec? preset)
    {
        public ProviderSpec Provider { get; } = provider;

        public string? PresetName { get; } = presetName;

        public PresetSpec? Preset { get; } = preset;

        public Dictionary<string, string> Aliases { get; } = new(
            (preset is null ? provider.Aliases : preset.Aliases) ?? [],
            StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Patterns { get; } = (preset is null ? provider.ModelPatterns : preset.ModelPatterns) ?? [];

        public IReadOnlyList<ModelSpec> Models { get; } = (preset is null ? provider.Models : preset.Models) ?? [];

        public ModelSpec? FindModel(string name) =>
            Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public string Label => (PresetName ?? Provider.Name);
    }

    private readonly record struct Candidate(Scope Scope, string Name, ModelSpec? Model, ModelMatch Match, int Specificity)
    {
        public string Key => Scope.Provider.Name + "|" + Scope.PresetName + "|" + Name.ToUpperInvariant();

        public string Display => Scope.Label + ":" + Name;
    }

    private static Scope[] BuildScopes(ProviderSpec[] providers)
    {
        var scopes = new List<Scope>();
        foreach (ProviderSpec provider in providers)
        {
            scopes.Add(new Scope(provider, null, null));
            if (provider.Presets is not null)
            {
                foreach ((string presetName, PresetSpec preset) in provider.Presets)
                {
                    scopes.Add(new Scope(provider, presetName, preset));
                }
            }
        }

        return [.. scopes];
    }

    private ResolvedModel? ResolveCore(string requested, out string? problem)
    {
        problem = null;
        string name = requested.Trim();
        IReadOnlyList<Scope> scopes = _scopes;
        bool qualified = false;

        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            string prefix = name[..colon];
            Scope[] byProvider = [.. _scopes.Where(s => string.Equals(s.Provider.Name, prefix, StringComparison.OrdinalIgnoreCase))];
            Scope[] limited = byProvider.Length > 0
                ? byProvider
                : [.. _scopes.Where(s => s.PresetName is not null && string.Equals(s.PresetName, prefix, StringComparison.OrdinalIgnoreCase))];
            if (limited.Length > 0)
            {
                scopes = limited;
                name = name[(colon + 1)..];
                qualified = true;
            }
        }

        if (name.Length == 0)
        {
            problem = $"'{requested}' names a provider but no model.";
            return null;
        }

        var found = new List<Candidate>();

        // 1. Aliases, followed to the model they point to within the same provider or preset.
        foreach (Scope scope in scopes)
        {
            if (!scope.Aliases.ContainsKey(name))
            {
                continue;
            }

            string target = name;
            int depth = 0;
            while (scope.Aliases.TryGetValue(target, out string? next) && !string.Equals(next, target, StringComparison.OrdinalIgnoreCase))
            {
                target = next;
                if (++depth > MaxAliasDepth)
                {
                    problem = $"the alias '{name}' in {scope.Label} never reaches a model (it loops or is longer than {MaxAliasDepth} steps).";
                    return null;
                }
            }

            found.Add(new Candidate(scope, target, scope.FindModel(target), ModelMatch.Alias, 0));
        }

        // 2. Exact model names.
        if (found.Count == 0)
        {
            foreach (Scope scope in scopes)
            {
                if (scope.FindModel(name) is { } model)
                {
                    found.Add(new Candidate(scope, model.Name, model, ModelMatch.Exact, 0));
                }
            }
        }

        // 3. Patterns: the most specific one wins.
        if (found.Count == 0)
        {
            foreach (Scope scope in scopes)
            {
                int best = -1;
                foreach (string pattern in scope.Patterns)
                {
                    if (Glob.IsMatch(pattern, name))
                    {
                        best = Math.Max(best, Glob.LiteralLength(pattern));
                    }
                }

                if (best >= 0)
                {
                    found.Add(new Candidate(scope, name, null, ModelMatch.Pattern, best));
                }
            }

            if (found.Count > 1)
            {
                int top = found.Max(c => c.Specificity);
                found = [.. found.Where(c => c.Specificity == top)];
            }
        }

        Candidate[] distinct = [.. found.DistinctBy(c => c.Key)];
        if (distinct.Length == 1)
        {
            Candidate c = distinct[0];
            return new ResolvedModel(requested, c.Scope.Provider, c.Scope.PresetName, c.Scope.Preset, c.Name, c.Model, c.Match, qualified);
        }

        problem = distinct.Length == 0
            ? $"no model matches '{requested}'."
            : $"'{requested}' matches more than one model ({string.Join(", ", distinct.Select(d => d.Display))}); write it as provider:model.";
        return null;
    }

    // ---- merging ----

    private static void MergeInto(JsonObject target, JsonObject source, string layer, string path, Dictionary<string, string> provenance)
    {
        foreach ((string key, JsonNode? value) in source.ToArray())
        {
            string valuePath = path.Length == 0 ? key : path + "." + key;
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeInto(targetObject, sourceObject, layer, valuePath, provenance);
            }
            else if (key == "models" && value is JsonArray sourceModels && target[key] is JsonArray targetModels)
            {
                MergeModels(targetModels, sourceModels, layer, valuePath, provenance);
            }
            else
            {
                target[key] = value?.DeepClone();
                RecordTree(valuePath, key, value, layer, provenance);
            }
        }
    }

    private static void MergeModels(JsonArray target, JsonArray source, string layer, string path, Dictionary<string, string> provenance)
    {
        // The path of a model is the models path with its name in brackets: "models[gpt-6]" or "presets.groq.models[x]".
        string bracketBase = path;
        foreach (JsonNode? item in source)
        {
            if (item is not JsonObject model || model["name"]?.GetValue<string>() is not { } modelName)
            {
                continue;
            }

            string modelPath = bracketBase + "[" + modelName + "]";
            JsonObject? existing = target.OfType<JsonObject>().FirstOrDefault(m => m["name"]?.GetValue<string>() == modelName);
            if (existing is null)
            {
                target.Add(model.DeepClone());
                RecordTree(modelPath, "", model, layer, provenance);
            }
            else
            {
                MergeInto(existing, model, layer, modelPath, provenance);
            }
        }
    }

    // Records the layer for the value at a path and, for a mapping, for every value below it. Lists of models record per model.
    private static void RecordTree(string path, string key, JsonNode? node, string layer, Dictionary<string, string> provenance)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string childKey, JsonNode? child) in obj)
                {
                    string childPath = path.Length == 0 ? childKey : path + "." + childKey;
                    RecordTree(childPath, childKey, child, layer, provenance);
                }

                break;
            case JsonArray array when key == "models":
                foreach (JsonNode? item in array)
                {
                    if (item is JsonObject model && model["name"]?.GetValue<string>() is { } modelName)
                    {
                        RecordTree(path + "[" + modelName + "]", "", model, layer, provenance);
                    }
                }

                break;
            default:
                if (path.Length > 0)
                {
                    provenance[path] = layer;
                }

                break;
        }
    }
}
