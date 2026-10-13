namespace MonitoringApp.Tests;

public sealed class AgentAnalysisMarkdownRendererTests
{
    private static readonly Guid AnalysisId =
        Guid.Parse("7b341672-44d1-465d-b80c-80c4a38a6159");
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RendersStoredResultWithAuditableMetadataAndAllSections()
    {
        var job = CreateJob();

        var markdown = AgentAnalysisMarkdownRenderer.Render(job, CreateResult(AnalysisId));

        Assert.Contains($"Analysis ID: `{AnalysisId:D}`", markdown);
        Assert.Contains("Result version: `2`", markdown);
        Assert.Contains($"Created: `{CreatedAt:O}`", markdown);
        Assert.Contains("## Teams summary", markdown);
        Assert.Contains("## Executive summary", markdown);
        Assert.Contains("## Hypotheses", markdown);
        Assert.Contains("## Recommended checks", markdown);
        Assert.Contains("## Operator handoff", markdown);
        Assert.Contains("Microsoft sources: `source-a`", markdown);
        Assert.Equal(
            $"agent-analysis-{AnalysisId:D}-v2.md",
            AgentAnalysisMarkdownRenderer.CreateFileName(job));
    }

    [Theory]
    [InlineData(AgentAnalysisJobStatuses.Running, true)]
    [InlineData(AgentAnalysisJobStatuses.Completed, false)]
    public void RejectsIncompleteOrMismatchedResult(string status, bool matchingAnalysis)
    {
        var job = CreateJob();
        job.Status = status;
        var result = CreateResult(matchingAnalysis ? AnalysisId : Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            AgentAnalysisMarkdownRenderer.Render(job, result));
    }

    [Fact]
    public void CreatesTeamsTransportContractWithoutSending()
    {
        var job = CreateJob();

        var request = AgentAnalysisTeamsTransportRequest.Create(
            job,
            CreateResult(AnalysisId),
            new Uri($"https://monitoring.example/analysis/{AnalysisId:D}"),
            new Uri($"https://monitoring.example/api/agent-analysis/jobs/{job.Id:D}/attachment"));

        Assert.Equal(AnalysisId, request.AnalysisId);
        Assert.Equal(2, request.ResultVersion);
        Assert.Equal(job.ExpiresAt, request.Attachment.ExpiresAt);
        Assert.Equal("text/markdown; charset=utf-8", request.Attachment.ContentType);
        Assert.EndsWith("-v2.md", request.Attachment.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamsTransportContractRejectsNonHttpsLinks()
    {
        var job = CreateJob();

        Assert.Throws<ArgumentException>(() => AgentAnalysisTeamsTransportRequest.Create(
            job,
            CreateResult(AnalysisId),
            new Uri("http://monitoring.example/analysis/1"),
            new Uri("https://monitoring.example/attachment/1")));
    }

    private static AgentAnalysisJob CreateJob() => new()
    {
        AnalysisId = AnalysisId,
        ResultVersion = 2,
        Status = AgentAnalysisJobStatuses.Completed,
        ScopeType = "Target",
        ScopeValue = "server-01",
        CreatedAt = CreatedAt,
        ResultStoredAt = CreatedAt.AddMinutes(2),
        ExpiresAt = CreatedAt.AddDays(30)
    };

    private static AgentAnalysisResult CreateResult(Guid analysisId) => new(
        AgentAnalysisResultContract.CurrentVersion,
        analysisId,
        2,
        [new AgentTeamsSummaryBullet("- **server-01** requires review", ["executive-1"])],
        new AgentDetailedAnalysis(
            [new AgentExecutiveStatement(
                "executive-1",
                "ObservedFact",
                "The target has an active alert.",
                [Guid.Parse("0297b903-bf04-4ff2-b7ac-d1bb0235d37a")],
                [],
                ["source-a"])],
            [new AgentHypothesis(
                "hypothesis-1",
                "Connectivity may be impaired.",
                "Medium",
                [Guid.Parse("0297b903-bf04-4ff2-b7ac-d1bb0235d37a")],
                [],
                "A successful connection test would reject the hypothesis.")],
            [new AgentRecommendedCheck(
                "check-1",
                "Inspect connectivity.",
                "server-01",
                "Connection state",
                "Read-only",
                "Operator access",
                "None",
                [],
                ["source-a"])],
            new AgentOperatorHandoff(
                "operator-handoff",
                "Investigate",
                "Service interruption",
                "Current connectivity",
                "Operator",
                "[PORTAL_LINK]")));
}