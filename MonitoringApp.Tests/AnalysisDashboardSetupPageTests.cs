using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed partial class AnalysisDashboardPublishingTests
{
    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task DashboardSetupExportPreviewAndImportPreserveExistingAndAliasDashboards(string culture, bool alias)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, queryClient: client, aliases: alias);
        if (alias)
            await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = HistoricalAlias });
        var ownBefore = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!;
        var existing = ownBefore.Dashboards.ToDictionary(entry => entry.Id, entry => JsonSerializer.Serialize(entry));
        var aliasBefore = alias ? (await page.Storage.Repository.LoadAsync(HistoricalAlias)).Collection!.Serialize() : null;
        Assert.Contains(page.Text["DashboardSetup.Title"], page.Decoded);
        Assert.Contains("accept=\".json,application/json\"", page.Html);
        var cached = JsonSerializer.Serialize(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        await page.Invoke("ExportDashboardSetupAsync");
        var json = page.Get<string>("setupExportJson");
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), page.Html);
        Assert.Contains("download=\"dashboard-setup.json\"", page.Html);
        await page.Set("setupImportJson", json);
        await page.Invoke("PreviewDashboardSetupAsync");
        var preview = page.Get<AnalysisDashboardSetupPreview>("setupPreview");
        Assert.True(preview.CanImport);
        Assert.False(page.Get<bool>("HasUnsavedSetupChanges"));
        Assert.Equal(cached, JsonSerializer.Serialize(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults")));
        Assert.Equal(0, client.Calls);
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Null(page.Get<string?>("setupTransferError"));
        Assert.Contains(page.Text.Format("DashboardSetup.Imported", page.Dashboard.Document.Name), page.Decoded);
        var saved = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(ownBefore.Dashboards.Count + 1, saved.Dashboards.Count);
        Assert.All(existing, entry => Assert.Equal(entry.Value,
            JsonSerializer.Serialize(saved.Dashboards.Single(item => item.Id == entry.Key))));
        Assert.Equal("alice@example.com", page.Get<string>("dashboardOwnerKey"));
        Assert.Equal(saved.ActiveDashboardId, page.Get<AnalysisDashboardCollection>("dashboardCollection").ActiveDashboardId);
        Assert.Equal(AnalysisDashboardSetupJson.Parse(json).SelectionMode, saved.Active.Document.SelectionMode);
        Assert.NotNull(saved.Active.Document.ViewFilters);
        Assert.Null(saved.Active.Document.SourceTemplateId);
        Assert.Equal(0, client.Calls);
        Assert.DoesNotContain(await page.Storage.Publishing.ListAsync(Alice), publication => publication.Id == saved.ActiveDashboardId);
        if (alias)
            Assert.Equal(aliasBefore, (await page.Storage.Repository.LoadAsync(HistoricalAlias)).Collection!.Serialize());
        var importedId = saved.ActiveDashboardId;
        await page.Invoke("LoadWorkspaceAsync");
        Assert.Equal(importedId, page.Get<AnalysisDashboardCollection>("dashboardCollection").ActiveDashboardId);
        Assert.Equal(saved.Active.Document.ViewFilters!.WorkspaceKey, page.Get<string>("workspaceFilter"));
    }

    [Fact]
    public async Task DashboardSetupImportRechecksRemovedQueriesAndDoesNotChangeStorage()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true);
        await page.Invoke("ExportDashboardSetupAsync");
        await page.Set("setupImportJson", page.Get<string>("setupExportJson"));
        await page.Invoke("PreviewDashboardSetupAsync");
        var original = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize();
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.SingleAsync();
            query.Enabled = false;
            await context.SaveChangesAsync();
        }
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Equal(page.Text["DashboardSetup.Errors.DependenciesUnavailable"], page.Get<string>("setupTransferError"));
        Assert.False(page.Get<AnalysisDashboardSetupPreview>("setupPreview").CanImport);
        Assert.Equal(original, (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize());
    }

    [Fact]
    public async Task DashboardSetupImportDoesNotDiscardOrSaveUnsavedDashboardChanges()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true);
        await page.Invoke("ExportDashboardSetupAsync");
        await page.Set("setupImportJson", page.Get<string>("setupExportJson"));
        await page.Invoke("PreviewDashboardSetupAsync");
        var original = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize();
        page.Dashboard.UpdateWidget(page.Dashboard.Document.Widgets.Single().Id, widget => widget.Title = "Unsaved title");
        var unsaved = JsonSerializer.Serialize(page.Dashboard.Document);
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Equal(page.Text["DashboardSetup.UnsavedChanges"], page.Get<string>("setupTransferError"));
        Assert.Equal(original, (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize());
        Assert.Equal(unsaved, JsonSerializer.Serialize(page.Dashboard.Document));
    }

    [Fact]
    public async Task DashboardSetupFileImportChecksTheActualFileAndPreviewWithoutRunningQueries()
    {
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, queryClient: client);
        await page.Invoke("ExportDashboardSetupAsync");
        var json = page.Get<string>("setupExportJson");
        await page.Invoke("ReadDashboardSetupFileAsync", new InputFileChangeEventArgs([new SetupBrowserFile(json)]));
        Assert.True(page.Get<AnalysisDashboardSetupPreview>("setupPreview").CanImport);
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Null(page.Get<string?>("setupTransferError"));
        Assert.Equal(0, client.Calls);
        await page.Invoke("ReadDashboardSetupFileAsync", new InputFileChangeEventArgs(
            [new SetupBrowserFile(new string(' ', AnalysisDashboardSetupJson.MaxBytes + 1))]));
        Assert.Null(page.Get<AnalysisDashboardSetupPreview?>("setupPreview"));
        Assert.Equal(page.Text["DashboardSetup.Errors.TooLarge"], page.Get<string>("setupTransferError"));
    }

    [Fact]
    public async Task DashboardSetupImportRespectsConcurrentCollectionUpdates()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true);
        await page.Invoke("ExportDashboardSetupAsync");
        await page.Set("setupImportJson", page.Get<string>("setupExportJson"));
        await page.Invoke("PreviewDashboardSetupAsync");
        var stored = await page.Storage.Repository.LoadAsync("alice@example.com");
        stored.Collection!.RenameActive("Changed in another session");
        await page.Storage.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Equal(page.Text["Analysis.Errors.CollectionChanged"], page.Get<string>("setupTransferError"));
        Assert.Equal(stored.Collection.Serialize(), (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize());
    }

    [Fact]
    public async Task DashboardSetupImportKeepsAutoRefreshStoredButRequiresExplicitExecutionInTheCurrentSession()
    {
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, queryClient: client);
        await page.Invoke("ExportDashboardSetupAsync");
        var node = JsonNode.Parse(page.Get<string>("setupExportJson"))!;
        node["autoRefreshEnabled"] = true;
        await page.Set("setupImportJson", node.ToJsonString());
        await page.Invoke("PreviewDashboardSetupAsync");
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Null(page.Get<string?>("setupTransferError"));
        Assert.True(page.Dashboard.Document.AutoRefreshEnabled);
        Assert.True(page.Get<bool>("setupAwaitingExecution"));
        Assert.Contains(page.Text["DashboardSetup.ExecutionPending"], page.Decoded);
        await page.Invoke("RefreshDashboardTickAsync");
        Assert.Equal(0, client.Calls);
        await page.Invoke("RunDashboardAsync");
        Assert.False(page.Get<bool>("setupAwaitingExecution"));
        Assert.Equal(2, client.Calls);
        await page.Invoke("RefreshDashboardTickAsync");
        Assert.Equal(4, client.Calls);
        Assert.True((await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.AutoRefreshEnabled);
    }

    [Fact]
    public async Task DashboardSetupImportRequiresAcknowledgementAgainWhenInventoryWarningsChangeAfterPreview()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true);
        await page.Invoke("ExportDashboardSetupAsync");
        var node = JsonNode.Parse(page.Get<string>("setupExportJson"))!;
        node["inventoryRoles"] = new JsonArray("Future Role", "File Server");
        await page.Set("setupImportJson", node.ToJsonString());
        await page.Invoke("PreviewDashboardSetupAsync");
        Assert.Equal(["Future Role"], page.Get<AnalysisDashboardSetupPreview>("setupPreview").EmptyRoles);
        await page.Set("setupAcknowledgeInventory", true);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.RemoveRange(await context.ComputerInventory.Where(entry => entry.Role == "File Server").ToArrayAsync());
            await context.SaveChangesAsync();
        }
        var original = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize();
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.False(page.Get<bool>("setupAcknowledgeInventory"));
        Assert.Equal(["Future Role", "File Server"], page.Get<AnalysisDashboardSetupPreview>("setupPreview").EmptyRoles);
        Assert.Equal(original, (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Serialize());
        Assert.Equal(page.Text["DashboardSetup.Errors.DependenciesUnavailable"], page.Get<string>("setupTransferError"));
    }

    [Fact]
    public async Task DashboardSetupImportAllowsWorkspaceMappingAfterInventoryLimitPreviewFailure()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true);
        await page.Invoke("ExportDashboardSetupAsync");
        await page.Set("setupImportJson", page.Get<string>("setupExportJson"));
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.AddRange(Enumerable.Range(1, 101).Select(index => new ComputerInventoryEntry
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                Computer = $"EXTRA-{index}", Role = "Domain Controller"
            }));
            await context.SaveChangesAsync();
        }
        await page.Invoke("PreviewDashboardSetupAsync");
        Assert.NotNull(page.Get<AnalysisDashboardSetup>("pendingSetup"));
        Assert.Null(page.Get<AnalysisDashboardSetupPreview?>("setupPreview"));
        Assert.Contains(page.Text["DashboardSetup.TargetWorkspace"], page.Decoded);
        await page.Invoke("SetupWorkspaceChangedAsync", new ChangeEventArgs { Value = "b" });
        Assert.True(page.Get<AnalysisDashboardSetupPreview>("setupPreview").CanImport);
        await page.Set("setupAcknowledgeInventory", true);
        await page.Invoke("ImportDashboardSetupAsync");
        Assert.Null(page.Get<string?>("setupTransferError"));
        Assert.Equal("b", page.Get<string>("workspaceFilter"));
        Assert.Equal("b", (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.ViewFilters!.WorkspaceKey);
    }

    [Fact]
    public async Task DashboardSetupPreparedAliasDataIsClearedWhenAliasAccessIsWithdrawn()
    {
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, aliases: true);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = HistoricalAlias });
        await page.Invoke("ExportDashboardSetupAsync");
        await page.Set("setupImportJson", page.Get<string>("setupExportJson"));
        await page.Invoke("PreviewDashboardSetupAsync");
        Assert.NotNull(page.Get<AnalysisDashboardSetup>("pendingSetup"));
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.HistoricalUserAliases.RemoveRange(await context.HistoricalUserAliases.ToArrayAsync());
            await context.SaveChangesAsync();
        }
        await page.Invoke("ReloadPublicationsAsync");
        Assert.Null(page.Get<string?>("setupExportJson"));
        Assert.Null(page.Get<AnalysisDashboardSetup?>("pendingSetup"));
        Assert.Empty(page.Get<string>("setupImportJson"));
        Assert.Equal(page.Text["AnalysisDashboards.AccessDenied"], page.Get<string>("errorMessage"));
    }

    private sealed class SetupBrowserFile(string json) : IBrowserFile
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(json);
        public string Name => "dashboard-setup.json";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("File exceeds the configured size limit.");
            return new MemoryStream(bytes, writable: false);
        }
    }
}
