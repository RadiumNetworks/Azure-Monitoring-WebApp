using Microsoft.EntityFrameworkCore;

namespace UIDynamic.Data;

public sealed class LayoutDbContext(DbContextOptions<LayoutDbContext> options) : DbContext(options)
{
    public DbSet<SavedLayout> SavedLayouts => Set<SavedLayout>();
    public DbSet<MetricReading> MetricReadings => Set<MetricReading>();
    public DbSet<TrendSample> TrendSamples => Set<TrendSample>();
    public DbSet<OperationalAlert> OperationalAlerts => Set<OperationalAlert>();
    public DbSet<TeamNote> TeamNotes => Set<TeamNote>();
    public DbSet<ServiceHealthEntry> ServiceHealthEntries => Set<ServiceHealthEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var layout = modelBuilder.Entity<SavedLayout>();
        layout.ToTable("SavedLayouts");
        layout.HasKey(item => item.Id);
        layout.HasIndex(item => item.OwnerKey).IsUnique();
        layout.Property(item => item.OwnerKey).HasMaxLength(64).IsRequired();
        layout.Property(item => item.Name).HasMaxLength(120).IsRequired();
        layout.Property(item => item.DocumentJson).IsRequired();

        var metric = modelBuilder.Entity<MetricReading>();
        metric.ToTable("MetricReadings");
        metric.HasKey(item => item.Id);
        metric.HasIndex(item => item.DataSourceKey).IsUnique();
        metric.Property(item => item.DataSourceKey).HasMaxLength(100).IsRequired();
        metric.Property(item => item.Label).HasMaxLength(160).IsRequired();
        metric.Property(item => item.Unit).HasMaxLength(20).IsRequired();

        var trend = modelBuilder.Entity<TrendSample>();
        trend.ToTable("TrendSamples");
        trend.HasKey(item => item.Id);
        trend.HasIndex(item => new { item.DataSourceKey, item.RecordedAt }).IsUnique();
        trend.Property(item => item.DataSourceKey).HasMaxLength(100).IsRequired();
        trend.Property(item => item.SeriesLabel).HasMaxLength(160).IsRequired();

        var alert = modelBuilder.Entity<OperationalAlert>();
        alert.ToTable("OperationalAlerts");
        alert.HasKey(item => item.Id);
        alert.HasIndex(item => item.DataSourceKey);
        alert.Property(item => item.DataSourceKey).HasMaxLength(100).IsRequired();
        alert.Property(item => item.Title).HasMaxLength(160).IsRequired();
        alert.Property(item => item.Location).HasMaxLength(160).IsRequired();
        alert.Property(item => item.Priority).HasMaxLength(8).IsRequired();

        var note = modelBuilder.Entity<TeamNote>();
        note.ToTable("TeamNotes");
        note.HasKey(item => item.Id);
        note.HasIndex(item => item.DataSourceKey).IsUnique();
        note.Property(item => item.DataSourceKey).HasMaxLength(100).IsRequired();
        note.Property(item => item.Content).HasMaxLength(4000).IsRequired();
        note.Property(item => item.Author).HasMaxLength(100).IsRequired();

        var health = modelBuilder.Entity<ServiceHealthEntry>();
        health.ToTable("ServiceHealthEntries");
        health.HasKey(item => item.Id);
        health.HasIndex(item => new { item.DataSourceKey, item.ComponentName }).IsUnique();
        health.Property(item => item.DataSourceKey).HasMaxLength(100).IsRequired();
        health.Property(item => item.ComponentName).HasMaxLength(100).IsRequired();
        health.Property(item => item.Status).HasMaxLength(30).IsRequired();
    }
}

public sealed class SavedLayout
{
    public int Id { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public int DocumentVersion { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MetricReading
{
    public int Id { get; set; }
    public string DataSourceKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public double Change { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class TrendSample
{
    public int Id { get; set; }
    public string DataSourceKey { get; set; } = string.Empty;
    public string SeriesLabel { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class OperationalAlert
{
    public int Id { get; set; }
    public string DataSourceKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public DateTimeOffset RaisedAt { get; set; }
}

public sealed class TeamNote
{
    public int Id { get; set; }
    public string DataSourceKey { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ServiceHealthEntry
{
    public int Id { get; set; }
    public string DataSourceKey { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}