namespace MonitoringApp.Tests;

public sealed class AlertCommentLogbookTests
{
    [Fact]
    public void CreatesEntryWithUserCommentAlertAndTarget()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        var alert = CreateAlert();

        var entry = AlertCommentLogbook.CreateEntry(alert, " operator ", " Investigating replication. ", createdAt);

        Assert.NotNull(entry);
        Assert.Equal(createdAt, entry.CreatedAt);
        Assert.Equal("operator", entry.User);
        Assert.Contains("Investigating replication.", entry.Comment);
        Assert.Contains("Replication alert", entry.Comment);
        Assert.Contains("DC-01", entry.Comment);
    }

    [Fact]
    public void EmptyCommentDoesNotCreateEntry()
    {
        var entry = AlertCommentLogbook.CreateEntry(
            CreateAlert(),
            "operator",
            "   ",
            DateTimeOffset.UtcNow);

        Assert.Null(entry);
    }

    [Fact]
    public void CommentRequiresLoggedOnUser()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AlertCommentLogbook.CreateEntry(
                CreateAlert(),
                string.Empty,
                "Investigating",
                DateTimeOffset.UtcNow));

        Assert.Contains("logged-on user", exception.Message);
    }

    private static AlertRecord CreateAlert() =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-31T11:55:00Z"),
            "alert-42",
            "Replication alert",
            "Sev1",
            "Fired",
            "Log",
            "Fired",
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.HybridCompute/machines/DC-01",
            "rg",
            "sub",
            DateTimeOffset.Parse("2026-08-31T11:55:00Z"),
            string.Empty,
            string.Empty,
            string.Empty,
            "{}");
}
