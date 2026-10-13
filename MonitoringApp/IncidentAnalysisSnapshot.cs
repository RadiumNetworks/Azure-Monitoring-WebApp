using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public enum IncidentAnalysisScopeType
{
    Incident,
    GraphSubtree,
    Target
}

public enum IncidentEvidenceTier
{
    Primary = 1,
    SystemHistory = 2,
    Context24Hours = 3
}

public enum IncidentRuleDecisionType
{
    None,
    Categorized,
    SystemOutage,
    Suppressed
}

public enum IncidentAssessmentOrigin
{
    Telemetry,
    Rule,
    AgentHypothesis
}

public sealed record IncidentAnalysisScope(
    IncidentAnalysisScopeType Type,
    string Value = "",
    AlertGraphLayer? Layer = null);

public sealed record IncidentAnalysisSnapshotOptions(
    int SystemHistoryDays = 7,
    int ContextHours = 24,
    int MaxAlerts = 100,
    int MaxCharacters = 120_000,
    TimeSpan? CurrentHeartbeatAge = null,
    bool IncludeSystemHistory = true,
    bool IncludeRelatedContext = true,
    IReadOnlyList<AlertGraphLayer>? InventoryCorrelationLayers = null)
{
    public TimeSpan EffectiveCurrentHeartbeatAge => CurrentHeartbeatAge ?? AlertGraphTargetStatusResolver.CurrentThreshold;

    public IReadOnlyList<AlertGraphLayer> EffectiveInventoryCorrelationLayers =>
        InventoryCorrelationLayers ?? [AlertGraphLayer.Domain, AlertGraphLayer.Role];

    public void Validate()
    {
        if (SystemHistoryDays is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(SystemHistoryDays));
        }

        if (ContextHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextHours));
        }

        if (MaxAlerts is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAlerts));
        }

        if (MaxCharacters is < 1_000 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCharacters));
        }

        var contextOptions = new AgentAnalysisContextOptions
        {
            InventoryCorrelationLayers = EffectiveInventoryCorrelationLayers.ToArray()
        };
        if (contextOptions.Validate().Count > 0)
        {
            throw new ArgumentException(
                string.Join(" ", contextOptions.Validate()),
                nameof(InventoryCorrelationLayers));
        }
    }
}

