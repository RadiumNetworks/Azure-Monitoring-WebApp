using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Evaluates enabled database rules against the normalized query results shown in the inbox.
/// </summary>
public sealed class AlertRuleEvaluator(QueryResultPresenter queryResultPresenter)
{
    public AlertCategorizationResult Categorize(
        IReadOnlyList<AlertRecord> alerts,
        IEnumerable<AlertRule> rules,
        IReadOnlyList<ComputerInventoryEntry>? inventory = null,
        IReadOnlyList<AlertRecord>? aggregationAlerts = null) =>
        EvaluateCategorization(alerts, rules, inventory, aggregationAlerts).Categorization;

    public AlertRuleEvaluationResult EvaluateCategorization(
        IReadOnlyList<AlertRecord> alerts,
        IEnumerable<AlertRule> rules,
        IReadOnlyList<ComputerInventoryEntry>? inventory = null,
        IReadOnlyList<AlertRecord>? aggregationAlerts = null)
    {
        var assignments = new Dictionary<Guid, AlertRule>();
        var decisions = new List<AlertRuleDecisionTrace>();
        var orderedRules = rules
            .Where(rule => rule.RuleType == AlertRuleTypes.Categorization)
            .OrderBy(rule => rule.Priority)
            .ToArray();

        foreach (var rule in orderedRules)
        {
            if (!rule.Enabled)
            {
                decisions.AddRange(alerts.Select(alert => CreateDecision(
                    alert,
                    rule,
                    AlertRuleEvaluationOutcomes.Disabled,
                    false,
                    null,
                    assignments.GetValueOrDefault(alert.Id)?.Id,
                    "The rule is disabled.",
                    [])));
                continue;
            }

            var directEvaluations = IsDomainControllerAggregateCondition(rule.ConditionType)
                ? EvaluateDomainControllerAggregateRule(
                    rule,
                    alerts,
                    aggregationAlerts ?? alerts,
                    inventory ?? [])
                : alerts.ToDictionary(alert => alert.Id, alert => EvaluateMatch(rule, alert));
            var triggers = alerts.Where(alert => directEvaluations[alert.Id].Matched).ToArray();

            foreach (var alert in alerts)
            {
                var direct = directEvaluations[alert.Id];
                var trigger = direct.Matched
                    ? alert
                    : rule.ApplyToTarget
                        ? triggers.FirstOrDefault(candidate => SameTarget(alert, candidate))
                        : null;
                if (trigger is null)
                {
                    decisions.Add(CreateDecision(
                        alert,
                        rule,
                        AlertRuleEvaluationOutcomes.NotMatched,
                        false,
                        null,
                        assignments.GetValueOrDefault(alert.Id)?.Id,
                        direct.Reason,
                        direct.Criteria));
                    continue;
                }

                var triggerEvaluation = direct.Matched ? direct : directEvaluations[trigger.Id];
                var isApplied = assignments.TryAdd(alert.Id, rule);
                var winner = assignments[alert.Id];
                var expanded = trigger.Id != alert.Id;
                decisions.Add(CreateDecision(
                    alert,
                    rule,
                    isApplied ? AlertRuleEvaluationOutcomes.Applied : AlertRuleEvaluationOutcomes.Superseded,
                    direct.Matched,
                    trigger.Id,
                    winner.Id,
                    isApplied
                        ? expanded
                            ? $"Rule matched event {trigger.Id} and expanded to the same target."
                            : "All configured criteria matched."
                        : $"Rule matched but rule {winner.Id} already won by priority.",
                    expanded
                        ? triggerEvaluation.Criteria.Concat(
                            [new AlertRuleCriterionEvaluation(
                                "Target",
                                "SameTarget",
                                trigger.TargetName,
                                alert.TargetName,
                                true)]).ToArray()
                        : triggerEvaluation.Criteria));
            }
        }

        var categories = assignments
            .GroupBy(assignment => assignment.Value.Id)
            .Select(group =>
            {
                var rule = group.First().Value;
                var memberIds = group.Select(assignment => assignment.Key).ToHashSet();
                return (Rule: rule, Group: new AlertCategoryGroup(
                    rule.Id,
                    rule.CategoryName,
                    rule.Tone,
                    rule.Collapsed,
                    alerts.Where(alert => memberIds.Contains(alert.Id)).ToArray(),
                    rule.AnalysisClassification,
                    rule.IsCritical));
            })
            .OrderBy(item => item.Rule.Priority)
            .Select(item => item.Group)
            .ToArray();
        var categorizedIds = assignments.Keys.ToHashSet();

        return new AlertRuleEvaluationResult(
            new AlertCategorizationResult(
                categories,
                alerts.Where(alert => !categorizedIds.Contains(alert.Id)).ToArray()),
            decisions.ToArray());
    }

