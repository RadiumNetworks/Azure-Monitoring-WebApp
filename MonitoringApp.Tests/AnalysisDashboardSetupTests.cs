using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardSetupTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private static readonly AnalysisDashboardViewFilters Filters = new()
        { WorkspaceKey = "a", Domain = "example.test", Site = "Site A", Role = "Domain Controller" };

    [Fact]
    public void RoundTripPreservesEverySetupSettingAndMapsKeysToNewIdsWithoutLeakingInternalData()
    {
        var source = Document();
        var query = Query(source.Widgets[0].QueryDefinitionId);
        var json = AnalysisDashboardSetupJson.Export(source, Filters, [query]);
        var parsed = AnalysisDashboardSetupJson.Parse(json);
        var target = Query(Guid.NewGuid());
        var imported = AnalysisDashboardSetupJson.CreateDocument(parsed, "a", [target], Inventory(), Resolver(), false);
        Assert.Equal(json, AnalysisDashboardSetupJson.Export(imported, imported.ViewFilters!, [target]));
        Assert.Equal(target.Id, imported.Widgets[0].QueryDefinitionId);
        Assert.NotEqual(source.Widgets[0].Id, imported.Widgets[0].Id);
        Assert.Null(imported.SourceTemplateId);
        Assert.Null(imported.SourceTemplateVersion);
        Assert.Empty(imported.HiddenQueryIds);
        Assert.DoesNotContain(source.Widgets[0].QueryDefinitionId.ToString(), json);
        Assert.DoesNotContain(source.Widgets[0].Id, json);
        Assert.DoesNotContain("queryDefinitionId", json);
        Assert.DoesNotContain("sourceTemplate", json);
        Assert.DoesNotContain("hiddenQuery", json);
        Assert.DoesNotContain("KQL MUST NOT BE EXPORTED", json);
        using var tree = JsonDocument.Parse(json);
        Assert.Equal("query.with spaces", tree.RootElement.GetProperty("widgets")[0].GetProperty("queryKey").GetString());
        Assert.Equal(AnalysisDashboardSetupJson.Format, tree.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, tree.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("draft")]
    [InlineData("workspace")]
    public void MissingUnpublishedDisabledOrUnassignedQueriesBlockImport(string issue)
    {
        var query = Query(Guid.NewGuid());
        var setup = AnalysisDashboardSetupJson.Parse(AnalysisDashboardSetupJson.Export(Document(query.Id), Filters, [query]));
        if (issue == "disabled") query.Enabled = false;
        if (issue == "draft") query.PublishedVersionId = null;
        if (issue == "workspace") query.Workspaces.Single().Enabled = false;
        IReadOnlyList<AnalysisQueryDefinition> queries = issue == "missing" ? [] : [query];
        var preview = AnalysisDashboardSetupJson.Preview(setup, "a", queries, Inventory(), Resolver());
        Assert.False(preview.CanImport);
        Assert.Equal([query.Key], preview.UnavailableQueries);
        Assert.Equal(AnalysisDashboardSetupError.DependenciesUnavailable,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.CreateDocument(
                setup, "a", queries, Inventory(), Resolver(), true)).Code);
    }

    [Fact]
    public void UnknownWorkspaceRequiresAnExplicitConfiguredMappingAndChecksItsAssignments()
    {
        var query = Query(Guid.NewGuid());
        var setup = AnalysisDashboardSetupJson.Parse(AnalysisDashboardSetupJson.Export(
            Document(query.Id), Filters with { WorkspaceKey = "old-workspace" }, [query]));
        Assert.True(AnalysisDashboardSetupJson.Preview(setup, null, [query], Inventory(), Resolver()).WorkspaceUnavailable);
        Assert.False(AnalysisDashboardSetupJson.Preview(setup, "missing", [query], Inventory(), Resolver()).CanImport);
        var mapped = AnalysisDashboardSetupJson.CreateDocument(setup, "a", [query], Inventory(), Resolver(), false);
        Assert.Equal("a", mapped.ViewFilters!.WorkspaceKey);
        Assert.Equal(Filters.Domain, mapped.ViewFilters.Domain);
        Assert.True(AnalysisDashboardSetupJson.Preview(setup, "", [query], Inventory(), Resolver()).CanImport);
    }

    [Fact]
    public void MissingFixedSystemsAreRetainedOnlyAfterExplicitAcknowledgement()
    {
        var query = Query(Guid.NewGuid());
        var setup = AnalysisDashboardSetupJson.Parse(AnalysisDashboardSetupJson.Export(Document(query.Id), Filters, [query]));
        var preview = AnalysisDashboardSetupJson.Preview(setup, "a", [query], [], Resolver());
        Assert.True(preview.CanImport);
        Assert.True(preview.HasInventoryWarnings);
        Assert.Equal("DC01", Assert.Single(preview.MissingSystems).Computer);
        Assert.Throws<AnalysisDashboardSetupException>(() =>
            AnalysisDashboardSetupJson.CreateDocument(setup, "a", [query], [], Resolver(), false));
        Assert.Equal("DC01", Assert.Single(AnalysisDashboardSetupJson.CreateDocument(
            setup, "a", [query], [], Resolver(), true).Systems).Computer);
    }

    [Fact]
    public void InventoryRolesIncludingFutureRolesRemainDynamicAndNeedAcknowledgementWhenEmpty()
    {
        var query = Query(Guid.NewGuid());
        var document = Document(query.Id);
        document.SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles;
        document.InventoryRoles = ["Domain Controller", "Future Role"];
        var setup = AnalysisDashboardSetupJson.Parse(AnalysisDashboardSetupJson.Export(document, Filters, [query]));
        var preview = AnalysisDashboardSetupJson.Preview(setup, "a", [query], Inventory(), Resolver());
        Assert.Equal(["Future Role"], preview.EmptyRoles);
        var imported = AnalysisDashboardSetupJson.CreateDocument(setup, "a", [query], Inventory(), Resolver(), true);
        Assert.Equal(AnalysisDashboardSelectionMode.InventoryRoles, imported.SelectionMode);
        Assert.Equal(document.InventoryRoles, imported.InventoryRoles);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void MalformedOrIncompleteJsonIsRejected(string json) =>
        Assert.Equal(AnalysisDashboardSetupError.InvalidJson,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(json)).Code);

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    public void UnsupportedEnvelopeIsRejected(string field)
    {
        var node = JsonNode.Parse(Export())!;
        node[field] = field == "format" ? JsonValue.Create("other-format") : JsonValue.Create(2);
        Assert.Equal(AnalysisDashboardSetupError.UnsupportedVersion,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString())).Code);
    }

    [Theory]
    [InlineData("kql")]
    [InlineData("queryDefinitionId")]
    [InlineData("owner")]
    [InlineData("published")]
    public void ExtraFieldsIncludingInlineQueriesAndPermissionsAreRejected(string field)
    {
        var node = JsonNode.Parse(Export())!;
        node[field] = "not allowed";
        Assert.Equal(AnalysisDashboardSetupError.InvalidJson,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString())).Code);
    }

    [Fact]
    public void DuplicatePropertiesAreRejectedAtTheRootAndInsideWidgets()
    {
        var json = Export();
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(
            json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1")));
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(
            json.Replace("\"column\": 2", "\"column\": 2, \"column\": 2")));
    }

    [Theory]
    [InlineData("columns", 1)]
    [InlineData("columns", 7)]
    [InlineData("rows", 121)]
    [InlineData("rowHeight", 133)]
    [InlineData("refreshSeconds", 7)]
    [InlineData("binMinutesOverride", 5)]
    public void InvalidSettingsAreRejectedRatherThanNormalized(string field, int value)
    {
        var node = JsonNode.Parse(Export())!;
        node[field] = value;
        Assert.Equal(AnalysisDashboardSetupError.InvalidSettings,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString())).Code);
    }

    [Fact]
    public void NullListsOverlapsOutOfBoundsAndWidgetLimitsAreRejected()
    {
        var node = JsonNode.Parse(Export())!;
        node["systems"] = null;
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString()));
        node = JsonNode.Parse(Export())!;
        node["widgets"]!.AsArray().Add(node["widgets"]![0]!.DeepClone());
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString()));
        node = JsonNode.Parse(Export())!;
        node["widgets"]![0]!["rowSpan"] = 11;
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString()));
        node = JsonNode.Parse(Export())!;
        var widgets = node["widgets"]!.AsArray();
        while (widgets.Count < 13) widgets.Add(widgets[0]!.DeepClone());
        Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(node.ToJsonString()));
    }

    [Fact]
    public void SizeLimitIsMeasuredInUtf8BytesAndAcceptsTheExactBoundary()
    {
        var json = Export();
        json += new string(' ', AnalysisDashboardSetupJson.MaxBytes - Encoding.UTF8.GetByteCount(json));
        Assert.NotNull(AnalysisDashboardSetupJson.Parse(json));
        Assert.Equal(AnalysisDashboardSetupError.TooLarge,
            Assert.Throws<AnalysisDashboardSetupException>(() => AnalysisDashboardSetupJson.Parse(json + " ")).Code);
        Assert.Equal(AnalysisDashboardSetupError.TooLarge, Assert.Throws<AnalysisDashboardSetupException>(() =>
            AnalysisDashboardSetupJson.Parse(new string('\u00e9', AnalysisDashboardSetupJson.MaxBytes / 2 + 1))).Code);
    }

    [Fact]
    public void CollectionImportPreservesAllExistingDocumentsAndLibraryPreferencesAndHandlesNameCollisions()
    {
        var source = Document();
        var collection = AnalysisDashboardCollection.Create(source);
        collection.HiddenQueryIds = [Guid.NewGuid()];
        var original = JsonSerializer.Serialize(collection.Dashboards[0]);
        var hidden = collection.HiddenQueryIds.ToArray();
        collection.AddImported(source);
        Assert.Equal(original, JsonSerializer.Serialize(collection.Dashboards[0]));
        Assert.Equal(hidden, collection.HiddenQueryIds);
        Assert.Equal(source.Name + " (import)", collection.Active.Document.Name);
        Assert.NotEqual(collection.Dashboards[0].Id, collection.ActiveDashboardId);
        Assert.NotEqual(source.Widgets[0].Id, collection.Active.Document.Widgets[0].Id);
        Assert.Null(collection.Active.Document.SourceTemplateId);
        collection.AddImported(source);
        Assert.Equal(source.Name + " (import 2)", collection.Active.Document.Name);
    }

    private static string Export()
    {
        var document = Document();
        return AnalysisDashboardSetupJson.Export(document, Filters, [Query(document.Widgets[0].QueryDefinitionId)]);
    }

    private static AnalysisDashboardDocument Document(Guid? queryId = null) => new()
    {
        Name = "Dashboard <test> &", Columns = 6, Rows = 24, RowHeight = 104,
        RefreshSeconds = 300, RefreshPaused = true, AutoRefreshEnabled = true,
        TimeRangeMinutesOverride = 2880, BinMinutesOverride = 30,
        SystemsPanel = new() { Width = 420, Height = 360 },
        QueryLibraryPanel = new() { Width = 480, Height = 400 },
        Systems = [new() { SubscriptionId = Subscription, Computer = "DC01" }],
        SourceTemplateId = Guid.NewGuid(), SourceTemplateVersion = 8, HiddenQueryIds = [Guid.NewGuid()],
        Widgets = [new()
        {
            QueryDefinitionId = queryId ?? Guid.NewGuid(), Title = "Widget <test>", Accent = "violet",
            TimeRangeMinutes = 60, BinMinutes = 15, GroupBy = "NC",
            Column = 2, Row = 5, ColumnSpan = 3, RowSpan = 6
        }]
    };

    private static AnalysisQueryDefinition Query(Guid id) => new()
    {
        Id = id, Key = "query.with spaces", Kql = "KQL MUST NOT BE EXPORTED",
        PublishedVersionId = Guid.NewGuid(), Workspaces = [new() { WorkspaceKey = "a", Enabled = true }]
    };

    private static ComputerInventoryEntry[] Inventory() =>
        [new() { SubscriptionId = Subscription, Computer = "DC01", Domain = "example.test", Role = "Domain Controller" }];
    private static TargetWorkspaceResolver Resolver() => new(new()
    {
        Workspaces = [new()
        {
            Key = "a", WorkspaceId = Guid.NewGuid().ToString(),
            SubscriptionIds = [Subscription], Domains = ["example.test"]
        }]
    });
}
