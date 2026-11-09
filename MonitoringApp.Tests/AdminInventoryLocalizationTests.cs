using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AdminInventoryLocalizationTests
{
    [Theory]
    [InlineData("en-US", 0, false)]
    [InlineData("en-US", 1, false)]
    [InlineData("en-US", 2, false)]
    [InlineData("de-DE", 0, false)]
    [InlineData("de-DE", 1, false)]
    [InlineData("de-DE", 2, false)]
    [InlineData("en-US", 0, true)]
    [InlineData("en-US", 1, true)]
    [InlineData("en-US", 2, true)]
    [InlineData("de-DE", 0, true)]
    [InlineData("de-DE", 1, true)]
    [InlineData("de-DE", 2, true)]
    public async Task InventoryFrameFiltersCardsAndDisplayValuesKeepStoredDataUnchanged(string culture, int count, bool lifecycle)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            for (var index = 0; index < count; index++)
                context.ComputerInventory.Add(Entry(index));
            await context.SaveChangesAsync();
        }
        var activator = new InventoryActivator();
        var logs = new InventoryLogProvider();
        await using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs)).AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IDbContextFactory<AlertDbContext>>(factory)
            .AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()))
            .AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")))
            .AddSingleton<AlertRuleEvaluator>().AddSingleton<AlertStore>().BuildServiceProvider();
        var store = services.GetRequiredService<AlertStore>();
        var original = JsonSerializer.Serialize(await store.GetComputerInventoryAsync());
        var text = services.GetRequiredService<UiText>();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = renderer.BeginRenderingComponent(typeof(AdminInventory), ParameterView.Empty);
            await page.QuiescenceTask;
            return page;
        });
        var html = await HtmlAsync();
        Assert.Contains(culture == "de-DE" ? "<h1>Computerinventar</h1>" : "<h1>Computer inventory</h1>", html);
        Assert.Contains(culture == "de-DE" ? ">Eintrag erstellen</button>" : ">Create entry</button>", html);
        Assert.Contains(culture == "de-DE" ? ">Aktualisieren</button>" : ">Refresh</button>", html);
        Assert.Contains(text["AdminInventory.Description"], html);
        Assert.Contains("InventoryHeartbeat", html);
        Assert.Contains(text.Plural("AdminInventory.Count", count), html);
        Assert.Contains(text["AdminInventory.SelectSystem"], html);
        foreach (var key in new[] { "AdminInventory.Search", "AdminInventory.SearchPlaceholder", "AdminInventory.Filters",
            "AdminInventory.AllSubscriptions", "AdminInventory.AllResourceGroups", "Filters.AllSites",
            "Filters.AllDomains", "Filters.AllRoles", "AdminInventory.ClearFilters" })
            Assert.Contains(text[key], html);
        Assert.Equal("OperatorAccess", Assert.Single(typeof(AdminInventory).GetCustomAttributes<AuthorizeAttribute>()).Policy);

        if (count == 0)
        {
            Assert.Contains(text["AdminInventory.NoMatches"], html);
            await SetAsync("isLoading", true);
            html = await HtmlAsync();
            Assert.Contains(text["AdminInventory.Loading"], html);
            Assert.Contains(text["Common.Refreshing"], html);
            await SetAsync("isLoading", false);
            await InvokeAsync("LoadInventoryAsync");
        }
        else
        {
            var entries = await store.GetComputerInventoryAsync();
            foreach (var entry in entries)
            {
                Assert.Contains(entry.Computer, html);
                await InvokeAsync("ToggleExpanded", entry);
            }
            html = await HtmlAsync();
            foreach (var key in new[] { "AdminInventory.PrivateIp", "AdminInventory.LastHeartbeat", "AdminInventory.LastBootTime", "AdminInventory.NtdsSize",
                "AdminInventory.Certificates", "AdminInventory.SerialNumber", "AdminInventory.Expires",
                "AdminInventory.Services", "Common.Edit", "Common.Delete" })
                Assert.Contains(text[key], html);
            Assert.Contains("Maintenance", html);
            Assert.Contains("Not available", html);
            Assert.Contains("Running <original service>", html);
            Assert.Contains("Certificate <original>", html);
            Assert.Contains("serial-<original>", html);
            Assert.Contains("2026-11-06 21:30:00 UTC", html);
            Assert.Contains("2026-11-06", html);
            Assert.Contains($"{1234.567:N2} GB", html);
            foreach (var key in new[] { "Stopped", "StartPending", "StopPending", "Running",
                "ContinuePending", "PausePending", "Paused", "Unknown" })
                Assert.Contains(text[$"AdminInventory.Service.{key}"], html);
            foreach (var code in new[] { -1, 0, 99 })
                Assert.Contains(text.Format("AdminInventory.Service.Other", code.ToString(CultureInfo.CurrentCulture)), html);
            if (count == 2)
            {
                Assert.Contains(text["AdminInventory.NotAvailable"], html);
                Assert.Contains(text["AdminInventory.Maintenance"], html);
                Assert.Contains("inventory-maintenance", html);
            }
            var raw = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.DoesNotContain("<original>", raw);
            Assert.DoesNotContain("<original service>", raw);
            foreach (var (field, value) in new[]
            {
                ("subscriptionFilter", "SUB-0"), ("resourceGroupFilter", "REFRESH"),
                ("siteFilter", "ALL SITES"), ("domainFilter", "NOT AVAILABLE"), ("roleFilter", "MAINTENANCE")
            })
            {
                await SetAsync(field, value);
                html = await HtmlAsync();
                Assert.Contains(text.Plural("AdminInventory.Count", 1), html);
                Assert.Contains("Computer <0>", html);
                Assert.DoesNotContain("Computer <1>", html);
                await InvokeAsync("ClearInventoryFilters");
                Assert.Contains(text.Plural("AdminInventory.Count", count), await HtmlAsync());
            }
            await SetAsync("searchText", "does-not-match");
            Assert.Contains(text["AdminInventory.NoMatches"], await HtmlAsync());
            Assert.Contains(text.Plural("AdminInventory.Count", 0), await HtmlAsync());
            await SetAsync("searchText", "not available");
            Assert.Contains(text.Plural("AdminInventory.Count", 1), await HtmlAsync());
            await SetAsync("searchText", "");
            await InvokeAsync("ToggleExpanded", entries[0]);
            Assert.DoesNotContain("Running <original service>", await HtmlAsync());
            await InvokeAsync("ToggleExpanded", entries[0]);
            Assert.Contains("Running <original service>", await HtmlAsync());
        }
        Assert.Equal(original, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
        if (lifecycle)
        {
            await CheckEditorLifecycleAsync();
            await CheckErrorLifecycleAsync();
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);

        async Task CheckEditorLifecycleAsync()
        {
            ComputerInventoryEntry saved;
            if (count == 0)
            {
                await InvokeAsync("BeginCreate");
                Assert.Contains(text["AdminInventory.NoSubscriptions"], await HtmlAsync());
                await CheckEditorAsync(true);
                await using (var context = factory.CreateDbContext())
                {
                    context.Alerts.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "seed", "Original alert", "Sev0",
                        "New", "Log", "Fired", "Original computer", "rg", "sub-new", DateTimeOffset.UtcNow, "", "", "", "{}"));
                    await context.SaveChangesAsync();
                }
                await InvokeAsync("LoadInventoryAsync");
                await InvokeAsync("BeginCreate");
                Assert.Equal("sub-new", Draft().SubscriptionId);
                await using (var context = factory.CreateDbContext())
                {
                    context.Alerts.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "other", "Original alert", "Sev0",
                        "New", "Log", "Fired", "Original computer", "rg", "sub-other", DateTimeOffset.UtcNow, "", "", "", "{}"));
                    await context.SaveChangesAsync();
                }
                await InvokeAsync("LoadInventoryAsync");
                await InvokeAsync("BeginCreate");
                Assert.Equal("", Draft().SubscriptionId);
                Draft().SubscriptionId = "sub-new";
                Draft().Computer = "Create <manual>'";
                await SetAsync("isSaving", true);
                Assert.Contains(text["Common.Saving"], await HtmlAsync());
                await InvokeAsync("SaveAsync");
                Assert.Empty(await store.GetComputerInventoryAsync());
                await SetAsync("isSaving", false);
                await InvokeAsync("SaveAsync");
                saved = Assert.Single(await store.GetComputerInventoryAsync());
                Assert.Contains(text.Format("AdminInventory.Created", saved.Computer), await HtmlAsync());
                Assert.Null(Field("editingEntry"));
                await InvokeAsync("BeginCreate");
                Draft().SubscriptionId = saved.SubscriptionId;
                Draft().Computer = saved.Computer;
                await InvokeAsync("SaveAsync");
                Assert.Equal(text.Format("AdminInventory.Errors.Duplicate", saved.Computer, saved.SubscriptionId), Field("errorMessage"));
                Assert.Single(await store.GetComputerInventoryAsync());
                await InvokeAsync("CancelEdit");
            }
            else
            {
                var before = (await store.GetComputerInventoryAsync())[0];
                if (count == 2)
                {
                    await InvokeAsync("BeginEdit", (await store.GetComputerInventoryAsync())[1]);
                    Assert.Contains(text["AdminInventory.NoCertificates"], await HtmlAsync());
                    Assert.Contains(text["AdminInventory.NoServices"], await HtmlAsync());
                    Assert.Contains(text.Format("AdminInventory.ServiceHeartbeat", text["AdminInventory.NotAvailable"]), await HtmlAsync());
                    await InvokeAsync("CancelEdit");
                }
                await InvokeAsync("BeginEdit", before);
                await CheckEditorAsync(false);
                Assert.NotSame(before, Draft());
                Draft().Site = "Cancel <original>";
                await InvokeAsync("CancelEdit");
                Assert.Equal(original, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
                await InvokeAsync("BeginEdit", before);
                Draft().Site = "  Site <manual>  ";
                Draft().Domain = new string('a', 256);
                Draft().Role = "Role <manual>";
                Draft().ResourceGroup = "Group <manual>";
                Draft().Maintenance = true;
                Draft().PrivateIP = "changed read-only";
                Draft().LastHeartbeatAt = null;
                Draft().NtdsFileSizeInGb = 0;
                Draft().CertificatesJson = "[]";
                Draft().ServicesJson = "[]";
                await SetAsync("isSaving", true);
                await InvokeAsync("SaveAsync");
                Assert.Equal(original, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
                await SetAsync("isSaving", false);
                await using (var context = factory.CreateDbContext())
                    await context.Database.ExecuteSqlRawAsync(
                        "CREATE TRIGGER FailInventoryEdit BEFORE UPDATE ON ComputerInventory BEGIN SELECT RAISE(ABORT, 'synthetic inventory failure'); END;");
                await InvokeAsync("SaveAsync");
                Assert.Equal(text["AdminInventory.Errors.UpdateUnconfirmed"], Field("errorMessage"));
                Assert.Equal(original, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
                Assert.NotNull(Field("editingEntry"));
                await using (var context = factory.CreateDbContext())
                    await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailInventoryEdit;");
                await InvokeAsync("SaveAsync");
                saved = (await store.GetComputerInventoryAsync())[0];
                Assert.Equal("Site <manual>", saved.Site);
                Assert.Equal(new string('a', 256), saved.Domain);
                Assert.Equal("Role <manual>", saved.Role);
                Assert.Equal("Group <manual>", saved.ResourceGroup);
                Assert.True(saved.Maintenance);
                Assert.Equal(before.PrivateIP, saved.PrivateIP);
                Assert.Equal(before.LastHeartbeatAt, saved.LastHeartbeatAt);
                Assert.Equal(before.NtdsFileSizeInGb, saved.NtdsFileSizeInGb);
                Assert.Equal(before.CertificatesJson, saved.CertificatesJson);
                Assert.Equal(before.ServicesJson, saved.ServicesJson);
                Assert.Contains(text.Format("AdminInventory.Updated", saved.Computer), await HtmlAsync());
                Assert.Null(Field("editingEntry"));
            }
            await using var alertsBefore = factory.CreateDbContext();
            var unchangedAlerts = JsonSerializer.Serialize(await alertsBefore.Alerts.AsNoTracking().ToArrayAsync());
            await InvokeAsync("BeginEdit", saved);
            var victim = count == 2 ? (await store.GetComputerInventoryAsync())[1] : saved;
            var beforeDelete = JsonSerializer.Serialize(await store.GetComputerInventoryAsync());
            await InvokeAsync("BeginDelete", victim);
            var dialog = await HtmlAsync();
            Assert.Contains(text["AdminInventory.DeleteTitle"], dialog);
            Assert.Contains(text["Common.PermanentAction"], dialog);
            Assert.Contains(text.Format("AdminInventory.DeleteBody", $"<strong>{victim.Computer}</strong>",
                $"<strong>{victim.SubscriptionId}</strong>"), dialog);
            Assert.DoesNotContain("<manual>", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            await SetAsync("isDeleting", true);
            Assert.Contains(text["Common.Deleting"], await HtmlAsync());
            await InvokeAsync("DeleteAsync");
            Assert.Equal(beforeDelete, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
            await SetAsync("isDeleting", false);
            await InvokeAsync("CancelDelete");
            Assert.Equal(beforeDelete, JsonSerializer.Serialize(await store.GetComputerInventoryAsync()));
            await InvokeAsync("BeginDelete", victim);
            await InvokeAsync("DeleteAsync");
            Assert.Contains(text.Format("AdminInventory.Deleted", victim.Computer), await HtmlAsync());
            Assert.Null(Field("entryToDelete"));
            if (count == 2)
                Assert.Equal(saved.Computer, Draft().Computer);
            else
                Assert.Null(Field("editingEntry"));
            await InvokeAsync("BeginDelete", victim);
            await InvokeAsync("DeleteAsync");
            Assert.Contains(text["AdminInventory.Missing"], await HtmlAsync());
            Assert.NotNull(Field("entryToDelete"));
            await InvokeAsync("CancelDelete");
            await using var alertsAfter = factory.CreateDbContext();
            Assert.Equal(unchangedAlerts, JsonSerializer.Serialize(await alertsAfter.Alerts.AsNoTracking().ToArrayAsync()));
            Assert.Equal(count == 2 ? 1 : 0, (await store.GetComputerInventoryAsync()).Count);
        }
        async Task CheckErrorLifecycleAsync()
        {
            const string subscription = "sub-errors";
            await using (var context = factory.CreateDbContext())
            {
                context.Alerts.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "errors", "Original <alert>", "Sev0",
                    "New", "Log", "Fired", "Original computer", "rg", subscription, DateTimeOffset.UtcNow, "", "", "", "{}"));
                context.Alerts.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "boundary", "Original <alert>", "Sev0",
                    "New", "Log", "Fired", "Original computer", "rg", new string('s', 64), DateTimeOffset.UtcNow, "", "", "", "{}"));
                await context.SaveChangesAsync();
            }
            await InvokeAsync("LoadInventoryAsync");
            async Task<string> Snapshot() => JsonSerializer.Serialize(await store.GetComputerInventoryAsync());
            await using var alertContext = factory.CreateDbContext();
            var alertsBefore = JsonSerializer.Serialize(await alertContext.Alerts.AsNoTracking().ToArrayAsync());
            foreach (var (property, value, code, message) in new[]
            {
                ("SubscriptionId", "", ComputerInventoryErrorCode.Subscription, "Subscription ID is required and may contain at most 64 characters."),
                ("SubscriptionId", " ", ComputerInventoryErrorCode.Subscription, "Subscription ID is required and may contain at most 64 characters."),
                ("SubscriptionId", new string('s', 65), ComputerInventoryErrorCode.Subscription, "Subscription ID is required and may contain at most 64 characters."),
                ("Computer", "", ComputerInventoryErrorCode.Computer, "Computer name is required and may contain at most 256 characters."),
                ("Computer", new string('c', 257), ComputerInventoryErrorCode.Computer, "Computer name is required and may contain at most 256 characters."),
                ("Domain", new string('d', 257), ComputerInventoryErrorCode.DomainLength, "Domain may contain at most 256 characters."),
                ("Site", new string('s', 257), ComputerInventoryErrorCode.SiteLength, "Site may contain at most 256 characters."),
                ("ResourceGroup", new string('r', 257), ComputerInventoryErrorCode.ResourceGroupLength, "Resource group may contain at most 256 characters."),
                ("Role", new string('r', 257), ComputerInventoryErrorCode.RoleLength, "Role may contain at most 256 characters."),
                ("SubscriptionId", "unobserved <subscription>", ComputerInventoryErrorCode.UnobservedSubscription, "Select a subscription ID that has been observed in an alert.")
            })
            {
                await NewDraft("Validation <computer>");
                typeof(ComputerInventoryEntry).GetProperty(property)!.SetValue(Draft(), value);
                var before = await Snapshot();
                var error = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.CreateComputerInventoryEntryAsync(Draft()));
                Assert.Equal(code, error.Code);
                Assert.Equal(message, error.Message);
                Assert.Null(error.InnerException);
                await InvokeAsync("SaveAsync");
                await CheckError(code, error.Arguments.ToArray());
                Assert.NotNull(Field("editingEntry"));
                Assert.Equal(before, await Snapshot());
            }
            await NewDraft(new string('c', 256));
            Draft().SubscriptionId = new string('s', 64);
            await InvokeAsync("SaveAsync");
            Assert.Null(Field("errorMessage"));
            var boundary = (await store.GetComputerInventoryAsync()).Single(entry => entry.Computer == new string('c', 256));
            Assert.Equal(64, boundary.SubscriptionId.Length);
            await store.DeleteComputerInventoryEntryAsync(boundary.SubscriptionId, boundary.Computer);
            await NewDraft("Failure <computer>'");
            var beforeCreate = await Snapshot();
            await Trigger("FailInventoryInsert", "INSERT", true);
            try
            {
                var error = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.CreateComputerInventoryEntryAsync(Draft()));
                Assert.Equal(ComputerInventoryErrorCode.CreateUnconfirmed, error.Code);
                Assert.IsType<DbUpdateException>(error.InnerException);
                Assert.Equal("The inventory record could not be created. Verify that it does not already exist.", error.Message);
                await InvokeAsync("SaveAsync");
                await CheckError(ComputerInventoryErrorCode.CreateUnconfirmed);
                Assert.Equal(beforeCreate, await Snapshot());
            }
            finally { await Trigger("FailInventoryInsert", "INSERT", false); }

            Draft().Domain = new string('d', 256);
            Draft().Site = new string('s', 256);
            Draft().ResourceGroup = new string('g', 256);
            Draft().Role = new string('r', 256);
            await InvokeAsync("SaveAsync");
            var saved = (await store.GetComputerInventoryAsync()).Single(entry => entry.Computer == "Failure <computer>'");
            Assert.Equal(256, saved.Domain!.Length);
            Assert.Equal(256, saved.Site!.Length);
            Assert.Equal(256, saved.ResourceGroup!.Length);
            Assert.Equal(256, saved.Role!.Length);
            await NewDraft(saved.Computer);
            var duplicate = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.CreateComputerInventoryEntryAsync(Draft()));
            Assert.Equal(ComputerInventoryErrorCode.Duplicate, duplicate.Code);
            Assert.Equal($"Computer '{saved.Computer}' already exists in subscription '{subscription}'.", duplicate.Message);
            await InvokeAsync("SaveAsync");
            await CheckError(ComputerInventoryErrorCode.Duplicate, saved.Computer, subscription);
            Assert.DoesNotContain("<computer>", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

            await InvokeAsync("BeginEdit", saved);
            Draft().Site = "Updated <site>";
            var beforeUpdate = await Snapshot();
            await Trigger("FailInventoryUpdate", "UPDATE", true);
            try
            {
                var error = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.SaveComputerInventoryEntryAsync(Draft()));
                Assert.Equal(ComputerInventoryErrorCode.UpdateUnconfirmed, error.Code);
                Assert.IsType<DbUpdateException>(error.InnerException);
                await InvokeAsync("SaveAsync");
                await CheckError(ComputerInventoryErrorCode.UpdateUnconfirmed);
                Assert.NotNull(Field("editingEntry"));
                Assert.Equal(beforeUpdate, await Snapshot());
            }
            finally { await Trigger("FailInventoryUpdate", "UPDATE", false); }
            await InvokeAsync("BeginDelete", saved);
            await Trigger("FailInventoryDelete", "DELETE", true);
            try
            {
                var error = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.DeleteComputerInventoryEntryAsync(subscription, saved.Computer));
                Assert.Equal(ComputerInventoryErrorCode.DeleteUnconfirmed, error.Code);
                Assert.IsType<SqliteException>(error.InnerException);
                Assert.Equal("The inventory record could not be deleted.", error.Message);
                await InvokeAsync("DeleteAsync");
                await CheckError(ComputerInventoryErrorCode.DeleteUnconfirmed);
                Assert.NotNull(Field("entryToDelete"));
                Assert.Equal(beforeUpdate, await Snapshot());
            }
            finally { await Trigger("FailInventoryDelete", "DELETE", false); }

            var fault = new InvalidOperationException("Synthetic <technical storage detail>");
            foreach (var operation in new[] { "LoadInventoryAsync", "SaveAsync", "DeleteAsync" })
            {
                var before = await Snapshot();
                var previousLogCount = logs.Exceptions.Count;
                factory.OnCreate = () => throw fault;
                try { await InvokeAsync(operation); }
                finally { factory.OnCreate = null; }
                Assert.Equal(text[operation == "LoadInventoryAsync" ? "AdminInventory.Errors.LoadFailed"
                    : "AdminInventory.Errors.ChangeUnconfirmed"], Field("errorMessage"));
                Assert.DoesNotContain(fault.Message, await HtmlAsync());
                Assert.Contains(logs.Exceptions.Skip(previousLogCount), error => ReferenceEquals(error, fault));
                Assert.Equal(before, await Snapshot());
            }
            await store.DeleteComputerInventoryEntryAsync(subscription, saved.Computer);
            var missing = await Assert.ThrowsAsync<ComputerInventoryException>(() => store.SaveComputerInventoryEntryAsync(Draft()));
            Assert.Equal(ComputerInventoryErrorCode.Missing, missing.Code);
            Assert.Equal("The inventory record no longer exists. Refresh the inventory and try again.", missing.Message);
            await InvokeAsync("SaveAsync");
            await CheckError(ComputerInventoryErrorCode.Missing);
            await InvokeAsync("CancelDelete");

            foreach (var reloadFails in new[] { false, true })
            {
                await NewDraft("Committed <computer>");
                var notifications = 0;
                Action failure = () =>
                {
                    notifications++;
                    if (reloadFails) factory.OnCreate = () => throw fault;
                    throw fault;
                };
                store.Changed += failure;
                try { await InvokeAsync("SaveAsync"); }
                finally { store.Changed -= failure; factory.OnCreate = null; }
                Assert.Null(Field("editingEntry"));
                Assert.False(Assert.IsType<bool>(Field("isCreating")));
                await CheckCommittedError(ComputerInventoryErrorCode.SavedNotificationFailed, reloadFails);
                var committed = (await store.GetComputerInventoryAsync()).Single(entry => entry.Computer == "Committed <computer>");
                if (!reloadFails)
                {
                    await InvokeAsync("BeginEdit", committed);
                    Draft().Site = "Committed <update>";
                    store.Changed += failure;
                    try { await InvokeAsync("SaveAsync"); }
                    finally { store.Changed -= failure; }
                    await CheckCommittedError(ComputerInventoryErrorCode.SavedNotificationFailed, false);
                    Assert.Null(Field("editingEntry"));
                    committed = (await store.GetComputerInventoryAsync()).Single(entry => entry.Computer == committed.Computer);
                    Assert.Equal("Committed <update>", committed.Site);
                }
                var other = (await store.GetComputerInventoryAsync()).FirstOrDefault(entry => entry.Computer != committed.Computer);
                await InvokeAsync("BeginEdit", other ?? committed);
                await InvokeAsync("BeginDelete", committed);
                store.Changed += failure;
                try { await InvokeAsync("DeleteAsync"); }
                finally { store.Changed -= failure; factory.OnCreate = null; }
                await CheckCommittedError(ComputerInventoryErrorCode.DeletedNotificationFailed, reloadFails);
                Assert.Null(Field("entryToDelete"));
                if (other is null) Assert.Null(Field("editingEntry"));
                else Assert.Equal(other.Computer, Draft().Computer);
                Assert.DoesNotContain(await store.GetComputerInventoryAsync(), entry => entry.Computer == committed.Computer);
                Assert.Equal(reloadFails ? 2 : 3, notifications);
            }
            await NewDraft("Reload <computer>");
            Action failReload = () => factory.OnCreate = () => throw fault;
            store.Changed += failReload;
            try { await InvokeAsync("SaveAsync"); }
            finally { store.Changed -= failReload; factory.OnCreate = null; }
            Assert.Null(Field("editingEntry"));
            Assert.Equal(text.Format("AdminInventory.Created", "Reload <computer>"), Field("statusMessage"));
            Assert.Equal(text["AdminInventory.Errors.LoadFailed"], Field("errorMessage"));
            Assert.Contains(await store.GetComputerInventoryAsync(), entry => entry.Computer == "Reload <computer>");
            await store.DeleteComputerInventoryEntryAsync(subscription, "Reload <computer>");
            Assert.Equal(alertsBefore, JsonSerializer.Serialize(await alertContext.Alerts.AsNoTracking().ToArrayAsync()));
            foreach (var code in Enum.GetValues<ComputerInventoryErrorCode>())
                Assert.False(string.IsNullOrWhiteSpace(UiResources.GetEnglish("AdminInventory.Errors." + code)));

            async Task NewDraft(string computer)
            {
                await InvokeAsync("BeginCreate");
                Draft().SubscriptionId = subscription;
                Draft().Computer = computer;
            }
            async Task CheckError(ComputerInventoryErrorCode code, params object[] arguments)
            {
                var expected = text.Format("AdminInventory.Errors." + code, arguments);
                Assert.Equal(expected, Field("errorMessage"));
                Assert.Contains(expected, await HtmlAsync());
                Assert.False(Assert.IsType<bool>(Field("isSaving")));
                Assert.False(Assert.IsType<bool>(Field("isDeleting")));
            }
            async Task CheckCommittedError(ComputerInventoryErrorCode code, bool reloadFails)
            {
                Assert.Equal(text[reloadFails ? "AdminInventory.Errors.CommittedReloadFailed" : "AdminInventory.Errors." + code], Field("errorMessage"));
                Assert.DoesNotContain(fault.Message, await HtmlAsync());
                Assert.Contains(logs.Exceptions, error => error is ComputerInventoryException known &&
                    known.Code == code && ReferenceEquals(known.InnerException, fault));
                Assert.False(Assert.IsType<bool>(Field("isSaving")));
                Assert.False(Assert.IsType<bool>(Field("isDeleting")));
                Assert.False(Assert.IsType<bool>(Field("isLoading")));
                Assert.Null(Field("statusMessage"));
            }
            async Task Trigger(string name, string operation, bool create)
            {
                await using var context = factory.CreateDbContext();
                await context.Database.ExecuteSqlRawAsync(create
                    ? $"CREATE TRIGGER {name} BEFORE {operation} ON ComputerInventory BEGIN SELECT RAISE(ABORT, 'synthetic inventory failure'); END;"
                    : $"DROP TRIGGER {name};");
            }
        }
        async Task CheckEditorAsync(bool creating)
        {
            var editor = await HtmlAsync();
            foreach (var key in new[] { "AdminInventory.SubscriptionId", "AdminInventory.Computer",
                "AdminInventory.SiteHelp", "AdminInventory.DomainHelp", "AdminInventory.ResourceGroupHelp",
                "AdminInventory.RoleHelp", "AdminInventory.MaintenanceHelp", "Common.Cancel" })
                Assert.Contains(text[key], editor);
            Assert.Contains(text[creating ? "AdminInventory.NewEntry" : "AdminInventory.EditSystem"], editor);
            var raw = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Equal(creating ? 5 : 4, raw.Split("maxlength=\"256\"", StringSplitOptions.None).Length - 1);
            if (creating)
            {
                Assert.Contains(text["AdminInventory.CreateComputer"], editor);
                Assert.Contains(text["AdminInventory.SelectSubscription"], editor);
                Assert.Contains(text["AdminInventory.SubscriptionsHelp"], editor);
                Assert.Contains(text["AdminInventory.PrimaryKeyHelp"], editor);
                Assert.Contains("select required", raw);
                Assert.Contains("input required maxlength=\"256\"", raw);
            }
            else
            {
                foreach (var key in new[] { "AdminInventory.FixedKeyHelp", "AdminInventory.HeartbeatHelp",
                    "AdminInventory.GraphStatusHelp", "AdminInventory.NtdsHelp", "AdminInventory.CertificateInventory",
                    "AdminInventory.ServiceInventory", "AdminInventory.Service", "AdminInventory.LastStatus", "Common.SaveChanges" })
                    Assert.Contains(text[key], editor);
                Assert.Contains(text.Format("AdminInventory.ServiceHeartbeat", "2026-11-06 21:30:00 UTC"), editor);
                Assert.Contains(text["AdminInventory.LastBootTime"], editor);
                Assert.Contains("2026-11-05 20:30:00 UTC", editor);
                Assert.Equal(6, raw.Split(" readonly", StringSplitOptions.None).Length - 1);
            }
        }
        object? Field(string name) => typeof(AdminInventory).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(activator.Page);
        ComputerInventoryEntry Draft() => Assert.IsType<ComputerInventoryEntry>(Field("editingEntry"));
        async Task<string> HtmlAsync() => WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Task SetAsync(string field, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(AdminInventory).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Page, value);
            Refresh();
        });
        Task InvokeAsync(string method, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AdminInventory).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, arguments);
            if (result is Task task) await task;
            Refresh();
        });
        void Refresh() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(activator.Page, null);
    }

    private static ComputerInventoryEntry Entry(int index) => new()
    {
        SubscriptionId = $"sub-{index}", Computer = $"Computer <{index}>",
        Site = index == 0 ? "All sites" : null, Domain = index == 0 ? "Not available" : null,
        ResourceGroup = index == 0 ? "Refresh" : null, Role = index == 0 ? "Maintenance" : null,
        PrivateIP = index == 0 ? "10.0.0.1" : null,
        LastHeartbeatAt = index == 0 ? new DateTimeOffset(2026, 11, 6, 23, 30, 0, TimeSpan.FromHours(2)) : null,
        LastBootTime = index == 0 ? new DateTimeOffset(2026, 11, 5, 22, 30, 0, TimeSpan.FromHours(2)) : null,
        NtdsFileSizeInGb = index == 0 ? 1234.567 : null, Maintenance = index == 1,
        CertificatesJson = index == 0 ? JsonSerializer.Serialize(new[]
        {
            new InventoryCertificate("Certificate <original>", "serial-<original>",
                new DateTimeOffset(2026, 11, 7, 1, 5, 0, TimeSpan.FromHours(2))),
            new InventoryCertificate("", "", null)
        }) : "[]",
        ServicesJson = index == 0 ? JsonSerializer.Serialize(
            new int?[] { null, -1, 0, 1, 2, 3, 4, 5, 6, 7, 99 }
                .Select(code => new InventoryService("Running <original service>", code))) : "[]"
    };

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public Action? OnCreate { get; set; }
        public AlertDbContext CreateDbContext()
        {
            OnCreate?.Invoke();
            return new(options);
        }
    }

    private sealed class InventoryLogProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => new InventoryLogger(Exceptions);
        public void Dispose() { }
        private sealed class InventoryLogger(System.Collections.Concurrent.ConcurrentQueue<Exception> exceptions) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is not null) exceptions.Enqueue(exception);
            }
        }
    }

    private sealed class InventoryActivator : IComponentActivator
    {
        public AdminInventory? Page { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AdminInventory page) Page = page;
            return component;
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported render mode: {renderMode.GetType().Name}.");
    }
}
