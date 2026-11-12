using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MonitoringApp.Tests;

public sealed class AgentAnalysisPersistenceTests
{
    [Fact]
    public async Task VerifiedLegacyActorsPreserveJobIdempotencyAfterIdentityActivation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.Jobs.CreateAsync(fixture.Request);
        var stable = DirectoryIdentity.OwnerKey(Guid.NewGuid());
        var reused = await fixture.Jobs.CreateAsync(fixture.Request with { CreatedBy = stable, LegacyActors = ["operator"] });
        Assert.Equal(old.Id, reused.Id);
        Assert.Equal(old.IdempotencyKey, reused.IdempotencyKey);
        Assert.Equal("operator", reused.CreatedBy);
        Assert.Single(await fixture.Jobs.ListForActorsAsync([stable, "operator"], false));
        Assert.Empty(await fixture.Jobs.ListForActorsAsync(["other"], false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task StateAndResultValidationNeverWritesOrChangesStoredJob(int scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(fixture.Request);
        if (scenario >= 2)
        {
            fixture.Clock.Advance();
            job = await fixture.Jobs.TransitionAsync(job.Id, job.Revision, AgentAnalysisJobStatuses.Running);
        }
        var before = JsonSerializer.Serialize(job);
        var attempts = 0;
        fixture.Interceptor.BeforeSave = () =>
        {
            attempts++;
            throw new InvalidOperationException("Validation must not save.");
        };
        Task<AgentAnalysisJob> Update() => scenario switch
        {
            0 => fixture.Jobs.TransitionAsync(job.Id, job.Revision, "Unknown <status>"),
            1 => fixture.Jobs.CompleteAsync(job.Id, job.Revision, Result(job)),
            2 => fixture.Jobs.TransitionAsync(job.Id, job.Revision, AgentAnalysisJobStatuses.Failed),
            3 => fixture.Jobs.CompleteAsync(job.Id, job.Revision, Result(job) with { AnalysisId = Guid.Empty }),
            4 => fixture.Jobs.CompleteAsync(job.Id, job.Revision, Result(job) with { ResultVersion = job.ResultVersion + 1 }),
            _ => fixture.Jobs.CompleteAsync(job.Id, job.Revision, Result(job) with { ContractVersion = "custom <contract>" })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(Update);
        Assert.Equal(0, attempts);
        Assert.Equal(before, JsonSerializer.Serialize(await fixture.Jobs.GetAsync(job.Id)));
    }

    [Theory]
    [InlineData(0, AgentAnalysisJobValidationCode.CreatedByInvalid, "CreatedBy is required and may contain at most 256 characters.")]
    [InlineData(1, AgentAnalysisJobValidationCode.CreatedByInvalid, "CreatedBy is required and may contain at most 256 characters.")]
    [InlineData(2, AgentAnalysisJobValidationCode.ExecutionDisabled, "An analysis job cannot be queued for a disabled execution mode.")]
    [InlineData(3, AgentAnalysisJobValidationCode.ExpiryInvalid, "ExpiresAt must be later than CreatedAt.")]
    [InlineData(4, AgentAnalysisJobValidationCode.ExpiryInvalid, "ExpiresAt must be later than CreatedAt.")]
    [InlineData(5, AgentAnalysisJobValidationCode.EvidenceRequired, "An analysis job requires at least one primary evidence event.")]
    public async Task CreateValidationKeepsDiagnosticsAndNeverWrites(int scenario, AgentAnalysisJobValidationCode code, string message)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request;
        request = scenario switch
        {
            0 => request with { CreatedBy = " " },
            1 => request with { CreatedBy = new string('a', 257) },
            2 => request with { ExecutionState = request.ExecutionState with { IsEnabled = false } },
            3 => request with { ExpiresAt = fixture.Clock.GetUtcNow() },
            4 => request with { ExpiresAt = fixture.Clock.GetUtcNow().AddTicks(-1) },
            _ => request with { Snapshot = request.Snapshot with { PrimaryEvidence = [] } }
        };
        fixture.Interceptor.BeforeSave = () => throw new InvalidOperationException("Validation must not write.");
        var exception = await Assert.ThrowsAsync<AgentAnalysisJobValidationException>(() => fixture.Jobs.CreateAsync(request));
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
        Assert.Empty(await fixture.Jobs.ListAsync("operator", true));
    }

    [Fact]
    public async Task CreateKeepsExactNormalizedActorAndExpiryLimits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(fixture.Request with
        {
            CreatedBy = $" {new string('a', 256)} ", ExpiresAt = fixture.Clock.GetUtcNow().AddTicks(1)
        });
        Assert.Equal(256, job.CreatedBy.Length);
        Assert.Equal(fixture.Clock.GetUtcNow().AddTicks(1), job.ExpiresAt);
    }

    [Fact]
    public async Task ReusesQueuedRunningAndCompletedJobsWithoutOverwritingTheirSnapshots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Jobs.CreateAsync(fixture.Request);
        var retry = fixture.Request with
        {
            CreatedBy = " OPERATOR ",
            Snapshot = fixture.Request.Snapshot with
            {
                GeneratedAt = fixture.Request.Snapshot.GeneratedAt.AddMinutes(1),
                PrimaryEvidence = fixture.Request.Snapshot.PrimaryEvidence.Select(evidence =>
                    evidence with { AgeAtSnapshot = evidence.AgeAtSnapshot.Add(TimeSpan.FromMinutes(1)) }).ToArray()
            }
        };
        Assert.Equal(first.Id, (await fixture.Jobs.CreateAsync(retry)).Id);
        fixture.Clock.Advance();
        var running = await fixture.Jobs.TransitionAsync(first.Id, first.Revision, AgentAnalysisJobStatuses.Running);
        Assert.Equal(first.Id, (await fixture.Jobs.CreateAsync(retry)).Id);
        fixture.Clock.Advance();
        var completed = await fixture.Jobs.CompleteAsync(running.Id, running.Revision, Result(running));
        var reused = await fixture.Jobs.CreateAsync(retry);
        Assert.Equal(completed.Id, reused.Id);
        Assert.Equal(completed.Revision, reused.Revision);
        Assert.Equal(completed.ResultJson, reused.ResultJson);
        Assert.Equal(first.SnapshotJson, reused.SnapshotJson);
        Assert.Equal("operator", reused.CreatedBy);
        Assert.Single(await fixture.Jobs.ListAsync("operator", includeAll: false));
    }

