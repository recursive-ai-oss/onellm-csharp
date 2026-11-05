using OneLlm.Instances;
using OneLlm.Messages;
using OneYaml;

namespace OneLlm.Definitions;

/// <summary>Reads and writes definition, instance, decider and flow files.</summary>
internal static class DefinitionFiles
{
    public static AgentDefinition ParseAgent(string yaml, string? source, DefinitionLoadOptions options)
    {
        AgentFileWire wire = ReadWire(yaml, source);
        var def = new AgentDefinition { SourceName = source };
        ApplyDefinitionFields(wire, def);
        ValidateAndGrant(yaml, source, def, options);
        def.OriginalText = yaml;
        def.LoadedSerialization = WriteAgent(def);
        return def;
    }

    public static Instance ParseInstance(string yaml, string? source)
    {
        AgentFileWire wire = ReadWire(yaml, source);
        var inst = new Instance { SourceName = source };
        ApplyDefinitionFields(wire, inst);
        inst.InstanceId = wire.InstanceId ?? "";
        inst.DefinitionPath = wire.DefinitionPath;
        inst.RequestedModel = wire.RequestedModel;
        inst.MaxImageSize = wire.MaxImageSize;
        inst.State = wire.State;
        inst.CallerInstanceId = wire.CallerInstanceId;
        inst.SpawnerInstanceId = wire.SpawnerInstanceId;
        inst.CallerToolCallId = wire.CallerToolCallId;
        inst.DelegationPrompt = wire.DelegationPrompt;
        inst.RunStartedAt = wire.RunStartedAt;
        inst.RenderedSystemPrompt = wire.Metadata?.RenderedSystemPrompt;
        if (wire.Metadata?.ExtensionData is { Count: > 0 } meta)
        {
            (inst.ExtensionData ??= [])["metadata"] = meta;
        }

        inst.Messages = wire.Messages ?? [];
        inst.PendingApprovals = wire.PendingApprovals ?? [];
        inst.Compactions = wire.Compactions ?? [];
        inst.OriginalText = yaml;
        inst.LoadedSerialization = InstanceWriter.Write(inst);
        return inst;
    }

    public static string WriteAgent(AgentDefinition def) => Serialize(ToWire(def, includeInitialPrompts: true));

    public static AgentFileWire ToWire(AgentDefinition def, bool includeInitialPrompts)
    {
        var w = new AgentFileWire
        {
            Name = def.Name.Length == 0 && def is not Instance ? null : def.Name,
            Description = def.Description,
            Model = def.Model,
            ServiceTier = def.ServiceTier,
            Thinking = def.Thinking,
            Temperature = def.Temperature,
            MaxIterations = def.MaxIterations,
            MaxTokens = def.MaxTokens,
            LlmTurnLogs = def.LlmTurnLogs,
            InvocationMode = def.InvocationMode,
            MissingKey = def.MissingKey,
            WorkingDirectory = def.WorkingDirectory,
            Headers = def.Headers,
            Inputs = def.Inputs,
            SystemPrompt = def.SystemPrompt.Length == 0 && def.OriginalText is null ? null : def.SystemPrompt,
            References = ListOrNull(def.References, def.EmptyKeys, "references"),
            Hooks = ListOrNull(def.Hooks, def.EmptyKeys, "hooks"),
            InitialPrompts = includeInitialPrompts ? ListOrNull(def.InitialPrompts, def.EmptyKeys, "initialPrompts") : null,
            ExtensionData = def.ExtensionData,
        };
        if (def is Instance i)
        {
            w.RequestedModel = i.RequestedModel;
            w.MaxImageSize = i.MaxImageSize;
            w.State = i.State;
            w.InstanceId = i.InstanceId.Length == 0 ? null : i.InstanceId;
            w.DefinitionPath = i.DefinitionPath;
            w.CallerInstanceId = i.CallerInstanceId;
            w.SpawnerInstanceId = i.SpawnerInstanceId;
            w.CallerToolCallId = i.CallerToolCallId;
            w.DelegationPrompt = i.DelegationPrompt;
            w.RunStartedAt = i.RunStartedAt;
            Dictionary<string, object?>? metaExt = null;
            if (i.ExtensionData is not null && i.ExtensionData.TryGetValue("metadata", out object? m) && m is Dictionary<string, object?> md)
            {
                metaExt = md;
                // The metadata mapping is written through InstanceMetadataWire, not as a loose extension key.
                w.ExtensionData = new Dictionary<string, object?>(i.ExtensionData);
                w.ExtensionData.Remove("metadata");
            }

            if (i.RenderedSystemPrompt is not null || metaExt is not null)
            {
                w.Metadata = new InstanceMetadataWire { RenderedSystemPrompt = i.RenderedSystemPrompt, ExtensionData = metaExt };
            }

            w.PendingApprovals = ListOrNull(i.PendingApprovals, i.EmptyKeys, "pendingApprovals");
            w.Compactions = ListOrNull(i.Compactions, i.EmptyKeys, "compactions");
        }

        return w;
    }

