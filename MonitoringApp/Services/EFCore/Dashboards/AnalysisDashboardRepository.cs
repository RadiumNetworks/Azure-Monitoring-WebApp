using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AnalysisDashboardRepository(IDbContextFactory<AlertDbContext> contextFactory)
{
    public async Task<(AnalysisDashboardCollection? Collection, int Revision)> LoadAsync(
        string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ownerKey = DashboardOwnerKeys.Normalize(ownerKey, "dashboard");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.AnalysisDashboards.AsNoTracking()
            .Where(item => item.OwnerKey == ownerKey)
            .Select(item => new { item.DocumentJson, item.Revision })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
            return (null, 0);
        try
        {
            return (AnalysisDashboardCollection.Deserialize(stored.DocumentJson), stored.Revision);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The saved analysis dashboard document is invalid.", exception);
        }
    }

    public async Task<AnalysisDashboardSaveResult> SaveAsync(
        string ownerKey,
        AnalysisDashboardCollection collection,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ownerKey = DashboardOwnerKeys.Normalize(ownerKey, "dashboard");
        var now = DateTimeOffset.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var retainedIds = collection.Dashboards.Select(item => item.Id).ToArray();
        if (await context.AnalysisDashboardTemplates.AnyAsync(
            item => item.Published && item.UpdatedBy == ownerKey && !retainedIds.Contains(item.Id), cancellationToken))
            throw new InvalidOperationException("Withdraw the published dashboard before deleting it.");
        var entity = await context.AnalysisDashboards.SingleOrDefaultAsync(
            item => item.OwnerKey == ownerKey,
            cancellationToken);
        if (entity is null)
        {
            if (expectedRevision != 0)
                throw new DbUpdateConcurrencyException("The analysis dashboard was changed or removed.");
            entity = new AnalysisDashboard
            {
                OwnerKey = ownerKey,
                CreatedAt = now,
                Revision = 1
            };
            context.AnalysisDashboards.Add(entity);
        }
        else
        {
            if (entity.Revision != expectedRevision)
                throw new DbUpdateConcurrencyException("The analysis dashboard was changed by another session.");
            entity.Revision++;
        }

        entity.Name = collection.Active.Document.Name;
        entity.DocumentJson = collection.Serialize();
        entity.DocumentVersion = collection.Version;
        entity.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
        return new(entity.Revision, entity.UpdatedAt);
    }

    public async Task<IReadOnlyList<AnalysisDashboardTemplate>> ListPublishedTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AnalysisDashboardTemplates.AsNoTracking()
            .Where(template => template.Published)
            .OrderBy(template => template.Name)
            .ThenByDescending(template => template.Version)
            .ToArrayAsync(cancellationToken);
    }

}