public sealed record IncidentAnalysisSnapshot(
    DateTimeOffset GeneratedAt,
    IncidentAnalysisScope Scope,
    DateTimeOffset DataFrom,
    DateTimeOffset DataTo,
    int MaxAlerts,
    int MaxCharacters,
    IReadOnlyList<IncidentAlertEvidence> PrimaryEvidence,
    IReadOnlyList<IncidentAlertEvidence> SystemHistory,
    IReadOnlyList<IncidentAlertEvidence> Context24Hours,
    IReadOnlyList<IncidentAlertLifecycle> Lifecycles,
    IncidentGraphContext Graph,
    IReadOnlyList<IncidentInventoryEvidence> Inventory,
    IReadOnlyList<IncidentRuleEvidence> Rules,
    IReadOnlyList<AlertRuleDecisionTrace> RuleEvaluations,
    IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<string> ConflictingEvidence,
    IReadOnlyList<string> TruncationDetails)
{
    public const string CurrentVersion = "incident-analysis-snapshot-v1";

    public int AlertCount => PrimaryEvidence.Count + SystemHistory.Count + Context24Hours.Count;

    [JsonIgnore]
    public int CharacterCount => ToCanonicalJson().Length;

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public sealed record IncidentAlertEvidence(
    Guid EventId,
    string AlertId,
    IncidentEvidenceTier Tier,
    string SelectionReason,
    TimeSpan AgeAtSnapshot,
    IReadOnlyList<Guid> RelatedPrimaryEventIds,
    IReadOnlyList<string> RelevanceReasons,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? FiredAt,
    string Name,
    string Severity,
    string SignalType,
    string MonitorCondition,
    string SubscriptionId,
    string ResourceGroup,
    string Target,
    string Site,
    string Domain,
    string Role,
    string Description,
    IncidentRuleDecision Decision,
    IncidentDiagnosticEvidence? Diagnostics);

public sealed record IncidentRuleDecision(
    IncidentRuleDecisionType Type,
    IncidentAssessmentOrigin Origin,
    Guid? RuleId,
    string RuleName,
    string CategoryName,
    bool EventPersisted,
    bool RuleMatched,
    string VisibleClassification,
    bool Collapsed);

public sealed record IncidentAlertLifecycle(
    string AlertId,
    IReadOnlyList<IncidentLifecycleEvent> Events);

public sealed record IncidentLifecycleEvent(
    Guid EventId,
    DateTimeOffset ReceivedAt,
    string MonitorCondition,
    IncidentEvidenceTier Tier);

public sealed record IncidentDiagnosticEvidence(
    string Label,
    IReadOnlyList<string> Summary,
    IReadOnlyList<IncidentDiagnosticRow> Rows);

public sealed record IncidentDiagnosticRow(
    string Title,
    IReadOnlyList<IncidentDiagnosticValue> Values);

public sealed record IncidentDiagnosticValue(string Label, string Value);

public sealed record IncidentGraphContext(
    AlertGraphLayer Layer1,
    AlertGraphLayer Layer2,
    AlertGraphLayer Layer3,
    string SelectionRule,
    IReadOnlyList<AlertGraphHierarchyNode> Roots);

public sealed record IncidentInventoryEvidence(
    string SubscriptionId,
    string Computer,
    string ResourceGroup,
    string Domain,
    string Site,
    string Role,
    DateTimeOffset? LastHeartbeatAt,
    string HeartbeatStatus);

public sealed record IncidentRuleEvidence(
    Guid Id,
    string Name,
    string RuleType,
    string AnalysisClassification,
    int Priority,
    bool Enabled,
    string AlertNameContains,
    string QueryResultType,
    string ConditionType,
    int Threshold,
    string FailedItemName,
    string CategoryName,
    bool ApplyToTarget,
    bool Collapsed,
    bool IsCritical);

public sealed class IncidentAnalysisSnapshotBuilder(
    QueryResultPresenter queryResultPresenter,
    AlertRuleEvaluator alertRuleEvaluator)
{
    public IncidentAnalysisSnapshot Build(
        IEnumerable<AlertRecord> primaryAlerts,
        IEnumerable<AlertRecord> allAlerts,
        IEnumerable<AlertGraphRecord> graphRecords,
        IEnumerable<ComputerInventoryEntry> inventory,
        IEnumerable<AlertRule> rules,
        DateTimeOffset generatedAt,
        IncidentAnalysisScope scope,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3,
        IncidentAnalysisSnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(primaryAlerts);
        ArgumentNullException.ThrowIfNull(allAlerts);
        ArgumentNullException.ThrowIfNull(graphRecords);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(scope);

        options ??= new IncidentAnalysisSnapshotOptions();
        options.Validate();

        var primary = OrderAlerts(primaryAlerts)
            .DistinctBy(alert => alert.Id)
            .Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert))
            .ToArray();
        if (primary.Length == 0)
        {
            throw new InvalidOperationException("At least one primary alert is required.");
        }

        if (primary.Length > options.MaxAlerts)
        {
            throw new InvalidOperationException("Primary evidence exceeds the configured alert limit.");
        }

        var alerts = OrderAlerts(allAlerts.Concat(primary))
            .DistinctBy(alert => alert.Id)
            .Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert))
            .ToArray();
        var graph = graphRecords
            .Where(record => record.ReceivedAt <= generatedAt)
            .OrderBy(record => record.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Target, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(record => record.ReceivedAt)
            .ToArray();
        var orderedRules = rules
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Id)
            .ToArray();
        options = options with
        {
            CurrentHeartbeatAge = options.CurrentHeartbeatAge ?? InventoryHeartbeatFreshnessRule.Resolve(orderedRules)
        };
        var ruleEvaluation = alertRuleEvaluator.EvaluateCategorization(
            alerts,
            orderedRules,
            inventory.ToArray());
        var decisions = BuildDecisions(alerts, orderedRules, ruleEvaluation);
        var graphLookup = BuildGraphLookup(graph);
        var primaryIds = primary.Select(alert => alert.Id).ToHashSet();
        var primaryKeys = primary.Select(alert => TargetKey(alert, graphLookup)).ToHashSet();
        var primarySubscriptions = primary
            .Select(alert => alert.SubscriptionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasSystemOutage = primary.Any(alert =>
            decisions.GetValueOrDefault(alert.Id)?.Type == IncidentRuleDecisionType.SystemOutage);

        var systemCutoff = generatedAt.AddDays(-options.SystemHistoryDays);
        var systemHistory = hasSystemOutage && options.IncludeSystemHistory
            ? alerts.Where(alert =>
                    !primaryIds.Contains(alert.Id) &&
                    alert.ReceivedAt <= generatedAt &&
                    alert.ReceivedAt >= systemCutoff &&
                    primaryKeys.Contains(TargetKey(alert, graphLookup)))
                .ToArray()
            : [];
        var systemIds = systemHistory.Select(alert => alert.Id).ToHashSet();
        var contextCutoff = generatedAt.AddHours(-options.ContextHours);
        var context = options.IncludeRelatedContext
            ? alerts.Where(alert =>
                    !primaryIds.Contains(alert.Id) &&
                    !systemIds.Contains(alert.Id) &&
                    alert.ReceivedAt <= generatedAt &&
                    alert.ReceivedAt >= contextCutoff &&
                    primarySubscriptions.Contains(alert.SubscriptionId) &&
                    IsRelevantContext(alert, primary, graphLookup))
                .ToArray()
            : [];

        var truncation = new List<string>();
        var remaining = options.MaxAlerts - primary.Length;
        systemHistory = TakeWithNotice(systemHistory, remaining, "system history", truncation);
        remaining -= systemHistory.Length;
        context = TakeWithNotice(context, remaining, "24-hour context", truncation);

        var primaryEvidence = BuildEvidence(
            primary, primary, IncidentEvidenceTier.Primary, "Selected primary evidence",
            generatedAt, decisions, graphLookup, layer1, layer2, layer3);
        var systemEvidence = BuildEvidence(
            systemHistory, primary, IncidentEvidenceTier.SystemHistory,
            $"Same subscription and target within {options.SystemHistoryDays} days of a System Outage",
            generatedAt, decisions, graphLookup, layer1, layer2, layer3);
        var contextEvidence = BuildEvidence(
            context, primary, IncidentEvidenceTier.Context24Hours,
            $"Related alert within the authorized {options.ContextHours}-hour context",
            generatedAt, decisions, graphLookup, layer1, layer2, layer3);

        var selectedGraphRecords = SelectGraphRecords(
            graph,
            primary,
            scope,
            graphLookup,
            options.EffectiveInventoryCorrelationLayers);
        var graphContext = new IncidentGraphContext(
            layer1,
            layer2,
            layer3,
            DescribeGraphSelection(scope, layer1),
            AlertGraphHierarchy.Build(selectedGraphRecords, layer1, layer2, layer3));
        var inventoryEvidence = BuildInventory(
            inventory,
            selectedGraphRecords,
            primaryKeys,
            generatedAt,
            options.EffectiveCurrentHeartbeatAge);
        var missing = FindMissingEvidence(primaryEvidence, inventoryEvidence);
        var conflicts = FindConflictingEvidence(primaryEvidence, systemEvidence, contextEvidence);
        var lifecycles = BuildLifecycles(primaryEvidence, systemEvidence, contextEvidence);
        var selectedEventIds = primaryEvidence.Concat(systemEvidence).Concat(contextEvidence)
            .Select(evidence => evidence.EventId)
            .ToHashSet();

        var dataFrom = new List<DateTimeOffset> { primary.Min(alert => alert.ReceivedAt) };
        if (options.IncludeSystemHistory)
        {
            dataFrom.Add(systemCutoff);
        }

        if (options.IncludeRelatedContext)
        {
            dataFrom.Add(contextCutoff);
        }

        var snapshot = new IncidentAnalysisSnapshot(
            generatedAt,
            scope,
            dataFrom.Min(),
            generatedAt,
            options.MaxAlerts,
            options.MaxCharacters,
            primaryEvidence,
            systemEvidence,
            contextEvidence,
            lifecycles,
            graphContext,
            inventoryEvidence,
            orderedRules.Select(ToRuleEvidence).ToArray(),
            ruleEvaluation.Decisions.Where(decision => selectedEventIds.Contains(decision.AlertEventId)).ToArray(),
            missing,
            conflicts,
            truncation.Order(StringComparer.Ordinal).ToArray());
        return ApplyCharacterLimit(snapshot);
    }

    private Dictionary<Guid, IncidentRuleDecision> BuildDecisions(
        IReadOnlyList<AlertRecord> alerts,
        IReadOnlyList<AlertRule> rules,
        AlertRuleEvaluationResult evaluation)
    {
        var result = alerts.ToDictionary(
            alert => alert.Id,
            _ => NoRuleDecision);
        var rulesById = rules.ToDictionary(rule => rule.Id);
        var categoriesByRuleId = evaluation.Categorization.Categories.ToDictionary(category => category.RuleId);
        foreach (var applied in evaluation.Decisions.Where(decision =>
            decision.Outcome == AlertRuleEvaluationOutcomes.Applied))
        {
            var rule = rulesById[applied.RuleId];
            var category = categoriesByRuleId[applied.RuleId];
            var type = rule.AnalysisClassification switch
            {
                AlertRuleAnalysisClassifications.SystemOutage => IncidentRuleDecisionType.SystemOutage,
                AlertRuleAnalysisClassifications.Suppressed => IncidentRuleDecisionType.Suppressed,
                _ => IncidentRuleDecisionType.Categorized
            };
            result[applied.AlertEventId] = new IncidentRuleDecision(
                type,
                IncidentAssessmentOrigin.Rule,
                rule.Id,
                rule.Name,
                category.Name,
                true,
                true,
                category.Name,
                category.Collapsed);
        }

        return result;
    }

    private IReadOnlyList<IncidentAlertEvidence> BuildEvidence(
        IEnumerable<AlertRecord> alerts,
        IReadOnlyList<AlertRecord> primary,
        IncidentEvidenceTier tier,
        string reason,
        DateTimeOffset generatedAt,
        IReadOnlyDictionary<Guid, IncidentRuleDecision> decisions,
        IReadOnlyDictionary<string, AlertGraphRecord> graphLookup,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3) => alerts
        .Select(alert =>
        {
            graphLookup.TryGetValue(GraphKey(alert), out var graph);
            var relationships = DescribeRelationships(
                alert, primary, generatedAt, graphLookup, layer1, layer2, layer3);
            return new IncidentAlertEvidence(
                alert.Id,
                alert.AlertId,
                tier,
                reason,
                generatedAt >= alert.ReceivedAt ? generatedAt - alert.ReceivedAt : TimeSpan.Zero,
                relationships.PrimaryEventIds,
                relationships.Reasons,
                alert.ReceivedAt,
                alert.FiredAt,
                Normalize(alert.Name),
                Normalize(alert.Severity),
                Normalize(alert.SignalType),
                Normalize(alert.MonitorCondition),
                Normalize(alert.SubscriptionId),
                Normalize(string.IsNullOrWhiteSpace(graph?.ResourceGroup) ? alert.ResourceGroup : graph.ResourceGroup),
                Normalize(string.IsNullOrWhiteSpace(graph?.Target) ? alert.TargetName : graph.Target),
                Normalize(graph?.Site ?? alert.SiteName),
                Normalize(graph?.Domain ?? string.Empty),
                Normalize(graph?.Role ?? string.Empty),
                Normalize(alert.Description),
                decisions.GetValueOrDefault(alert.Id) ?? NoRuleDecision,
                BuildDiagnostics(alert.RawJson));
        })
        .ToArray();

    private static (IReadOnlyList<Guid> PrimaryEventIds, IReadOnlyList<string> Reasons) DescribeRelationships(
        AlertRecord alert,
        IReadOnlyList<AlertRecord> primary,
        DateTimeOffset generatedAt,
        IReadOnlyDictionary<string, AlertGraphRecord> graphLookup,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3)
    {
        graphLookup.TryGetValue(GraphKey(alert), out var alertGraph);
        var relatedIds = new List<Guid>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var primaryAlert in primary)
        {
            graphLookup.TryGetValue(GraphKey(primaryAlert), out var primaryGraph);
            var related = false;
            if (alert.Id == primaryAlert.Id)
            {
                reasons.Add("Selected primary evidence");
                related = true;
            }
            if (TargetKey(alert, graphLookup).Equals(TargetKey(primaryAlert, graphLookup), StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Same subscription and target");
                related = true;
            }
            if (SameNonEmpty(alertGraph?.Site, primaryGraph?.Site))
            {
                reasons.Add("Same site");
                related = true;
            }
            if (SameNonEmpty(alertGraph?.Role, primaryGraph?.Role))
            {
                reasons.Add("Same role");
                related = true;
            }
            if (alert.Name.Equals(primaryAlert.Name, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Same alert type");
                related = true;
            }
            if (alertGraph is not null && primaryGraph is not null &&
                new[] { layer1, layer2, layer3 }.Any(layer =>
                    SameNonEmpty(LayerValue(alertGraph, layer), LayerValue(primaryGraph, layer))))
            {
                reasons.Add("Shared configured graph path");
                related = true;
            }
            if (generatedAt >= alert.ReceivedAt && generatedAt - alert.ReceivedAt <= TimeSpan.FromHours(24))
            {
                reasons.Add("Within 24 hours");
            }
            if (related)
            {
                relatedIds.Add(primaryAlert.Id);
            }
        }

        return (
            relatedIds.Distinct().Order().ToArray(),
            reasons.Order(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<IncidentAlertLifecycle> BuildLifecycles(
        params IReadOnlyList<IncidentAlertEvidence>[] tiers) => tiers
        .SelectMany(tier => tier)
        .Where(alert => !string.IsNullOrWhiteSpace(alert.AlertId))
        .GroupBy(alert => alert.AlertId, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new IncidentAlertLifecycle(
            group.Key,
            group.OrderBy(alert => alert.ReceivedAt)
                .ThenBy(alert => alert.MonitorCondition, StringComparer.OrdinalIgnoreCase)
                .ThenBy(alert => alert.EventId)
                .Select(alert => new IncidentLifecycleEvent(
                    alert.EventId,
                    alert.ReceivedAt,
                    alert.MonitorCondition,
                    alert.Tier))
                .ToArray()))
        .ToArray();

    private IncidentDiagnosticEvidence? BuildDiagnostics(string rawJson)
    {
        var presentation = queryResultPresenter.Parse(rawJson);
        if (presentation is null)
        {
            return null;
        }

        return new IncidentDiagnosticEvidence(
            Normalize(presentation.Label),
            presentation.Summary.Select(item => Normalize(item.Text)).ToArray(),
            presentation.Rows.Select(row => new IncidentDiagnosticRow(
                Normalize(row.Title),
                row.Metadata.Concat(row.Facts)
                    .Select(value => new IncidentDiagnosticValue(Normalize(value.Label), Normalize(value.Value)))
                    .Concat(row.Alerts.Select(value =>
                        new IncidentDiagnosticValue(Normalize(value.Label), Normalize(value.Value))))
                    .Concat(row.Details.SelectMany(details => details.Items.Select(value =>
                        new IncidentDiagnosticValue(
                            $"{Normalize(details.Label)}: {Normalize(value.Label)}",
                            Normalize(value.Value)))))
                    .ToArray()))
                .ToArray());
    }

    private static IReadOnlyDictionary<string, AlertGraphRecord> BuildGraphLookup(
        IEnumerable<AlertGraphRecord> records) => records
        .Where(record => !record.IsInventoryOnly && !string.IsNullOrWhiteSpace(record.AlertId))
        .GroupBy(GraphKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(record => record.ReceivedAt).First(),
            StringComparer.OrdinalIgnoreCase);

    private static AlertGraphRecord[] SelectGraphRecords(
        IReadOnlyList<AlertGraphRecord> records,
        IReadOnlyList<AlertRecord> primary,
        IncidentAnalysisScope scope,
        IReadOnlyDictionary<string, AlertGraphRecord> graphLookup,
        IReadOnlyList<AlertGraphLayer> correlationLayers)
    {
        var primarySubscriptions = primary.Select(alert => alert.SubscriptionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (scope.Type == IncidentAnalysisScopeType.GraphSubtree && scope.Layer.HasValue)
        {
            return records.Where(record => primarySubscriptions.Contains(record.SubscriptionId) &&
                    LayerValue(record, scope.Layer.Value).Equals(scope.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (scope.Type == IncidentAnalysisScopeType.Target)
        {
            return records.Where(record => primarySubscriptions.Contains(record.SubscriptionId) &&
                    record.Target.Equals(scope.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var primaryRecords = primary
            .Select(alert => graphLookup.GetValueOrDefault(GraphKey(alert)))
            .Where(record => record is not null)
            .Cast<AlertGraphRecord>()
            .ToArray();
        var primaryKeys = primary.Select(alert => TargetKey(alert, graphLookup))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return records.Where(record =>
                primaryKeys.Contains(TargetKey(record.SubscriptionId, record.Target)) ||
                primaryRecords.Any(primaryRecord =>
                    record.SubscriptionId.Equals(primaryRecord.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                    correlationLayers.All(layer => SameNonEmpty(
                        LayerValue(record, layer),
                        LayerValue(primaryRecord, layer)))))
            .ToArray();
    }

    private static IReadOnlyList<IncidentInventoryEvidence> BuildInventory(
        IEnumerable<ComputerInventoryEntry> inventory,
        IEnumerable<AlertGraphRecord> selectedGraphRecords,
        IReadOnlySet<string> primaryKeys,
        DateTimeOffset generatedAt,
        TimeSpan currentHeartbeatAge)
    {
        var graphKeys = selectedGraphRecords
            .Select(record => TargetKey(record.SubscriptionId, record.Target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return inventory
            .Where(entry => graphKeys.Contains(TargetKey(entry.SubscriptionId, entry.Computer)) ||
                primaryKeys.Contains(TargetKey(entry.SubscriptionId, entry.Computer)))
            .OrderBy(entry => entry.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Computer, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new IncidentInventoryEvidence(
                Normalize(entry.SubscriptionId),
                Normalize(entry.Computer),
                Normalize(entry.ResourceGroup ?? string.Empty),
                Normalize(entry.Domain ?? string.Empty),
                Normalize(entry.Site ?? string.Empty),
                Normalize(entry.Role ?? string.Empty),
                entry.LastHeartbeatAt,
                !entry.LastHeartbeatAt.HasValue
                    ? "Unknown"
                    : InventoryHeartbeatFreshnessRule.IsCurrent(entry.LastHeartbeatAt, generatedAt, currentHeartbeatAge)
                        ? "Current"
                        : "Stale"))
            .ToArray();
    }

    private static IReadOnlyList<string> FindMissingEvidence(
        IEnumerable<IncidentAlertEvidence> primary,
        IReadOnlyList<IncidentInventoryEvidence> inventory)
    {
        var inventoryKeys = inventory.Select(item => TargetKey(item.SubscriptionId, item.Computer))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return primary
            .SelectMany(alert =>
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(alert.SubscriptionId))
                {
                    missing.Add($"Alert event {alert.EventId} has no subscription ID.");
                }
                if (string.IsNullOrWhiteSpace(alert.Target))
                {
                    missing.Add($"Alert event {alert.EventId} has no target.");
                }
                else if (!inventoryKeys.Contains(TargetKey(alert.SubscriptionId, alert.Target)))
                {
                    missing.Add($"Alert event {alert.EventId} has no matching inventory entry.");
                }
                return missing;
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> FindConflictingEvidence(params IReadOnlyList<IncidentAlertEvidence>[] tiers) => tiers
        .SelectMany(tier => tier)
        .Where(alert => !string.IsNullOrWhiteSpace(alert.AlertId))
        .GroupBy(alert => alert.AlertId, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.GroupBy(alert => alert.ReceivedAt)
            .Any(events => events.Select(alert => alert.MonitorCondition).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        .Select(group => $"Alert {group.Key} has conflicting conditions at the same timestamp.")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static IncidentRuleEvidence ToRuleEvidence(AlertRule rule) => new(
        rule.Id,
        Normalize(rule.Name),
        Normalize(rule.RuleType),
        Normalize(rule.AnalysisClassification),
        rule.Priority,
        rule.Enabled,
        Normalize(rule.AlertNameContains),
        Normalize(rule.QueryResultType),
        Normalize(rule.ConditionType),
        rule.Threshold,
        Normalize(rule.FailedItemName),
        Normalize(rule.CategoryName),
        rule.ApplyToTarget,
        rule.Collapsed,
        rule.IsCritical);

    private static AlertRecord[] TakeWithNotice(
        IReadOnlyList<AlertRecord> alerts,
        int count,
        string label,
        ICollection<string> truncation)
    {
        var take = Math.Max(0, count);
        if (alerts.Count > take)
        {
            truncation.Add($"Removed {alerts.Count - take} {label} alerts because the alert limit was reached.");
        }
        return alerts.Take(take).ToArray();
    }

    private static IncidentAnalysisSnapshot ApplyCharacterLimit(IncidentAnalysisSnapshot snapshot)
    {
        var context = snapshot.Context24Hours.ToList();
        var systemHistory = snapshot.SystemHistory.ToList();
        var removedContext = 0;
        var removedSystemHistory = 0;
        while (snapshot.CharacterCount > snapshot.MaxCharacters && context.Count > 0)
        {
            context.RemoveAt(context.Count - 1);
            removedContext++;
            snapshot = WithCharacterTruncation(snapshot, systemHistory, context, removedSystemHistory, removedContext);
        }
        while (snapshot.CharacterCount > snapshot.MaxCharacters && systemHistory.Count > 0)
        {
            systemHistory.RemoveAt(systemHistory.Count - 1);
            removedSystemHistory++;
            snapshot = WithCharacterTruncation(snapshot, systemHistory, context, removedSystemHistory, removedContext);
        }

        if (snapshot.CharacterCount > snapshot.MaxCharacters)
        {
            throw new InvalidOperationException(
                "Primary evidence and structural context exceed the configured character limit.");
        }

        return snapshot;
    }

    private static IncidentAnalysisSnapshot WithCharacterTruncation(
        IncidentAnalysisSnapshot snapshot,
        IReadOnlyList<IncidentAlertEvidence> systemHistory,
        IReadOnlyList<IncidentAlertEvidence> context,
        int removedSystemHistory,
        int removedContext)
    {
        var details = snapshot.TruncationDetails
            .Where(detail => !detail.EndsWith("because the character limit was reached.", StringComparison.Ordinal))
            .ToList();
        if (removedContext > 0)
        {
            details.Add($"Removed {removedContext} 24-hour context alerts because the character limit was reached.");
        }
        if (removedSystemHistory > 0)
        {
            details.Add($"Removed {removedSystemHistory} system history alerts because the character limit was reached.");
        }
        return snapshot with
        {
            SystemHistory = systemHistory.ToArray(),
            Context24Hours = context.ToArray(),
            Lifecycles = BuildLifecycles(snapshot.PrimaryEvidence, systemHistory, context),
            RuleEvaluations = snapshot.RuleEvaluations
                .Where(decision => snapshot.PrimaryEvidence.Any(evidence => evidence.EventId == decision.AlertEventId) ||
                    systemHistory.Any(evidence => evidence.EventId == decision.AlertEventId) ||
                    context.Any(evidence => evidence.EventId == decision.AlertEventId))
                .ToArray(),
            TruncationDetails = details.Order(StringComparer.Ordinal).ToArray()
        };
    }

    private static AlertRecord[] OrderAlerts(IEnumerable<AlertRecord> alerts) => alerts
        .OrderByDescending(alert => alert.ReceivedAt)
        .ThenBy(alert => alert.AlertId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(alert => alert.MonitorCondition, StringComparer.OrdinalIgnoreCase)
        .ThenBy(alert => alert.Id)
        .ToArray();

    private static bool IsRelevantContext(
        AlertRecord candidate,
        IReadOnlyList<AlertRecord> primary,
        IReadOnlyDictionary<string, AlertGraphRecord> graphLookup)
    {
        graphLookup.TryGetValue(GraphKey(candidate), out var candidateGraph);
        return primary.Any(item =>
        {
            graphLookup.TryGetValue(GraphKey(item), out var primaryGraph);
            return candidate.TargetName.Equals(item.TargetName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) ||
                SameNonEmpty(candidateGraph?.Site, primaryGraph?.Site) ||
                SameNonEmpty(candidateGraph?.Domain, primaryGraph?.Domain) ||
                SameNonEmpty(candidateGraph?.Role, primaryGraph?.Role);
        });
    }

    private static bool SameNonEmpty(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string DescribeGraphSelection(IncidentAnalysisScope scope, AlertGraphLayer rootLayer) => scope.Type switch
    {
        IncidentAnalysisScopeType.GraphSubtree => $"{scope.Layer}: {Normalize(scope.Value)}",
        IncidentAnalysisScopeType.Target => $"Target: {Normalize(scope.Value)}",
        _ => $"Root {rootLayer} values referenced by primary evidence"
    };

    private static string TargetKey(AlertRecord alert, IReadOnlyDictionary<string, AlertGraphRecord> graphLookup)
    {
        graphLookup.TryGetValue(GraphKey(alert), out var graph);
        return TargetKey(alert.SubscriptionId, string.IsNullOrWhiteSpace(graph?.Target) ? alert.TargetName : graph.Target);
    }

    private static string TargetKey(string subscriptionId, string target) =>
        $"{subscriptionId.Trim()}\u001f{target.Trim()}";

    private static string GraphKey(AlertRecord alert) =>
        $"{alert.AlertId}\u001f{alert.MonitorCondition}\u001f{alert.ReceivedAt:O}";

    private static string GraphKey(AlertGraphRecord alert) =>
        $"{alert.AlertId}\u001f{alert.MonitorCondition}\u001f{alert.ReceivedAt:O}";

    private static string LayerValue(AlertGraphRecord record, AlertGraphLayer layer) => layer switch
    {
        AlertGraphLayer.Subscription => record.SubscriptionId,
        AlertGraphLayer.AlertName => record.AlertName,
        AlertGraphLayer.ResourceGroup => record.ResourceGroup,
        AlertGraphLayer.Target => record.Target,
        AlertGraphLayer.Site => record.Site,
        AlertGraphLayer.Domain => record.Domain,
        AlertGraphLayer.Role => record.Role,
        _ => string.Empty
    };

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static readonly IncidentRuleDecision NoRuleDecision = new(
        IncidentRuleDecisionType.None,
        IncidentAssessmentOrigin.Telemetry,
        null,
        string.Empty,
        string.Empty,
        true,
        false,
        "Uncategorized",
        false);
}