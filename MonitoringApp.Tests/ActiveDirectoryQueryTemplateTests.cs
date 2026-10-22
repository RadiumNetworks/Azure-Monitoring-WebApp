using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class ActiveDirectoryQueryTemplateTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory = Directory.CreateTempSubdirectory("ad-query-templates-").FullName;

    [Fact]
    public void ReplicationTemplateFillsMissingIntervalsAndReturnsTypedChartRows()
    {
        var query = Assert.Single(ActiveDirectoryAnalysisQueries.Create(), item => item.Key == "ad-replication-errors");
        Assert.Equal(AnalysisQueryVisualizationTypes.LineChart, query.VisualizationType);
        Assert.Contains("let StartTime = ago(_TimePeriod);", query.Kql);
        Assert.Contains("let EndTime = now();", query.Kql);
        Assert.Contains("TimeGenerated >= StartTime and TimeGenerated < EndTime", query.Kql);
        Assert.Contains("make-series Value = countif(toint(NumberOfFailures) > 0)", query.Kql);
        Assert.Contains("default=0", query.Kql);
        Assert.Contains("from StartTime to EndTime step _BinSize", query.Kql);
        Assert.DoesNotContain("| where toint(NumberOfFailures) > 0", query.Kql);
        Assert.Contains("| mv-expand\n    Timestamp = TimeGenerated to typeof(datetime),\n    Value to typeof(long)", query.Kql);
        Assert.Contains("| project Timestamp, Series, Value", query.Kql);

        var table = new AnalysisQueryTable("Workspace",
            [new("Timestamp", "datetime"), new("Series", "string"), new("Value", "long")],
            [["2026-10-21T10:00:00Z", "DC01", "0"],
             ["2026-10-21T10:15:00Z", "DC01", "1"],
             ["2026-10-21T10:30:00Z", "DC01", "0"]]);
        var result = AnalysisQueryRunner.NormalizeResult(table, query.VisualizationType);
        Assert.Null(result.Error);
        Assert.Equal([0d, 1d, 0d], result.Rows.Select(row => row.Value));
    }

    [Fact]
    public void ReloadsKqlPresentationAndNewFilesOnEveryRead()
    {
        var template = Template();
        Write("first.json", template);
        var original = Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory));

        template["name"] = "Customized template";
        template["description"] = "Updated description";
        template["category"] = "Updated category";
        template["kql"] = original.Kql + "\n| take 5";
        template["visualizationType"] = "Table";
        template["unit"] = "Updated unit";
        template["defaultTimeRangeMinutes"] = 60;
        template["maxTimeRangeMinutes"] = 10080;
        template["defaultBinMinutes"] = 30;
        template["minimumRole"] = "Admin";
        Write("first.json", template);

        var updated = Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Equal("Customized template", updated.Name);
        Assert.Equal("Updated description", updated.Description);
        Assert.Equal("Updated category", updated.Category);
        Assert.Equal(original.Kql + "\n| take 5", updated.Kql);
        Assert.Equal("Table", updated.VisualizationType);
        Assert.Equal("Updated unit", updated.Unit);
        Assert.Equal(60, updated.DefaultTimeRangeMinutes);
        Assert.Equal(10080, updated.MaxTimeRangeMinutes);
        Assert.Equal(30, updated.DefaultBinMinutes);
        Assert.Equal("Admin", updated.MinimumRole);
        Assert.Equal(AnalysisQueryStatuses.Draft, updated.Status);
        Assert.Equal(Guid.Empty, updated.Id);
        Assert.Null(updated.PublishedVersionId);
        Assert.Empty(updated.Workspaces);
        Assert.Empty(updated.Versions);
        Assert.True(updated.Enabled);

        template["key"] = "ad-additional";
        Write("second.json", template);
        Assert.Equal(2, ActiveDirectoryAnalysisQueries.Create(directory).Count);
        Assert.NotEqual(original.Kql, updated.Kql);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("category")]
    [InlineData("kql")]
    [InlineData("visualizationType")]
    [InlineData("unit")]
    [InlineData("defaultTimeRangeMinutes")]
    [InlineData("maxTimeRangeMinutes")]
    [InlineData("defaultBinMinutes")]
    [InlineData("minimumRole")]
    public void RejectsMissingFields(string property)
    {
        var template = Template();
        template.Remove(property);
        Write("missing.json", template);

        var error = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Contains("missing.json", error.Message);
        Assert.Contains(property, error.Message);
    }

    [Theory]
    [InlineData("key", "null")]
    [InlineData("kql", "null")]
    [InlineData("description", "null")]
    [InlineData("key", "\" \"")]
    [InlineData("kql", "\"\"")]
    [InlineData("kql", "\"Heartbeat | take 1\"")]
    [InlineData("visualizationType", "\"UnknownChart\"")]
    [InlineData("minimumRole", "\"UnknownRole\"")]
    [InlineData("defaultTimeRangeMinutes", "0")]
    [InlineData("maxTimeRangeMinutes", "60")]
    [InlineData("defaultBinMinutes", "0")]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000001\"")]
    [InlineData("visualisationType", "\"Table\"")]
    [InlineData("scope", "\"Unknown\"")]
    [InlineData("scope", "null")]
    [InlineData("visualization", """{"color":"invalid"}""")]
    [InlineData("visualization", """{"columns":[{"name":"Value","width":5}]}""")]
    [InlineData("visualization", """{"thresholds":[{"color":"red"}]}""")]
    [InlineData("visualization", """{"thresholds":[{"value":0,"color":"green"},{"value":0,"color":"red"}]}""")]
    [InlineData("visualization", """{"unknown":true}""")]
    public void RejectsInvalidValuesAndUnknownFields(string property, string json)
    {
        var template = Template();
        template[property] = JsonNode.Parse(json);
        Write("invalid.json", template);

        var error = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Contains("invalid.json", error.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void RejectsMalformedOrNonObjectJson(string json)
    {
        File.WriteAllText(Path.Combine(directory, "broken.json"), json);

        var error = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Contains("broken.json", error.Message);
    }

    [Fact]
    public void RejectsDuplicateKeysIgnoringCaseAndWhitespace()
    {
        var template = Template();
        Write("first.json", template);
        template["key"] = $" {template["key"]!.GetValue<string>().ToUpperInvariant()} ";
        Write("second.json", template);

        var error = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Contains("Duplicate AD query key", error.Message);
        Assert.Contains("second.json", error.Message);
    }

    [Fact]
    public void ValidatesEveryFileBeforeReturningTemplates()
    {
        Write("first.json", Template());
        File.WriteAllText(Path.Combine(directory, "last.json"), "{");

        Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
    }

    [Fact]
    public void ReportsMissingAndEmptyDirectories()
    {
        var empty = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(directory));
        Assert.Contains("No AD query templates", empty.Message);
        var missing = Path.Combine(directory, "missing");
        var error = Assert.Throws<InvalidOperationException>(() => ActiveDirectoryAnalysisQueries.Create(missing));
        Assert.Contains(missing, error.Message);
        Assert.IsType<DirectoryNotFoundException>(error.InnerException);
    }

    [Fact]
    public void ReloadsVisualizationOptionsAndKeepsOldTemplatesCompatible()
    {
        var template = Template();
        template.Remove("visualization");
        Write("first.json", template);
        Assert.Empty(AnalysisVisualization.Parse(Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory)).VisualizationJson).Columns);
        template["visualization"] = JsonNode.Parse("""{"columns":[{"name":"Value","color":"purple","decimals":3}]}""");
        Write("first.json", template);
        var column = Assert.Single(AnalysisVisualization.Parse(Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory)).VisualizationJson).Columns);
        Assert.Equal("Value", column.Name);
        Assert.Equal("purple", column.Color);
        Assert.Equal(3, column.Decimals);
    }

    [Fact]
    public void LegacyTemplatesDefaultToSelectedSystemsAndScopeMustBeExplicit()
    {
        var template = Template();
        template.Remove("scope");
        Write("first.json", template);
        Assert.Equal(AnalysisQueryScopes.SelectedSystems, Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory)).Scope);
        template["scope"] = AnalysisQueryScopes.Workspace;
        Write("first.json", template);
        Assert.Equal(AnalysisQueryScopes.Workspace, Assert.Single(ActiveDirectoryAnalysisQueries.Create(directory)).Scope);
    }

    [Theory]
    [InlineData("ad-gpo-statistics")]
    [InlineData("ad-gpo-history")]
    public void GpoTemplatesAreWorkspaceWideAndKeepEnvironmentsSeparate(string key)
    {
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == key);
        Assert.Equal(AnalysisQueryScopes.Workspace, query.Scope);
        Assert.DoesNotContain("Computer", query.Kql);
        Assert.DoesNotContain("_SelectedSystems", query.Kql);
        Assert.Contains("by Environment", query.Kql);
        Assert.Contains("Unknown environment", query.Kql);
        Assert.Contains("real(null)", query.Kql);
        Assert.Empty(AnalysisQueryValidator.ValidateKql(query.Kql, query.VisualizationType, query.Scope));
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(query.Kql, query.VisualizationType));
    }

    [Fact]
    public void GrafanaVariantsCoverEveryPanelWithScopedPortableQueriesAndPresentation()
    {
        string[] keys = [
            "ad-heartbeat-overview", "ad-kerberos-event-count", "ad-dcdiag-summary",
            "ad-fsmo-roles", "ad-fsmo-role-changes", "ad-dc-inventory", "ad-certificates-overview",
            "ad-replication-details", "ad-services-overview", "ad-cpu-idle-gauge", "ad-storage-gauge",
            "ad-memory-overview", "ad-gpo-history", "ad-port-usage", "ad-latency-gauge",
            "ad-gpo-statistics", "ad-system-event-summary", "ad-service-tls-event-summary",
            "ad-certificate-mapping-overview"];
        var variants = ActiveDirectoryAnalysisQueries.Create().Where(query => keys.Contains(query.Key)).ToArray();
        Assert.Equal(19, variants.Length);
        foreach (var query in variants)
        {
            if (query.Scope == AnalysisQueryScopes.SelectedSystems)
                Assert.Contains("Computer in~ (_SelectedSystems)", query.Kql);
            else
                Assert.Contains("Environment", query.Kql);
            Assert.Contains("ago(_TimePeriod)", query.Kql);
            Assert.DoesNotContain("$", query.Kql);
            Assert.DoesNotContain("/subscriptions/", query.Kql);
            Assert.DoesNotContain("workspace(", query.Kql);
            var options = AnalysisVisualization.Parse(query.VisualizationJson);
            Assert.True(options.Thresholds.Count > 0 || options.Columns.Count > 0);
        }
        Assert.Contains(variants, query => query.VisualizationType == AnalysisQueryVisualizationTypes.Stat);
        Assert.Contains(variants, query => query.VisualizationType == AnalysisQueryVisualizationTypes.StatusHistory);
        Assert.Equal(3, variants.Count(query => query.VisualizationType == AnalysisQueryVisualizationTypes.BarGauge));
        var kerberos = variants.Single(query => query.Key == "ad-kerberos-event-count");
        Assert.StartsWith("SecurityEvent", kerberos.Kql);
        Assert.Contains("EventID in (4768, 4769)", kerberos.Kql);
        var fsmo = variants.Single(query => query.Key == "ad-fsmo-role-changes");
        Assert.Contains("Computer == PreviousComputer and DNSRoot == PreviousDomain and Role == PreviousRole", fsmo.Kql);
        Assert.Contains("isnotempty(OldHolder) and isnotempty(NewHolder)", fsmo.Kql);
        Assert.Contains("real(null)", variants.Single(query => query.Key == "ad-dcdiag-summary").Kql);
        Assert.Contains("real(null)", variants.Single(query => query.Key == "ad-gpo-statistics").Kql);
        Assert.DoesNotContain("default=0", variants.Single(query => query.Key == "ad-gpo-history").Kql);
        Assert.Contains("by Computer, EventID, CertificateIdentity",
            variants.Single(query => query.Key == "ad-certificate-mapping-overview").Kql);
    }

    private static JsonObject Template() =>
        JsonSerializer.SerializeToNode(
            AnalysisQueryRegistry.CreateSnapshot(ActiveDirectoryAnalysisQueries.Create().First()), JsonOptions)!.AsObject();

    private void Write(string name, JsonObject template) =>
        File.WriteAllText(Path.Combine(directory, name), template.ToJsonString(JsonOptions));

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
