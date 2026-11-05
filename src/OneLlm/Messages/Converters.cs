using System.Text.Json;
using System.Text.Json.Serialization;
using OneYaml;

namespace OneLlm.Messages;

// Message and Part are richer than their files, so they are read and written through the wire classes in Wire.cs. These
// converters hold their own metadata for the wire classes: they work under any caller's options and contexts, and the
// file shape does not depend on a caller's naming policy.

/// <summary>Reads and writes <see cref="Part"/> in JSON as the content-item object of the file format.</summary>
public sealed class PartJsonConverter : JsonConverter<Part>
{
    /// <inheritdoc />
    public override Part? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        PartWire? wire = JsonSerializer.Deserialize(ref reader, WireJsonContext.Default.PartWire);
        return wire is null ? null : MessageMapping.FromWire(wire);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Part value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, MessageMapping.ToWire(value), WireJsonContext.Default.PartWire);
}

/// <summary>Reads and writes <see cref="Message"/> in JSON as the message object of the file format.</summary>
public sealed class MessageJsonConverter : JsonConverter<Message>
{
    /// <inheritdoc />
    public override Message? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        MessageWire? wire = JsonSerializer.Deserialize(ref reader, WireJsonContext.Default.MessageWire);
        return wire is null ? null : MessageMapping.FromWire(wire);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, MessageMapping.ToWire(value), WireJsonContext.Default.MessageWire);
}

/// <summary>Reads and writes <see cref="Part"/> in YAML as the content-item mapping of the file format.</summary>
public sealed class PartYamlConverter : YamlConverter<Part>
{
    /// <inheritdoc />
    public override Part? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        YamlTypeInfo<PartWire> info = WireYamlContext.Default.PartWire;
        PartWire? wire = info.Converter.Read(ref reader, typeof(PartWire), info.Options);
        return wire is null ? null : MessageMapping.FromWire(wire);
    }

    /// <inheritdoc />
    public override void Write(Utf8YamlEmitter writer, Part value, YamlSerializerOptions options)
    {
        YamlTypeInfo<PartWire> info = WireYamlContext.Default.PartWire;
        info.Converter.Write(writer, MessageMapping.ToWire(value), info.Options);
    }
}

/// <summary>Reads and writes <see cref="Message"/> in YAML as the message mapping of the file format.</summary>
public sealed class MessageYamlConverter : YamlConverter<Message>
{
    /// <inheritdoc />
    public override Message? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        YamlTypeInfo<MessageWire> info = WireYamlContext.Default.MessageWire;
        MessageWire? wire = info.Converter.Read(ref reader, typeof(MessageWire), info.Options);
        return wire is null ? null : MessageMapping.FromWire(wire);
    }

    /// <inheritdoc />
    public override void Write(Utf8YamlEmitter writer, Message value, YamlSerializerOptions options)
    {
        YamlTypeInfo<MessageWire> info = WireYamlContext.Default.MessageWire;
        info.Converter.Write(writer, MessageMapping.ToWire(value), info.Options);
    }
}