    // A list that was written as an empty list in the file stays an empty list; one that was absent stays absent.
    private static List<T>? ListOrNull<T>(List<T> list, HashSet<string>? emptyKeys, string key) =>
        list.Count > 0 ? list : emptyKeys is not null && emptyKeys.Contains(key) ? list : null;

    private static void NoteEmpty<T>(AgentDefinition d, List<T>? list, string key)
    {
        if (list is { Count: 0 })
        {
            (d.EmptyKeys ??= []).Add(key);
        }
    }

    public static string Serialize(AgentFileWire wire) =>
        YamlSerializer.Serialize(wire, DefinitionsYamlContext.Default.AgentFileWire);

    public static string SerializeMessages(List<Message> messages) =>
        YamlSerializer.Serialize(messages, DefinitionsYamlContext.Default.ListMessage);

    private static AgentFileWire ReadWire(string yaml, string? source)
    {
        try
        {
            AgentFileWire? wire = YamlSerializer.Deserialize(yaml, DefinitionsYamlContext.Default.AgentFileWire);
            return wire ?? throw new BadDefinitionException("The file is empty.", source, 1, 1);
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, source, e.Line, e.Column, e);
        }
    }

    private static void ApplyDefinitionFields(AgentFileWire w, AgentDefinition d)
    {
        d.Name = w.Name ?? "";
        d.Description = w.Description;
        d.Model = w.Model;
        d.ServiceTier = w.ServiceTier;
        d.Thinking = w.Thinking;
        d.Temperature = w.Temperature;
        d.MaxIterations = w.MaxIterations;
        d.MaxTokens = w.MaxTokens;
        d.SystemPrompt = w.SystemPrompt ?? "";
        d.References = w.References ?? [];
        d.WorkingDirectory = w.WorkingDirectory;
        d.InitialPrompts = w.InitialPrompts ?? [];
        d.Inputs = w.Inputs;
        d.Headers = w.Headers;
        d.InvocationMode = w.InvocationMode;
        d.LlmTurnLogs = w.LlmTurnLogs;
        d.MissingKey = w.MissingKey;
        d.Hooks = w.Hooks ?? [];
        d.ExtensionData = w.ExtensionData;
        NoteEmpty(d, w.References, "references");
        NoteEmpty(d, w.Hooks, "hooks");
        NoteEmpty(d, w.InitialPrompts, "initialPrompts");
        NoteEmpty(d, w.PendingApprovals, "pendingApprovals");
        NoteEmpty(d, w.Compactions, "compactions");
        NoteEmpty(d, w.Messages, "messages");
    }

    private static void ValidateAndGrant(string yaml, string? source, AgentDefinition def, DefinitionLoadOptions options)
    {
        if (def.MissingKey is not (null or "default" or "error"))
        {
            Problem(yaml, source, def, options, "missingKey", $"missingKey must be 'default' or 'error', not '{def.MissingKey}'.");
            def.MissingKey = null;
        }

        for (int i = def.Hooks.Count - 1; i >= 0; i--)
        {
            if (def.Hooks[i].File.Length == 0)
            {
                Problem(yaml, source, def, options, $"hooks/{i}", "A hook needs a 'file'.");
                def.Hooks.RemoveAt(i);
            }
        }

        if (options.IsGranted is null)
        {
            return;
        }

        for (int i = def.References.Count - 1; i >= 0; i--)
        {
            ToolReference r = def.References[i];
            if (!options.IsGranted(r))
            {
                string what = r.Routine ?? r.Swarm ?? "(empty reference)";
                Problem(yaml, source, def, options, $"references/{i}", $"The tool source '{what}' is not granted to this runtime. Grant it on the Runtime, or load leniently to drop it.");
                def.References.RemoveAt(i);
            }
        }
    }

    // In strict mode a problem fails the load with the position of the offending node; in lenient mode it becomes a warning.
    private static void Problem(string yaml, string? source, AgentDefinition def, DefinitionLoadOptions options, string pointer, string message)
    {
        if (!options.IsStrict)
        {
            def.Warnings.Add(message);
            return;
        }

        (int line, int column) = Locate(yaml, pointer);
        throw new BadDefinitionException(message, source, line, column);
    }

    private static (int Line, int Column) Locate(string yaml, string pointer)
    {
        try
        {
            using YamlDocument doc = YamlDocument.Parse(yaml);
            YamlNode node = doc.Root;
            foreach (string part in pointer.Split('/'))
            {
                node = int.TryParse(part, out int index) ? node[index] : node[part];
            }

            YamlPosition p = node.Position;
            return (p.Line, p.Column);
        }
        catch (Exception e) when (e is YamlException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return (0, 0);
        }
    }

    public static Instance CopyFields(AgentDefinition d)
    {
        var i = new Instance { SourceName = d.SourceName };
        CopyDefinitionInto(d, i);
        return i;
    }

    public static Instance CopyInstance(Instance s)
    {
        var i = new Instance { SourceName = s.SourceName };
        CopyDefinitionInto(s, i);
        i.DefinitionPath = s.DefinitionPath;
        i.RequestedModel = s.RequestedModel;
        i.MaxImageSize = s.MaxImageSize;
        i.State = s.State;
        i.CallerInstanceId = s.CallerInstanceId;
        i.SpawnerInstanceId = s.SpawnerInstanceId;
        i.CallerToolCallId = s.CallerToolCallId;
        i.DelegationPrompt = s.DelegationPrompt;
        i.RenderedSystemPrompt = s.RenderedSystemPrompt;
        i.RunStartedAt = s.RunStartedAt;
        i.Messages = [.. s.Messages];
        i.PendingApprovals = [.. s.PendingApprovals];
        i.Compactions = [.. s.Compactions];
        return i;
    }

    private static void CopyDefinitionInto(AgentDefinition s, AgentDefinition d)
    {
        d.Name = s.Name;
        d.Description = s.Description;
        d.Model = s.Model;
        d.ServiceTier = s.ServiceTier;
        d.Thinking = s.Thinking;
        d.Temperature = s.Temperature;
        d.MaxIterations = s.MaxIterations;
        d.MaxTokens = s.MaxTokens;
        d.SystemPrompt = s.SystemPrompt;
        d.References = [.. s.References];
        d.WorkingDirectory = s.WorkingDirectory;
        d.InitialPrompts = [.. s.InitialPrompts];
        d.Inputs = s.Inputs is null ? null : new Dictionary<string, object?>(s.Inputs);
        d.Headers = s.Headers is null ? null : new Dictionary<string, string>(s.Headers);
        d.InvocationMode = s.InvocationMode;
        d.LlmTurnLogs = s.LlmTurnLogs;
        d.MissingKey = s.MissingKey;
        d.Hooks = [.. s.Hooks];
        d.EmptyKeys = s.EmptyKeys is null ? null : [.. s.EmptyKeys];
        d.ExtensionData = s.ExtensionData is null ? null : new Dictionary<string, object?>(s.ExtensionData);
        d.Flow = s.Flow;
    }
}

