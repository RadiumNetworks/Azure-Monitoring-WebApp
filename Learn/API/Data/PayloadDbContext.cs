using Microsoft.EntityFrameworkCore;

namespace APILearning.Data;

public sealed class PayloadDbContext(DbContextOptions<PayloadDbContext> options) : DbContext(options)
{
    public DbSet<PayloadRecord> Payloads => Set<PayloadRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payload = modelBuilder.Entity<PayloadRecord>();
        payload.ToTable("Payloads");
        payload.HasKey(item => item.Id);
        payload.Property(item => item.Name).HasMaxLength(200);
        payload.Property(item => item.Category).HasMaxLength(100);
        payload.Property(item => item.Source).HasMaxLength(200);
        payload.Property(item => item.Summary).HasMaxLength(500);
        payload.Property(item => item.RawJson).HasColumnType("TEXT");
        payload.HasIndex(item => item.ReceivedAt);
        payload.HasIndex(item => item.Category);
        payload.HasIndex(item => item.Source);
        payload.HasIndex(item => item.Severity);
    }
}

public sealed class PayloadRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Severity { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string RawJson { get; set; } = "{}";
}