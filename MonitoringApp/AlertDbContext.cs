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
    public DbSet<DatabaseSetting> Settings => Set<DatabaseSetting>();
    public DbSet<ParsedAlertRecord> ParsedAlerts => Set<ParsedAlertRecord>();
    public DbSet<LogbookEntry> LogbookEntries => Set<LogbookEntry>();
    public DbSet<InboxLayout> InboxLayouts => Set<InboxLayout>();
    public DbSet<AgentAnalysisJob> AgentAnalysisJobs => Set<AgentAnalysisJob>();
    public DbSet<AgentAnalysisReviewEvent> AgentAnalysisReviewEvents => Set<AgentAnalysisReviewEvent>();

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
        alert.Ignore(record => record.QueryResultRefreshedAt);
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
        rule.Property(record => record.RuleType).HasMaxLength(64);
        rule.Property(record => record.AlertNameContains).HasMaxLength(256);
        rule.Property(record => record.QueryResultType).HasMaxLength(128);
        rule.Property(record => record.ConditionType).HasMaxLength(64);
        rule.Property(record => record.FailedItemName).HasMaxLength(256);
        rule.Property(record => record.CategoryName).HasMaxLength(256);
        rule.Property(record => record.Tone).HasMaxLength(32);
        rule.Property(record => record.InventoryRole).HasMaxLength(256);
        rule.Property(record => record.AnalysisClassification).HasMaxLength(64);
        rule.HasIndex(record => record.Name).IsUnique();
        rule.HasIndex(record => record.RuleType).IsUnique()
            .HasFilter("[RuleType] = 'InventoryHeartbeatFreshness'");
        rule.HasIndex(record => new { record.Enabled, record.Priority });

        var inventoryEntry = modelBuilder.Entity<ComputerInventoryEntry>();
        inventoryEntry.ToTable("ComputerInventory");
        inventoryEntry.Property(entry => entry.Maintenance).HasDefaultValue(false);
        inventoryEntry.HasKey(entry => new { entry.SubscriptionId, entry.Computer });
        inventoryEntry.Property(entry => entry.SubscriptionId).HasMaxLength(64);
        inventoryEntry.Property(entry => entry.Domain).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.Site).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.ResourceGroup).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.Role).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.PrivateIP).HasMaxLength(256);
        inventoryEntry.Property(entry => entry.LastHeartbeatAt).HasColumnType("datetimeoffset");
        inventoryEntry.Property(entry => entry.NtdsFileSizeInGb).HasColumnType("float");
        inventoryEntry.Property(entry => entry.CertificatesJson).HasDefaultValue("[]");
        inventoryEntry.Property(entry => entry.ServicesJson).HasDefaultValue("[]");
        inventoryEntry.Property(entry => entry.Computer).HasMaxLength(256);

        var parsedAlert = modelBuilder.Entity<ParsedAlertRecord>();
        parsedAlert.ToTable("ParsedAlerts");
        parsedAlert.HasKey(record => record.Id);
        parsedAlert.Property(record => record.Id).ValueGeneratedNever();
        parsedAlert.Property(record => record.AlertId).HasMaxLength(512);
        parsedAlert.Property(record => record.OriginalAlertId).HasMaxLength(512);
        parsedAlert.Property(record => record.Severity).HasMaxLength(64);
        parsedAlert.Property(record => record.MonitorCondition).HasMaxLength(64);
        parsedAlert.Property(record => record.Dimensions).HasColumnType("nvarchar(max)");
        parsedAlert.Property(record => record.SearchQuery).HasColumnType("nvarchar(max)");
        parsedAlert.Property(record => record.QueryResults).HasColumnType("nvarchar(max)");
        parsedAlert.Property(record => record.QueryResultRefreshedAt).HasColumnType("datetimeoffset");
        parsedAlert.Property(record => record.AlertName).HasMaxLength(512);
        parsedAlert.Property(record => record.ResourceGroup).HasMaxLength(256);
        parsedAlert.Property(record => record.ResolvedAt).HasColumnType("datetimeoffset");
        parsedAlert.Property(record => record.InventorySubscriptionId).HasMaxLength(64);
        parsedAlert.Property(record => record.InventoryComputer).HasMaxLength(256);
        parsedAlert.HasOne(record => record.Alert)
            .WithOne()
            .HasForeignKey<ParsedAlertRecord>(record => record.Id)
            .OnDelete(DeleteBehavior.Cascade);
        parsedAlert.HasOne(record => record.InventoryComputerEntry)
            .WithMany(entry => entry.ParsedAlerts)
            .HasForeignKey(record => new { record.InventorySubscriptionId, record.InventoryComputer })
            .OnDelete(DeleteBehavior.SetNull);
        parsedAlert.HasIndex(record => record.AlertId);
        parsedAlert.HasIndex(record => new { record.InventorySubscriptionId, record.InventoryComputer });

        var authenticationUser = modelBuilder.Entity<SqlAuthenticationUser>();
        authenticationUser.ToTable("AuthenticationUsers");
        authenticationUser.HasKey(user => user.Username);
        authenticationUser.Property(user => user.Username).HasMaxLength(128);
        authenticationUser.Property(user => user.PasswordHash).HasMaxLength(512);
        authenticationUser.Property(user => user.Role).HasMaxLength(16);

        var setting = modelBuilder.Entity<DatabaseSetting>();
        setting.ToTable("Settings");
        setting.HasKey(value => value.Name);
        setting.Property(value => value.Name).HasMaxLength(128);
        setting.Property(value => value.JsonValue).HasColumnType("nvarchar(max)");

        var logbookEntry = modelBuilder.Entity<LogbookEntry>();
        logbookEntry.ToTable("LogbookEntries");
        logbookEntry.HasKey(entry => entry.Id);
        logbookEntry.Property(entry => entry.Id).ValueGeneratedNever();
        logbookEntry.Property(entry => entry.CreatedAt).HasColumnType("datetimeoffset");
        logbookEntry.Property(entry => entry.User).HasMaxLength(256);
        logbookEntry.Property(entry => entry.Comment).HasColumnType("nvarchar(max)");
        logbookEntry.HasIndex(entry => entry.CreatedAt);

        var inboxLayout = modelBuilder.Entity<InboxLayout>();
        inboxLayout.ToTable("InboxLayouts");
        inboxLayout.HasKey(layout => layout.Id);
        inboxLayout.Property(layout => layout.Id).UseIdentityColumn();
        inboxLayout.Property(layout => layout.OwnerKey).HasMaxLength(256);
        inboxLayout.Property(layout => layout.Name).HasMaxLength(256);
        inboxLayout.Property(layout => layout.DocumentJson).HasColumnType("nvarchar(max)");
        inboxLayout.Property(layout => layout.CreatedAt).HasColumnType("datetimeoffset");
        inboxLayout.Property(layout => layout.UpdatedAt).HasColumnType("datetimeoffset");
        inboxLayout.HasIndex(layout => layout.OwnerKey).IsUnique();

        var analysisJob = modelBuilder.Entity<AgentAnalysisJob>();
        analysisJob.ToTable("AgentAnalysisJobs");
        analysisJob.HasKey(job => job.Id);
        analysisJob.Property(job => job.Id).ValueGeneratedNever();
        analysisJob.Property(job => job.IdempotencyKey).HasMaxLength(64);
        analysisJob.Property(job => job.Status).HasMaxLength(32);
        analysisJob.Property(job => job.StatusReason).HasMaxLength(1024);
        analysisJob.Property(job => job.Revision).IsConcurrencyToken();
        analysisJob.Property(job => job.Mode).HasMaxLength(32);
        analysisJob.Property(job => job.ExecutionProfile).HasMaxLength(64);
        analysisJob.Property(job => job.PromptVersion).HasMaxLength(64);
        analysisJob.Property(job => job.SnapshotVersion).HasMaxLength(64);
        analysisJob.Property(job => job.ResultContractVersion).HasMaxLength(64);
        analysisJob.Property(job => job.ScopeType).HasMaxLength(32);
        analysisJob.Property(job => job.ScopeValue).HasMaxLength(512);
        analysisJob.Property(job => job.PrimaryEventIdsJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.SnapshotJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.ResultJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.TeamsSummaryJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.DetailedAnalysisJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.SourceIdsJson).HasColumnType("nvarchar(max)");
        analysisJob.Property(job => job.ResultStoredAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.TeamsDeliveryStatus).HasMaxLength(32);
        analysisJob.Property(job => job.TeamsDeliveredAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.SnapshotGeneratedAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.DataFrom).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.DataTo).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.CreatedBy).HasMaxLength(256);
        analysisJob.Property(job => job.CreatedAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.UpdatedAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.StartedAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.CompletedAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.ExpiresAt).HasColumnType("datetimeoffset");
        analysisJob.Property(job => job.ErrorCode).HasMaxLength(128);
        analysisJob.Property(job => job.ErrorMessage).HasMaxLength(4000);
        analysisJob.HasIndex(job => job.AnalysisId);
        analysisJob.HasIndex(job => job.IdempotencyKey)
            .IsUnique()
            .HasFilter("[Status] IN (N'Queued', N'Running', N'Completed')");
        analysisJob.HasIndex(job => new { job.Status, job.CreatedAt });
        analysisJob.HasIndex(job => new { job.CreatedBy, job.CreatedAt });

        var analysisReview = modelBuilder.Entity<AgentAnalysisReviewEvent>();
        analysisReview.ToTable("AgentAnalysisReviewEvents");
        analysisReview.HasKey(entry => entry.Id);
        analysisReview.Property(entry => entry.Id).ValueGeneratedNever();
        analysisReview.Property(entry => entry.Action).HasMaxLength(32);
        analysisReview.Property(entry => entry.Comment).HasMaxLength(4000);
        analysisReview.Property(entry => entry.Actor).HasMaxLength(256);
        analysisReview.Property(entry => entry.CreatedAt).HasColumnType("datetimeoffset");
        analysisReview.HasOne<AgentAnalysisJob>()
            .WithMany()
            .HasForeignKey(entry => entry.AgentAnalysisJobId)
            .OnDelete(DeleteBehavior.Cascade);
        analysisReview.HasIndex(entry => new { entry.AgentAnalysisJobId, entry.CreatedAt });
    }
}