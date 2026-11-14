using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace SecureProxy.Api.Data;

public class ProxyDbContext(DbContextOptions<ProxyDbContext> options) : DbContext(options)
{
    /// <summary>Names of properties whose values are never copied into configuration history.</summary>
    private static readonly HashSet<string> RedactedProperties =
        new(StringComparer.Ordinal) { nameof(EncryptionProfile.KeyReference), nameof(Destination.AuthProfileReference) };

    private static readonly HashSet<string> IgnoredProperties =
        new(StringComparer.Ordinal) { "RowVersion", "CreatedAtUtc", "UpdatedAtUtc" };

    /// <summary>Who is changing configuration. Set by the admin endpoints; "system" otherwise.</summary>
    public string Actor { get; set; } = "system";

    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<ParameterRule> ParameterRules => Set<ParameterRule>();
    public DbSet<HeaderRule> HeaderRules => Set<HeaderRule>();
    public DbSet<EncryptionProfile> EncryptionProfiles => Set<EncryptionProfile>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
    public DbSet<ConfigurationHistory> ConfigurationHistory => Set<ConfigurationHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var sqlServer = Database.IsSqlServer();

        b.Entity<Destination>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.AllowedPorts).HasMaxLength(100);
            e.Property(x => x.AuthProfileReference).HasMaxLength(200);
            ConfigureVersion(e.Property(x => x.RowVersion), sqlServer);
        });

        b.Entity<Operation>(e =>
        {
            e.HasIndex(x => x.OperationKey).IsUnique();
            e.Property(x => x.OperationKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.HttpMethod).HasMaxLength(10).IsRequired();
            e.Property(x => x.PathTemplate).HasMaxLength(500).IsRequired();
            e.HasOne(x => x.Destination).WithMany(d => d.Operations)
                .HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.EncryptionProfile).WithMany()
                .HasForeignKey(x => x.EncryptionProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.ParameterRules).WithOne().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.HeaderRules).WithOne().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
            ConfigureVersion(e.Property(x => x.RowVersion), sqlServer);
        });

        b.Entity<ParameterRule>(e =>
        {
            e.HasIndex(x => new { x.OperationId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Pattern).HasMaxLength(500);
            e.Property(x => x.MinValue).HasPrecision(28, 6);
            e.Property(x => x.MaxValue).HasPrecision(28, 6);
            e.Property(x => x.DataType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Location).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<HeaderRule>(e =>
        {
            e.HasIndex(x => new { x.OperationId, x.HeaderName }).IsUnique();
            e.Property(x => x.HeaderName).HasMaxLength(100).IsRequired();
        });

        b.Entity<EncryptionProfile>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Algorithm).HasMaxLength(30).IsRequired();
            e.Property(x => x.KeyReference).HasMaxLength(200).IsRequired();
        });

        b.Entity<RequestLog>(e =>
        {
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.StartedAtUtc);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Caller).HasMaxLength(100);
            e.Property(x => x.OperationKey).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(100);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
        });

        b.Entity<ConfigurationHistory>(e =>
        {
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
            e.Property(x => x.ChangeType).HasMaxLength(20).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(100).IsRequired();
        });
    }

    private static void ConfigureVersion(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<byte[]> property, bool sqlServer)
    {
        // SQL Server generates the token itself (rowversion). Other providers (used by the
        // automated tests) get an application-generated token with the same semantics.
        if (sqlServer) property.IsRowVersion();
        else property.IsConcurrencyToken();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        var now = DateTime.UtcNow;
        var sqlServer = Database.IsSqlServer();
        var history = new List<ConfigurationHistory>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            if (entry.Entity is IConfigurationEntity cfg)
            {
                var changes = DescribeChanges(entry);
                if (entry.State != EntityState.Modified || changes.Count > 0)
                {
                    history.Add(new ConfigurationHistory
                    {
                        EntityType = entry.Metadata.ClrType.Name,
                        EntityId = cfg.Id,
                        ChangeType = entry.State.ToString(),
                        ChangedAtUtc = now,
                        ChangedBy = Actor,
                        Changes = JsonSerializer.Serialize(changes)
                    });
                }
            }

            if (entry.State == EntityState.Deleted) continue;

            if (entry.Metadata.FindProperty("UpdatedAtUtc") is not null)
                entry.Property("UpdatedAtUtc").CurrentValue = now;
            if (entry.State == EntityState.Added && entry.Metadata.FindProperty("CreatedAtUtc") is not null)
                entry.Property("CreatedAtUtc").CurrentValue = now;

            if (!sqlServer && entry.Entity is IVersioned versioned)
                versioned.RowVersion = Guid.NewGuid().ToByteArray();
        }

        if (history.Count > 0) ConfigurationHistory.AddRange(history);
    }

    private static Dictionary<string, object?> DescribeChanges(EntityEntry entry)
    {
        var result = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (IgnoredProperties.Contains(name) || p.Metadata.IsPrimaryKey()) continue;
            if (entry.State == EntityState.Modified && !p.IsModified) continue;

            if (RedactedProperties.Contains(name))
            {
                result[name] = "[redacted]";
                continue;
            }

            result[name] = entry.State switch
            {
                EntityState.Added => new { @new = Format(p.CurrentValue) },
                EntityState.Deleted => new { old = Format(p.OriginalValue) },
                _ => new { old = Format(p.OriginalValue), @new = Format(p.CurrentValue) }
            };
        }
        return result;
    }

    private static string? Format(object? value) =>
        value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}
