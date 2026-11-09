using System.Globalization;

namespace MonitoringApp.Tests;

public sealed class DomainIdentityAndResolutionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T20:00:00Z");

    [Theory]
    [InlineData("", "", "\u001f")]
    [InlineData(" \t", "\r\n", "\u001f")]
    [InlineData(" Sub-A ", " Dc-01 ", "Sub-A\u001fDc-01")]
    [InlineData("sub", "\u00e4DC", "sub\u001f\u00e4DC")]
    [InlineData("ab", "c", "ab\u001fc")]
    [InlineData("a", "bc", "a\u001fbc")]
    public void SharedKeyPreservesCaseAndPairBoundaries(string subscription, string computer, string expected)
    {
        Assert.Equal(expected, SubscriptionComputerKey.Create(subscription, computer));
    }

    [Fact]
    public void InventoryConsumersTrimPairsMatchCaseInsensitivelyAndKeepSubscriptionsSeparate()
    {
        var matching = Alert("SUB-A", "dc-01");
        var otherSubscription = Alert("sub-b", "dc-01");
        var inventory = new ComputerInventoryEntry
        {
            SubscriptionId = " \tsub-a ",
            Computer = " DC-01\r\n",
            Domain = "example.test",
            Site = " Site A ",
            Maintenance = true
        };
        var alerts = new[] { matching, otherSubscription };

        Assert.Equal([otherSubscription], InventoryMaintenance.FilterAlerts(alerts, [inventory]));
        var displayed = InboxTargetOutput.WithInventorySites(alerts, [inventory]);
        Assert.Equal("Site A", displayed[0].SiteName);
        Assert.Equal(string.Empty, displayed[1].SiteName);
        var target = Assert.Single(Assert.Single(Assert.Single(
            AlertNavigation.Build(alerts, alerts, [inventory], Now.AddMinutes(-1))).Roles).Targets);
        Assert.Equal(1, target.Count);
        Assert.Equal(1, target.HistoryCount);
    }

    [Fact]
    public void DashboardDeduplicatesTrimmedPairsWithoutRewritingStoredIdentity()
    {
        var state = new AnalysisDashboardState();
        state.Load(new AnalysisDashboardDocument
        {
            Systems =
            [
                new() { SubscriptionId = " Sub-A ", Computer = " DC-01 " },
                new() { SubscriptionId = "sub-a", Computer = "dc-01" },
                new() { SubscriptionId = "sub-b", Computer = "dc-01" }
            ]
        });

        Assert.Equal(2, state.Document.Systems.Count);
        Assert.Equal(" Sub-A ", state.Document.Systems[0].SubscriptionId);
        Assert.Equal(" DC-01 ", state.Document.Systems[0].Computer);
        Assert.Equal("sub-b", state.Document.Systems[1].SubscriptionId);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Resolved", true)]
    [InlineData("rEsOlVeD", true)]
    [InlineData("Operator: RESOLVED.", true)]
    [InlineData("line one\nResolved\nline three", true)]
    [InlineData("(Resolved)", true)]
    [InlineData("not Resolved", true)]
    [InlineData("Unresolved", false)]
    [InlineData("ResolvedAgain", false)]
    [InlineData("Resolved_1", false)]
    [InlineData("1Resolved", false)]
    [InlineData("éResolved", false)]
    [InlineData("Resolvedé", false)]
    public void InboxAndGraphRecognizeTheSameStandaloneMarkerInEveryCulture(string comments, bool resolved)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var alert = Alert("sub-a", "DC-01") with { Comments = comments };

                Assert.Equal(!resolved, AlertLifecycle.GetActiveAlerts([alert]).Any());
                Assert.Equal(!resolved, AlertGraphActiveAlerts.ResolveIds([Graph(alert)]).Contains(alert.Id));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void InboxGroupsByAlertIdButGraphKeepsSubscriptionsSeparate()
    {
        var fired = Alert("sub-a", "DC-01") with { AlertId = "Same-Alert", ReceivedAt = Now.AddMinutes(-1) };
        var resolved = fired with
        {
            Id = Guid.NewGuid(), AlertId = "same-alert", SubscriptionId = "sub-b",
            MonitorCondition = "Resolved", ReceivedAt = Now
        };

        Assert.Empty(AlertLifecycle.GetActiveAlerts([fired, resolved]));
        Assert.Equal(fired.Id, Assert.Single(AlertGraphActiveAlerts.ResolveIds([Graph(fired), Graph(resolved)])));
    }

    [Fact]
    public void MissingAlertIdsRemainIndependentEvents()
    {
        var first = Alert("sub-a", "DC-01") with { AlertId = "" };
        var second = first with { Id = Guid.NewGuid(), ReceivedAt = Now.AddMinutes(1) };

        Assert.Equal(2, AlertLifecycle.GetActiveAlerts([first, second]).Count);
        Assert.Equal(2, AlertGraphActiveAlerts.ResolveIds([Graph(first), Graph(second)]).Count);
    }

    private static AlertRecord Alert(string subscription, string target) => new(
        Guid.NewGuid(), Now, Guid.NewGuid().ToString(), "Test", "Sev1", "", "Log", "Fired",
        target, "rg-a", subscription, Now, "", "", "", "{}");

    private static AlertGraphRecord Graph(AlertRecord alert) => new(
        alert.Id, alert.ReceivedAt, alert.AlertId, alert.MonitorCondition, alert.Comments,
        alert.Name, alert.SubscriptionId, alert.ResourceGroup, alert.TargetName, "", "", "");
}
