using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Provides the Entity Framework Core session for stored alert records. Its primary constructor receives the database options configured at application startup.
/// </summary>
public sealed class AlertDbContext(DbContextOptions<AlertDbContext> options) : DbContext(options)
{
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<ComputerInventoryEntry> ComputerInventory => Set<ComputerInventoryEntry>();
    public DbSet<SqlAuthenticationUser> AuthenticationUsers => Set<SqlAuthenticationUser>();

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
        alert.Ignore(record => record.DisplayIdentity);
        alert.Ignore(record => record.TargetName);
        alert.Ignore(record => record.SiteName);
        alert.Ignore(record => record.TargetDisplayName);
        alert.Ignore(record => record.SearchQuery);
        alert.HasIndex(record => record.ReceivedAt);
        alert.HasIndex(record => new { record.AlertId, record.MonitorCondition });

        var rule = modelBuilder.Entity<AlertRule>();
        rule.ToTable("AlertRules");
        rule.HasKey(record => record.Id);
        rule.Property(record => record.Id).ValueGeneratedNever();
        rule.Property(record => record.Name).HasMaxLength(256);
        rule.Property(record => record.AlertNameContains).HasMaxLength(256);
        rule.Property(record => record.QueryResultType).HasMaxLength(128);
        rule.Property(record => record.ConditionType).HasMaxLength(64);
        rule.Property(record => record.FailedItemName).HasMaxLength(256);
        rule.Property(record => record.CategoryName).HasMaxLength(256);
        rule.Property(record => record.Tone).HasMaxLength(32);
        rule.HasIndex(record => record.Name).IsUnique();
        rule.HasIndex(record => new { record.Enabled, record.Priority });

        var inventoryEntry = modelBuilder.Entity<ComputerInventoryEntry>();
        inventoryEntry.ToTable("ComputerInventory");
        inventoryEntry.HasKey(entry => new { entry.SubscriptionId, entry.Computer });
        inventoryEntry.Property(entry => entry.SubscriptionId).HasMaxLength(64);
        inventoryEntry.Property(entry => entry.Domain).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.Site).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.Computer).HasMaxLength(256);

        var authenticationUser = modelBuilder.Entity<SqlAuthenticationUser>();
        authenticationUser.ToTable("AuthenticationUsers");
        authenticationUser.HasKey(user => user.Username);
        authenticationUser.Property(user => user.Username).HasMaxLength(128);
        authenticationUser.Property(user => user.PasswordHash).HasMaxLength(512);
    }
}