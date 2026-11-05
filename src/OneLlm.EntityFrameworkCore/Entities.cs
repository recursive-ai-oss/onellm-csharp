using Microsoft.EntityFrameworkCore;

namespace OneLlm.EntityFrameworkCore;

/// <summary>One stored instance: the header columns the listing needs, plus the whole instance as its YAML document.</summary>
public class OneLlmInstance
{
    /// <summary>The instance id (primary key).</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>The definition's name, or empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>The state string, or null.</summary>
    public string? State { get; set; }

    /// <summary>The model, or null.</summary>
    public string? Model { get; set; }

    /// <summary>The calling instance, or null.</summary>
    public string? CallerInstanceId { get; set; }

    /// <summary>The spawning instance, or null. Together with the caller it defines an instance's subtree.</summary>
    public string? SpawnerInstanceId { get; set; }

    /// <summary>When the row was last saved, by the store's clock.</summary>
    public DateTimeOffset Modified { get; set; }

    /// <summary>The instance in its file format (YAML), the source of truth for loading.</summary>
    public string Yaml { get; set; } = "";

    /// <summary>Counts saves. It is the row's concurrency token: two writers that read the same version cannot both update the row.</summary>
    public long Version { get; set; }
}

/// <summary>One audit record: the columns queries need, plus the record's JSON line.</summary>
public class OneLlmRecord
{
    /// <summary>Row id; also the append order.</summary>
    public long Id { get; set; }

    /// <summary>The instance the record belongs to, or empty for a runtime-level record.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>The record kind, such as <c>inference</c> or <c>toolCall</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When it happened, by the runtime's clock.</summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>The message it belongs to, or null.</summary>
    public string? MessageId { get; set; }

    /// <summary>The money this record spent (inference and tool calls), or null for records that cost nothing.</summary>
    public decimal? Cost { get; set; }

    /// <summary>The record as one line of the JSON record format.</summary>
    public string Json { get; set; } = "";
}

/// <summary>The right to run one instance, held until it expires or is released.</summary>
public class OneLlmLease
{
    /// <summary>The instance id (primary key).</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>A token naming the holder.</summary>
    public string Owner { get; set; } = "";

    /// <summary>When the lease lapses unless its holder renews it. A crashed runner stops renewing, so its lease lapses.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Adds the OneLLM tables to your own model.</summary>
public static class OneLlmModelBuilderExtensions
{
    /// <summary>Maps <see cref="OneLlmInstance"/>, <see cref="OneLlmRecord"/> and <see cref="OneLlmLease"/> into your model.</summary>
    /// <param name="modelBuilder">The builder, usually in <c>OnModelCreating</c>.</param>
    /// <param name="tablePrefix">Prepended to the three table names; <c>OneLlm</c> by default.</param>
    /// <param name="schema">The schema, or null for the provider's default.</param>
    /// <returns>The same builder.</returns>
    public static ModelBuilder ApplyOneLlm(this ModelBuilder modelBuilder, string tablePrefix = "OneLlm", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(tablePrefix);

        // Times are stored as UTC ticks so they sort and compare the same way on every provider (some cannot order DateTimeOffset).
        var ticks = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        modelBuilder.Entity<OneLlmInstance>(e =>
        {
            e.ToTable(tablePrefix + "Instances", schema);
            e.HasKey(x => x.InstanceId);
            e.Property(x => x.InstanceId).HasMaxLength(128);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Yaml).IsRequired();
            e.Property(x => x.Modified).HasConversion(ticks);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Modified);
            e.HasIndex(x => x.CallerInstanceId);
            e.HasIndex(x => x.SpawnerInstanceId);
        });

        modelBuilder.Entity<OneLlmRecord>(e =>
        {
            e.ToTable(tablePrefix + "Records", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.InstanceId).HasMaxLength(128);
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Time).HasConversion(ticks);
            e.Property(x => x.Cost).HasPrecision(28, 12);
            e.Property(x => x.Json).IsRequired();
            e.HasIndex(x => new { x.InstanceId, x.Id });
        });

        modelBuilder.Entity<OneLlmLease>(e =>
        {
            e.ToTable(tablePrefix + "Leases", schema);
            e.HasKey(x => x.InstanceId);
            e.Property(x => x.InstanceId).HasMaxLength(128);
            e.Property(x => x.Owner).IsRequired();
            e.Property(x => x.ExpiresAt).HasConversion(ticks);
        });

        return modelBuilder;
    }
}
