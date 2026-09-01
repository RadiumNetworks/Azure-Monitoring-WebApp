using Microsoft.EntityFrameworkCore;

namespace UILearning.Data;

public static class DemoDataSeeder
{
    public static async Task SeedAsync(UiDbContext db)
    {
        if (await db.Departments.AnyAsync())
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();

        var platform = new Department { Name = "Platform" };
        var operations = new Department { Name = "Operations" };
        var experience = new Department { Name = "Experience" };
        var teams = new[]
        {
            new Team { Name = "Cloud Core", Department = platform },
            new Team { Name = "Data Services", Department = platform },
            new Team { Name = "Observability", Department = operations },
            new Team { Name = "Response", Department = operations },
            new Team { Name = "Web Studio", Department = experience },
            new Team { Name = "Research", Department = experience }
        };

        var names = new[]
        {
            "Ava Klein", "Noah Fischer", "Mia Becker", "Leo Wagner", "Ella Hoffmann", "Finn Weber",
            "Lina Schäfer", "Paul Koch", "Emilia Richter", "Ben Wolf", "Ida Neumann", "Luis Schwarz"
        };
        var jobs = new[] { "Engineer", "Analyst", "Product Owner", "Designer" };
        for (var index = 0; index < names.Length; index++)
        {
            teams[index % teams.Length].People.Add(new Person
            {
                Name = names[index],
                JobTitle = jobs[index % jobs.Length]
            });
        }

        db.Departments.AddRange(platform, operations, experience);
        db.Teams.AddRange(teams);
        db.UserProfiles.AddRange(
            new UserProfile { DisplayName = "Ava Klein", Email = "ava@example.test", Department = "Platform", TimeZone = "Europe/Berlin", ReceiveReports = true, UpdatedAt = DateTimeOffset.UtcNow },
            new UserProfile { DisplayName = "Noah Fischer", Email = "noah@example.test", Department = "Operations", TimeZone = "UTC", ReceiveReports = false, UpdatedAt = DateTimeOffset.UtcNow },
            new UserProfile { DisplayName = "Mia Becker", Email = "mia@example.test", Department = "Experience", TimeZone = "America/New_York", ReceiveReports = true, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var random = new Random(42);
        var regions = new[] { "North", "South", "East", "West" };
        var start = DateTimeOffset.UtcNow.Date.AddDays(-83);
        foreach (var team in teams)
        {
            for (var day = 0; day < 84; day++)
            {
                db.PerformanceMetrics.Add(new PerformanceMetric
                {
                    TeamId = team.Id,
                    RecordedAt = start.AddDays(day).AddHours(12),
                    Region = regions[(team.Id + day) % regions.Length],
                    HealthScore = random.Next(48, 100),
                    ResponseTimeMs = Math.Round(60 + random.NextDouble() * 340, 1),
                    Throughput = Math.Round(100 + random.NextDouble() * 900, 1)
                });
            }
        }

        var eventTitles = new[]
        {
            "Release deployed", "Capacity threshold reached", "Database maintenance completed",
            "Incident investigated", "New dashboard published", "Service ownership changed",
            "Security review completed", "Performance regression detected", "Configuration updated"
        };
        var categories = new[] { "Release", "Warning", "Maintenance", "Incident" };
        for (var index = 0; index < 24; index++)
        {
            db.TimelineEvents.Add(new TimelineEvent
            {
                OccurredAt = DateTimeOffset.UtcNow.AddHours(-index * 9 - random.Next(0, 6)),
                Title = eventTitles[index % eventTitles.Length],
                Description = $"Sample database event #{index + 1}. Select the item to see this detail.",
                Category = categories[index % categories.Length],
                Severity = index % 4
            });
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}