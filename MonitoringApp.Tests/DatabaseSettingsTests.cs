using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class DatabaseSettingsTests
{
    [Fact]
    public void DeserializesAuthenticationSetting()
    {
        var setting = DatabaseSettingsLoader.Deserialize<ApplicationAuthenticationOptions>(
            DatabaseSettingsLoader.Authentication,
            "{\"Type\":\"sql\"}");

        Assert.True(setting.IsSql);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertHistorySetting()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AlertHistoryOptions>(
            DatabaseSettingsLoader.AlertHistory,
            "{\"Days\":30}");

        Assert.Equal(30, setting.Days);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertGraphEnumsFromNames()
    {
        const string json = """
            {
              "Layer1":[{"Value":"Subscription","Label":"Subscription"}],
              "Layer2":[{"Value":"Site","Label":"Site"}],
              "Layer3":[{"Value":"Target","Label":"Target"}],
              "DefaultLayer1":"Subscription",
              "DefaultLayer2":"Site",
              "DefaultLayer3":"Target"
            }
            """;

        var setting = DatabaseSettingsLoader.Deserialize<AlertGraphOptions>(
            DatabaseSettingsLoader.AlertGraph,
            json);

        Assert.Equal(AlertGraphLayer.Subscription, setting.DefaultLayer1);
        Assert.Equal(AlertGraphLayer.Site, setting.DefaultLayer2);
        Assert.Equal(AlertGraphLayer.Target, setting.DefaultLayer3);
        Assert.Empty(setting.Validate());
    }

    [Fact]
    public void DeserializesAlertSeverityDisplaySetting()
    {
        const string json = """
            {
              "Severities":[{"Severity":"Sev0","Color":"red","FontStyle":"bold"}],
              "Default":{"Color":"black","FontStyle":"normal"}
            }
            """;

        var setting = DatabaseSettingsLoader.Deserialize<AlertSeverityDisplayOptions>(
            DatabaseSettingsLoader.AlertSeverityDisplay,
            json);

        Assert.Equal("severity-color-red severity-style-bold", setting.CssClass("Sev0"));
        Assert.Empty(setting.Validate());
        Assert.Contains(DatabaseSettingsLoader.AlertSeverityDisplay, DatabaseSettingsLoader.RequiredNames);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettingsLoader.Deserialize<AlertHistoryOptions>(
                DatabaseSettingsLoader.AlertHistory,
                "not-json"));
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
