using System.Runtime.CompilerServices;

namespace MonitoringApp.Tests;

public sealed class SourceOrganizationTests
{
    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    [Fact]
    public void ApplicationRootContainsOnlyTheCompositionRoot()
    {
        var file = Assert.Single(Directory.GetFiles(ProjectDirectory(), "*.cs"));

        Assert.Equal("Program.cs", Path.GetFileName(file));
    }

    [Theory]
    [InlineData("Domain", "Alerts", "AlertRecord.cs")]
    [InlineData("Domain", "Rca", "RcaContracts.cs")]
    [InlineData("Domain", "AgentAnalysis", "AgentAnalysisJobContracts.cs")]
    [InlineData("Domain", "Analysis", "PublishedAnalysisDashboard.cs")]
    [InlineData("Presentation", "Dashboards", "DashboardGridEngine.cs")]
    public void ModelContractAndEngineSourcesUseDescriptiveFileNames(string area, string feature, string file)
    {
        var path = Path.Combine(ProjectDirectory(), area, feature, file);

        Assert.True(File.Exists(path), $"Expected organized source file: {path}");
    }

    [Fact]
    public void ApplicationRootKeepsOnlyReadmeAndRuntimeJson()
    {
        Assert.All(Directory.GetFiles(ProjectDirectory(), "*.md"),
            path => Assert.Equal("README.md", Path.GetFileName(path)));
        Assert.All(Directory.GetFiles(ProjectDirectory(), "*.json"),
            path => Assert.True(
                Path.GetFileName(path) is "appsettings.json" or "appsettings.Development.json",
                $"Unexpected root JSON file: {path}"));
    }

    [Fact]
    public void RepositoryRootKeepsReadmeAndMovesConfigurationToConfig()
    {
        var root = Path.GetFullPath(Path.Combine(ProjectDirectory(), ".."));

        Assert.All(Directory.GetFiles(root, "*.md"),
            path => Assert.Equal("README.md", Path.GetFileName(path)));
        Assert.Empty(Directory.GetFiles(root, "*.json"));
        Assert.True(File.Exists(Path.Combine(root, "config", "alertconsole.local.example.json")));
    }

    [Theory]
    [InlineData("Build-CodeViews.ps1")]
    [InlineData("Validate-CodeViews.ps1")]
    [InlineData("query-enrichment-cases.json")]
    [InlineData("query-enrichment.codeview.template.json")]
    [InlineData("query-refresh.codeview.template.json")]
    [InlineData("heartbeat-inventory-sync.codeview.json")]
    [InlineData("dcdiag-alert-refresh.codeview.json")]
    [InlineData("dcport-alert-refresh.codeview.json")]
    [InlineData("heartbeat-alert-refresh.codeview.json")]
    [InlineData("replication-alert-refresh.codeview.json")]
    public void LogicAppAssetsAreGroupedWithDocumentation(string file)
    {
        var path = Path.Combine(ProjectDirectory(), "doc", "LogicApps", file);

        Assert.True(File.Exists(path), $"Expected Logic App documentation asset: {path}");
    }
}
