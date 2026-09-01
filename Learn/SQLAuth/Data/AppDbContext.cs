using Microsoft.EntityFrameworkCore;
using SQLAuth.Authentication;

namespace SQLAuth.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<ApplicationUser>();
        user.ToTable("Users");
        user.HasKey(candidate => candidate.Username);
        user.Property(candidate => candidate.Username).HasMaxLength(128);
        user.Property(candidate => candidate.PasswordHash).HasMaxLength(512);
        user.Property(candidate => candidate.Role).HasMaxLength(16);

        var note = modelBuilder.Entity<Note>();
        note.ToTable("Notes");
        note.HasKey(candidate => candidate.Id);
        note.Property(candidate => candidate.Text).HasMaxLength(500);
        note.Property(candidate => candidate.OwnerUsername).HasMaxLength(128);
        note.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(candidate => candidate.OwnerUsername)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ApplicationUser
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = UserRoles.User;
}

public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}