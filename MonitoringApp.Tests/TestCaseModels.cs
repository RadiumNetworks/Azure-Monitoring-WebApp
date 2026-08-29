using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class CorrelatedAlertTestCases
{
    public IReadOnlyList<CorrelatedAlertPresentationCase> Alerts { get; init; } = [];
}

public sealed class CorrelatedAlertPresentationCase
{
    public string AlertId { get; init; } = string.Empty;
    public string TargetName { get; init; } = string.Empty;
    public string SiteName { get; init; } = string.Empty;
    public bool CollapseRows { get; init; }
    public IReadOnlyList<string> Summary { get; init; } = [];
    public IReadOnlyList<string> RowTitles { get; init; } = [];
    public IReadOnlyList<string> Metadata { get; init; } = [];
    public IReadOnlyList<string> Alerts { get; init; } = [];
    public IReadOnlyList<string> Facts { get; init; } = [];
}

public sealed class CommonAlertFixture
{
    public JsonObject Data { get; init; } = [];
    public JsonObject QueryResult { get; init; } = [];
}

public sealed class AlertQueryTestCases
{
    public AlertRecordDefaults Defaults { get; init; } = new();
    public AlertSetCase ActiveQuery { get; init; } = new();
    public AlertSetCase EventQuery { get; init; } = new();
    public QueryItemCase QueryItem { get; init; } = new();
    public IReadOnlyList<IdentityCase> Identities { get; init; } = [];
    public SearchQueryCase SearchQuery { get; init; } = new();
}

