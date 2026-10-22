namespace MonitoringApp;

public interface IDashboardGridWidget
{
    string Id { get; set; }
    int Column { get; set; }
    int Row { get; set; }
    int ColumnSpan { get; set; }
    int RowSpan { get; set; }
}

public static class DashboardGridEngine
{
    public static int Normalize<TWidget>(
        IReadOnlyList<TWidget> widgets,
        int columns,
        int requestedRows,
        int minimumRows = 8,
        int maximumRows = 40,
        bool allowRowGrowth = false)
        where TWidget : IDashboardGridWidget
    {
        var occupiedArea = widgets.Sum(widget =>
            Math.Clamp(widget.ColumnSpan, 1, columns) * Math.Clamp(widget.RowSpan, 1, 10));
        var requiredRows = Math.Max(minimumRows, (int)Math.Ceiling(occupiedArea / (double)columns));
        var rows = Math.Clamp(requestedRows, requiredRows, maximumRows);
        var occupied = new HashSet<(int Column, int Row)>();

        foreach (var widget in widgets)
        {
            widget.ColumnSpan = Math.Clamp(widget.ColumnSpan, 1, columns);
            widget.RowSpan = Math.Clamp(widget.RowSpan, 1, 10);
            widget.Column = Math.Clamp(widget.Column, 1, columns - widget.ColumnSpan + 1);
            widget.Row = Math.Clamp(widget.Row, 1, rows - widget.RowSpan + 1);
            if (!Fits(widget, occupied, columns, rows))
            {
                while (!PlaceInFirstAvailableCell(widget, occupied, columns, rows))
                {
                    if (allowRowGrowth && rows < maximumRows)
                    {
                        rows++;
                        continue;
                    }
                    widget.ColumnSpan = 1;
                    widget.RowSpan = 1;
                    widget.Column = 1;
                    widget.Row = rows;
                    break;
                }
            }
            foreach (var cell in Cells(widget))
                occupied.Add(cell);
        }

        return rows;
    }

    private static bool Fits<TWidget>(
        TWidget widget,
        HashSet<(int Column, int Row)> occupied,
        int columns,
        int rows)
        where TWidget : IDashboardGridWidget =>
        widget.Column + widget.ColumnSpan - 1 <= columns &&
        widget.Row + widget.RowSpan - 1 <= rows &&
        Cells(widget).All(cell => !occupied.Contains(cell));

    private static bool PlaceInFirstAvailableCell<TWidget>(
        TWidget widget,
        HashSet<(int Column, int Row)> occupied,
        int columns,
        int rows)
        where TWidget : IDashboardGridWidget
    {
        for (var row = 1; row <= rows; row++)
        for (var column = 1; column <= columns; column++)
        {
            widget.Column = column;
            widget.Row = row;
            if (Fits(widget, occupied, columns, rows))
                return true;
        }

        return false;
    }

    private static IEnumerable<(int Column, int Row)> Cells<TWidget>(TWidget widget)
        where TWidget : IDashboardGridWidget
    {
        for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
        for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
            yield return (column, row);
    }
}
