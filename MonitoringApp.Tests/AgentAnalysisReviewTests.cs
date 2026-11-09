using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class AgentAnalysisReviewTests
{
    [Theory]
    [InlineData(AgentAnalysisReviewActions.Accepted, null)]
    [InlineData(AgentAnalysisReviewActions.Commented, "Needs another check")]
    [InlineData(AgentAnalysisReviewActions.Rejected, "Evidence is incomplete")]
    [InlineData(AgentAnalysisReviewActions.HandoffCopied, null)]
    public void CreatesVersionBoundAuditEvent(string action, string? comment)
    {
        var job = CreateCompletedJob();
        var createdAt = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);

        var entry = AgentAnalysisReviewEvent.Create(job, " operator ", action, comment, createdAt);

        Assert.Equal(job.Id, entry.AgentAnalysisJobId);
        Assert.Equal(job.AnalysisId, entry.AnalysisId);
        Assert.Equal(job.ResultVersion, entry.ResultVersion);
        Assert.Equal("operator", entry.Actor);
        Assert.Equal(createdAt, entry.CreatedAt);
    }

    [Theory]
    [InlineData(AgentAnalysisReviewActions.Commented)]
    [InlineData(AgentAnalysisReviewActions.Rejected)]
    public void RequiresCommentForExplanatoryActions(string action)
    {
        Assert.Throws<AgentAnalysisReviewValidationException>(() =>
            AgentAnalysisReviewEvent.Create(CreateCompletedJob(), "operator", action, " ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RejectsReviewOfIncompleteJob()
    {
        var job = CreateCompletedJob();
        job.Status = AgentAnalysisJobStatuses.Running;

        Assert.Throws<AgentAnalysisReviewValidationException>(() => AgentAnalysisReviewEvent.Create(
            job,
            "operator",
            AgentAnalysisReviewActions.Accepted,
            null,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MapsReviewEventsToAnalysisJobsWithCascadeDelete()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(AgentAnalysisReviewEvent)));
        var foreignKey = Assert.Single(entity.GetForeignKeys());

        Assert.Equal("AgentAnalysisReviewEvents", entity.GetTableName());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Equal(nameof(AgentAnalysisReviewEvent.AgentAnalysisJobId), Assert.Single(foreignKey.Properties).Name);
    }

    [Theory]
    [InlineData(0, AgentAnalysisReviewValidationCode.ResultRequired, "Only a completed analysis result can be reviewed.")]
    [InlineData(1, AgentAnalysisReviewValidationCode.ActionInvalid, "Unknown analysis review action 'custom <action>'.")]
    [InlineData(2, AgentAnalysisReviewValidationCode.ActorInvalid, "Review actor is required and may contain at most 256 characters.")]
    [InlineData(3, AgentAnalysisReviewValidationCode.ActorInvalid, "Review actor is required and may contain at most 256 characters.")]
    [InlineData(4, AgentAnalysisReviewValidationCode.CommentTooLong, "Review comment may contain at most 4000 characters.")]
    [InlineData(5, AgentAnalysisReviewValidationCode.CommentRequired, "Comment and rejection actions require a review comment.")]
    [InlineData(6, AgentAnalysisReviewValidationCode.ResultRequired, "Only a completed analysis result can be reviewed.")]
    public void ValidationCodesPreserveNeutralDiagnostics(int scenario, AgentAnalysisReviewValidationCode code, string message)
    {
        var job = CreateCompletedJob();
        if (scenario == 0) job.Status = AgentAnalysisJobStatuses.Running;
        if (scenario == 6) job.ResultJson = " ";
        var actor = scenario == 2 ? " " : scenario == 3 ? new string('a', 257) : "operator";
        var action = scenario == 1 ? "custom <action>" : AgentAnalysisReviewActions.Commented;
        var comment = scenario == 4 ? new string('c', 4001) : scenario == 5 ? " " : "Review";
        var exception = Assert.Throws<AgentAnalysisReviewValidationException>(() =>
            AgentAnalysisReviewEvent.Create(job, actor, action, comment, DateTimeOffset.UtcNow));
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ExactNormalizedLimitsRemainValid()
    {
        var entry = AgentAnalysisReviewEvent.Create(CreateCompletedJob(), $" {new string('a', 256)} ",
            AgentAnalysisReviewActions.Commented, $" {new string('c', 4000)} ", DateTimeOffset.UtcNow);
        Assert.Equal(256, entry.Actor.Length);
        Assert.Equal(4000, entry.Comment.Length);
    }

    private static AgentAnalysisJob CreateCompletedJob() => new()
    {
        Id = Guid.Parse("31ae8d52-1905-4453-8d1e-337c69631880"),
        AnalysisId = Guid.Parse("75193ca5-a0d8-4d09-991b-31ef896a8e41"),
        ResultVersion = 3,
        Status = AgentAnalysisJobStatuses.Completed,
        ResultJson = "{}"
    };
}