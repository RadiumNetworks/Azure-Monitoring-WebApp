using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using APILearning.Api;

namespace APILearning.Data;

public static class PayloadDemoData
{
    public static async Task SeedAsync(PayloadDbContext db)
    {
        if (await db.Payloads.AnyAsync()) return;

        var samples = new[]
        {
            """{"name":"High CPU","category":"Performance","source":"web-01","severity":3,"summary":"CPU exceeded 90%","value":94.2,"tags":["production","compute"]}""",
            """{"name":"Deployment completed","category":"Release","source":"pipeline","severity":0,"summary":"Version 4.8 deployed successfully","version":"4.8.0"}""",
            """{"name":"Queue backlog","category":"Capacity","source":"orders-worker","severity":2,"summary":"Pending messages above target","queueDepth":1842}""",
            """{"name":"Login anomaly","category":"Security","source":"identity","severity":4,"summary":"Unusual sign-in location detected","country":"DE"}""",
            """{"name":"Backup verified","category":"Maintenance","source":"database","severity":0,"summary":"Nightly backup integrity check passed","durationSeconds":186}"""
        };

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < samples.Length; index++)
        {
            using var document = JsonDocument.Parse(samples[index]);
            var root = document.RootElement;
            db.Payloads.Add(new PayloadRecord
            {
                Id = Guid.NewGuid(),
                ReceivedAt = now.AddMinutes(-index * 37),
                OccurredAt = now.AddMinutes(-index * 37 - 2),
                Name = root.GetProperty("name").GetString()!,
                Category = root.GetProperty("category").GetString()!,
                Source = root.GetProperty("source").GetString()!,
                Severity = root.GetProperty("severity").GetInt32(),
                Summary = root.GetProperty("summary").GetString()!,
                RawJson = JsonSerializer.Serialize(root, JsonOptions.Indented)
            });
        }
        await db.SaveChangesAsync();
    }
}