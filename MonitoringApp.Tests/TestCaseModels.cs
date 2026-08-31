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
    public IReadOnlyList<GraphRecordGroupingCase> RecordGroupings { get; init; } = [];
}

public sealed class GraphRecordGroupingCase
{
    public string Name { get; init; } = string.Empty;
    public GraphRecordCase Alert { get; init; } = new();
    public IReadOnlyList<AlertGraphLayer> Layers { get; init; } = [];
    public IReadOnlyList<string> ExpectedNames { get; init; } = [];
    public int ExpectedRootCount { get; init; }
    public int ExpectedRootHistoryCount { get; init; }
}

public sealed class GraphRecordCase
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string AlertId { get; init; } = string.Empty;
    public string MonitorCondition { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string AlertName { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Site { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
}

public sealed class AlertGraphFilterTestCases
{
    public IReadOnlyList<GraphRecordCase> Alerts { get; init; } = [];
    public IReadOnlyList<AlertGraphFilterCase> Cases { get; init; } = [];
}

public sealed class AlertGraphFilterCase
{
    public string Name { get; init; } = string.Empty;
    public string? Filter { get; init; }
    public IReadOnlyList<string> ExpectedAlertIds { get; init; } = [];
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

public sealed class AlertRecordFixtureCase
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string AlertId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SignalType { get; init; } = string.Empty;
    public string MonitorCondition { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public DateTimeOffset? FiredAt { get; init; }
    public string Description { get; init; } = string.Empty;
    public string SearchResultsUrl { get; init; } = string.Empty;
    public string Comments { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = [];
}

public sealed class CriticalAlertLogbookTestCases
{
    public AlertRecordFixtureCase Alert { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<CriticalAlertLogbookCase> Cases { get; init; } = [];
}

public sealed class CriticalAlertLogbookCase
{
    public string Name { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public bool IsCritical { get; init; }
    public bool ExpectEntry { get; init; }
    public string ExpectedPrefix { get; init; } = string.Empty;
    public IReadOnlyList<string> ExpectedContains { get; init; } = [];
}

public sealed class AlertCommentLogbookTestCases
{
    public AlertRecordFixtureCase Alert { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<AlertCommentLogbookCase> Cases { get; init; } = [];
}

public sealed class AlertCommentLogbookCase
{
    public string Name { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public bool ExpectEntry { get; init; }
    public string ExpectedUser { get; init; } = string.Empty;
    public IReadOnlyList<string> ExpectedContains { get; init; } = [];
    public string ExpectedError { get; init; } = string.Empty;
}

public sealed class InventoryRoleRuleMatcherTestCases
{
    public AlertRecordFixtureCase Alert { get; init; } = new();
    public IReadOnlyList<InventoryRoleRuleMatcherCase> Cases { get; init; } = [];
}

public sealed class InventoryRoleRuleMatcherCase
{
    public string Name { get; init; } = string.Empty;
    public string QueryResultType { get; init; } = string.Empty;
    public IReadOnlyList<AlertRule> Rules { get; init; } = [];
    public string? ExpectedRole { get; init; }
}

public sealed class AuthenticationTestCases
{
    public IReadOnlyList<AuthenticationTypeCase> SupportedTypes { get; init; } = [];
    public IReadOnlyList<string?> UnsupportedTypes { get; init; } = [];
    public IReadOnlyList<string> MalformedHashes { get; init; } = [];
    public IReadOnlyList<AuthenticationRoleCase> SupportedRoles { get; init; } = [];
    public IReadOnlyList<string> UnsupportedRoles { get; init; } = [];
    public string Password { get; init; } = string.Empty;
    public string WrongPassword { get; init; } = string.Empty;
}

public sealed class AuthenticationTypeCase
{
    public string Type { get; init; } = string.Empty;
    public bool IsOpen { get; init; }
    public bool IsSql { get; init; }
}

public sealed class AuthenticationRoleCase
{
    public string Input { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
}

public sealed class AlertHistoryOptionsTestCases
{
    public DateTimeOffset ReferenceTime { get; init; }
    public DateTimeOffset ExpectedCutoff { get; init; }
    public IReadOnlyList<int> InvalidDays { get; init; } = [];
}

public sealed class AlertSeverityDisplayOptionsTestCases
{
    public AlertSeverityDisplayOptions Valid { get; init; } = new();
    public string ConfiguredSeverity { get; init; } = string.Empty;
    public string UnknownSeverity { get; init; } = string.Empty;
    public string ExpectedConfiguredClass { get; init; } = string.Empty;
    public string ExpectedDefaultClass { get; init; } = string.Empty;
    public AlertSeverityDisplayOptions Unsupported { get; init; } = new();
    public IReadOnlyList<string> UnsupportedErrors { get; init; } = [];
    public AlertSeverityDisplayOptions Duplicates { get; init; } = new();
    public string DuplicateError { get; init; } = string.Empty;
}

public sealed class AlertGraphOptionsTestCases
{
    public AlertGraphOptions Valid { get; init; } = new();
    public AlertGraphOptions MissingDefault { get; init; } = new();
    public string MissingDefaultError { get; init; } = string.Empty;
}

public sealed class DatabaseSettingsTestCases
{
    public JsonObject Authentication { get; init; } = [];
    public JsonObject AlertHistory { get; init; } = [];
    public int ExpectedHistoryDays { get; init; }
    public JsonObject AlertGraph { get; init; } = [];
    public AlertGraphLayer ExpectedLayer1 { get; init; }
    public AlertGraphLayer ExpectedLayer2 { get; init; }
    public AlertGraphLayer ExpectedLayer3 { get; init; }
    public JsonObject SeverityDisplay { get; init; } = [];
    public string Severity { get; init; } = string.Empty;
    public string ExpectedSeverityClass { get; init; } = string.Empty;
    public string MalformedJson { get; init; } = string.Empty;
}

public sealed class ParsedAlertRecordTestCases
{
    public AlertRecordFixtureCase Alert { get; init; } = new();
    public string ExpectedOriginalAlertId { get; init; } = string.Empty;
    public string ExpectedInventoryComputer { get; init; } = string.Empty;
    public string ExpectedInventorySubscriptionId { get; init; } = string.Empty;
    public string ExpectedQueryResultType { get; init; } = string.Empty;
}

public sealed class ParsedAlertLifecycleTestCases
{
    public AlertRecordFixtureCase Alert { get; init; } = new();
    public DateTimeOffset FiredAt { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }
    public JsonObject FiredPayload { get; init; } = [];
    public AlertRule CriticalRule { get; init; } = new();
    public string CriticalAlertId { get; init; } = string.Empty;
    public string StandardAlertId { get; init; } = string.Empty;
}

public sealed class CriticalAlertTimelineTestCases
{
    public IReadOnlyList<CriticalAlertTimelineCase> Cases { get; init; } = [];
    public int InvalidHours { get; init; }
}

public sealed class CriticalAlertTimelineCase
{
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset FirstHour { get; init; }
    public DateTimeOffset Now { get; init; }
    public int Hours { get; init; }
    public IReadOnlyList<CriticalAlertLifecycleCase> Lifecycles { get; init; } = [];
    public IReadOnlyList<int> ExpectedCounts { get; init; } = [];
}

public sealed class CriticalAlertLifecycleCase
{
    public string AlertId { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}