using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class DatabaseSettingsTests
{
    private static readonly DatabaseSettingsTestCases TestCases =
        TestCaseLoader.Load<DatabaseSettingsTestCases>("database-settings.json");

    [Fact]
    public void DeserializesAuthenticationSetting()
    {
        var setting = DatabaseSettingsLoader.Deserialize<ApplicationAuthenticationOptions>(
            DatabaseSettingsLoader.Authentication,
            TestCases.Authentication.ToJsonString());

        Assert.True(setting.IsSql);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertHistorySetting()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AlertHistoryOptions>(
            DatabaseSettingsLoader.AlertHistory,
            TestCases.AlertHistory.ToJsonString());

        Assert.Equal(TestCases.ExpectedHistoryDays, setting.Days);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertGraphEnumsFromNames()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AlertGraphOptions>(
            DatabaseSettingsLoader.AlertGraph,
            TestCases.AlertGraph.ToJsonString());

        Assert.Equal(TestCases.ExpectedLayer1, setting.DefaultLayer1);
        Assert.Equal(TestCases.ExpectedLayer2, setting.DefaultLayer2);
        Assert.Equal(TestCases.ExpectedLayer3, setting.DefaultLayer3);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertSeverityDisplaySetting()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AlertSeverityDisplayOptions>(
            DatabaseSettingsLoader.AlertSeverityDisplay,
            TestCases.SeverityDisplay.ToJsonString());

        Assert.Equal(TestCases.ExpectedSeverityClass, setting.CssClass(TestCases.Severity));
        Assert.Empty(setting.Validate());
        Assert.Contains(DatabaseSettingsLoader.AlertSeverityDisplay, DatabaseSettingsLoader.RequiredNames);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettingsLoader.Deserialize<AlertHistoryOptions>(
                DatabaseSettingsLoader.AlertHistory,
                TestCases.MalformedJson));
    }

    [Fact]
    public void MapsSettingsTableWithExpectedColumns()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(DatabaseSetting)));

        Assert.Equal("Settings", entity.GetTableName());
        Assert.Equal(
            [nameof(DatabaseSetting.JsonValue), nameof(DatabaseSetting.Name)],
            entity.GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            [nameof(DatabaseSetting.Name)],
            entity.FindPrimaryKey()?.Properties.Select(property => property.Name));
    }
}