/// <summary>The keys of the <c>flow.yaml</c> beside a <c>.flow</c> file. OneLLM keeps it whole and does not interpret it.</summary>
public sealed class FlowMetadata
{
    /// <summary>The flow's name, or null.</summary>
    public string? Name { get; init; }

    /// <summary>The flow's description, or null.</summary>
    public string? Description { get; init; }

    /// <summary>The flow's version number, or null.</summary>
    public int? Version { get; init; }

    /// <summary>Every other key, in file order (version history, input examples, rubrics, ...).</summary>
    public Dictionary<string, object?>? Other { get; init; }

    internal string? OriginalText { get; init; }

    internal string? LoadedSerialization { get; init; }

    /// <summary>Parses a <c>flow.yaml</c>.</summary>
    /// <param name="yaml">The file contents.</param>
    /// <param name="sourceName">The name used in error positions.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="BadDefinitionException">The YAML is malformed; carries file, line and column.</exception>
    public static FlowMetadata Parse(string yaml, string? sourceName = null)
    {
        try
        {
            FlowMetadataWire w = YamlSerializer.Deserialize(yaml, DefinitionsYamlContext.Default.FlowMetadataWire)
                ?? throw new BadDefinitionException("The file is empty.", sourceName, 1, 1);
            var meta = new FlowMetadata { Name = w.Name, Description = w.Description, Version = w.Version, Other = w.ExtensionData, OriginalText = yaml };
            return new FlowMetadata { Name = meta.Name, Description = meta.Description, Version = meta.Version, Other = meta.Other, OriginalText = yaml, LoadedSerialization = meta.WriteTyped() };
        }
        catch (YamlException e)
        {
            throw new BadDefinitionException(e.Message, sourceName, e.Line, e.Column, e);
        }
    }

    /// <summary>Writes the metadata as YAML. Unknown keys survive; metadata that was loaded and not changed comes back byte for byte.</summary>
    /// <returns>The YAML text.</returns>
    public string ToYaml()
    {
        string text = WriteTyped();
        return OriginalText is not null && LoadedSerialization == text ? OriginalText : text;
    }

    internal string WriteTyped() => YamlSerializer.Serialize(
        new FlowMetadataWire { Name = Name, Description = Description, Version = Version, ExtensionData = Other },
        DefinitionsYamlContext.Default.FlowMetadataWire);
}