    [Theory]
    [InlineData(AgentAnalysisJobStatuses.Failed)]
    [InlineData(AgentAnalysisJobStatuses.Cancelled)]
    [InlineData(AgentAnalysisJobStatuses.Expired)]
    public async Task TerminalFailuresAllowANewJobWithTheSameKey(string status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Jobs.CreateAsync(fixture.Request);
        if (status == AgentAnalysisJobStatuses.Failed)
            original = await fixture.Jobs.TransitionAsync(original.Id, original.Revision, AgentAnalysisJobStatuses.Running);
        await fixture.Jobs.TransitionAsync(original.Id, original.Revision, status,
            errorCode: "ProviderTimeout", errorMessage: "Provider unavailable.");
        fixture.Clock.Advance();
        var retry = await fixture.Jobs.CreateAsync(fixture.Request);
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(original.IdempotencyKey, retry.IdempotencyKey);
        Assert.Equal(AgentAnalysisJobStatuses.Queued, retry.Status);
        Assert.Equal(1, retry.Revision);
        Assert.Equal(retry.Id, (await fixture.Jobs.CreateAsync(fixture.Request)).Id);
        Assert.Equal(2, (await fixture.Jobs.ListAsync("operator", includeAll: false)).Count);
    }

    [Fact]
    public async Task ConcurrentCreationReturnsTheUniqueIndexWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        AgentAnalysisJob? winner = null;
        fixture.Interceptor.BeforeSave = async () => winner = await fixture.Jobs.CreateAsync(fixture.Request);
        var actual = await fixture.Jobs.CreateAsync(fixture.Request);
        Assert.NotNull(winner);
        Assert.Equal(winner.Id, actual.Id);
        Assert.Equal(winner.AnalysisId, actual.AnalysisId);
        Assert.Single(await fixture.Jobs.ListAsync("operator", includeAll: false));
    }

    [Fact]
    public async Task CreationDoesNotHideWriteFailuresWithoutAReusableJob()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync(
            "CREATE TRIGGER FailJobCreate BEFORE INSERT ON AgentAnalysisJobs BEGIN SELECT RAISE(ABORT, 'job create failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Jobs.CreateAsync(fixture.Request));
        Assert.Empty(await fixture.Jobs.ListAsync("operator", includeAll: true));
        await fixture.ExecuteAsync("DROP TRIGGER FailJobCreate;");
        Assert.Equal(1, (await fixture.Jobs.CreateAsync(fixture.Request)).Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RevisionChecksProtectTransitionsAndResultsBeforeAndDuringSave(bool complete, bool race)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Jobs.CreateAsync(fixture.Request);
        if (complete)
            original = await fixture.Jobs.TransitionAsync(original.Id, original.Revision, AgentAnalysisJobStatuses.Running);
        fixture.Clock.Advance();
        if (race)
            fixture.Interceptor.BeforeSave = async () =>
            {
                await using var context = fixture.Factory.CreateDbContext();
                await context.AgentAnalysisJobs.Where(job => job.Id == original.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(job => job.Revision, job => job.Revision + 1));
            };
        var revision = race ? original.Revision : original.Revision - 1;
        var exception = await Assert.ThrowsAsync<AgentAnalysisJobConcurrencyException>(() =>
            complete ? fixture.Jobs.CompleteAsync(original.Id, revision, Result(original)) :
                fixture.Jobs.TransitionAsync(original.Id, revision, AgentAnalysisJobStatuses.Running));
        if (race)
            Assert.IsType<DbUpdateConcurrencyException>(exception.InnerException);
        else
            Assert.Null(exception.InnerException);
        var stored = await fixture.Jobs.GetAsync(original.Id);
        Assert.NotNull(stored);
        Assert.Equal(original.Revision + (race ? 1 : 0), stored.Revision);
        Assert.Equal(original.Status, stored.Status);
        Assert.Equal(original.UpdatedAt, stored.UpdatedAt);
        Assert.Equal(original.SnapshotJson, stored.SnapshotJson);
        Assert.Empty(stored.ResultJson);
        Assert.Empty(stored.TeamsSummaryJson);
        Assert.Empty(stored.DetailedAnalysisJson);
        Assert.Equal("[]", stored.SourceIdsJson);
        Assert.Null(stored.ResultStoredAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedUpdatesLeaveStateAndResultUnchangedUntilSuccessfulRetry(bool complete)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Jobs.CreateAsync(fixture.Request);
        if (complete)
            original = await fixture.Jobs.TransitionAsync(original.Id, original.Revision, AgentAnalysisJobStatuses.Running);
        fixture.Clock.Advance();
        await fixture.ExecuteAsync(
            "CREATE TRIGGER FailJobUpdate BEFORE UPDATE ON AgentAnalysisJobs BEGIN SELECT RAISE(ABORT, 'job update failed'); END;");
        Task<AgentAnalysisJob> Update() => complete
            ? fixture.Jobs.CompleteAsync(original.Id, original.Revision, Result(original))
            : fixture.Jobs.TransitionAsync(original.Id, original.Revision, AgentAnalysisJobStatuses.Running);
        await Assert.ThrowsAsync<DbUpdateException>(() => Update());
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await fixture.Jobs.GetAsync(original.Id)));
        await fixture.ExecuteAsync("DROP TRIGGER FailJobUpdate;");
        var updated = await Update();
        Assert.Equal(original.Revision + 1, updated.Revision);
        Assert.Equal(complete ? AgentAnalysisJobStatuses.Completed : AgentAnalysisJobStatuses.Running, updated.Status);
        if (complete)
        {
            Assert.Equal(Result(original).ToCanonicalJson(), updated.ResultJson);
            Assert.Equal(fixture.Clock.GetUtcNow(), updated.ResultStoredAt);
            Assert.Equal(AgentAnalysisDeliveryStatuses.NotRequested, updated.TeamsDeliveryStatus);
            Assert.Equal(new[] { "source-a", "source-b" }, JsonSerializer.Deserialize<string[]>(updated.SourceIdsJson));
        }
    }

    [Fact]
    public async Task ReviewsAppendVersionBoundHistoryWithoutChangingTheJobOrEarlierEntries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.CompleteAsync(fixture.Request);
        var before = JsonSerializer.Serialize(job);
        var actions = new[]
        {
            AgentAnalysisReviewActions.Accepted, AgentAnalysisReviewActions.Commented,
            AgentAnalysisReviewActions.Rejected, AgentAnalysisReviewActions.HandoffCopied
        };
        foreach (var action in actions)
        {
            fixture.Clock.Advance();
            var entry = await fixture.Reviews.AddAsync(job, " reviewer ", action, " Evidence checked ");
            Assert.Equal(job.AnalysisId, entry.AnalysisId);
            Assert.Equal(job.ResultVersion, entry.ResultVersion);
            Assert.Equal(fixture.Clock.GetUtcNow(), entry.CreatedAt);
            entry.Actor = "modified detached entry";
            entry.Comment = "modified detached comment";
        }
        var entries = await fixture.Reviews.ListAsync(job.Id);
        Assert.Equal(actions.Reverse(), entries.Select(entry => entry.Action));
        Assert.Equal(4, entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.Equal("reviewer", entry.Actor);
            Assert.Equal("Evidence checked", entry.Comment);
            Assert.Equal(job.Id, entry.AgentAnalysisJobId);
            Assert.Equal(job.AnalysisId, entry.AnalysisId);
            Assert.Equal(job.ResultVersion, entry.ResultVersion);
        });
        Assert.Equal(before, JsonSerializer.Serialize(await fixture.Jobs.GetAsync(job.Id)));
    }

    [Fact]
    public async Task InvalidAndFailedReviewsNeverModifyTheJobOrCreatePartialHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.Jobs.CreateAsync(fixture.Request);
        var invalidReview = await Assert.ThrowsAsync<AgentAnalysisReviewValidationException>(() => fixture.Reviews.AddAsync(
            queued, "reviewer", AgentAnalysisReviewActions.Accepted, null));
        Assert.Equal(AgentAnalysisReviewValidationCode.ResultRequired, invalidReview.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(invalidReview);
        Assert.Empty(await fixture.Reviews.ListAsync(queued.Id));
        var completed = await fixture.CompleteAsync(fixture.Request);
        await fixture.ExecuteAsync(
            "CREATE TRIGGER FailReview BEFORE INSERT ON AgentAnalysisReviewEvents BEGIN SELECT RAISE(ABORT, 'review failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Reviews.AddAsync(
            completed, "reviewer", AgentAnalysisReviewActions.Commented, "Evidence checked"));
        Assert.Empty(await fixture.Reviews.ListAsync(completed.Id));
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(await fixture.Jobs.GetAsync(completed.Id)));
        await fixture.ExecuteAsync("DROP TRIGGER FailReview;");
        await fixture.Reviews.AddAsync(completed, "reviewer", AgentAnalysisReviewActions.Commented, "Evidence checked");
        Assert.Single(await fixture.Reviews.ListAsync(completed.Id));
    }

    [Fact]
    public async Task RetentionUsesInclusiveExpiryAndCascadesReviewsButPreservesNewerRunningJobs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cutoff = fixture.Clock.GetUtcNow().AddMinutes(10);
        var early = await fixture.Jobs.CreateAsync(fixture.Request with { ExpiresAt = cutoff.AddTicks(-1) });
        var boundary = await fixture.CompleteAsync(fixture.Request with
        {
            Snapshot = fixture.Request.Snapshot with { MissingEvidence = ["boundary"] },
            ExpiresAt = cutoff
        });
        await fixture.Reviews.AddAsync(boundary, "reviewer", AgentAnalysisReviewActions.Accepted, null);
        var survivor = await fixture.Jobs.CreateAsync(fixture.Request with
        {
            Snapshot = fixture.Request.Snapshot with { MissingEvidence = ["survivor"] },
            ExpiresAt = cutoff.AddTicks(1)
        });
        survivor = await fixture.Jobs.TransitionAsync(survivor.Id, survivor.Revision, AgentAnalysisJobStatuses.Running);
        Assert.Equal(2, await fixture.Jobs.DeleteExpiredAsync(cutoff));
        Assert.Null(await fixture.Jobs.GetAsync(early.Id));
        Assert.Null(await fixture.Jobs.GetAsync(boundary.Id));
        Assert.Empty(await fixture.Reviews.ListAsync(boundary.Id));
        Assert.Equal(JsonSerializer.Serialize(survivor), JsonSerializer.Serialize(await fixture.Jobs.GetAsync(survivor.Id)));
        Assert.Equal(0, await fixture.Jobs.DeleteExpiredAsync(cutoff));
    }

    [Fact]
    public async Task ListingKeepsOwnerFilteringLimitAndNewestFirstOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.Jobs.CreateAsync(fixture.Request);
        fixture.Clock.Advance();
        var other = await fixture.Jobs.CreateAsync(fixture.Request with { CreatedBy = "other" });
        Assert.Equal(own.Id, Assert.Single(await fixture.Jobs.ListAsync("operator", includeAll: false)).Id);
        Assert.Equal(other.Id, Assert.Single(await fixture.Jobs.ListAsync("operator", includeAll: true, limit: 1)).Id);
        Assert.Empty(await fixture.Jobs.ListAsync("unknown", includeAll: false));
        Assert.Null(await fixture.Jobs.GetAsync(Guid.NewGuid()));
    }

    private static AgentAnalysisResult Result(AgentAnalysisJob job) =>
        AgentAnalysisJobStateMachineTests.CreateResult(job.AnalysisId, job.ResultVersion);

    private sealed class Fixture(SqliteConnection connection, Factory factory, SaveInterceptor interceptor) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public SaveInterceptor Interceptor { get; } = interceptor;
        public Clock Clock { get; } = new();
        public AgentAnalysisJobStore Jobs => new(Factory, Clock);
        public AgentAnalysisReviewStore Reviews => new(Factory, Clock);
        public AgentAnalysisJobCreateRequest Request => new(
            snapshot, new(1, AgentAnalysisExecutionModes.CopilotPrompt, null, true, null),
            "operator", Clock.GetUtcNow().AddHours(1));
        private readonly IncidentAnalysisSnapshot snapshot = AgentAnalysisJobApiTests.CreateSnapshot("subscription-a");

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var interceptor = new SaveInterceptor();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, TimestampModelCustomizer>()
                .AddInterceptors(interceptor).Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (statement.Contains("\"AgentAnalysisJobs\"", StringComparison.Ordinal) ||
                    statement.Contains("\"AgentAnalysisReviewEvents\"", StringComparison.Ordinal))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
            return new(connection, factory, interceptor);
        }

        public async Task<AgentAnalysisJob> CompleteAsync(AgentAnalysisJobCreateRequest request)
        {
            var job = await Jobs.CreateAsync(request);
            job = await Jobs.TransitionAsync(job.Id, job.Revision, AgentAnalysisJobStatuses.Running);
            return await Jobs.CompleteAsync(job.Id, job.Revision, Result(job));
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var context = Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddMinutes(1);
    }

    private sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var callback = BeforeSave;
            BeforeSave = null;
            if (callback is not null)
                await callback();
            return result;
        }
    }

    public sealed class TimestampModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AgentAnalysisJob>().Property(job => job.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AgentAnalysisJob>().Property(job => job.ExpiresAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AgentAnalysisReviewEvent>().Property(entry => entry.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }
}
