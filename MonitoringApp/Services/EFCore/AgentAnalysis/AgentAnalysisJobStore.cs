using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AgentAnalysisJobStore(
    IDbContextFactory<AlertDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    public async Task<AgentAnalysisJob> CreateAsync(
        AgentAnalysisJobCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        ArgumentNullException.ThrowIfNull(request.ExecutionState);

        var createdBy = request.CreatedBy.Trim();
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Length > 256)
        {
            throw new AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode.CreatedByInvalid, "CreatedBy is required and may contain at most 256 characters.");
        }

        if (!request.ExecutionState.IsEnabled)
        {
            throw new AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode.ExecutionDisabled, "An analysis job cannot be queued for a disabled execution mode.");
        }

        var createdAt = timeProvider.GetUtcNow();
        if (request.ExpiresAt <= createdAt)
        {
            throw new AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode.ExpiryInvalid, "ExpiresAt must be later than CreatedAt.");
        }

        var primaryEventIds = request.Snapshot.PrimaryEvidence
            .Select(evidence => evidence.EventId)
            .Distinct()
            .Order()
            .ToArray();
        if (primaryEventIds.Length == 0)
        {
            throw new AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode.EvidenceRequired, "An analysis job requires at least one primary evidence event.");
        }

        var idempotencyKey = AgentAnalysisJobIdempotency.Compute(
            request.Snapshot,
            AlertAgentPrompt.SnapshotPromptVersion,
            createdBy);

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var previousKeys = request.LegacyActors.Select(actor =>
            AgentAnalysisJobIdempotency.Compute(request.Snapshot, AlertAgentPrompt.SnapshotPromptVersion, actor)).ToArray();
        if (previousKeys.Length > 0)
        {
            var previous = await context.AgentAnalysisJobs.AsNoTracking().Where(job =>
                previousKeys.Contains(job.IdempotencyKey) &&
                (job.Status == AgentAnalysisJobStatuses.Queued || job.Status == AgentAnalysisJobStatuses.Running ||
                 job.Status == AgentAnalysisJobStatuses.Completed))
                .OrderByDescending(job => job.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (previous is not null)
                return previous;
        }
        var existingJob = await FindReusableJobAsync(context, idempotencyKey, cancellationToken);
        if (existingJob is not null)
        {
            return existingJob;
        }

        var job = new AgentAnalysisJob
        {
            Id = Guid.NewGuid(),
            AnalysisId = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            ResultVersion = 1,
            Status = AgentAnalysisJobStatuses.Queued,
            StatusReason = "Analysis queued.",
            Revision = 1,
            Mode = request.ExecutionState.Mode,
            ExecutionProfile = request.ExecutionState.Profile ?? string.Empty,
            PromptVersion = AlertAgentPrompt.SnapshotPromptVersion,
            SnapshotVersion = IncidentAnalysisSnapshot.CurrentVersion,
            ResultContractVersion = AgentAnalysisResultContract.CurrentVersion,
            ScopeType = request.Snapshot.Scope.Type.ToString(),
            ScopeValue = Normalize(request.Snapshot.Scope.Value, 512),
            PrimaryEventIdsJson = JsonSerializer.Serialize(primaryEventIds),
            SnapshotJson = request.Snapshot.ToCanonicalJson(),
            SnapshotGeneratedAt = request.Snapshot.GeneratedAt,
            DataFrom = request.Snapshot.DataFrom,
            DataTo = request.Snapshot.DataTo,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ExpiresAt = request.ExpiresAt
        };

        context.AgentAnalysisJobs.Add(job);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException)
        {
            context.Entry(job).State = EntityState.Detached;
            var concurrentJob = await FindReusableJobAsync(context, idempotencyKey, cancellationToken);
            if (concurrentJob is not null)
            {
                return concurrentJob;
            }

            throw;
        }
    }

    public async Task<AgentAnalysisJob?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentAnalysisJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentAnalysisJob>> ListAsync(
        string createdBy,
        bool includeAll,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return await ListForActorsAsync([createdBy], includeAll, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentAnalysisJob>> ListForActorsAsync(
        IReadOnlyList<string> actors, bool includeAll, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (actors.Count == 0 || actors.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("CreatedBy is required.", nameof(actors));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentAnalysisJobs
            .AsNoTracking()
            .Where(job => includeAll || actors.Contains(job.CreatedBy))
            .OrderByDescending(job => job.CreatedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset expiresAtOrBefore,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentAnalysisJobs
            .Where(job => job.ExpiresAt <= expiresAtOrBefore)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<AgentAnalysisJob> TransitionAsync(
        Guid id,
        int expectedRevision,
        string targetStatus,
        string? reason = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default) =>
        UpdateJobAsync(id, expectedRevision, job => AgentAnalysisJobStateMachine.Transition(
            job,
            targetStatus,
            timeProvider.GetUtcNow(),
            reason,
            errorCode,
            errorMessage), cancellationToken);

    public async Task<AgentAnalysisJob> CompleteAsync(
        Guid id,
        int expectedRevision,
        AgentAnalysisResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        return await UpdateJobAsync(id, expectedRevision,
            job => AgentAnalysisJobResultPersistence.Store(job, result, timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private async Task<AgentAnalysisJob> UpdateJobAsync(
        Guid id,
        int expectedRevision,
        Action<AgentAnalysisJob> update,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.AgentAnalysisJobs.SingleOrDefaultAsync(
            job => job.Id == id,
            cancellationToken) ?? throw new KeyNotFoundException($"Agent analysis job '{id}' was not found.");

        if (job.Revision != expectedRevision)
        {
            throw new AgentAnalysisJobConcurrencyException(
                $"Agent analysis job '{id}' has revision {job.Revision}; expected {expectedRevision}.");
        }

        update(job);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AgentAnalysisJobConcurrencyException(
                $"Agent analysis job '{id}' was changed by another operation.",
                exception);
        }

        return job;
    }

    private static string Normalize(string value, int maxLength)
    {
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static Task<AgentAnalysisJob?> FindReusableJobAsync(
        AlertDbContext context,
        string idempotencyKey,
        CancellationToken cancellationToken) => context.AgentAnalysisJobs
        .AsNoTracking()
        .Where(job => job.IdempotencyKey == idempotencyKey &&
            (job.Status == AgentAnalysisJobStatuses.Queued ||
             job.Status == AgentAnalysisJobStatuses.Running ||
             job.Status == AgentAnalysisJobStatuses.Completed))
        .OrderByDescending(job => job.CreatedAt)
        .FirstOrDefaultAsync(cancellationToken);
}
