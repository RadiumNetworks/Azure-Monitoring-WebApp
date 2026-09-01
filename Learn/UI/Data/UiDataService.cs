using Microsoft.EntityFrameworkCore;

namespace UILearning.Data;

public sealed class UiDataService(IDbContextFactory<UiDbContext> contextFactory)
{
    public async Task<List<Department>> GetOrganizationAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Departments.AsNoTracking()
            .Include(item => item.Teams).ThenInclude(item => item.People)
            .AsSplitQuery()
            .OrderBy(item => item.Name).ToListAsync();
    }

    public async Task<List<UserProfile>> GetProfilesAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.UserProfiles.AsNoTracking().OrderBy(item => item.DisplayName).ToListAsync();
    }

    public async Task<UserProfile> SaveProfileAsync(UserProfile input)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        UserProfile entity;
        if (input.Id == 0)
        {
            entity = new UserProfile();
            db.UserProfiles.Add(entity);
        }
        else
        {
            entity = await db.UserProfiles.FindAsync(input.Id)
                ?? throw new InvalidOperationException("The selected profile no longer exists.");
        }

        entity.DisplayName = input.DisplayName.Trim();
        entity.Email = input.Email.Trim();
        entity.Department = input.Department.Trim();
        entity.TimeZone = input.TimeZone.Trim();
        entity.ReceiveReports = input.ReceiveReports;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteProfileAsync(int id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await db.UserProfiles.Where(item => item.Id == id).ExecuteDeleteAsync();
    }

    public async Task<List<PerformanceMetric>> GetMetricsAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var metrics = await db.PerformanceMetrics.AsNoTracking().Include(item => item.Team).ToListAsync();
        return metrics.OrderBy(item => item.RecordedAt).ToList();
    }

    public async Task<List<TimelineEvent>> GetTimelineAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var events = await db.TimelineEvents.AsNoTracking().ToListAsync();
        return events.OrderByDescending(item => item.OccurredAt).ToList();
    }
}