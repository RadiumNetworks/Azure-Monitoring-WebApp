using System.Text.Json;
using UIDynamic.Models;

namespace UIDynamic.Services;

public sealed class DashboardDesignerState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    public DashboardDocument Document { get; private set; } = DashboardCatalog.CreateDefault();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Load(DashboardDocument document)
    {
        Document = document;
        _undo.Clear();
        _redo.Clear();
        Normalize();
    }

    public void Reset() => Load(DashboardCatalog.CreateDefault());
    public void ChangeLayout(DashboardLayout layout) => Mutate(() => Document.Layout = layout);

    public DashboardWidget AddWidget(WidgetKind kind, int column, int row)
    {
        DashboardWidget? added = null;
        Mutate(() =>
        {
            added = DashboardCatalog.CreateWidget(kind, column, row);
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

    public void UpdateWidget(string id, Action<DashboardWidget> update) => Mutate(() => update(Find(id)));
    public void RemoveWidget(string id) => Mutate(() => Document.Widgets.Remove(Find(id)));
    public void UpdateWorkspace(Action<DashboardDocument> update) => Mutate(() => update(Document));

    public void Undo()
    {
        if (_undo.TryPop(out var previous))
        {
            _redo.Push(Serialize(Document));
            Document = Deserialize(previous);
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var next))
        {
            _undo.Push(Serialize(Document));
            Document = Deserialize(next);
        }
    }

    private void Mutate(Action mutation)
    {
        _undo.Push(Serialize(Document));
        _redo.Clear();
        mutation();
        Normalize();
    }

    private void Normalize()
    {
        var layout = DashboardCatalog.GetLayout(Document.Layout);
        Document.PaletteWidth = Math.Clamp(Document.PaletteWidth, 210, 360);
        Document.InspectorWidth = Math.Clamp(Document.InspectorWidth, 240, 380);
        Document.RowHeight = Math.Clamp(Document.RowHeight, 72, 132);

        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var widget in Document.Widgets)
        {
            if (string.IsNullOrWhiteSpace(widget.DataSourceKey))
            {
                widget.DataSourceKey = DashboardCatalog.GetDefaultDataSourceKey(widget.Kind);
            }

            widget.ColumnSpan = Math.Clamp(widget.ColumnSpan, 1, layout.Columns);
            widget.RowSpan = Math.Clamp(widget.RowSpan, 1, 5);
            widget.Column = Math.Clamp(widget.Column, 1, layout.Columns - widget.ColumnSpan + 1);
            widget.Row = Math.Clamp(widget.Row, 1, layout.Rows - widget.RowSpan + 1);

            if (!Fits(widget, occupied, layout))
            {
                PlaceInFirstAvailableCell(widget, occupied, layout);
            }

            foreach (var cell in Cells(widget))
            {
                occupied.Add(cell);
            }
        }
    }

    private static bool Fits(DashboardWidget widget, HashSet<(int Column, int Row)> occupied, LayoutOption layout) =>
        widget.Column + widget.ColumnSpan - 1 <= layout.Columns &&
        widget.Row + widget.RowSpan - 1 <= layout.Rows &&
        Cells(widget).All(cell => !occupied.Contains(cell));

    private static void PlaceInFirstAvailableCell(DashboardWidget widget, HashSet<(int Column, int Row)> occupied, LayoutOption layout)
    {
        for (var row = 1; row <= layout.Rows; row++)
        {
            for (var column = 1; column <= layout.Columns; column++)
            {
                widget.Column = column;
                widget.Row = row;
                if (Fits(widget, occupied, layout)) return;
            }
        }

        widget.ColumnSpan = 1;
        widget.RowSpan = 1;
        widget.Column = 1;
        widget.Row = layout.Rows;
    }

    private static IEnumerable<(int Column, int Row)> Cells(DashboardWidget widget)
    {
        for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
        for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
            yield return (column, row);
    }

    private DashboardWidget Find(string id) => Document.Widgets.First(widget => widget.Id == id);
    private static string Serialize(DashboardDocument document) => JsonSerializer.Serialize(document, JsonOptions);
    private static DashboardDocument Deserialize(string json) => JsonSerializer.Deserialize<DashboardDocument>(json, JsonOptions) ?? DashboardCatalog.CreateDefault();
}