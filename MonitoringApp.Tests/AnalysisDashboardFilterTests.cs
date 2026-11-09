using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardFilterTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SharedFiltersLimitSystemPickerAndPreserveDomainDisplayNames()
    {
        var page = CreatePage();
        Set(page, "workspaceFilter", "workspace-a");
        Set(page, "domainFilter", "ALPHA.EXAMPLE.");
        Set(page, "siteFilter", "North");
        Set(page, "roleFilter", "Domain Controller");

        Assert.Equal("DC01", Assert.Single(Get<IEnumerable<ComputerInventoryEntry>>(page, "VisibleSystems")).Computer);
        var domains = Get<IEnumerable<(string Value, string Label)>>(page, "AvailableDomains").ToArray();
        Assert.Contains(domains, domain => domain.Value == "alpha.example" && domain.Label == "Company A");
        Assert.Contains(domains, domain => domain.Value == "beta.example" && domain.Label == "Company B");
        Assert.DoesNotContain(domains, domain => domain.Value == "gamma.example");
    }

    [Theory]
    [InlineData("WorkspaceChangedAsync", "", "", "")]
    [InlineData("DomainChangedAsync", "alpha.example", "", "")]
    [InlineData("SiteChangedAsync", "alpha.example", "North", "")]
    [InlineData("DashboardFiltersChangedAsync", "alpha.example", "North", "Domain Controller")]
    public async Task CascadingFiltersRefreshBothDashboardTypesWithoutChangingSavedSelections(
        string changed, string expectedDomain, string expectedSite, string expectedRole)
    {
        var page = CreatePage();
        Set(page, "workspaceFilter", "workspace-a");
        Set(page, "domainFilter", "alpha.example");
        Set(page, "siteFilter", "North");
        Set(page, "roleFilter", "Domain Controller");
        Set(page, "selectedPublishedDashboard", Guid.NewGuid());
        var selected = Get<HashSet<string>>(page, "selectedSystems");
        selected.Add("sub-a\u001fDC01");
        selected.Add("sub-b\u001fDC04");
        var dashboard = Get<AnalysisDashboardState>(page, "Dashboard");
        dashboard.Document.Systems.Add(new() { SubscriptionId = "sub-a", Computer = "DC01" });

        await (Task)typeof(Analysis).GetMethod(changed, Flags)!.Invoke(page, [])!;

        Assert.Equal(expectedDomain, Get<string>(page, "domainFilter"));
        Assert.Equal(expectedSite, Get<string>(page, "siteFilter"));
        Assert.Equal(expectedRole, Get<string>(page, "roleFilter"));
        Assert.Equal("workspace-a", Get<string>(page, "workspaceFilter"));
        Assert.True(Get<bool>(page, "runPublishedOnFilterChange"));
        Assert.Equal(2, selected.Count);
        Assert.Equal("DC01", Assert.Single(dashboard.Document.Systems).Computer);
    }

    [Fact]
    public async Task SelectVisibleUsesAllFourFilters()
    {
        var page = CreatePage();
        Set(page, "workspaceFilter", "workspace-a");
        Set(page, "domainFilter", "beta.example");
        Set(page, "siteFilter", "North");
        Set(page, "roleFilter", "SQL");

        Assert.Equal("SQL01", Assert.Single(Get<IEnumerable<ComputerInventoryEntry>>(page, "VisibleSystems")).Computer);
        await (Task)typeof(Analysis).GetMethod("SelectVisibleAsync", Flags)!.Invoke(page, [])!;
        Assert.Equal("SQL01", Assert.Single(Get<AnalysisDashboardState>(page, "Dashboard").Document.Systems).Computer);
        Assert.Single(Get<HashSet<string>>(page, "selectedSystems"));
        Set(page, "roleFilter", "");
        Set(page, "siteFilter", "");
        Set(page, "domainFilter", "");
        Set(page, "workspaceFilter", "");
        Assert.Equal(4, Get<IEnumerable<ComputerInventoryEntry>>(page, "VisibleSystems").Count());
    }

    [Theory]
    [InlineData("en-US", 99)]
    [InlineData("de-DE", 99)]
    [InlineData("en-US", 100)]
    [InlineData("de-DE", 100)]
    public async Task SelectionLimitFeedbackAndGuardsPreserveExactCapacityOriginalKeysAndClearBehavior(string culture, int initialCount)
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var page = CreatePage();
            typeof(Analysis).GetProperty("Text", Flags)!.SetValue(page, services.GetRequiredService<UiText>());
            var records = Enumerable.Range(0, 101).Select(index => new ComputerInventoryEntry
            {
                SubscriptionId = "Original subscription", Computer = $"Original computer {index}", Domain = "alpha.example",
                Site = "Original site", Role = "Original role"
            }).ToArray();
            var original = JsonSerializer.Serialize(records);
            Set(page, "inventory", records);
            var selected = Get<HashSet<string>>(page, "selectedSystems");
            string Key(int index) => $"{records[index].SubscriptionId}\u001f{records[index].Computer}";
            selected.UnionWith(Enumerable.Range(0, initialCount).Select(Key));
            async Task Invoke(string method, params object[] arguments) =>
                await (Task)typeof(Analysis).GetMethod(method, Flags)!.Invoke(page, arguments)!;
            await Invoke("SelectVisibleAsync");
            Assert.Equal(100, selected.Count);
            Assert.Equal(Enumerable.Range(0, 100).Select(Key).Order(), selected.Order());
            Assert.Equal(100, Get<AnalysisDashboardState>(page, "Dashboard").Document.Systems.Count);
            Assert.Equal(culture == "de-DE"
                ? "Die Auswahl ist auf 100 Systeme begrenzt. Die Filter eingrenzen oder die Auswahl leeren, um andere Systeme auszuwählen."
                : "The selection is limited to 100 systems. Narrow the filters or clear the selection to select other systems.",
                Get<string>(page, "errorMessage"));
            await Invoke("ToggleSystemAsync", Key(100), new ChangeEventArgs { Value = true });
            var limit = culture == "de-DE" ? "Höchstens 100 Systeme auswählen." : "Select at most 100 systems.";
            Assert.Equal(limit, Get<string>(page, "errorMessage"));
            Assert.Equal(100, selected.Count);
            Assert.DoesNotContain(Key(100), selected);
            foreach (var guard in new[] { "isDashboardRunning", "selectedPublishedDashboard" })
            {
                Set(page, guard, guard == "isDashboardRunning" ? true : Guid.NewGuid());
                await Invoke("ClearSelectionAsync");
                await Invoke("ToggleSystemAsync", Key(0), new ChangeEventArgs { Value = false });
                await Invoke("SelectVisibleAsync");
                Assert.Equal(100, selected.Count);
                Assert.Equal(limit, Get<string>(page, "errorMessage"));
                Set(page, guard, guard == "isDashboardRunning" ? false : null);
            }
            await Invoke("ToggleSystemAsync", Key(0), new ChangeEventArgs { Value = false });
            Assert.Equal(99, selected.Count);
            await Invoke("ToggleSystemAsync", Key(100), new ChangeEventArgs { Value = true });
            Assert.Equal(100, selected.Count);
            Assert.Contains(Key(100), selected);
            Assert.DoesNotContain(Key(0), selected);
            await Invoke("ClearSelectionAsync");
            Assert.Empty(selected);
            Assert.Empty(Get<AnalysisDashboardState>(page, "Dashboard").Document.Systems);
            Assert.Equal(limit, Get<string>(page, "errorMessage"));
            Assert.Equal(original, JsonSerializer.Serialize(records));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    private static Analysis CreatePage()
    {
        var options = new CrossTenantLogAnalyticsOptions
        {
            Workspaces =
            [
                new()
                {
                    Key = "workspace-a", DisplayName = "Workspace A", Domains = ["alpha.example", "beta.example"],
                    DomainDisplayNames = new() { ["alpha.example"] = "Company A", ["beta.example"] = "Company B" }
                },
                new() { Key = "workspace-b", DisplayName = "Workspace B", Domains = ["gamma.example"] }
            ]
        };
        var page = new Analysis();
        typeof(Analysis).GetProperty("Options", Flags)!.SetValue(page, options);
        typeof(Analysis).GetProperty("Dashboard", Flags)!.SetValue(page, new AnalysisDashboardState());
        typeof(Analysis).GetProperty("QueryRunner", Flags)!.SetValue(page,
            new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options)));
        Set(page, "inventory", (IReadOnlyList<ComputerInventoryEntry>)
        [
            new() { SubscriptionId = "sub-a", Computer = "DC01", Domain = "Alpha.Example.", Site = "North", Role = "Domain Controller" },
            new() { SubscriptionId = "sub-a", Computer = "DC02", Domain = "alpha.example", Site = "South", Role = "Domain Controller" },
            new() { SubscriptionId = "sub-a", Computer = "SQL01", Domain = "beta.example", Site = "North", Role = "SQL" },
            new() { SubscriptionId = "sub-b", Computer = "DC04", Domain = "gamma.example", Site = "North", Role = "Domain Controller" }
        ]);
        return page;
    }

    private static void Set(Analysis page, string field, object? value) =>
        typeof(Analysis).GetField(field, Flags)!.SetValue(page, value);

    private static T Get<T>(Analysis page, string name) =>
        (T)(typeof(Analysis).GetField(name, Flags)?.GetValue(page)
            ?? typeof(Analysis).GetProperty(name, Flags)!.GetValue(page))!;

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("System filtering must not access Key Vault.");
    }
}
