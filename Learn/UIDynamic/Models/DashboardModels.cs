namespace UIDynamic.Models;

public enum DashboardLayout { Operations, Balanced, Focus }

public enum WidgetKind { Metric, Trend, Alerts, Notes, Health }

public sealed class DashboardDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Operations overview";
    public DashboardLayout Layout { get; set; } = DashboardLayout.Operations;
    public int PaletteWidth { get; set; } = 248;
    public int InspectorWidth { get; set; } = 286;
    public int RowHeight { get; set; } = 92;
    public bool ShowGrid { get; set; } = true;
    public List<DashboardWidget> Widgets { get; set; } = [];
}

public sealed class DashboardWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetKind Kind { get; set; }
    public string Title { get; set; } = "Widget";
    public string DataSourceKey { get; set; } = string.Empty;
    public string Accent { get; set; } = "teal";
    public int Column { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 2;
}

public sealed record LayoutOption(DashboardLayout Value, string Name, int Columns, int Rows, string Description);

public sealed record WidgetTemplate(WidgetKind Kind, string Name, string Description, string Icon, int DefaultColumnSpan, int DefaultRowSpan);

public static class DashboardCatalog
{
    public static IReadOnlyList<LayoutOption> Layouts { get; } =
    [
        new(DashboardLayout.Operations, "Operations", 4, 8, "Four-column control room"),
        new(DashboardLayout.Balanced, "Balanced", 3, 9, "Three equal content lanes"),
        new(DashboardLayout.Focus, "Focus", 2, 10, "Large panels for deep work")
    ];

    public static IReadOnlyList<WidgetTemplate> Widgets { get; } =
    [
        new(WidgetKind.Metric, "Metric", "A prominent value and context", "01", 1, 2),
        new(WidgetKind.Trend, "Trend", "A native SVG time series", "02", 2, 3),
        new(WidgetKind.Alerts, "Alert queue", "Prioritized operational events", "03", 2, 3),
        new(WidgetKind.Notes, "Team notes", "Editable runbook context", "04", 1, 3),
        new(WidgetKind.Health, "Service health", "Compact component status", "05", 1, 3)
    ];

    public static LayoutOption GetLayout(DashboardLayout layout) => Layouts.First(item => item.Value == layout);

    public static DashboardWidget CreateWidget(WidgetKind kind, int column = 1, int row = 1)
    {
        var template = Widgets.First(item => item.Kind == kind);
        return new DashboardWidget
        {
            Kind = kind,
            Title = template.Name,
            DataSourceKey = GetDefaultDataSourceKey(kind),
            Column = column,
            Row = row,
            ColumnSpan = template.DefaultColumnSpan,
            RowSpan = template.DefaultRowSpan,
            Accent = kind switch
            {
                WidgetKind.Alerts => "coral",
                WidgetKind.Notes => "violet",
                WidgetKind.Health => "blue",
                _ => "teal"
            }
        };
    }

    public static string GetDefaultDataSourceKey(WidgetKind kind) => kind switch
    {
        WidgetKind.Metric => "metric:availability",
        WidgetKind.Trend => "trend:requests",
        WidgetKind.Alerts => "alerts:operations",
        WidgetKind.Notes => "notes:operations-team",
        WidgetKind.Health => "health:platform",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static DashboardDocument CreateDefault()
    {
        var document = new DashboardDocument();
        document.Widgets.AddRange(
        [
            CreateWidget(WidgetKind.Metric, 1, 1),
            CreateWidget(WidgetKind.Trend, 2, 1),
            CreateWidget(WidgetKind.Health, 4, 1),
            CreateWidget(WidgetKind.Alerts, 1, 4),
            CreateWidget(WidgetKind.Notes, 3, 4)
        ]);
        return document;
    }
}