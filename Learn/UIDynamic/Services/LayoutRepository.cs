using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using UIDynamic.Data;
using UIDynamic.Models;

namespace UIDynamic.Services;

public sealed record SavedLayoutInfo(int Id, int Revision, DateTimeOffset UpdatedAt);

public sealed class LayoutRepository(IDbContextFactory<LayoutDbContext> contextFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<DashboardDocument?> LoadAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        ValidateOwnerKey(ownerKey);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.SavedLayouts.AsNoTracking()
            .Where(item => item.OwnerKey == ownerKey)
            .Select(item => item.DocumentJson)
            .SingleOrDefaultAsync(cancellationToken);

        return json is null ? null : JsonSerializer.Deserialize<DashboardDocument>(json, JsonOptions);
    }

    public async Task<SavedLayoutInfo> SaveAsync(
        string ownerKey,
        DashboardDocument document,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerKey(ownerKey);
        var now = DateTimeOffset.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SavedLayouts.SingleOrDefaultAsync(
            item => item.OwnerKey == ownerKey,
            cancellationToken);

        if (entity is null)
        {
            entity = new SavedLayout
            {
                OwnerKey = ownerKey,
                CreatedAt = now,
                Revision = 1
            };
            db.SavedLayouts.Add(entity);
        }
        else
        {
            entity.Revision++;
        }

        entity.Name = string.IsNullOrWhiteSpace(document.Name) ? "Untitled dashboard" : document.Name.Trim();
        entity.DocumentJson = JsonSerializer.Serialize(document, JsonOptions);
        entity.DocumentVersion = document.Version;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return new SavedLayoutInfo(entity.Id, entity.Revision, entity.UpdatedAt);
    }

    private static void ValidateOwnerKey(string ownerKey)
    {
        if (!Guid.TryParse(ownerKey, out _))
        {
            throw new ArgumentException("The layout owner key must be a GUID.", nameof(ownerKey));
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}