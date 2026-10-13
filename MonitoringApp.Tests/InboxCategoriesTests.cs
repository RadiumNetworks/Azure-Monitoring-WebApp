namespace MonitoringApp.Tests;

public sealed class InboxCategoriesTests
{
    [Fact]
    public void MergesNamesAndCountsUsingFirstRulePresentationAndCurrentAlertOrder()
    {
        var dcport = CreateAlert("DCPort");
        var dcdiag = CreateAlert("DCDiag");
        var dfsr = CreateAlert("DCDiag DFSREvent");
        var outage = CreateAlert("Outage");
        var categories = new[]
        {
            CreateCategory("Suppressed alerts", "failure", false, dcport),
            CreateCategory("System Outage", "failure", false, outage),
            CreateCategory("suppressed ALERTS", "info", true, dcdiag, dfsr)
        };

        var groups = InboxCategories.Build(categories, [dfsr, outage, dcdiag, dcport]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Suppressed alerts", groups[0].Name);
        Assert.Equal("failure", groups[0].Tone);
        Assert.False(groups[0].Collapsed);
        Assert.Equal([dfsr.Id, dcdiag.Id, dcport.Id], groups[0].Alerts.Select(alert => alert.Id));
        Assert.Equal("System Outage", groups[1].Name);
        Assert.Equal(outage.Id, Assert.Single(groups[1].Alerts).Id);
        Assert.Equal(3, categories.Length);
        Assert.Single(categories[0].Alerts);
        Assert.Equal(2, categories[2].Alerts.Count);
    }

    [Fact]
    public void SharedCollapseStateSurvivesChangesToContributingRulesAndNamesCase()
    {
        var state = new InboxCategoryState();
        var original = new InboxCategoryGroup("Suppressed alerts", "info", true, []);
        var filtered = new InboxCategoryGroup("suppressed ALERTS", "failure", true, []);
        var other = new InboxCategoryGroup("System Outage", "failure", false, []);

        Assert.True(state.IsCollapsed(original));
        Assert.False(state.IsCollapsed(other));
        state.Toggle(original);
        Assert.False(state.IsCollapsed(original));
        Assert.False(state.IsCollapsed(filtered));
        Assert.False(state.IsCollapsed(other));
        state.Toggle(filtered);
        Assert.True(state.IsCollapsed(original));
        Assert.True(state.IsCollapsed(filtered));
        Assert.False(state.IsCollapsed(other));
    }

    [Fact]
    public void UntoggledGroupUsesCurrentFirstContributingRuleDefault()
    {
        var state = new InboxCategoryState();
        var group = new InboxCategoryGroup("Suppressed alerts", "info", true, []);

        Assert.True(state.IsCollapsed(group));
        Assert.False(state.IsCollapsed(group with { Collapsed = false }));
        state.Toggle(group with { Collapsed = false });
        Assert.True(state.IsCollapsed(group with { Collapsed = false }));
    }

    [Fact]
    public void ProjectionPreservesSeparateRuleAssignmentsAndDecisionTraces()
    {
        var port = CreateAlert("Port check") with
        {
            RawJson = """{"queryResult":{"type":"DCPort","columns":[],"rows":[]}}"""
        };
        var diag = CreateAlert("DCDiag") with
        {
            RawJson = """{"queryResult":{"type":"DCDiag","columns":[],"rows":[]}}"""
        };
        var unrelated = CreateAlert("Heartbeat");
        var portRule = DCPortSuppressionRuleTests.CreateDefaultRule();
        var diagRule = new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = "Suppress healthy DCDiag results",
            Priority = 30,
            QueryResultType = "DCDiag",
            ConditionType = AlertRuleConditionTypes.NoFailedItems,
            CategoryName = "Suppressed alerts",
            Collapsed = true,
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
        };
        var evaluator = new AlertRuleEvaluator(
            new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));
        var evaluation = evaluator.EvaluateCategorization([diag, port, unrelated], [diagRule, portRule]);

        var merged = InboxCategories.Build(evaluation.Categorization.Categories, [diag, port, unrelated]);

