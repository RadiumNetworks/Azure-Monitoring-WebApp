using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace UILearning.Data;

public sealed class UiDbContext(DbContextOptions<UiDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<PerformanceMetric> PerformanceMetrics => Set<PerformanceMetric>();
    public DbSet<TimelineEvent> TimelineEvents => Set<TimelineEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Department>().Property(item => item.Name).HasMaxLength(80);
        modelBuilder.Entity<Team>().Property(item => item.Name).HasMaxLength(80);
        modelBuilder.Entity<Person>().Property(item => item.Name).HasMaxLength(100);
        modelBuilder.Entity<Person>().Property(item => item.JobTitle).HasMaxLength(100);
        modelBuilder.Entity<UserProfile>().Property(item => item.DisplayName).HasMaxLength(100);
        modelBuilder.Entity<UserProfile>().Property(item => item.Email).HasMaxLength(200);
        modelBuilder.Entity<UserProfile>().Property(item => item.Department).HasMaxLength(80);
        modelBuilder.Entity<UserProfile>().Property(item => item.TimeZone).HasMaxLength(80);
        modelBuilder.Entity<PerformanceMetric>().Property(item => item.Region).HasMaxLength(40);
        modelBuilder.Entity<TimelineEvent>().Property(item => item.Title).HasMaxLength(160);
        modelBuilder.Entity<TimelineEvent>().Property(item => item.Description).HasMaxLength(500);
        modelBuilder.Entity<TimelineEvent>().Property(item => item.Category).HasMaxLength(40);
    }
}

public sealed class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Team> Teams { get; set; } = [];
}

public sealed class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public List<Person> People { get; set; } = [];
}

public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public int TeamId { get; set; }
    public Team? Team { get; set; }
}

public sealed class UserProfile
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Department { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string TimeZone { get; set; } = "UTC";

    public bool ReceiveReports { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PerformanceMetric
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Region { get; set; } = string.Empty;
    public int HealthScore { get; set; }
    public double ResponseTimeMs { get; set; }
    public double Throughput { get; set; }
}

public sealed class TimelineEvent
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Severity { get; set; }
}