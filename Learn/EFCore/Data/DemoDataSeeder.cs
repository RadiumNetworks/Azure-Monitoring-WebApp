using Microsoft.EntityFrameworkCore;

namespace EFCoreLearning.Data;

public static class DemoDataSeeder
{
    public static async Task SeedAsync(LearningDbContext db)
    {
        if (await db.InventoryItems.AnyAsync()) return;

        await using var transaction = await db.Database.BeginTransactionAsync();
        db.InventoryItems.AddRange(
            new InventoryItem { Sku = "MON-27", Name = "27-inch Monitor", Price = 329.90m, Stock = 18 },
            new InventoryItem { Sku = "KEY-MX", Name = "Mechanical Keyboard", Price = 119.00m, Stock = 42 },
            new InventoryItem { Sku = "HUB-8P", Name = "USB-C Hub", Price = 79.50m, Stock = 0, IsActive = false },
            new InventoryItem { Sku = "CAM-4K", Name = "4K Webcam", Price = 149.90m, Stock = 11 });

        var customers = new[]
        {
            new Customer { Name = "Northwind Labs", Email = "orders@northwind.test" },
            new Customer { Name = "Contoso Research", Email = "buying@contoso.test" },
            new Customer { Name = "Fabrikam Studio", Email = "hello@fabrikam.test" }
        };
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        var random = new Random(42);
        for (var index = 1; index <= 12; index++)
        {
            db.Orders.Add(new Order
            {
                Reference = $"ORD-{2026000 + index}",
                CustomerId = customers[index % customers.Length].Id,
                CreatedAt = DateTime.UtcNow.Date.AddDays(-index * 3),
                Total = random.Next(40, 850) + .90m,
                IsPaid = index % 3 != 0
            });
        }

        var students = new[]
        {
            new Student { Name = "Ava Klein" }, new Student { Name = "Noah Fischer" },
            new Student { Name = "Mia Becker" }, new Student { Name = "Leo Wagner" }
        };
        var courses = new[]
        {
            new Course { Title = "EF Core Fundamentals", Credits = 4 },
            new Course { Title = "Relational Modeling", Credits = 3 },
            new Course { Title = "LINQ Query Design", Credits = 4 },
            new Course { Title = "Database Performance", Credits = 5 }
        };
        db.Students.AddRange(students);
        db.Courses.AddRange(courses);
        await db.SaveChangesAsync();

        for (var studentIndex = 0; studentIndex < students.Length; studentIndex++)
        {
            for (var courseIndex = 0; courseIndex < courses.Length; courseIndex++)
            {
                if ((studentIndex + courseIndex) % 3 != 0)
                {
                    db.Enrollments.Add(new Enrollment
                    {
                        StudentId = students[studentIndex].Id,
                        CourseId = courses[courseIndex].Id,
                        EnrolledAt = DateTime.UtcNow.Date.AddDays(-30 - studentIndex * 7),
                        Grade = new[] { "A", "B+", "B", "A-" }[(studentIndex + courseIndex) % 4]
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}