        Assert.Equal([diag.Id, port.Id], Assert.Single(merged).Alerts.Select(alert => alert.Id));
        Assert.Equal([portRule.Id, diagRule.Id],
            evaluation.Categorization.Categories.Select(category => category.RuleId));
        Assert.All(evaluation.Categorization.Categories, category =>
            Assert.Equal(AlertRuleAnalysisClassifications.Suppressed, category.AnalysisClassification));
        var cell = Assert.Single(InboxTargetOutput.Build(
            [diag, port], [diag, port], [], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5),
            categories: evaluation.Categorization.Categories));
        Assert.Equal(InboxTargetColor.Green, cell.Color);
        Assert.Equal(2, cell.Count);
        Assert.Equal(unrelated.Id, Assert.Single(evaluation.Categorization.Uncategorized).Id);
        var applied = evaluation.Decisions.Where(decision =>
            decision.Outcome == AlertRuleEvaluationOutcomes.Applied).ToArray();
        Assert.Equal(2, applied.Length);
        Assert.Contains(applied, decision => decision.AlertEventId == port.Id &&
            decision.WinningRuleId == portRule.Id &&
            decision.AnalysisClassification == AlertRuleAnalysisClassifications.Suppressed);
        Assert.Contains(applied, decision => decision.AlertEventId == diag.Id &&
            decision.WinningRuleId == diagRule.Id &&
            decision.AnalysisClassification == AlertRuleAnalysisClassifications.Suppressed);
    }

    [Fact]
    public void EmptyCategorizationHasNoDisplayGroups()
    {
        Assert.Empty(InboxCategories.Build([], [CreateAlert("Uncategorized")]));
    }

    [Fact]
    public void EvaluatedCriticalOutageMetadataControlsTargetColorAndCategoryPosition()
    {
        var alert = CreateAlert("DCPort") with
        {
            RawJson = """{"queryResult":{"type":"DCPort","rows":[["Failed"]]}}"""
        };
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            QueryResultType = "DCPort",
            ConditionType = AlertRuleConditionTypes.RowCountGreaterThan,
            Threshold = 0,
            CategoryName = "Renamed outage",
            AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage,
            IsCritical = true
        };
        var evaluator = new AlertRuleEvaluator(
            new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));
        var categories = evaluator.Categorize([alert], [rule]).Categories;

        Assert.True(Assert.Single(categories).IsCritical);
        Assert.True(Assert.Single(InboxCategories.Build(categories, [alert])).IsOutage);
        var cell = Assert.Single(InboxTargetOutput.Build(
            [alert], [alert], [], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), categories: categories));
        Assert.True(cell.IsCriticalOutage);
        Assert.Equal(InboxTargetColor.Red, cell.Color);
    }

    [Fact]
    public void PlacesOutageFirstUncategorizedInMiddleAndSuppressedLastRegardlessOfNames()
    {
        var suppressed = CreateAlert("Suppressed member");
        var outage = CreateAlert("Outage member");
        var categorized = CreateAlert("Other category member");
        var uncategorized = CreateAlert("Uncategorized member");
        var categories = new[]
        {
            CreateCategory("Renamed quiet category", "info", false, suppressed) with
            {
                AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
            },
            CreateCategory("Renamed important category", "failure", false, outage) with
            {
                AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
            },
            CreateCategory("Other category", "info", false, categorized)
        };

        var items = InboxCategories.BuildItems(
            new AlertCategorizationResult(categories, [uncategorized]),
            [suppressed, outage, categorized, uncategorized],
            new InboxCategoryState());

        Assert.Equal(
            [
                "Renamed important category", "Outage member",
                "Other category", "Other category member",
                "Uncategorized alerts", "Uncategorized member",
                "Renamed quiet category", "Suppressed member"
            ],
            items.Select(item => item.Category?.Name ??
                (item.IsUncategorizedHeader ? "Uncategorized alerts" : item.Alert!.Name)));
    }

    [Fact]
    public void MixedClassificationGroupRemainsOutageAndRetainsFirstRulePresentation()
    {
        var suppressed = CreateAlert("Suppressed");
        var outage = CreateAlert("Outage");
        var categories = new[]
        {
            CreateCategory("Combined", "info", true, suppressed) with
            {
                AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
            },
            CreateCategory("Combined", "failure", false, outage) with
            {
                AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
            }
        };

        var group = Assert.Single(InboxCategories.Build(categories, [outage, suppressed]));

        Assert.True(group.IsOutage);
        Assert.False(group.IsSuppressed);
        Assert.Equal("info", group.Tone);
        Assert.True(group.Collapsed);
    }

    [Fact]
    public void CollapsedSuppressedGroupFollowsUncategorizedAndExpandsAsOneGroup()
    {
        var suppressed = CreateAlert("Suppressed");
        var uncategorized = CreateAlert("Uncategorized");
        var category = CreateCategory("Suppressed alerts", "info", true, suppressed) with
        {
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
        };
        var result = new AlertCategorizationResult([category], [uncategorized]);
        var state = new InboxCategoryState();
        var items = InboxCategories.BuildItems(result, [suppressed, uncategorized], state);

        Assert.Equal(3, items.Count);
        Assert.True(items[0].IsUncategorizedHeader);
        Assert.Equal(uncategorized.Id, items[1].Alert!.Id);
        Assert.Equal("Suppressed alerts", items[2].Category!.Name);
        state.Toggle(items[2].Category!);
        var expanded = InboxCategories.BuildItems(result, [suppressed, uncategorized], state);
        Assert.Equal(suppressed.Id, expanded[3].Alert!.Id);
        Assert.Equal("Suppressed alerts", expanded[2].Category!.Name);
        Assert.Empty(InboxCategories.BuildItems(new AlertCategorizationResult([], []), [], state));
    }

    private static AlertCategoryGroup CreateCategory(
        string name, string tone, bool collapsed, params AlertRecord[] alerts) =>
        new(Guid.NewGuid(), name, tone, collapsed, alerts);

    private static AlertRecord CreateAlert(string name) => new(
        Guid.NewGuid(), DateTimeOffset.Parse("2026-10-09T11:00:00Z"), Guid.NewGuid().ToString(),
        name, "Sev1", "", "Log", "Fired", "dc-1", "rg-test", "sub-test",
        null, "", "", "", "{}");
}
