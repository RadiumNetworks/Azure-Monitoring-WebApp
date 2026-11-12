using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AgentAnalysisReviewStore(
    IDbContextFactory<AlertDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IdentityDirectoryOptions? directory = null, ApplicationAuthenticationOptions? authentication = null)
{
    public async Task<IReadOnlyList<AgentAnalysisReviewEvent>> ListAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await context.AgentAnalysisReviewEvents
            .AsNoTracking()
            .Where(entry => entry.AgentAnalysisJobId == jobId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToArrayAsync(cancellationToken);
        var labels = await UserReferenceQueries.LabelsAsync(context, entries.Select(entry => entry.Actor),
            directory?.Enabled == true, cancellationToken, authentication?.IsOpen != true);
        foreach (var entry in entries) entry.ActorDisplayName = labels.GetValueOrDefault(entry.Actor);
        return entries;
    }

    public async Task<AgentAnalysisReviewEvent> AddAsync(
        AgentAnalysisJob job,
        string actor,
        string action,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var entry = AgentAnalysisReviewEvent.Create(
            job,
            actor,
            action,
            comment,
            timeProvider.GetUtcNow());

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        context.AgentAnalysisReviewEvents.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }
}