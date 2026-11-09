using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AgentAnalysisReviewStore(
    IDbContextFactory<AlertDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AgentAnalysisReviewEvent>> ListAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentAnalysisReviewEvents
            .AsNoTracking()
            .Where(entry => entry.AgentAnalysisJobId == jobId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToArrayAsync(cancellationToken);
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