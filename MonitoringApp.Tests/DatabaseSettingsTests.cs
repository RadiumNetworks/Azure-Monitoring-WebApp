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
    public void DeserializesSafeAgentAnalysisDefault()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AgentAnalysisExecutionOptions>(
            DatabaseSettingsLoader.AgentAnalysisExecution,
            """{"Version":1,"Mode":"CopilotPrompt","TeamsWorkflowProfile":null,"ApiProfile":null}""");

        Assert.Equal(AgentAnalysisExecutionModes.CopilotPrompt, setting.Mode);
        Assert.Empty(setting.Validate());
        Assert.Contains(DatabaseSettingsLoader.AgentAnalysisExecution, DatabaseSettingsLoader.RequiredNames);
    }

    [Fact]
    public void DeserializesDefaultAgentAnalysisContext()
    {
        var setting = DatabaseSettingsLoader.Deserialize<AgentAnalysisContextOptions>(
            DatabaseSettingsLoader.AgentAnalysisContext,
            """{"InventoryCorrelationLayers":["Domain","Role"]}""");

        Assert.Equal([AlertGraphLayer.Domain, AlertGraphLayer.Role], setting.InventoryCorrelationLayers);
        Assert.Empty(setting.Validate());
        Assert.Contains(DatabaseSettingsLoader.AgentAnalysisContext, DatabaseSettingsLoader.RequiredNames);
    }

    [Fact]
    public void RejectsEmptyOrUnsafeAgentAnalysisContext()
    {
        Assert.NotEmpty(new AgentAnalysisContextOptions { InventoryCorrelationLayers = [] }.Validate());
        Assert.NotEmpty(new AgentAnalysisContextOptions
        {
            InventoryCorrelationLayers = [AlertGraphLayer.Target]
        }.Validate());
    }

    [Theory]
    [InlineData("Unknown", null, null)]
    [InlineData("TeamsWorkflow", null, null)]
    [InlineData("TeamsWorkflow", "https://example.com/hook", null)]
    [InlineData("Api", null, "../provider")]
    [InlineData("CopilotPrompt", "workflow-prod", null)]
    public void RejectsUnsafeAgentAnalysisExecution(
        string mode,
        string? teamsWorkflowProfile,
        string? apiProfile)
    {
        var setting = new AgentAnalysisExecutionOptions
        {
            Mode = mode,
            TeamsWorkflowProfile = teamsWorkflowProfile,
            ApiProfile = apiProfile
        };

        Assert.NotEmpty(setting.Validate());
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

    [Fact]
    public void MapsAgentAnalysisJobsWithConcurrencyAndOperationalIndexes()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(AgentAnalysisJob)));

        Assert.Equal("AgentAnalysisJobs", entity.GetTableName());
        Assert.True(entity.FindProperty(nameof(AgentAnalysisJob.Revision))?.IsConcurrencyToken);
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AgentAnalysisJob.Status), nameof(AgentAnalysisJob.CreatedAt)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AgentAnalysisJob.CreatedBy), nameof(AgentAnalysisJob.CreatedAt)]));
    }
}
