using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public static class AgentAnalysisReviewActions
{
    public const string Accepted = "Accepted";
    public const string Commented = "Commented";
    public const string Rejected = "Rejected";
    public const string HandoffCopied = "HandoffCopied";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Accepted, Commented, Rejected, HandoffCopied],
        StringComparer.Ordinal);
}

public sealed class AgentAnalysisReviewEvent
{
    public Guid Id { get; set; }
    public Guid AgentAnalysisJobId { get; set; }
    public Guid AnalysisId { get; set; }
    public int ResultVersion { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public static AgentAnalysisReviewEvent Create(
        AgentAnalysisJob job,
        string actor,
        string action,
        string? comment,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(job);
        var normalizedActor = actor.Trim();
        var normalizedComment = comment?.Trim() ?? string.Empty;

        if (job.Status != AgentAnalysisJobStatuses.Completed || string.IsNullOrWhiteSpace(job.ResultJson))
        {
            throw new InvalidOperationException("Only a completed analysis result can be reviewed.");
        }

        if (!AgentAnalysisReviewActions.All.Contains(action))
        {
            throw new InvalidOperationException($"Unknown analysis review action '{action}'.");
        }

        if (normalizedActor.Length is 0 or > 256)
        {
            throw new InvalidOperationException("Review actor is required and may contain at most 256 characters.");
        }

        if (normalizedComment.Length > 4000)
        {
            throw new InvalidOperationException("Review comment may contain at most 4000 characters.");
        }

        if (action is AgentAnalysisReviewActions.Commented or AgentAnalysisReviewActions.Rejected &&
            normalizedComment.Length == 0)
        {
            throw new InvalidOperationException("Comment and rejection actions require a review comment.");
        }

        return new AgentAnalysisReviewEvent
        {
            Id = Guid.NewGuid(),
            AgentAnalysisJobId = job.Id,
            AnalysisId = job.AnalysisId,
            ResultVersion = job.ResultVersion,
            Action = action,
            Comment = normalizedComment,
            Actor = normalizedActor,
            CreatedAt = createdAt
        };
    }
}

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