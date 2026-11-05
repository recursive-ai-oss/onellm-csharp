namespace OneLlm.Catalog;

/// <summary>How a model name was matched.</summary>
public enum ModelMatch
{
    /// <summary>The name is a model in the catalog.</summary>
    Exact = 0,

    /// <summary>The name is an alias; <see cref="ResolvedModel.Name"/> is what it points to.</summary>
    Alias = 1,

    /// <summary>The name matched a provider's or preset's model name pattern. The catalog has no entry for it: <see cref="ResolvedModel.Model"/> is null.</summary>
    Pattern = 2,
}

/// <summary>The result of looking a model name up in a <see cref="ModelCatalog"/>.</summary>
public sealed class ResolvedModel
{
    internal ResolvedModel(string requested, ProviderSpec provider, string? presetName, PresetSpec? preset, string name, ModelSpec? model, ModelMatch match, bool qualified)
    {
        Requested = requested;
        Provider = provider;
        PresetName = presetName;
        Preset = preset;
        Name = name;
        Model = model;
        Match = match;
        WasQualified = qualified;
    }

    /// <summary>The name the caller used.</summary>
    public string Requested { get; }

    /// <summary>The provider that serves the model.</summary>
    public ProviderSpec Provider { get; }

    /// <summary>The preset (server) the model came from, or null for the provider itself.</summary>
    public string? PresetName { get; }

    /// <summary>The preset's settings, or null.</summary>
    public PresetSpec? Preset { get; }

    /// <summary>The model's name as the provider's API takes it, after aliases.</summary>
    public string Name { get; }

    /// <summary>The catalog entry, or null when the name matched only a pattern.</summary>
    public ModelSpec? Model { get; }

    /// <summary>How the name was matched.</summary>
    public ModelMatch Match { get; }

    /// <summary>True when the caller used the <c>provider:model</c> form.</summary>
    public bool WasQualified { get; }

    /// <summary>The unambiguous form of the model: <c>provider:name</c>, or <c>preset:name</c> for a preset's model.</summary>
    public string Id => (PresetName ?? Provider.Name) + ":" + Name;

    /// <inheritdoc />
    public override string ToString() => Id;
}
