namespace MonitoringApp.Tests;

public sealed class CriticalAlertLogbookTests
{
    [Theory]
    [InlineData("Fired", "Critical alert fired:")]
    [InlineData("Resolved", "Critical alert resolved:")]
    [InlineData("fired", "Critical alert fired:")]
    public void CreatesSystemEntryForCriticalLifecycleEvent(string condition, string expectedPrefix)
    {
        var createdAt = DateTimeOffset.Parse("2026-08-31T10:15:00Z");
        var alert = CreateAlert(condition);

        var entry = CriticalAlertLogbook.CreateEntry(alert, true, createdAt);

        Assert.NotNull(entry);
        Assert.Equal(createdAt, entry.CreatedAt);
        Assert.Equal("System", entry.User);
        Assert.StartsWith(expectedPrefix, entry.Comment);
        Assert.Contains("Port health alert", entry.Comment);
        Assert.Contains("DC-01", entry.Comment);
        Assert.Contains("Sev0", entry.Comment);
        Assert.Contains("critical-alert-42", entry.Comment);
    }

    [Fact]
    public void DoesNotCreateEntryForNonCriticalAlert()
    {
        var entry = CriticalAlertLogbook.CreateEntry(
            CreateAlert("Fired"),
            false,
            DateTimeOffset.UtcNow);

        Assert.Null(entry);
    }

    [Fact]
    public void DoesNotCreateEntryForOtherCriticalCondition()
    {
        var entry = CriticalAlertLogbook.CreateEntry(
            CreateAlert("Acknowledged"),
            true,
            DateTimeOffset.UtcNow);

        Assert.Null(entry);
    }

    private static AlertRecord CreateAlert(string condition) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            "critical-alert-42",
            "Port health alert",
            "Sev0",
            condition,
            "Log",
            condition,
            "DC-01",
            "rg-test",
            "sub-test",
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            string.Empty,
            string.Empty,
            string.Empty,
            "{}");
}