public sealed class AlertRecordDefaults
{
    public string Name { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string SignalType { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class AlertSetCase
{
    public DateTimeOffset Since { get; init; }
    public IReadOnlyList<AlertEventCase> Alerts { get; init; } = [];
    public IReadOnlyList<string> ExpectedAlertIds { get; init; } = [];
}

public sealed class AlertEventCase
{
    public string AlertId { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
    public string Comments { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
}

public sealed class QueryItemCase
{
    public JsonObject Payload { get; init; } = [];
    public string ExpectedTarget { get; init; } = string.Empty;
    public string? ExpectedSearchResultLink { get; init; }
}

public sealed class IdentityCase
{
    public string Name { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = [];
    public string ExpectedTarget { get; init; } = string.Empty;
    public string ExpectedSite { get; init; } = string.Empty;
    public string ExpectedDisplayName { get; init; } = string.Empty;
}

public sealed class SearchQueryCase
{
    public JsonObject Payload { get; init; } = [];
    public string Expected { get; init; } = string.Empty;
}

public sealed class AlertPromptTestCases
{
    public DateTimeOffset GeneratedAt { get; init; }
    public PromptAlertCase DefaultAlert { get; init; } = new();
    public IReadOnlyList<string> DefaultContains { get; init; } = [];
    public IReadOnlyList<string> DefaultExcludes { get; init; } = [];
    public AlertAgentPromptOptions ExcludedOptions { get; init; } = new();
    public IReadOnlyList<string> ExcludedContent { get; init; } = [];
    public IReadOnlyList<string> DiagnosticAlertIds { get; init; } = [];
    public IReadOnlyList<string> DiagnosticContains { get; init; } = [];
}

public sealed class PromptAlertCase
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string AlertId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string SignalType { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public DateTimeOffset FiredAt { get; init; }
    public string Description { get; init; } = string.Empty;
    public string SearchResultsUrl { get; init; } = string.Empty;
    public string Comments { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = [];
}

public sealed class AlertTimeZoneTestCases
{
    public IReadOnlyList<TimeZoneConversionCase> Conversions { get; init; } = [];
    public WallClockConversionCase WallClockConversion { get; init; } = new();
    public WallClockFormatCase WallClockFormat { get; init; } = new();
}

public sealed class TimeZoneConversionCase
{
    public DateTimeOffset Timestamp { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public DateTime Expected { get; init; }
}

public sealed class WallClockConversionCase
{
    public DateTime Value { get; init; }
    public string SourceZone { get; init; } = string.Empty;
    public string DestinationZone { get; init; } = string.Empty;
    public DateTime Expected { get; init; }
}

public sealed class WallClockFormatCase
{
    public DateTime Value { get; init; }
    public string ExpectedText { get; init; } = string.Empty;
    public IReadOnlyList<string> ParseValues { get; init; } = [];
}

public sealed class AuthorizationTestCases
{
    public AlertIngestionAuthenticationOptions Options { get; init; } = new();
    public IReadOnlyList<PrincipalAuthorizationCase> Principals { get; init; } = [];
    public IReadOnlyList<string> MissingConfigurationErrorPrefixes { get; init; } = [];
}

public sealed class PrincipalAuthorizationCase
{
    public string Name { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string IdentityType { get; init; } = string.Empty;
    public string? Scope { get; init; }
    public bool ExpectedAuthorized { get; init; }
}

public sealed class AlertLifecycleTestCases
{
    public AlertRecordDefaults Defaults { get; init; } = new();
    public IReadOnlyList<LifecycleActiveCase> ActiveCases { get; init; } = [];
    public LifecycleNavigationCase Navigation { get; init; } = new();
    public LifecycleRecentNavigationCase RecentNavigation { get; init; } = new();
}

public sealed class LifecycleActiveCase
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<AlertEventCase> Alerts { get; init; } = [];
    public IReadOnlyList<string> ExpectedKeys { get; init; } = [];
}

public sealed class LifecycleNavigationCase
{
    public IReadOnlyList<AlertEventCase> Alerts { get; init; } = [];
    public int ExpectedActiveCount { get; init; }
    public IReadOnlyList<NavigationCountCase> Counts { get; init; } = [];
}

public sealed class NavigationCountCase
{
    public string SubscriptionId { get; init; } = string.Empty;
    public string? ResourceGroup { get; init; }
    public string? Target { get; init; }
    public int Expected { get; init; }
}

public sealed class LifecycleRecentNavigationCase
{
    public DateTimeOffset Cutoff { get; init; }
    public IReadOnlyList<AlertEventCase> Alerts { get; init; } = [];
    public IReadOnlyList<string> ExpectedSubscriptions { get; init; } = [];
    public IReadOnlyList<string> ExcludedSubscriptions { get; init; } = [];
    public IReadOnlyList<NavigationBranchCase> Branches { get; init; } = [];
}

public sealed class NavigationBranchCase
{
    public string SubscriptionId { get; init; } = string.Empty;
    public int Count { get; init; }
    public int HistoryCount { get; init; }
    public int ResourceGroupHistoryCount { get; init; }
    public int TargetHistoryCount { get; init; }
}

public sealed class AlertGraphHierarchyTestCases
{
    public DateTimeOffset BaseTime { get; init; }
    public IReadOnlyList<GraphAlertCase> GroupingAlerts { get; init; } = [];
    public IReadOnlyList<GraphGroupingCase> Groupings { get; init; } = [];
    public IReadOnlyList<GraphAlertCase> SiteAlerts { get; init; } = [];
    public GraphGroupingCase SiteGrouping { get; init; } = new();
    public IReadOnlyList<GraphChoiceCase> Choices { get; init; } = [];
}

public sealed class GraphAlertCase
{
    public string AlertId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public string? DimensionName { get; init; }
    public string? DimensionValue { get; init; }
}

public sealed class GraphGroupingCase
{
    public IReadOnlyList<AlertGraphLayer> Layers { get; init; } = [];
    public IReadOnlyList<string> ExpectedMiddleNames { get; init; } = [];
    public int ExpectedRootCount { get; init; }
    public int ExpectedLeafCount { get; init; }
}

public sealed class GraphChoiceCase
{
    public int Level { get; init; }
    public IReadOnlyList<AlertGraphLayer> ExpectedValues { get; init; } = [];
    public IReadOnlyList<string> ExpectedLabels { get; init; } = [];
}

public sealed class AlertGraphLayoutTestCases
{
    public IReadOnlyList<GraphLayoutCase> Cases { get; init; } = [];
}

public sealed class GraphLayoutCase
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<GraphHierarchyNodeCase> Roots { get; init; } = [];
    public int ExpectedNodes { get; init; }
    public int ExpectedEdges { get; init; }
    public int ExpectedSubscriptions { get; init; }
    public int ExpectedMiddleNodes { get; init; }
    public int ExpectedTargets { get; init; }
    public string ExpectedLabel { get; init; } = string.Empty;
    public int ExpectedLabelCount { get; init; }
    public int ExpectedLabelHistoryCount { get; init; }
    public int MinimumDistinctX { get; init; }
    public int MinimumDistinctY { get; init; }
    public bool AssertNoOverlap { get; init; }
    public string SharedTarget { get; init; } = string.Empty;
    public int SharedTargetCount { get; init; }
    public int SharedTargetHistoryCount { get; init; }
    public int SharedTargetIncomingEdges { get; init; }
    public bool AssertMiddlePositionsDistinct { get; init; }
}

public sealed class GraphHierarchyNodeCase
{
    public string Name { get; init; } = string.Empty;
    public AlertGraphLayer Layer { get; init; }
    public int Count { get; init; }
    public int HistoryCount { get; init; }
    public IReadOnlyList<GraphHierarchyNodeCase> Children { get; init; } = [];
}

public sealed class AlertRuleEvaluatorTestCases
{
    public IReadOnlyList<AlertRuleEvaluatorCase> Cases { get; init; } = [];
}

public sealed class AlertRuleEvaluatorCase
{
    public string Name { get; init; } = string.Empty;
    public AlertRule Rule { get; init; } = new();
    public IReadOnlyList<AlertRuleAlertCase> Alerts { get; init; } = [];
    public IReadOnlyList<ExpectedAlertCategoryCase> ExpectedCategories { get; init; } = [];
    public IReadOnlyList<string> ExpectedUncategorizedAlertIds { get; init; } = [];
}

public sealed class AlertRuleAlertCase
{
    public string AlertId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Site { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = [];
}

public sealed class ExpectedAlertCategoryCase
{
    public string Name { get; init; } = string.Empty;
    public bool Collapsed { get; init; }
    public IReadOnlyList<string> AlertIds { get; init; } = [];
}