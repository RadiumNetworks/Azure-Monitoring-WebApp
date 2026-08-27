using System.Text.Json.Nodes;

namespace PlaywrightDemo.Models;

public sealed class ParsingTestCases
{
    public IReadOnlyList<ResultSummaryCase> ResultSummaries { get; init; } = [];
    public IReadOnlyList<ChartSummaryCase> ChartDescriptions { get; init; } = [];
    public IReadOnlyList<NavigationNodeCase> NavigationNodes { get; init; } = [];
    public IReadOnlyList<UtcTooltipCase> UtcTooltips { get; init; } = [];
    public IReadOnlyList<AlertPayloadCase> AlertPayloads { get; init; } = [];
    public IReadOnlyList<InvalidParserCase> InvalidCases { get; init; } = [];
}

public sealed record ResultSummaryCase(string Input, int Shown, int Available);
public sealed record ChartSummaryCase(string Input, int Total, int MaximumPerHour);
public sealed record NavigationNodeCase(string Input, string Name, int Count);
public sealed record UtcTooltipCase(string Input, DateTimeOffset Expected);
public sealed record AlertPayloadCase(JsonObject Payload, string MonitorCondition, string TargetName);
public sealed record InvalidParserCase(string Parser, string Input);

public sealed class AlertConsoleTestCases
{
    public string HistoryView { get; init; } = string.Empty;
    public string RootNodeName { get; init; } = string.Empty;
    public string NoMatchSearch { get; init; } = string.Empty;
    public string EmptyResultText { get; init; } = string.Empty;
    public string AlertSortColumn { get; init; } = string.Empty;
    public string CommentPrefix { get; init; } = string.Empty;
    public DateRangeCase DateRange { get; init; } = new();
    public ViewportCase MobileViewport { get; init; } = new();
    public IReadOnlyList<TimeZoneCase> TimeZones { get; init; } = [];
    public IReadOnlyList<CorrelatedAlertCase> CorrelatedAlerts { get; init; } = [];
    public GraphCase Graph { get; init; } = new();
    public GraphTopologyCase GraphTopology { get; init; } = new();
}

public sealed class DateRangeCase
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

public sealed class ViewportCase
{
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed record TimeZoneCase(string Value, string ReceivedHeader);

public sealed record CorrelatedAlertCase(
    string AlertId,
    string AlertName,
    string TargetName,
    IReadOnlyList<string> ResultContains);

public sealed class GraphCase
{
    public IReadOnlyList<string> Layer1Options { get; init; } = [];
    public IReadOnlyList<string> Layer2Options { get; init; } = [];
    public IReadOnlyList<string> Layer3Options { get; init; } = [];
    public string SelectedLayer2 { get; init; } = string.Empty;
}

public sealed class GraphTopologyCase
{
    public int RunIdLength { get; init; }
    public string RunPrefixTemplate { get; init; } = string.Empty;
    public string SubscriptionTemplate { get; init; } = string.Empty;
    public string ResourceGroupTemplate { get; init; } = string.Empty;
    public string TargetTemplate { get; init; } = string.Empty;
    public string AlertIdTemplate { get; init; } = string.Empty;
    public IReadOnlyList<IReadOnlyList<int>> ResourceGroupTargetCounts { get; init; } = [];
    public int ExpectedSubscriptions { get; init; }
    public int ExpectedResourceGroups { get; init; }
    public int ExpectedTargets { get; init; }
    public int ExpectedActiveNodes { get; init; }
    public JsonObject AlertPayloadTemplate { get; init; } = [];
}