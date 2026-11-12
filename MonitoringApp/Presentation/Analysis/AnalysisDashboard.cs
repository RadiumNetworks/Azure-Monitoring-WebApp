using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisDashboardSelectionMode>))]
public enum AnalysisDashboardSelectionMode
{
    FixedSystems,
    InventoryRoles
}

public sealed class AnalysisDashboardDocument
{
    public const int MaximumSystems = 100;

    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Personal analysis dashboard";
    public Guid? SourceTemplateId { get; set; }
    public int? SourceTemplateVersion { get; set; }
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 12;
    public int RowHeight { get; set; } = 88;
    public int RefreshSeconds { get; set; } = 60;
    public bool RefreshPaused { get; set; }
    public bool AutoRefreshEnabled { get; set; }
    public int? TimeRangeMinutesOverride { get; set; }
    public int? BinMinutesOverride { get; set; }
    public AnalysisDashboardPanelLayout SystemsPanel { get; set; } = new();
    public AnalysisDashboardPanelLayout QueryLibraryPanel { get; set; } = new();
    public List<AnalysisDashboardSystem> Systems { get; set; } = [];
    public AnalysisDashboardSelectionMode SelectionMode { get; set; }
    public List<string> InventoryRoles { get; set; } = [];
    public List<AnalysisDashboardWidget> Widgets { get; set; } = [];
    public List<Guid> HiddenQueryIds { get; set; } = [];

    public void ValidateSelection(bool allowInventoryRoles = true)
    {
        if (!Enum.IsDefined(SelectionMode) || Systems is null || Systems.Any(system => system is null) ||
            InventoryRoles is null || InventoryRoles.Any(string.IsNullOrWhiteSpace))
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardSelectionInvalid,
                "The dashboard system selection is invalid.");
        if (SelectionMode == AnalysisDashboardSelectionMode.InventoryRoles && InventoryRoles.Count == 0)
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardRolesRequired,
                "Select at least one inventory role.");
        if (!allowInventoryRoles && SelectionMode == AnalysisDashboardSelectionMode.InventoryRoles)
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardSelectionNotAvailable,
                "Inventory-role selection is not supported by this dashboard view yet.");
    }

    public AnalysisQueryRequest CreateExecutionRequest(
        AnalysisDashboardWidget widget, IReadOnlyList<AnalysisQuerySystem> systems)
    {
        ValidateSelection();
        ValidateExecutionOverrides(TimeRangeMinutesOverride, BinMinutesOverride);
        return new(widget.QueryDefinitionId, systems,
            TimeRangeMinutesOverride ?? widget.TimeRangeMinutes,
            BinMinutesOverride ?? widget.BinMinutes, widget.GroupBy);
    }

    public static void ValidateExecutionOverrides(int? timeRangeMinutes, int? binMinutes)
    {
        if (timeRangeMinutes.HasValue && !AnalysisQuerySelections.TimeRanges.Contains(timeRangeMinutes.Value))
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardTimeRangeInvalid,
                "Select a supported dashboard time range or use widget settings.");
        if (binMinutes.HasValue && !AnalysisQuerySelections.BinSizes.Contains(binMinutes.Value))
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardIntervalInvalid,
                "Select a dashboard averaging interval of 15 min, 30 min, or 1h, or use widget settings.");
    }
}

public sealed class AnalysisDashboardPanelLayout
{
    public int Width { get; set; }
    public int Height { get; set; } = 280;

    public void Normalize()
    {
        Width = Width <= 0 ? 0 : Math.Clamp(Width, 280, 2400);
        Height = Math.Clamp(Height, 160, 900);
    }
}

public sealed class AnalysisDashboardSystem
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Computer { get; set; } = string.Empty;
}

public sealed class AnalysisDashboardWidget : IDashboardGridWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid QueryDefinitionId { get; set; }
    public string Title { get; set; } = "Analysis widget";
    public string Accent { get; set; } = "teal";
    public int TimeRangeMinutes { get; set; } = 1440;
    public int? BinMinutes { get; set; }
    public string GroupBy { get; set; } = "SourceDSA";
    public int Column { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int ColumnSpan { get; set; } = 2;
    public int RowSpan { get; set; } = 4;
}

