using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Provides the Entity Framework Core session for stored alert records. Its primary constructor receives the database options configured at application startup.
/// </summary>
public sealed class AlertDbContext(DbContextOptions<AlertDbContext> options) : DbContext(options)
{
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();

    /// <summary>
    /// Configures the Alerts table, column sizes, indexes, and non-persisted computed properties. Entity Framework calls this method when it builds the database model.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var alert = modelBuilder.Entity<AlertRecord>();

        alert.ToTable("Alerts");
        alert.HasKey(record => record.Id);
        alert.Property(record => record.Id).ValueGeneratedNever();
        alert.Property(record => record.AlertId).HasMaxLength(512);
        alert.Property(record => record.Name).HasMaxLength(512);
        alert.Property(record => record.Severity).HasMaxLength(64);
        alert.Property(record => record.Status).HasMaxLength(64);
        alert.Property(record => record.SignalType).HasMaxLength(128);
        alert.Property(record => record.MonitorCondition).HasMaxLength(64);
        alert.Property(record => record.Target).HasMaxLength(2048);
        alert.Property(record => record.ResourceGroup).HasMaxLength(256);
        alert.Property(record => record.SubscriptionId).HasMaxLength(64);
        alert.Property(record => record.Description).HasMaxLength(4000);
        alert.Property(record => record.SearchResultsUrl).HasMaxLength(2048);
        alert.Property(record => record.Comments).HasMaxLength(4000);
        alert.Property(record => record.RawJson).HasColumnType("nvarchar(max)");
        alert.Ignore(record => record.TargetName);
        alert.Ignore(record => record.SiteName);
        alert.Ignore(record => record.TargetDisplayName);
        alert.Ignore(record => record.SearchQuery);
        alert.HasIndex(record => record.ReceivedAt);
        alert.HasIndex(record => new { record.AlertId, record.MonitorCondition });
    }
}