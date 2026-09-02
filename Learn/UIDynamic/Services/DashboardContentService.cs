using Microsoft.EntityFrameworkCore;
using UIDynamic.Data;

namespace UIDynamic.Services;

public sealed record DashboardContent(
    IReadOnlyDictionary<string, MetricReading> Metrics,
    IReadOnlyDictionary<string, IReadOnlyList<TrendSample>> Trends,
    IReadOnlyDictionary<string, IReadOnlyList<OperationalAlert>> Alerts,
    IReadOnlyDictionary<string, TeamNote> Notes,
    IReadOnlyDictionary<string, IReadOnlyList<ServiceHealthEntry>> Health)
{
    public static DashboardContent Empty { get; } = new(
        new Dictionary<string, MetricReading>(),
        new Dictionary<string, IReadOnlyList<TrendSample>>(),
        new Dictionary<string, IReadOnlyList<OperationalAlert>>(),
        new Dictionary<string, TeamNote>(),
        new Dictionary<string, IReadOnlyList<ServiceHealthEntry>>());
}

public sealed class DashboardContentService(IDbContextFactory<LayoutDbContext> contextFactory)
{
    public async Task<DashboardContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var metrics = await db.MetricReadings.AsNoTracking().ToListAsync(cancellationToken);
        var trends = await db.TrendSamples.AsNoTracking().ToListAsync(cancellationToken);
        trends = trends.OrderBy(item => item.RecordedAt).ToList();
        var alerts = await db.OperationalAlerts.AsNoTracking().ToListAsync(cancellationToken);
        alerts = alerts.OrderByDescending(item => item.RaisedAt).ToList();
        var notes = await db.TeamNotes.AsNoTracking().ToListAsync(cancellationToken);
        var health = await db.ServiceHealthEntries.AsNoTracking()
            .OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);

        return new DashboardContent(
            metrics.ToDictionary(item => item.DataSourceKey),
            trends.GroupBy(item => item.DataSourceKey)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<TrendSample>)group.ToList()),
            alerts.GroupBy(item => item.DataSourceKey)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<OperationalAlert>)group.ToList()),
            notes.ToDictionary(item => item.DataSourceKey),
            health.GroupBy(item => item.DataSourceKey)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ServiceHealthEntry>)group.ToList()));
    }

    public async Task<TeamNote> SaveTeamNoteAsync(
        string dataSourceKey,
        string content,
        string author,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataSourceKey))
        {
            throw new ArgumentException("A data source key is required.", nameof(dataSourceKey));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var note = await db.TeamNotes.SingleOrDefaultAsync(
            item => item.DataSourceKey == dataSourceKey,
            cancellationToken);

        if (note is null)
        {
            note = new TeamNote { DataSourceKey = dataSourceKey };
            db.TeamNotes.Add(note);
        }

        note.Content = string.IsNullOrWhiteSpace(content) ? "No team note has been entered." : content.Trim();
        note.Author = string.IsNullOrWhiteSpace(author) ? "Dashboard editor" : author.Trim();
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return note;
    }
}