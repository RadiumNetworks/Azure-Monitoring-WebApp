namespace MonitoringApp.Tests;

public sealed class AgentAnalysisJobStateMachineTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Unknown agent analysis job status 'Unknown <status>'.")]
    [InlineData(1, "Agent analysis job cannot transition from 'Queued' to 'Completed'.")]
    [InlineData(2, "Agent analysis job transition time cannot move backwards.")]
    [InlineData(3, "Agent analysis job transition time cannot move backwards.")]
    [InlineData(4, "A failed agent analysis job requires an error message.")]
    public void TransitionValidationPreservesNeutralContractAndEveryField(int scenario, string message)
    {
        var job = CreateJob();
        if (scenario is 3 or 4)
            AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(2));
        var before = System.Text.Json.JsonSerializer.Serialize(job);
        var target = scenario switch
        {
            0 => "Unknown <status>",
            1 or 3 => AgentAnalysisJobStatuses.Completed,
            4 => AgentAnalysisJobStatuses.Failed,
            _ => AgentAnalysisJobStatuses.Running
        };
        var time = scenario == 2 ? CreatedAt.AddTicks(-1) :
            scenario == 3 ? CreatedAt.AddMinutes(2).AddTicks(-1) : CreatedAt.AddMinutes(3);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobStateMachine.Transition(job, target, time));
        Assert.Equal(message, exception.Message);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData(0, "The analysis result identity and version must match the analysis job.")]
    [InlineData(1, "The analysis result identity and version must match the analysis job.")]
    [InlineData(2, "The analysis result contract version must match the analysis job.")]
    public void ResultPreflightPreservesNeutralContractAndEveryField(int scenario, string message)
    {
        var job = CreateJob();
        AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(1));
        var result = CreateResult(job.AnalysisId, job.ResultVersion);
        result = scenario switch
        {
            0 => result with { AnalysisId = Guid.Empty },
            1 => result with { ResultVersion = job.ResultVersion + 1 },
            _ => result with { ContractVersion = "custom <contract>" }
        };
        var before = System.Text.Json.JsonSerializer.Serialize(job);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobResultPersistence.Store(job, result, CreatedAt.AddMinutes(2)));
        Assert.Equal(message, exception.Message);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData("", "actor")]
    [InlineData(" ", "actor")]
    [InlineData("prompt", "")]
    [InlineData("prompt", " ")]
    public void IdempotencyPreflightRemainsNeutralAndDoesNotChangeSnapshot(string version, string actor)
    {
        var snapshot = CreateSnapshot();
        var before = snapshot.ToCanonicalJson();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobIdempotency.Compute(snapshot, version, actor));
        Assert.Equal("PromptVersion and CreatedBy are required for idempotency.", exception.Message);
        Assert.Equal(before, snapshot.ToCanonicalJson());
    }

    [Fact]
    public void MovesQueuedJobThroughRunningToCompleted()
    {
        var job = CreateJob();

        AgentAnalysisJobStateMachine.Transition(
            job,
            AgentAnalysisJobStatuses.Running,
            CreatedAt.AddMinutes(1),
            "Execution started");
        AgentAnalysisJobStateMachine.Transition(
            job,
            AgentAnalysisJobStatuses.Completed,
            CreatedAt.AddMinutes(2),
            "Validated result stored");

        Assert.Equal(AgentAnalysisJobStatuses.Completed, job.Status);
        Assert.Equal(CreatedAt.AddMinutes(1), job.StartedAt);
        Assert.Equal(CreatedAt.AddMinutes(2), job.CompletedAt);
        Assert.Equal(3, job.Revision);
        Assert.Empty(job.ErrorCode);
        Assert.Empty(job.ErrorMessage);
    }

    [Theory]
    [InlineData(AgentAnalysisJobStatuses.Queued, AgentAnalysisJobStatuses.Completed)]
    [InlineData(AgentAnalysisJobStatuses.Completed, AgentAnalysisJobStatuses.Running)]
    [InlineData(AgentAnalysisJobStatuses.Failed, AgentAnalysisJobStatuses.Running)]
    [InlineData(AgentAnalysisJobStatuses.Cancelled, AgentAnalysisJobStatuses.Running)]
    [InlineData(AgentAnalysisJobStatuses.Expired, AgentAnalysisJobStatuses.Running)]
    public void RejectsInvalidOrTerminalTransition(string currentStatus, string targetStatus)
    {
        var job = CreateJob();
        job.Status = currentStatus;

        Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobStateMachine.Transition(job, targetStatus, CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void FailedJobRequiresErrorAndStoresBoundedFailureState()
    {
        var job = CreateJob();
        AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobStateMachine.Transition(
                job,
                AgentAnalysisJobStatuses.Failed,
                CreatedAt.AddMinutes(2)));

        AgentAnalysisJobStateMachine.Transition(
            job,
            AgentAnalysisJobStatuses.Failed,
            CreatedAt.AddMinutes(2),
            "Provider request failed",
            "ProviderTimeout",
            "The approved provider did not respond before the configured timeout.");

        Assert.Equal(AgentAnalysisJobStatuses.Failed, job.Status);
        Assert.Equal("ProviderTimeout", job.ErrorCode);
        Assert.NotEmpty(job.ErrorMessage);
        Assert.Equal(CreatedAt.AddMinutes(2), job.CompletedAt);
    }

    [Fact]
    public void RejectsTransitionTimeBeforeLastUpdate()
    {
        var job = CreateJob();
        AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisJobStateMachine.Transition(
                job,
                AgentAnalysisJobStatuses.Completed,
                CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void StoresMatchingResultAndCompletesJobAtomically()
    {
        var job = CreateJob();
        AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(1));
        var result = CreateResult(job.AnalysisId, job.ResultVersion);

        AgentAnalysisJobResultPersistence.Store(job, result, CreatedAt.AddMinutes(2));

        Assert.Equal(AgentAnalysisJobStatuses.Completed, job.Status);
        Assert.Equal(result.ToCanonicalJson(), job.ResultJson);
        Assert.Contains("executive-1", job.TeamsSummaryJson);
        Assert.Contains("source-b", job.SourceIdsJson);
        Assert.True(job.SourceIdsJson.IndexOf("source-a", StringComparison.Ordinal) <
                    job.SourceIdsJson.IndexOf("source-b", StringComparison.Ordinal));
        Assert.Equal(CreatedAt.AddMinutes(2), job.ResultStoredAt);
        Assert.Equal(AgentAnalysisDeliveryStatuses.NotRequested, job.TeamsDeliveryStatus);
        Assert.Null(job.TeamsDeliveredResultVersion);
    }

    [Fact]
    public void RejectsResultForDifferentAnalysisWithoutCompletingJob()
    {
        var job = CreateJob();
        AgentAnalysisJobStateMachine.Transition(job, AgentAnalysisJobStatuses.Running, CreatedAt.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => AgentAnalysisJobResultPersistence.Store(
            job,
            CreateResult(Guid.NewGuid(), job.ResultVersion),
            CreatedAt.AddMinutes(2)));

        Assert.Equal(AgentAnalysisJobStatuses.Running, job.Status);
        Assert.Empty(job.ResultJson);
    }

    [Fact]
    public void IdempotencyKeyIgnoresGenerationTimeAndUserNameCasing()
    {
        var snapshot = CreateSnapshot();

        var first = AgentAnalysisJobIdempotency.Compute(snapshot, "prompt-v1", "Operator.One");
        var retry = AgentAnalysisJobIdempotency.Compute(
            snapshot with { GeneratedAt = snapshot.GeneratedAt.AddMinutes(1) },
            "prompt-v1",
            "operator.one");

        Assert.Equal(first, retry);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void IdempotencyKeyChangesWithScopeDataOrPrompt()
    {
        var snapshot = CreateSnapshot();
        var baseline = AgentAnalysisJobIdempotency.Compute(snapshot, "prompt-v1", "operator");

        Assert.NotEqual(baseline, AgentAnalysisJobIdempotency.Compute(
            snapshot with { Scope = new IncidentAnalysisScope(IncidentAnalysisScopeType.Target, "target-b") },
            "prompt-v1",
            "operator"));
        Assert.NotEqual(baseline, AgentAnalysisJobIdempotency.Compute(
            snapshot with { MissingEvidence = ["Heartbeat missing"] },
            "prompt-v1",
            "operator"));
        Assert.NotEqual(baseline, AgentAnalysisJobIdempotency.Compute(
            snapshot,
            "prompt-v2",
            "operator"));
    }

    internal static AgentAnalysisResult CreateResult(Guid analysisId, int resultVersion) => new(
        AgentAnalysisResultContract.CurrentVersion,
        analysisId,
        resultVersion,
        [new AgentTeamsSummaryBullet("- **Target** affected", ["executive-1"])],
        new AgentDetailedAnalysis(
            [new AgentExecutiveStatement(
                "executive-1",
                "ObservedFact",
                "Target is affected.",
                [],
                [],
                ["source-b", "source-a"])],
            [],
            [new AgentRecommendedCheck(
                "check-1",
                "Inspect status.",
                "Target",
                "Current state",
                "Read-only",
                "Access",
                "None",
                [],
                ["source-a"])],
            new AgentOperatorHandoff(
                "operator-handoff",
                "Inspect",
                "Unknown cause",
                "Runtime status",
                "Operator",
                "[PORTAL_LINK]")));

    private static IncidentAnalysisSnapshot CreateSnapshot() => new(
        CreatedAt,
        new IncidentAnalysisScope(IncidentAnalysisScopeType.Target, "target-a"),
        CreatedAt.AddHours(-1),
        CreatedAt,
        100,
        120_000,
        [],
        [],
        [],
        [],
        new IncidentGraphContext(
            AlertGraphLayer.Subscription,
            AlertGraphLayer.Site,
            AlertGraphLayer.Target,
            "Selected target",
            []),
        [],
        [],
        [],
        [],
        [],
        []);

    private static AgentAnalysisJob CreateJob() => new()
    {
        Id = Guid.Parse("5022547f-d4dc-42df-95e4-92bca89c8973"),
        AnalysisId = Guid.Parse("ee447b9f-d555-468d-bb59-5dc9869ac931"),
        ResultVersion = 1,
        ResultContractVersion = AgentAnalysisResultContract.CurrentVersion,
        Revision = 1,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        ExpiresAt = CreatedAt.AddDays(30)
    };
}