    public bool MatchesRule(AlertRule rule, AlertRecord alert)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(alert);

        if (!rule.Enabled || rule.RuleType != AlertRuleTypes.Categorization)
        {
            return false;
        }

        return EvaluateMatch(rule, alert).Matched;
    }

    public bool MatchesSuppressionRule(AlertRule rule, AlertRecord alert)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(alert);

        if (!rule.Enabled || rule.RuleType != AlertRuleTypes.Suppression)
        {
            return false;
        }

        return EvaluateMatch(rule, alert).Matched;
    }

    public bool MatchesInventoryStatusRule(AlertRule rule, AlertRecord alert)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(alert);

        if (!rule.Enabled || rule.RuleType != AlertRuleTypes.InventoryStatus)
        {
            return false;
        }

        return EvaluateMatch(rule, alert).Matched;
    }

    private RuleMatchEvaluation EvaluateMatch(AlertRule rule, AlertRecord alert)
    {
        var criteria = new List<AlertRuleCriterionEvaluation>();
        if (!string.IsNullOrWhiteSpace(rule.AlertNameContains) &&
            !alert.Name.Contains(rule.AlertNameContains, StringComparison.OrdinalIgnoreCase))
        {
            criteria.Add(new AlertRuleCriterionEvaluation(
                "AlertName",
                "Contains",
                rule.AlertNameContains,
                alert.Name,
                false));
            return new RuleMatchEvaluation(false, "Alert name does not contain the configured value.", criteria);
        }

        if (!string.IsNullOrWhiteSpace(rule.AlertNameContains))
        {
            criteria.Add(new AlertRuleCriterionEvaluation(
                "AlertName",
                "Contains",
                rule.AlertNameContains,
                alert.Name,
                true));
        }

        if (rule.ConditionType == AlertRuleConditionTypes.AnyMatchingAlert)
        {
            if (string.IsNullOrWhiteSpace(rule.QueryResultType))
            {
                return new RuleMatchEvaluation(true, "All configured criteria matched.", criteria);
            }

            var hasQueryResult = TryReadQueryResult(alert.RawJson, out var anyType, out _);
            var typeMatched = hasQueryResult &&
                anyType.Equals(rule.QueryResultType, StringComparison.OrdinalIgnoreCase);
            criteria.Add(new AlertRuleCriterionEvaluation(
                "QueryResultType",
                "Equals",
                rule.QueryResultType,
                hasQueryResult ? anyType : "<missing>",
                typeMatched));
            return new RuleMatchEvaluation(
                typeMatched,
                typeMatched ? "All configured criteria matched." : "Query-result type did not match.",
                criteria);
        }

        if (!TryReadQueryResult(alert.RawJson, out var type, out var rowCount) ||
            !type.Equals(rule.QueryResultType, StringComparison.OrdinalIgnoreCase))
        {
            criteria.Add(new AlertRuleCriterionEvaluation(
                "QueryResultType",
                "Equals",
                rule.QueryResultType,
                string.IsNullOrWhiteSpace(type) ? "<missing>" : type,
                false));
            return new RuleMatchEvaluation(false, "Query-result type did not match.", criteria);
        }

        criteria.Add(new AlertRuleCriterionEvaluation(
            "QueryResultType",
            "Equals",
            rule.QueryResultType,
            type,
            true));
        var conditionMatched = rule.ConditionType switch
        {
            AlertRuleConditionTypes.RowCountGreaterThan => rowCount > rule.Threshold,
            AlertRuleConditionTypes.RowCountEquals => rowCount == rule.Threshold,
            AlertRuleConditionTypes.OnlyFailedItems => HasOnlyFailedItems(alert, rule.FailedItemName),
            AlertRuleConditionTypes.NoFailedItems => HasNoFailedItems(alert),
            _ => false
        };
        criteria.Add(new AlertRuleCriterionEvaluation(
            ConditionField(rule.ConditionType),
            rule.ConditionType,
            ConditionExpected(rule),
            ConditionActual(rule.ConditionType, alert, rowCount),
            conditionMatched));
        return new RuleMatchEvaluation(
            conditionMatched,
            conditionMatched ? "All configured criteria matched." : "The configured condition did not match.",
            criteria);
    }

    private static Dictionary<Guid, RuleMatchEvaluation> EvaluateDomainControllerAggregateRule(
        AlertRule rule,
        IReadOnlyList<AlertRecord> alerts,
        IReadOnlyList<AlertRecord> aggregationAlerts,
        IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var domainControllers = inventory
            .Where(IsDomainController)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Domain))
            .ToArray();
        var inventoryByTarget = domainControllers
            .GroupBy(InventoryTargetKey)
            .ToDictionary(group => group.Key, group => group.First());
        var failingTargets = aggregationAlerts
            .Select(alert => InventoryTargetKey(alert.SubscriptionId, alert.TargetName))
            .Where(inventoryByTarget.ContainsKey)
            .ToHashSet();

        return alerts.ToDictionary(
            alert => alert.Id,
            alert => EvaluateDomainControllerAggregateAlert(
                rule,
                alert,
                domainControllers,
                inventoryByTarget,
                failingTargets));
    }

    private static RuleMatchEvaluation EvaluateDomainControllerAggregateAlert(
        AlertRule rule,
        AlertRecord alert,
        IReadOnlyList<ComputerInventoryEntry> domainControllers,
        IReadOnlyDictionary<string, ComputerInventoryEntry> inventoryByTarget,
        IReadOnlySet<string> failingTargets)
    {
        var targetKey = InventoryTargetKey(alert.SubscriptionId, alert.TargetName);
        if (!inventoryByTarget.TryGetValue(targetKey, out var target))
        {
            return new RuleMatchEvaluation(
                false,
                "The alert target is not a domain controller in the inventory.",
                [new AlertRuleCriterionEvaluation(
                    "InventoryRole",
                    "Equals",
                    "Domain Controller",
                    "<missing>",
                    false)]);
        }

        var scopeControllers = domainControllers
            .Where(candidate =>
                candidate.SubscriptionId.Equals(target.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                candidate.Domain!.Equals(target.Domain, StringComparison.OrdinalIgnoreCase) &&
                (rule.ConditionType != AlertRuleConditionTypes.AllDomainControllersInSite ||
                    (!string.IsNullOrWhiteSpace(target.Site) &&
                     candidate.Site?.Equals(target.Site, StringComparison.OrdinalIgnoreCase) == true)))
            .ToArray();
        var failingCount = scopeControllers.Count(candidate => failingTargets.Contains(InventoryTargetKey(candidate)));
        var totalCount = scopeControllers.Length;
        var actualPercentage = totalCount == 0 ? 0 : failingCount * 100d / totalCount;
        var matched = rule.ConditionType switch
        {
            AlertRuleConditionTypes.DomainControllerPercentageInDomain =>
                actualPercentage > rule.Threshold,
            AlertRuleConditionTypes.AllDomainControllersInSite =>
                !string.IsNullOrWhiteSpace(target.Site) &&
                totalCount > 0 &&
                failingCount == totalCount,
            _ => false
        };
        var scopeName = rule.ConditionType == AlertRuleConditionTypes.AllDomainControllersInSite
            ? $"{target.Domain}/{target.Site}"
            : target.Domain!;
        var criterion = rule.ConditionType == AlertRuleConditionTypes.AllDomainControllersInSite
            ? new AlertRuleCriterionEvaluation(
                "FailingDomainControllersInSite",
                "Equals",
                totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                failingCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                matched)
            : new AlertRuleCriterionEvaluation(
                "FailingDomainControllerPercentage",
                "GreaterThan",
                rule.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                actualPercentage.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                matched);

        return new RuleMatchEvaluation(
            matched,
            matched
                ? $"{failingCount} of {totalCount} domain controllers in {scopeName} have active alerts."
                : $"{failingCount} of {totalCount} domain controllers in {scopeName} have active alerts; the aggregate condition was not met.",
            [criterion]);
    }

    private static AlertRuleDecisionTrace CreateDecision(
        AlertRecord alert,
        AlertRule rule,
        string outcome,
        bool directMatch,
        Guid? triggerEventId,
        Guid? winningRuleId,
        string reason,
        IReadOnlyList<AlertRuleCriterionEvaluation> criteria) => new(
            alert.Id,
            alert.AlertId,
            rule.Id,
            rule.Name,
            rule.Priority,
            rule.AnalysisClassification,
            outcome,
            directMatch,
            triggerEventId,
            winningRuleId,
            reason,
            criteria);

    private string ConditionActual(string conditionType, AlertRecord alert, int rowCount) => conditionType switch
    {
        AlertRuleConditionTypes.RowCountGreaterThan or AlertRuleConditionTypes.RowCountEquals =>
            rowCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AlertRuleConditionTypes.OnlyFailedItems => string.Join(", ", GetFailedItems(alert)),
        AlertRuleConditionTypes.NoFailedItems => GetFailedItems(alert).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "<unsupported>"
    };

    private static string ConditionExpected(AlertRule rule) => rule.ConditionType switch
    {
        AlertRuleConditionTypes.RowCountGreaterThan or AlertRuleConditionTypes.RowCountEquals =>
            rule.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AlertRuleConditionTypes.OnlyFailedItems => rule.FailedItemName,
        AlertRuleConditionTypes.NoFailedItems => "0",
        _ => string.Empty
    };

    private static string ConditionField(string conditionType) => conditionType switch
    {
        AlertRuleConditionTypes.RowCountGreaterThan or AlertRuleConditionTypes.RowCountEquals => "QueryResultRowCount",
        AlertRuleConditionTypes.OnlyFailedItems => "FailedItems",
        AlertRuleConditionTypes.NoFailedItems => "FailedItemCount",
        _ => "Condition"
    };

    private bool HasOnlyFailedItems(AlertRecord alert, string failedItemNames)
    {
        var allowedFailures = failedItemNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = GetFailedItems(alert).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowedFailures.Count > 0 &&
            failures.Count > 0 &&
            failures.IsSubsetOf(allowedFailures);
    }

    private bool HasNoFailedItems(AlertRecord alert)
    {
        var presentation = queryResultPresenter.Parse(alert.RawJson);
        return presentation is not null && GetFailedItems(alert).Count == 0;
    }

    private IReadOnlyList<string> GetFailedItems(AlertRecord alert) =>
        queryResultPresenter.Parse(alert.RawJson)?.Rows
            .SelectMany(row => row.Alerts)
            .Where(item => !item.Value.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static bool TryReadQueryResult(string rawJson, out string type, out int rowCount)
    {
        type = string.Empty;
        rowCount = 0;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("queryResult", out var queryResult) ||
                !queryResult.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !queryResult.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            type = typeElement.GetString() ?? string.Empty;
            rowCount = rows.GetArrayLength();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SameTarget(AlertRecord left, AlertRecord right) =>
        left.SubscriptionId.Equals(right.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
        left.TargetName.Equals(right.TargetName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDomainControllerAggregateCondition(string conditionType) =>
        conditionType is
            AlertRuleConditionTypes.DomainControllerPercentageInDomain or
            AlertRuleConditionTypes.AllDomainControllersInSite;

    private static bool IsDomainController(ComputerInventoryEntry entry) =>
        InventoryRoleRuleMatcher.NormalizeRole(entry.Role) == "Domain Controller";

    private static string InventoryTargetKey(ComputerInventoryEntry entry) =>
        InventoryTargetKey(entry.SubscriptionId, entry.Computer);

    private static string InventoryTargetKey(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim().ToUpperInvariant()}\0{computer.Trim().ToUpperInvariant()}";

    private sealed record RuleMatchEvaluation(
        bool Matched,
        string Reason,
        IReadOnlyList<AlertRuleCriterionEvaluation> Criteria);
}