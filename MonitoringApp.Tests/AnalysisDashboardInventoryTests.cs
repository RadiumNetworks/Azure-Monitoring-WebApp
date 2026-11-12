using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardInventoryTests
{
    private const string SubscriptionA = "11111111-1111-1111-1111-111111111111";
    private const string SubscriptionB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void AllWorkspacesFollowCurrentConfigurationWithoutWideningAnExplicitWorkspace()
    {
        var document = RoleDocument();
        ComputerInventoryEntry[] inventory = [
            new() { SubscriptionId = SubscriptionA, Computer = "DC01", Role = "Domain Controller" },
            new() { SubscriptionId = SubscriptionB, Computer = "DC02", Role = "Domain Controller" }];
        var options = new CrossTenantLogAnalyticsOptions
        {
            Workspaces = [new() { Key = "workspace-a", SubscriptionIds = [SubscriptionA] }]
        };
        var initial = AnalysisDashboardInventorySelection.Resolve(document, [inventory[0]], new(options));
        Assert.Single(initial.Systems);
        var expanded = new CrossTenantLogAnalyticsOptions
        {
            Workspaces = [.. options.Workspaces, new() { Key = "workspace-b", SubscriptionIds = [SubscriptionB] }]
        };
        Assert.Equal(2, AnalysisDashboardInventorySelection.Resolve(document, inventory, new(expanded)).Systems.Count);
        Assert.Equal("DC01", Assert.Single(AnalysisDashboardInventorySelection.Resolve(
            document, inventory, new(expanded), "workspace-a").Systems).Computer);
        var reduced = new CrossTenantLogAnalyticsOptions { Workspaces = [expanded.Workspaces[1]] };
        Assert.Equal(AnalysisValidationCode.WorkspaceUnavailable, Assert.Throws<AnalysisValidationException>(() =>
            AnalysisDashboardInventorySelection.Resolve(document, [inventory[1]], new(reduced), "workspace-a")).Code);
        Assert.Equal("DC02", Assert.Single(AnalysisDashboardInventorySelection.Resolve(
            document, [inventory[1]], new(reduced)).Systems).Computer);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("WORKSPACE-A", 2)]
    [InlineData("workspace-b", 1)]
    public void RoleUnionUsesCurrentInventoryAndWorkspaceRoutingWithoutChangingTheRule(string? workspace, int expected)
    {
        var document = new AnalysisDashboardDocument
        {
            SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles,
            InventoryRoles = [" domaincontrollers ", " File Server "],
            Systems = [new() { SubscriptionId = SubscriptionA, Computer = "INACTIVE-FIXED" }]
        };
        ComputerInventoryEntry[] inventory =
        [
            new() { SubscriptionId = SubscriptionA, Computer = "DC01", Role = "Domain Controller", Maintenance = true },
            new() { SubscriptionId = SubscriptionA.ToUpperInvariant(), Computer = "dc01", Role = "domaincontrollers" },
            new() { SubscriptionId = SubscriptionA, Computer = "FILE01", Role = "file server", LastHeartbeatAt = DateTimeOffset.UnixEpoch },
            new() { SubscriptionId = SubscriptionB, Computer = "DC01", Role = " DOMAINCONTROLLERS " },
            new() { SubscriptionId = SubscriptionA, Computer = "INACTIVE-FIXED", Role = "Other" },
            new() { SubscriptionId = SubscriptionA, Computer = "UNASSIGNED" }
        ];
        var before = JsonSerializer.Serialize(document);

        var result = AnalysisDashboardInventorySelection.Resolve(document, inventory, Resolver(), workspace);

        Assert.Equal(expected, result.Systems.Count);
        Assert.Equal(AnalysisDashboardInventoryStatus.Ready, result.Status);
        Assert.Empty(result.MissingSystems);
        Assert.DoesNotContain(result.Systems, system => system.Computer is "INACTIVE-FIXED" or "UNASSIGNED");
        Assert.Equal(before, JsonSerializer.Serialize(document));
        if (string.IsNullOrEmpty(workspace))
            Assert.Equal(2, result.Systems.Count(system => system.Computer.Equals("dc01", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NewRemovedAndReassignedMembersAreReevaluatedWithoutRewritingTheDocument()
    {
        var document = RoleDocument();
        ComputerInventoryEntry first = new() { SubscriptionId = SubscriptionA, Computer = "DC01", Role = "Domain Controller" };
        ComputerInventoryEntry second = new() { SubscriptionId = SubscriptionA, Computer = "DC02", Role = "Domain Controller" };
        ComputerInventoryEntry added = new() { SubscriptionId = SubscriptionA, Computer = "DC03", Role = "domaincontrollers" };
        var original = JsonSerializer.Serialize(document);

        Assert.Equal(["DC01", "DC02"], AnalysisDashboardInventorySelection.Resolve(document, [first, second], Resolver()).Systems.Select(system => system.Computer));
        Assert.Equal(["DC02", "DC03"], AnalysisDashboardInventorySelection.Resolve(document, [second, added], Resolver()).Systems.Select(system => system.Computer));
        second.Role = "Other";
        Assert.Equal("DC03", Assert.Single(AnalysisDashboardInventorySelection.Resolve(document, [second, added], Resolver()).Systems).Computer);
        Assert.Equal(original, JsonSerializer.Serialize(document));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("workspace-b")]
    public void NoMatchingRoleOrWorkspaceIsAnExplicitEmptySelectionWithoutFixedFallback(string? workspace)
    {
        var document = RoleDocument();
        document.InventoryRoles = ["Role no longer present"];
        document.Systems = [new() { SubscriptionId = SubscriptionA, Computer = "DC01" }];
        ComputerInventoryEntry[] inventory = [new() { SubscriptionId = SubscriptionA, Computer = "DC01", Role = "Other" }];

        var result = AnalysisDashboardInventorySelection.Resolve(document, inventory, Resolver(), workspace);

        Assert.Empty(result.Systems);
        Assert.Empty(result.MissingSystems);
        Assert.Equal(AnalysisDashboardInventoryStatus.Empty, result.Status);
        Assert.Equal("Role no longer present", Assert.Single(document.InventoryRoles));
        document.InventoryRoles = ["Other"];
        Assert.Equal(workspace is null ? 1 : 0,
            AnalysisDashboardInventorySelection.Resolve(document, inventory, Resolver(), workspace).Systems.Count);
    }

    [Theory]
    [InlineData(0, AnalysisDashboardInventoryStatus.AllMissing)]
    [InlineData(1, AnalysisDashboardInventoryStatus.PartiallyMissing)]
    [InlineData(2, AnalysisDashboardInventoryStatus.Ready)]
    public void FixedModeIgnoresInactiveRolesAndPreservesSavedPairMatching(int remaining, AnalysisDashboardInventoryStatus status)
    {
        var document = new AnalysisDashboardDocument
        {
            InventoryRoles = ["Domain Controller"],
            Systems = [
                new() { SubscriptionId = SubscriptionA, Computer = "DC01" },
                new() { SubscriptionId = SubscriptionB, Computer = "DC02" }]
        };
        var inventory = new List<ComputerInventoryEntry> { new() { SubscriptionId = SubscriptionA, Computer = "NEW-DC", Role = "Domain Controller" } };
        if (remaining > 0) inventory.Add(new() { SubscriptionId = SubscriptionA, Computer = "dc01" });
        if (remaining > 1) inventory.Add(new() { SubscriptionId = SubscriptionB, Computer = "dc02" });

        var result = AnalysisDashboardInventorySelection.Resolve(document, inventory, Resolver());
        var legacy = AnalysisDashboardInventorySelection.Resolve(document.Systems, inventory);

        Assert.Equal(legacy.Systems, result.Systems);
        Assert.Equal(legacy.MissingSystems, result.MissingSystems);
        Assert.Equal(status, result.Status);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void DynamicMembershipIsNeverSilentlyTruncated(int count, bool exceedsLimit)
    {
        var inventory = Enumerable.Range(1, count).Select(index => new ComputerInventoryEntry
        {
            SubscriptionId = SubscriptionA, Computer = $"DC{index}", Role = "Domain Controller"
        }).ToArray();
        if (exceedsLimit)
            Assert.Equal(AnalysisValidationCode.DashboardSystemsLimitExceeded,
                Assert.Throws<AnalysisValidationException>(() =>
                    AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver())).Code);
        else
            Assert.Equal(100, AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver()).Systems.Count);
        Assert.Equal(AnalysisDashboardInventoryStatus.Empty,
            AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver(), "workspace-b").Status);
    }

    [Fact]
    public void MissingWorkspaceAndRoutingErrorsNeverBecomeEmptyOrPartialSuccess()
    {
        Assert.Equal(AnalysisValidationCode.WorkspaceUnavailable,
            Assert.Throws<AnalysisValidationException>(() =>
                AnalysisDashboardInventorySelection.Resolve(RoleDocument(), [], Resolver(), "removed-workspace")).Code);
        ComputerInventoryEntry[] inventory = [
            new() { SubscriptionId = SubscriptionA, Computer = "GOOD", Role = "Domain Controller" },
            new() { SubscriptionId = "unknown", Computer = "UNROUTED", Role = "Domain Controller" }];
        var noRoute = Assert.Throws<AnalysisValidationException>(() =>
            AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver()));
        Assert.Equal(AnalysisValidationCode.DashboardInventoryRoutingInvalid, noRoute.Code);
        Assert.Equal(TargetWorkspaceResolutionError.NoRoute, Assert.IsType<TargetWorkspaceResolutionException>(noRoute.InnerException).Error);
        inventory[1].SubscriptionId = SubscriptionA;
        inventory[1].Domain = "b.example";
        var conflicting = Assert.Throws<AnalysisValidationException>(() =>
            AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver(), "workspace-a"));
        Assert.Equal(AnalysisValidationCode.DashboardInventoryRoutingInvalid, conflicting.Code);
        Assert.Equal(TargetWorkspaceResolutionError.ConflictingRoutes,
            Assert.IsType<TargetWorkspaceResolutionException>(conflicting.InnerException).Error);
    }

    [Fact]
    public void DomainOnlyRoutingDoesNotGuessWhenAnIdentityHasConflictingInventoryRoutes()
    {
        ComputerInventoryEntry[] inventory = [
            new() { SubscriptionId = "unmapped", Domain = "a.example", Computer = "DC01", Role = "Domain Controller" },
            new() { SubscriptionId = "unmapped", Domain = "b.example", Computer = "dc01", Role = "Domain Controller" }];
        var conflicting = Assert.Throws<AnalysisValidationException>(() =>
            AnalysisDashboardInventorySelection.Resolve(RoleDocument(), inventory, Resolver()));
        Assert.Equal(AnalysisValidationCode.DashboardInventoryRoutingInvalid, conflicting.Code);
        Assert.Equal(TargetWorkspaceResolutionError.ConflictingRoutes,
            Assert.IsType<TargetWorkspaceResolutionException>(conflicting.InnerException).Error);
    }

    [Fact]
    public void MatchingInventoryWithAnEmptyIdentityIsRejected()
    {
        Assert.Equal(AnalysisValidationCode.DashboardSelectionInvalid,
            Assert.Throws<AnalysisValidationException>(() => AnalysisDashboardInventorySelection.Resolve(
                RoleDocument(), [new() { SubscriptionId = SubscriptionA, Role = "Domain Controller" }], Resolver())).Code);
    }

    private static AnalysisDashboardDocument RoleDocument() => new()
    {
        SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles, InventoryRoles = ["Domain Controller"]
    };

    private static TargetWorkspaceResolver Resolver() => new(new()
    {
        Workspaces = [
            new() { Key = "workspace-a", SubscriptionIds = [SubscriptionA], Domains = ["a.example"] },
            new() { Key = "workspace-b", SubscriptionIds = [SubscriptionB], Domains = ["b.example"] }]
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void SelectionValidationErrorsUseTheExistingLocalizedPresentation(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            foreach (var code in new[] {
                AnalysisValidationCode.DashboardSelectionInvalid, AnalysisValidationCode.DashboardRolesRequired,
                AnalysisValidationCode.DashboardSystemsLimitExceeded, AnalysisValidationCode.DashboardSelectionNotAvailable,
                AnalysisValidationCode.DashboardInventoryRoutingInvalid })
            {
                var key = $"Analysis.Validation.{code}";
                Assert.NotEqual(key, text[key]);
                Assert.Equal(text[key], AnalysisErrorPresentation.Message(
                    new AnalysisValidationException(code, "Internal English error"), text, "Analysis.Errors.ExecutionFailed"));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReconcilesExactPairsWithoutChangingSavedSelectionsOrIncludingOtherSystems(int remaining)
    {
        AnalysisDashboardSystem[] saved =
        [
            new() { SubscriptionId = "sub-a", Computer = "dc01" },
            new() { SubscriptionId = "sub-b", Computer = "DC02" }
        ];
        var original = JsonSerializer.Serialize(saved);
        var inventory = new List<ComputerInventoryEntry>
        {
            new() { SubscriptionId = "other-sub", Computer = "dc01" },
            new() { SubscriptionId = "sub-a", Computer = "not-selected" }
        };
        if (remaining > 0) inventory.Add(new() { SubscriptionId = "SUB-A", Computer = "DC01" });
        if (remaining > 1) inventory.Add(new() { SubscriptionId = "sub-b", Computer = "DC02" });

        var selection = AnalysisDashboardInventorySelection.Resolve(saved, inventory);

        Assert.Equal(remaining, selection.Systems.Count);
        Assert.Equal(2 - remaining, selection.MissingSystems.Count);
        Assert.DoesNotContain(selection.Systems, system => system.SubscriptionId == "other-sub" || system.Computer == "not-selected");
        Assert.Equal(original, JsonSerializer.Serialize(saved));
        if (remaining > 0) Assert.Equal("DC01", selection.Systems[0].Computer);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task MissingSystemsAreVisibleLocalizedAndEscaped(string culture, bool allMissing)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<AnalysisDashboardInventoryNotice>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["MissingSystems"] = new AnalysisDashboardSystem[] { new() { SubscriptionId = "sub", Computer = "<retired>" } },
                        ["AllMissing"] = allMissing
                    }))).ToHtmlString());
            Assert.Contains("&lt;retired&gt;", html);
            Assert.Contains(culture == "de-DE" ? "gespeicherte Auswahl bleibt" : "saved selection is unchanged", html);
            Assert.Equal(allMissing, System.Net.WebUtility.HtmlDecode(html).Contains(services.GetRequiredService<UiText>()["Analysis.DashboardAllSystemsMissing"], StringComparison.Ordinal));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }
}
