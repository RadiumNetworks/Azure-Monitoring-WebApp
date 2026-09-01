using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace EFCoreLearning.Data;

public sealed class LearningDbContext(DbContextOptions<LearningDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<InventoryItem>();
        item.Property(entity => entity.Name).HasMaxLength(120);
        item.Property(entity => entity.Sku).HasMaxLength(32);
        item.HasIndex(entity => entity.Sku).IsUnique();

        var customer = modelBuilder.Entity<Customer>();
        customer.Property(entity => entity.Name).HasMaxLength(120);
        customer.Property(entity => entity.Email).HasMaxLength(200);
        customer.HasMany(entity => entity.Orders)
            .WithOne(entity => entity.Customer)
            .HasForeignKey(entity => entity.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        var order = modelBuilder.Entity<Order>();
        order.Property(entity => entity.Reference).HasMaxLength(32);
        order.HasIndex(entity => entity.Reference).IsUnique();

        modelBuilder.Entity<Student>().Property(entity => entity.Name).HasMaxLength(120);
        modelBuilder.Entity<Course>().Property(entity => entity.Title).HasMaxLength(120);

        var enrollment = modelBuilder.Entity<Enrollment>();
        enrollment.HasKey(entity => new { entity.StudentId, entity.CourseId });
        enrollment.HasOne(entity => entity.Student)
            .WithMany(entity => entity.Enrollments)
            .HasForeignKey(entity => entity.StudentId);
        enrollment.HasOne(entity => entity.Course)
            .WithMany(entity => entity.Enrollments)
            .HasForeignKey(entity => entity.CourseId);
        enrollment.Property(entity => entity.Grade).HasMaxLength(4);
    }
}

public sealed class InventoryItem
{
    public int Id { get; set; }
    [Required, StringLength(32)] public string Sku { get; set; } = string.Empty;
    [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
    [Range(0, 1_000_000)] public decimal Price { get; set; }
    [Range(0, 1_000_000)] public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Customer
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    [Required, StringLength(32)] public string Reference { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    [Range(0, 1_000_000)] public decimal Total { get; set; }
    public bool IsPaid { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
}

public sealed class Student
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
    public List<Enrollment> Enrollments { get; set; } = [];
}

public sealed class Course
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Title { get; set; } = string.Empty;
    public int Credits { get; set; }
    public List<Enrollment> Enrollments { get; set; } = [];
}

public sealed class Enrollment
{
    public int StudentId { get; set; }
    public Student? Student { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public DateTime EnrolledAt { get; set; }
    public string? Grade { get; set; }
}