public sealed class AnalysisDashboardState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();

    public AnalysisDashboardDocument Document { get; private set; } = new();
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Load(AnalysisDashboardDocument document)
    {
        AnalysisDashboardDocument.ValidateExecutionOverrides(document.TimeRangeMinutesOverride, document.BinMinutesOverride);
        document.ValidateSelection();
        Document = document;
        undo.Clear();
        redo.Clear();
        Normalize();
    }

    public void Reset() => Load(new AnalysisDashboardDocument());
    public void UpdateWorkspace(Action<AnalysisDashboardDocument> update) => Mutate(() => update(Document));
    public void UpdateWidget(string id, Action<AnalysisDashboardWidget> update) => Mutate(() => update(Find(id)));

    public void SetExecutionOverrides(int? timeRangeMinutes, int? binMinutes)
    {
        AnalysisDashboardDocument.ValidateExecutionOverrides(timeRangeMinutes, binMinutes);
        UpdateWorkspace(document =>
        {
            document.TimeRangeMinutesOverride = timeRangeMinutes;
            document.BinMinutesOverride = binMinutes;
        });
    }

    public void SetSystemSelection(AnalysisDashboardSelectionMode mode, IEnumerable<string> inventoryRoles)
    {
        ArgumentNullException.ThrowIfNull(inventoryRoles);
        var selection = new AnalysisDashboardDocument { SelectionMode = mode, InventoryRoles = inventoryRoles.ToList() };
        selection.ValidateSelection();
        UpdateWorkspace(document =>
        {
            document.SelectionMode = selection.SelectionMode;
            document.InventoryRoles = selection.InventoryRoles;
        });
    }

    public AnalysisDashboardWidget AddWidget(
        Guid queryDefinitionId, string title, int timeRangeMinutes, int column = 1, int? row = null)
    {
        AnalysisDashboardWidget? added = null;
        Mutate(() =>
        {
            if (Document.Widgets.Count >= 12)
                throw new InvalidOperationException("An analysis dashboard may contain at most 12 widgets.");
            added = new AnalysisDashboardWidget
            {
                QueryDefinitionId = queryDefinitionId,
                Title = title,
                TimeRangeMinutes = timeRangeMinutes,
                Column = column,
                Row = row ?? Document.Rows
            };
            Document.Widgets.Insert(0, added);
        });
        return added!;
    }

    public void MoveWidget(string id, int column, int row) => Mutate(() =>
    {
        var widget = Find(id);
        Document.Widgets.Remove(widget);
        widget.Column = column;
        widget.Row = row;
        Document.Widgets.Insert(0, widget);
    });

    public void ResizeWidget(string id, int columnSpan, int rowSpan) => Mutate(() =>
    {
        var widget = Find(id);
        Document.Widgets.Remove(widget);
        widget.ColumnSpan = columnSpan;
        widget.RowSpan = rowSpan;
        Document.Widgets.Insert(0, widget);
    });

    public void RemoveWidget(string id) => Mutate(() => Document.Widgets.Remove(Find(id)));

    public void HideQuery(Guid queryId) => UpdateWorkspace(document =>
    {
        if (!document.HiddenQueryIds.Contains(queryId))
            document.HiddenQueryIds.Add(queryId);
    });

    public void RestoreQuery(Guid queryId) => UpdateWorkspace(document => document.HiddenQueryIds.Remove(queryId));

    public void ResizeGrid(int columns, int rows, int rowHeight) => UpdateWorkspace(document =>
    {
        document.Columns = columns;
        document.Rows = rows;
        document.RowHeight = rowHeight;
    });

    public void Undo()
    {
        if (!undo.TryPop(out var previous))
            return;
        redo.Push(Serialize(Document));
        Document = Deserialize(previous);
    }

    public void Redo()
    {
        if (!redo.TryPop(out var next))
            return;
        undo.Push(Serialize(Document));
        Document = Deserialize(next);
    }

    private void Mutate(Action mutation)
    {
        undo.Push(Serialize(Document));
        redo.Clear();
        mutation();
        Normalize();
    }

    private void Normalize()
    {
        AnalysisDashboardDocument.ValidateExecutionOverrides(Document.TimeRangeMinutesOverride, Document.BinMinutesOverride);
        Document.ValidateSelection();
        Document.InventoryRoles = Document.InventoryRoles
            .Select(role => InventoryRoleRuleMatcher.NormalizeRole(role.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Document.Version = 1;
        Document.Columns = Math.Clamp(Document.Columns, 2, 6);
        Document.RowHeight = Math.Clamp(Document.RowHeight, 64, 132);
        Document.SystemsPanel.Normalize();
        Document.QueryLibraryPanel.Normalize();
        Document.RefreshSeconds = Document.RefreshSeconds is 30 or 60 or 120 or 300 or 600 or 900
            ? Document.RefreshSeconds
            : 60;
        Document.Systems = Document.Systems
            .Where(system => !string.IsNullOrWhiteSpace(system.SubscriptionId) && !string.IsNullOrWhiteSpace(system.Computer))
            .DistinctBy(system => SubscriptionComputerKey.Create(system.SubscriptionId, system.Computer), StringComparer.OrdinalIgnoreCase)
            .Take(AnalysisDashboardDocument.MaximumSystems)
            .ToList();
        Document.HiddenQueryIds = Document.HiddenQueryIds.Where(id => id != Guid.Empty).Distinct().ToList();
        foreach (var widget in Document.Widgets)
        {
            widget.Title = string.IsNullOrWhiteSpace(widget.Title) ? "Analysis widget" : widget.Title.Trim();
            widget.Accent = widget.Accent is "teal" or "blue" or "violet" or "coral" ? widget.Accent : "teal";
            widget.TimeRangeMinutes = Math.Clamp(widget.TimeRangeMinutes, 5, 43200);
        }
        if (Document.Widgets.Count > 12)
            Document.Widgets = Document.Widgets.Take(12).ToList();
        Document.Rows = DashboardGridEngine.Normalize(
            Document.Widgets,
            Document.Columns,
            Document.Rows,
            maximumRows: 120,
            allowRowGrowth: true);
    }

    private AnalysisDashboardWidget Find(string id) =>
        Document.Widgets.First(widget => widget.Id == id);
    private static string Serialize(AnalysisDashboardDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);
    private static AnalysisDashboardDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<AnalysisDashboardDocument>(json, JsonOptions) ?? new();
}

public sealed class AnalysisDashboard
{
    public int Id { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public int DocumentVersion { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class AnalysisDashboardTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public string MinimumRole { get; set; } = SqlAuthenticationRoles.Operator;
    public bool Published { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

public sealed record AnalysisDashboardSaveResult(int Revision, DateTimeOffset UpdatedAt);
