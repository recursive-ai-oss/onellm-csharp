using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneYaml;

namespace OneLlm.Messages;

/// <summary>
/// Timestamps in files are RFC 3339 text the way Go writes them: <c>2026-03-04T05:06:07Z</c> or <c>2026-03-04T05:06:07.25+02:00</c>, with
/// the fraction trimmed of trailing zeros and omitted when zero. <see cref="DateTimeOffset"/> keeps 100 ns ticks, so digits
/// beyond the seventh fraction digit of a timestamp read from a file are dropped.
/// </summary>
internal static class Rfc3339
{
    public static string Format(DateTimeOffset value)
    {
        string text = value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        if (text.EndsWith('.'))
        {
            text = text[..^1];
        }

        if (value.Offset == TimeSpan.Zero)
        {
            return text + "Z";
        }

        TimeSpan offset = value.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return text + sign + offset.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null || text.Length < 20)
        {
            return false;
        }

        // Go can write more than seven fraction digits; DateTimeOffset reads at most seven, so cut the rest.
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            int end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }
}

/// <summary>Reads and writes a <see cref="DateTimeOffset"/> as RFC 3339 text in JSON, the way the file format writes timestamps.</summary>
public sealed class Rfc3339JsonConverter : JsonConverter<DateTimeOffset>
{
    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        return Rfc3339.TryParse(text, out DateTimeOffset value) ? value : throw new JsonException("The value is not an RFC 3339 timestamp.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Rfc3339.Format(value));
}

/// <summary>Reads and writes an optional <see cref="DateTimeOffset"/> as RFC 3339 text in YAML, the way the file format writes timestamps.</summary>
public sealed class Rfc3339YamlConverter : YamlConverter<DateTimeOffset?>
{
    /// <inheritdoc />
    public override DateTimeOffset? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        string text = reader.GetString();
        return Rfc3339.TryParse(text, out DateTimeOffset value) ? value : throw reader.CreateException("The value is not an RFC 3339 timestamp.");
    }

    /// <inheritdoc />
    public override void Write(Utf8YamlEmitter writer, DateTimeOffset? value, YamlSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteStringValue(Rfc3339.Format(v));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
