using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.ClientModel.Primitives;
using System.IO.Compression;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Services.EFCore.Migrations;

namespace MonitoringApp.Tests;

public sealed class QueryStandardTextProvenanceTests
{
    [Theory]
    [InlineData("en-US", "name", false)]
    [InlineData("de-DE", "name", false)]
    [InlineData("en-US", "description", false)]
    [InlineData("de-DE", "description", false)]
    [InlineData("en-US", "name", true)]
    [InlineData("de-DE", "name", true)]
    [InlineData("en-US", "description", true)]
    [InlineData("de-DE", "description", true)]
    public async Task DatabaseBackedExecutionUsesItsPublishedOriginAcrossConcurrentRenameAndRepublish(
        string culture, string field, bool legacyVersion)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin");
        var query = (await fixture.Registry.ListAsync()).Single(query => query.Key == "ad-processor");
        await fixture.MarkValidated(query.Id);
        await fixture.Registry.PublishAsync(query.Id, "admin");
        await fixture.AddExecutionInventory();
        if (legacyVersion) await fixture.ClearVersionOrigin(query.Id);
        query = (await fixture.Registry.ListAsync()).Single(item => item.Id == query.Id);
        var originalVersion = Assert.Single(query.Versions);
        var originalSnapshot = originalVersion.SnapshotJson;
        var originalOrigin = originalVersion.StandardTextProvenanceJson;
        var client = new AcceptanceClient();
        var execution = fixture.Execution(client);
        var request = new AnalysisQueryRequest(query.Id, [new(AcceptanceSubscription, "Acceptance-DC")], 60);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var display = services.GetRequiredService<QueryTextPresentation>();
        var text = services.GetRequiredService<UiText>();
        var pending = execution.ExecuteAsync(request);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (field == "name") query.Name = "Own processor <name>";
            else query.Description = "Own processor <description>";
            await fixture.Registry.SaveDraftAsync(query, "admin");
            await fixture.MarkValidated(query.Id);
            await fixture.Registry.PublishAsync(query.Id, "admin");
        }
        finally { client.Release.TrySetResult(); }
        var result = await pending;
        Assert.Equal(1, result.Version);
        Assert.Equal(Processor.Name, result.Name);
        Assert.Equal(legacyVersion ? Processor.Name : text[Processor.NameResourceKey], display.Name(result));
        Assert.Equal(originalOrigin, result.StandardTextDisplayContext!.ProvenanceJson);
        Assert.Contains(Processor.Name, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("standardText", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        var next = await execution.ExecuteAsync(request);
        Assert.Equal(2, next.Version);
        Assert.Equal(field == "name" ? query.Name : text[Processor.NameResourceKey], display.Name(next));
        query = (await fixture.Registry.ListAsync()).Single(item => item.Id == query.Id);
        var old = query.Versions.Single(version => version.Id == originalVersion.Id);
        Assert.Equal(originalSnapshot, old.SnapshotJson);
        Assert.Equal(originalOrigin, old.StandardTextProvenanceJson);
        query.Name = Processor.Name;
        query.Description = Processor.Description!;
        await fixture.Registry.SaveDraftAsync(query, "admin");
        await fixture.MarkValidated(query.Id);
        await fixture.Registry.PublishAsync(query.Id, "admin");
        var reverted = await execution.ExecuteAsync(request);
        Assert.Equal(3, reverted.Version);
        Assert.Equal(field == "name" ? Processor.Name : text[Processor.NameResourceKey], display.Name(reverted));
        var published = AnalysisQueryRegistry.CreatePublishedDefinition(
            (await fixture.Registry.ListAsync()).Single(item => item.Id == query.Id));
        Assert.Equal(field == "description" ? Processor.Description : text[Processor.DescriptionResourceKey!],
            display.Description(published));
        Assert.Equal(3, client.Calls);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task TrustedSqlBackedLibraryExportsNeutralJsonAndReimportCannotRetainItsOrigin(string culture)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin");
        var exporter = new AnalysisQueryExportService(fixture.Registry);
        static async Task<Dictionary<string, string>> Entries(byte[] zip)
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                entries.Add(entry.FullName, await reader.ReadToEndAsync());
            }
            return entries;
        }
        var original = await Entries(await exporter.ExportAsync());
        using (new CultureScope(culture == "de-DE" ? "en-US" : "de-DE"))
        {
            var opposite = await Entries(await exporter.ExportAsync());
            Assert.Equal(original.Keys.Order(), opposite.Keys.Order());
            foreach (var (name, json) in original) Assert.Equal(json, opposite[name]);
        }
        Assert.Equal(39, original.Count);
        var processorJson = original["ad-processor.json"];
        Assert.DoesNotContain("provenance", processorJson, StringComparison.OrdinalIgnoreCase);
        var imported = AnalysisQueryTemplates.Parse(processorJson);
        Assert.Equal(Processor.Name, imported.Name);
        Assert.Equal(Processor.Description, imported.Description);
        Assert.Null(imported.StandardTextProvenanceJson);
        imported.Key = "own-reimported-processor";
        imported.Workspaces = fixture.Assignments();
        var stored = await fixture.Registry.SaveDraftAsync(imported, "admin");
        Assert.Null(stored.StandardTextProvenanceJson);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        Assert.Equal(Processor.Name, services.GetRequiredService<QueryTextPresentation>().Name(stored));
        Assert.Equal(Processor.Description, services.GetRequiredService<QueryTextPresentation>().Description(stored));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ActualScheduledCardsEditorAndDeleteKeepOriginalInputsAndIndependentNames(string culture, bool trusted)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var template = ScheduledQueryTemplateDraft.Create("cpu-high.json");
        var rule = template.Rule;
        rule.WorkspaceKeys = ["production"];
        if (trusted) await fixture.Scheduled.SaveTemplateDraftAsync(template);
        else await fixture.Scheduled.SaveAsync(rule);
        var stored = Assert.Single(await fixture.Scheduled.ListAsync());
        var original = JsonSerializer.Serialize(stored);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).AddSingleton(fixture.Scheduled)
            .AddSingleton(Fixture.Options).BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        var expected = trusted ? text[QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high").NameResourceKey] : stored.Name;
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = activator.Page!;
            Assert.Contains($"<h3>{expected}</h3>", System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
            foreach (var eventId in ScheduledQueryRule.EventIds)
                Assert.Contains(text[QueryTemplateCatalog.GetActive($"kql/scheduled/event-{eventId}").NameResourceKey],
                    System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
            await Invoke(page, "EditAsync", stored);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(page, []);
            var editing = Field<ScheduledQueryRule>(page, "editing");
            Assert.Equal(stored.StandardTextProvenanceJson, editing.StandardTextProvenanceJson);
            var html = System.Net.WebUtility.HtmlDecode(root.ToHtmlString());
            Assert.Contains($"<h2>{expected}</h2>", html);
            Assert.Contains($"value=\"{stored.Name}\"", html);
            page.GetType().GetField("deleteRule", Flags)!.SetValue(page, stored);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(page, []);
            Assert.Contains(text.Format("AdminScheduledQueries.DeleteBody", expected),
                System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
            Assert.Equal(stored.Name, editing.Name);
        });
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(await fixture.Scheduled.ListAsync())));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ActualScheduledEditorKeepsOriginalInputAndCapturesItsNameChangeCallback(string culture, bool rename)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).AddSingleton(fixture.Scheduled)
            .AddSingleton(Fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = activator.Page!;
            await Invoke(page, "BeginNew", false);
            var rule = Field<ScheduledQueryRule>(page, "editing");
            var original = rule.Name;
            Assert.Equal("DC CPU usage above 90%", original);
            if (rename)
            {
                rule.Name = "My rule";
                page.GetType().GetMethod("TemplateNameChanged", Flags)!.Invoke(page, []);
                rule.Name = original;
                page.GetType().GetMethod("TemplateNameChanged", Flags)!.Invoke(page, []);
            }
            await Invoke(page, "SaveAsync");
            var stored = Assert.Single(await fixture.Scheduled.ListAsync());
            Assert.Equal(original, stored.Name);
            Assert.Equal(rename ? Array.Empty<string>() : new[] { "name" },
                StandardQueryTextProvenance.Parse(stored.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
            Assert.Null(page.GetType().GetField("templateDraft", Flags)!.GetValue(page));
        });
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ActualEditorDiscardsPendingOriginOnCancelOrImportWithoutBorrowingItsStandardName(string culture, bool import)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).AddSingleton(fixture.Scheduled)
            .AddSingleton(Fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = activator.Page!;
            await Invoke(page, "BeginNew", false);
            var context = Field<ScheduledQueryTemplateDraft>(page, "templateDraft");
            var name = context.Rule.Name;
            if (import)
                await Invoke(page, "ImportJsonAsync", QueryTemplateCatalog.ReadPackagedTemplate(
                    QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high")));
            else
            {
                page.GetType().GetMethod("CancelEdit", Flags)!.Invoke(page, []);
                await Invoke(page, "BeginNew", new object?[] { null });
                Field<ScheduledQueryRule>(page, "editing").Name = name;
                Field<ScheduledQueryRule>(page, "editing").Kql = context.Rule.Kql;
            }
            Assert.Throws<InvalidOperationException>(() => context.CaptureProvenance());
            Assert.Null(page.GetType().GetField("templateDraft", Flags)!.GetValue(page));
            await Invoke(page, "SaveAsync");
            var stored = Assert.Single(await fixture.Scheduled.ListAsync());
            Assert.Equal(name, stored.Name);
            Assert.Null(stored.StandardTextProvenanceJson);
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task TrustedLibraryMarksOnlyNewDefinitionsAndNeverBackfillsExistingMatches(string culture)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var imported = AnalysisQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(Processor));
        imported.Workspaces = fixture.Assignments();
        imported.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(Processor);
        var own = await fixture.Registry.SaveDraftAsync(imported, "admin");
        Assert.Null(own.StandardTextProvenanceJson);
        Assert.Equal(38, await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin"));
        var all = await fixture.Registry.ListAsync();
        Assert.Equal(39, all.Count);
        Assert.Null(all.Single(query => query.Id == own.Id).StandardTextProvenanceJson);
        foreach (var query in all.Where(query => query.Id != own.Id))
        {
            var provenance = StandardQueryTextProvenance.Parse(query.StandardTextProvenanceJson!, false);
            Assert.Equal("kql/ad/" + query.Key, provenance.TemplateId);
            Assert.Equal(new[] { "name", "description" }, provenance.UnmodifiedFieldIds);
            Assert.Equal(QueryTemplateCatalog.Get(provenance.TemplateId, provenance.TemplateRevision).Name, query.Name);
        }
        Assert.Equal(0, await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin"));
        Assert.Null((await fixture.Registry.ListAsync()).Single(query => query.Id == own.Id).StandardTextProvenanceJson);
    }

    [Theory]
    [InlineData("en-US", "name")]
    [InlineData("de-DE", "name")]
    [InlineData("en-US", "description")]
    [InlineData("de-DE", "description")]
    public async Task RenameInvalidationIsPerFieldIrreversibleAndIndependentOfPublishedVersions(string culture, string field)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin");
        var query = (await fixture.Registry.ListAsync()).Single(query => query.Key == "ad-processor");
        await fixture.MarkValidated(query.Id);
        await fixture.Registry.PublishAsync(query.Id, "admin");
        query = (await fixture.Registry.ListAsync()).Single(item => item.Id == query.Id);
        var originalMetadata = query.StandardTextProvenanceJson;
        var originalSnapshot = Assert.Single(query.Versions).SnapshotJson;
        if (field == "name") query.Name = "My processor";
        else query.Description = "My description";
        query.StandardTextProvenanceJson = """{"untrusted":true}""";
        await fixture.Registry.SaveDraftAsync(query, "admin");
        var saved = (await fixture.Registry.ListAsync()).Single(item => item.Id == query.Id);
        Assert.Equal(new[] { field == "name" ? "description" : "name" },
            StandardQueryTextProvenance.Parse(saved.StandardTextProvenanceJson!, false).UnmodifiedFieldIds);
        Assert.Equal(originalMetadata, Assert.Single(saved.Versions).StandardTextProvenanceJson);
        Assert.Equal(originalSnapshot, Assert.Single(saved.Versions).SnapshotJson);
        var published = AnalysisQueryRegistry.CreatePublishedDefinition(saved);
        Assert.Equal(Processor.Name, published.Name);
        Assert.Equal(originalMetadata, published.StandardTextProvenanceJson);
        saved.Name = Processor.Name;
        saved.Description = Processor.Description!;
        saved.Kql += "\n| take 1";
        saved.Category = "Custom category";
        await fixture.Registry.SaveDraftAsync(saved, "admin");
        saved = (await fixture.Registry.ListAsync()).Single(item => item.Id == saved.Id);
        Assert.DoesNotContain(field, StandardQueryTextProvenance.Parse(saved.StandardTextProvenanceJson!, false).UnmodifiedFieldIds);
        await fixture.MarkValidated(saved.Id);
        await fixture.Registry.PublishAsync(saved.Id, "admin");
        saved = (await fixture.Registry.ListAsync()).Single(item => item.Id == saved.Id);
        Assert.DoesNotContain(field, StandardQueryTextProvenance.Parse(
            AnalysisQueryRegistry.CreatePublishedDefinition(saved).StandardTextProvenanceJson!, false).UnmodifiedFieldIds);
        Assert.Equal(originalSnapshot, saved.Versions.Single(version => version.Version == 1).SnapshotJson);
        Assert.Equal(Processor.Name, AnalysisQueryTemplates.Parse(originalSnapshot).Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ScheduledTemplateContextSurvivesCopiesButNotRenameAndRevertBeforeFirstSave(string culture, bool rename)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var draft = ScheduledQueryTemplateDraft.Create("cpu-high.json");
        var originalName = draft.Rule.Name;
        draft.Rule.WorkspaceKeys = ["production"];
        if (rename)
        {
            draft.Rule.Name = "My rule";
            draft.ObserveName();
            draft.Rule.Name = originalName;
            draft.ObserveName();
        }
        var copy = ScheduledQueryExecution.CopyDefinition(draft.Rule);
        Assert.Null(copy.StandardTextProvenanceJson);
        Assert.Equal(originalName, copy.Name);
        await fixture.Scheduled.SaveTemplateDraftAsync(draft);
        var stored = Assert.Single(await fixture.Scheduled.ListAsync());
        Assert.Equal(originalName, stored.Name);
        Assert.False(stored.Enabled);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(rename ? Array.Empty<string>() : new[] { "name" },
            StandardQueryTextProvenance.Parse(stored.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scheduled.SaveTemplateDraftAsync(draft));
        Assert.Equal(1, Assert.Single(await fixture.Scheduled.ListAsync()).Revision);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ScheduledNormalImportsCannotForgeOriginAndExistingRenamesCannotRestoreIt(string culture)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var entry = QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high");
        var imported = ScheduledQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(entry));
        imported.WorkspaceKeys = ["production"];
        imported.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(entry);
        await fixture.Scheduled.SaveAsync(imported);
        var own = Assert.Single(await fixture.Scheduled.ListAsync());
        Assert.Null(own.StandardTextProvenanceJson);
        var trusted = ScheduledQueryTemplateDraft.Create("cpu-high.json");
        trusted.Rule.Name = "Second CPU rule";
        trusted.ObserveName();
        trusted.Rule.WorkspaceKeys = ["production"];
        await fixture.Scheduled.SaveTemplateDraftAsync(trusted);
        var custom = (await fixture.Scheduled.ListAsync()).Single(rule => rule.Id == trusted.Rule.Id);
        Assert.Empty(StandardQueryTextProvenance.Parse(custom.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
        custom.Name = "CPU rule with own name";
        await fixture.Scheduled.SaveAsync(custom);
        custom = (await fixture.Scheduled.ListAsync()).Single(rule => rule.Id == trusted.Rule.Id);
        custom.Name = "Second CPU rule";
        custom.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(entry);
        await fixture.Scheduled.SaveAsync(custom);
        custom = (await fixture.Scheduled.ListAsync()).Single(rule => rule.Id == trusted.Rule.Id);
        Assert.Empty(StandardQueryTextProvenance.Parse(custom.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
    }

    [Fact]
    public async Task FailedTemplateSaveCanRetryButCannotMarkAnExistingRule()
    {
        await using var fixture = await Fixture.Create();
        var draft = ScheduledQueryTemplateDraft.Create("cpu-high.json");
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Scheduled.SaveTemplateDraftAsync(draft));
        Assert.Empty(await fixture.Scheduled.ListAsync());
        draft.Rule.WorkspaceKeys = ["production"];
        await fixture.Scheduled.SaveAsync(draft.Rule);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Scheduled.SaveTemplateDraftAsync(draft));
        Assert.Null(Assert.Single(await fixture.Scheduled.ListAsync()).StandardTextProvenanceJson);
        draft.Complete();
        Assert.Throws<InvalidOperationException>(() => draft.CaptureProvenance());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ExistingScheduledStandardLosesItsNamePermissionOnRenameAndCannotRestoreIt(string culture)
    {
        using var selection = new CultureScope(culture);
        await using var fixture = await Fixture.Create();
        var draft = ScheduledQueryTemplateDraft.Create("cpu-high.json");
        draft.Rule.WorkspaceKeys = ["production"];
        await fixture.Scheduled.SaveTemplateDraftAsync(draft);
        var stored = Assert.Single(await fixture.Scheduled.ListAsync());
        var name = stored.Name;
        Assert.Equal(new[] { "name" }, StandardQueryTextProvenance.Parse(stored.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
        stored.Name = "Own CPU rule";
        await fixture.Scheduled.SaveAsync(stored);
        stored = Assert.Single(await fixture.Scheduled.ListAsync());
        Assert.Empty(StandardQueryTextProvenance.Parse(stored.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
        stored.Name = name;
        stored.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(
            QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high"));
        await fixture.Scheduled.SaveAsync(stored);
        stored = Assert.Single(await fixture.Scheduled.ListAsync());
        Assert.Equal(name, stored.Name);
        Assert.Empty(StandardQueryTextProvenance.Parse(stored.StandardTextProvenanceJson!, true).UnmodifiedFieldIds);
    }

    [Fact]
    public void ProvenanceIsNeverPartOfExistingEntityOrSnapshotJsonContracts()
    {
        var metadata = StandardQueryTextProvenance.Create(Processor);
        var query = AnalysisQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(Processor));
        query.StandardTextProvenanceJson = metadata;
        var rule = ScheduledQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(
            QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high")));
        rule.StandardTextProvenanceJson = metadata;
        var version = new AnalysisQueryVersion { StandardTextProvenanceJson = metadata };
        foreach (var json in new[]
        {
            JsonSerializer.Serialize(query), JsonSerializer.Serialize(rule),
            JsonSerializer.Serialize(version), JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
        })
            Assert.DoesNotContain("provenance", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(JsonSerializer.Deserialize<AnalysisQueryDefinition>(
            """{"Name":"own","StandardTextProvenanceJson":"untrusted"}""")!.StandardTextProvenanceJson);
        Assert.Null(JsonSerializer.Deserialize<ScheduledQueryRule>(
            """{"Name":"own","StandardTextProvenanceJson":"untrusted"}""")!.StandardTextProvenanceJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedStoredProvenanceFailsTheWriteWithoutReplacingConfirmedNames(bool scheduled)
    {
        await using var fixture = await Fixture.Create();
        if (scheduled)
        {
            var draft = ScheduledQueryTemplateDraft.Create("cpu-high.json");
            draft.Rule.WorkspaceKeys = ["production"];
            await fixture.Scheduled.SaveTemplateDraftAsync(draft);
            await fixture.CorruptProvenance(draft.Rule.Id, true);
            var stored = Assert.Single(await fixture.Scheduled.ListAsync());
            var originalName = stored.Name;
            stored.Name = "Unconfirmed own name";
            stored.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(
                QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scheduled.SaveAsync(stored));
            var retained = Assert.Single(await fixture.Scheduled.ListAsync());
            Assert.Equal(originalName, retained.Name);
            Assert.Equal("{", retained.StandardTextProvenanceJson);
        }
        else
        {
            await fixture.Registry.InstallActiveDirectoryLibraryAsync("admin");
            var stored = (await fixture.Registry.ListAsync()).Single(query => query.Key == "ad-processor");
            await fixture.CorruptProvenance(stored.Id, false);
            stored.Name = "Unconfirmed own name";
            stored.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(Processor);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.SaveDraftAsync(stored, "admin"));
            var retained = (await fixture.Registry.ListAsync()).Single(query => query.Id == stored.Id);
            Assert.Equal(Processor.Name, retained.Name);
            Assert.Equal("{", retained.StandardTextProvenanceJson);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":1,"templateId":"unknown","templateRevision":"unknown","unmodifiedFieldIds":[]}""")]
    public void InvalidProvenanceFailsExplicitly(string json) =>
        Assert.Throws<InvalidOperationException>(() => StandardQueryTextProvenance.Parse(json, false));

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("unmodifiedFieldIds", "[\"name\",\"name\"]")]
    [InlineData("unmodifiedFieldIds", "[\"Name\"]")]
    [InlineData("unmodifiedFieldIds", "[\"kql\"]")]
    [InlineData("unmodifiedFieldIds", "[null]")]
    [InlineData("unmodifiedFieldIds", "null")]
    [InlineData("untrusted", "true")]
    public void InvalidProvenanceFieldsCannotGrantDisplayPermission(string field, string value)
    {
        var node = JsonNode.Parse(StandardQueryTextProvenance.Create(Processor))!;
        node[field] = JsonNode.Parse(value);
        Assert.Throws<InvalidOperationException>(() => StandardQueryTextProvenance.Parse(node.ToJsonString(), false));
    }

    [Fact]
    public void NullOriginsRemainUnknownAndCrossKindMetadataIsRejected()
    {
        Assert.Null(StandardQueryTextProvenance.Retain(null, false, Processor.Name, Processor.Description, Processor.Name, Processor.Description));
        var metadata = StandardQueryTextProvenance.Create(Processor);
        Assert.Throws<InvalidOperationException>(() => StandardQueryTextProvenance.Parse(metadata, true));
        var changedBeforeSave = StandardQueryTextProvenance.Retain(metadata, false,
            "Own name already in SQL", Processor.Description, Processor.Name, Processor.Description);
        Assert.Equal(new[] { "description" }, StandardQueryTextProvenance.Parse(changedBeforeSave!, false).UnmodifiedFieldIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu-high.txt")]
    [InlineData(@"..\cpu-high.json")]
    [InlineData(@"C:\temp\cpu-high.json")]
    [InlineData("not-packaged.json")]
    public void AlternativeFilesCannotBecomeTrustedScheduledDrafts(string fileName) =>
        Assert.Throws<InvalidOperationException>(() => ScheduledQueryTemplateDraft.Create(fileName));

    [Fact]
    public void MigrationAddsOnlyThreeNullableColumnsAndNoBackfill()
    {
        var migration = new AddQueryStandardTextProvenance();
        var tables = new[] { "AnalysisQueryDefinitions", "AnalysisQueryVersions", "ScheduledQueryRules" };
        Assert.Equal(tables.Order(), migration.UpOperations.Select(operation => Assert.IsType<AddColumnOperation>(operation).Table).Order());
        Assert.Equal(tables.Order(), migration.DownOperations.Select(operation => Assert.IsType<DropColumnOperation>(operation).Table).Order());
        foreach (var operation in migration.UpOperations)
        {
            var column = Assert.IsType<AddColumnOperation>(operation);
            Assert.True(column.IsNullable);
            Assert.Equal("StandardTextProvenanceJson", column.Name);
            Assert.Equal("nvarchar(max)", column.ColumnType);
            Assert.Null(column.DefaultValue);
            Assert.Null(column.DefaultValueSql);
        }
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        foreach (var table in tables)
            Assert.Contains($"IF COL_LENGTH(N'dbo.{table}', N'StandardTextProvenanceJson') IS NULL", sql);
        foreach (var entry in QueryTemplateCatalog.Entries.Where(entry => entry.Library is null))
            Assert.Contains("N'" + StandardQueryTextProvenance.Create(entry) + "'", sql);
        Assert.Contains("20261108150142_AddQueryStandardTextProvenance", sql);
    }

    private static QueryTemplateCatalogEntry Processor => QueryTemplateCatalog.GetActive("kql/ad/ad-processor");

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        internal CultureScope(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory) : IAsyncDisposable
    {
        internal static readonly CrossTenantLogAnalyticsOptions Options = new()
        {
            Enabled = true,
            Workspaces = [new() { Key = "production", DisplayName = "Production", WorkspaceId = "22222222-2222-2222-2222-222222222222" }]
        };
        private static AnalysisQueryRunner Runner() =>
            new(Options, new Secret(), new TargetWorkspaceResolver(Options));
        internal AnalysisQueryRegistry Registry { get; } = new(factory, Options, Runner());
        internal ScheduledQueryService Scheduled { get; } = new(factory, Options, new(Options), Runner(),
            new AlertStore(factory, new(true, null, new SqlConnectionStringBuilder()),
                new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")),
                new(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"))),
                NullLogger<AlertStore>.Instance),
            TimeProvider.System, NullLogger<ScheduledQueryService>.Instance);

        internal List<AnalysisQueryWorkspace> Assignments() => [new() { WorkspaceKey = "production", Enabled = true }];

        internal AnalysisQueryExecutionService Execution(AcceptanceClient client)
        {
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = true,
                Workspaces = [new()
                {
                    Key = "production", DisplayName = "Production",
                    SubscriptionIds = [AcceptanceSubscription]
                }]
            };
            return new(factory, new(options, new AcceptanceSecret(), new TargetWorkspaceResolver(options), client));
        }

        internal async Task AddExecutionInventory()
        {
            await using var context = factory.CreateDbContext();
            context.ComputerInventory.Add(new() { Computer = "Acceptance-DC", SubscriptionId = AcceptanceSubscription });
            await context.SaveChangesAsync();
        }

        internal async Task ClearVersionOrigin(Guid id)
        {
            await using var context = factory.CreateDbContext();
            (await context.AnalysisQueryVersions.SingleAsync(version => version.QueryDefinitionId == id))
                .StandardTextProvenanceJson = null;
            await context.SaveChangesAsync();
        }

        internal static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            var tables = new[] { "AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions",
                "ScheduledQueryRules", "ScheduledQueryTargetStates", "ScheduledQueryNotifications", "ComputerInventory" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => tables.Any(table => statement.TrimStart().StartsWith($"CREATE TABLE \"{table}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            return new(connection, factory);
        }

        internal async Task MarkValidated(Guid id)
        {
            await using var context = factory.CreateDbContext();
            var query = await context.AnalysisQueryDefinitions.Include(query => query.Workspaces).SingleAsync(query => query.Id == id);
            query.Status = AnalysisQueryStatuses.Validated;
            foreach (var workspace in query.Workspaces) workspace.LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded;
            await context.SaveChangesAsync();
        }

        internal async Task CorruptProvenance(Guid id, bool scheduled)
        {
            await using var context = factory.CreateDbContext();
            if (scheduled)
                (await context.ScheduledQueryRules.SingleAsync(rule => rule.Id == id)).StandardTextProvenanceJson = "{";
            else
                (await context.AnalysisQueryDefinitions.SingleAsync(query => query.Id == id)).StandardTextProvenanceJson = "{";
            await context.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class Secret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provenance persistence must not contact Azure.");
    }

    private const string AcceptanceSubscription = "11111111-1111-1111-1111-111111111111";
    private sealed class AcceptanceSecret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("local-test");
    }

    private sealed class AcceptanceClient : IAnalysisLogAnalyticsQuery
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async Task<LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Assert.Contains("Perf", kql);
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Timestamp","type":"datetime"},{"name":"Series","type":"string"},{"name":"Value","type":"real"}],"rows":[["2026-11-08T12:00:00Z","Acceptance-DC",12.5]]}]}"""))!;
        }
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(IComponent component, string name) where T : class =>
        Assert.IsType<T>(component.GetType().GetField(name, Flags)!.GetValue(component));
    private static Task Invoke(IComponent component, string method, params object?[] values) =>
        (Task)component.GetType().GetMethod(method, Flags)!.Invoke(component, values)!;

    private sealed class CapturingActivator : IComponentActivator
    {
        internal Components.Pages.AdminScheduledQueries? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Components.Pages.AdminScheduledQueries page) Page = page;
            return component;
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported test render mode: {renderMode.GetType().Name}");
    }
}
