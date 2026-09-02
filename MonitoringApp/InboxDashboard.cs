using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public enum InboxLayoutMode
{
    Operations,
    Balanced,
    Focus
}

public enum InboxWidgetKind
{
    Trends,
    Filters,
    Navigation,
    Heatmap,
    Results
}

public sealed class InboxDashboardDocument
{
    public int Version { get; set; } = 3;
    public string Name { get; set; } = "Alert operations workspace";
    public InboxLayoutMode Layout { get; set; } = InboxLayoutMode.Operations;
    public int Rows { get; set; } = 15;
    public int RowHeight { get; set; } = 88;
    public bool ShowGrid { get; set; } = true;
    public List<InboxDashboardWidget> Widgets { get; set; } = [];
}

public sealed class InboxDashboardWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public InboxWidgetKind Kind { get; set; }
    public string Title { get; set; } = "Widget";
    public string Accent { get; set; } = "teal";
    public int Column { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 2;
}

public sealed record InboxLayoutOption(
    InboxLayoutMode Value,
    string Name,
    int Columns,
    int Rows,
    string Description);

public sealed record InboxWidgetTemplate(
    InboxWidgetKind Kind,
    string Name,
    string Description,
    string Icon,
    string Accent,
    int ColumnSpan,
    int RowSpan);

public static class InboxDashboardCatalog
{
    public static IReadOnlyList<InboxLayoutOption> Layouts { get; } =
    [
        new(InboxLayoutMode.Operations, "Operations", 4, 15, "Four-column control room"),
        new(InboxLayoutMode.Balanced, "Balanced", 3, 18, "Three equal content lanes"),
        new(InboxLayoutMode.Focus, "Focus", 2, 22, "Large panels for deep work")
    ];

    public static IReadOnlyList<InboxWidgetTemplate> Widgets { get; } =
    [
        new(InboxWidgetKind.Trends, "Time graphs", "Hourly volume and active critical alerts", "01", "teal", 4, 3),
        new(InboxWidgetKind.Filters, "Alert filters", "View, search, time zone, and time range", "02", "blue", 4, 3),
        new(InboxWidgetKind.Navigation, "Alert navigation", "Subscriptions, resource groups, and targets", "03", "violet", 2, 3),
        new(InboxWidgetKind.Heatmap, "Target output", "Alert density by target and site", "04", "coral", 2, 3),
        new(InboxWidgetKind.Results, "Inbox output", "Sortable alert results and details", "05", "teal", 4, 6)
    ];

    public static InboxLayoutOption GetLayout(InboxLayoutMode layout) =>
        Layouts.First(item => item.Value == layout);

    public static InboxDashboardWidget CreateWidget(InboxWidgetKind kind, int column = 1, int row = 1)
    {
        var template = Widgets.First(item => item.Kind == kind);
        return new InboxDashboardWidget
        {
            Kind = kind,
            Title = template.Name,
            Accent = template.Accent,
            Column = column,
            Row = row,
            ColumnSpan = template.ColumnSpan,
            RowSpan = template.RowSpan
        };
    }

    public static InboxDashboardDocument CreateDefault()
    {
        var document = new InboxDashboardDocument();
        document.Widgets.AddRange(
        [
            CreateWidget(InboxWidgetKind.Trends, 1, 1),
            CreateWidget(InboxWidgetKind.Filters, 1, 4),
            CreateWidget(InboxWidgetKind.Navigation, 1, 7),
            CreateWidget(InboxWidgetKind.Heatmap, 3, 7),
            CreateWidget(InboxWidgetKind.Results, 1, 10)
        ]);
        return document;
    }
}

public sealed class InboxDashboardState
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();

    public InboxDashboardDocument Document { get; private set; } = InboxDashboardCatalog.CreateDefault();
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Load(InboxDashboardDocument document)
    {
        Upgrade(document);
        Document = document;
        undo.Clear();
        redo.Clear();
        Normalize();
    }

    private static void Upgrade(InboxDashboardDocument document)
    {
        if (document.Version < 2)
        {
            var filters = document.Widgets.FirstOrDefault(widget => widget.Kind == InboxWidgetKind.Filters);
            if (filters is not null)
            {
                filters.RowSpan++;
            }
        }

        if (document.Version < 3)
        {
            document.Rows = InboxDashboardCatalog.GetLayout(document.Layout).Rows;
        }

        document.Version = 3;
    }

    public void Reset() => Load(InboxDashboardCatalog.CreateDefault());
    public void ChangeLayout(InboxLayoutMode layout) => Mutate(() => Document.Layout = layout);
    public void UpdateWorkspace(Action<InboxDashboardDocument> update) => Mutate(() => update(Document));
    public void UpdateWidget(string id, Action<InboxDashboardWidget> update) => Mutate(() => update(Find(id)));

    public InboxDashboardWidget AddWidget(InboxWidgetKind kind, int column = 1, int row = 1)
    {
        InboxDashboardWidget? added = null;
        Mutate(() =>
        {
            added = InboxDashboardCatalog.CreateWidget(kind, column, row);
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

    public void Undo()
    {
        if (undo.TryPop(out var previous))
        {
            redo.Push(Serialize(Document));
            Document = Deserialize(previous);
        }
    }

    public void Redo()
    {
        if (redo.TryPop(out var next))
        {
            undo.Push(Serialize(Document));
            Document = Deserialize(next);
        }
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
        var baseLayout = InboxDashboardCatalog.GetLayout(Document.Layout);
        var occupiedArea = Document.Widgets.Sum(widget =>
            Math.Clamp(widget.ColumnSpan, 1, baseLayout.Columns) * Math.Clamp(widget.RowSpan, 1, 8));
        var minimumRows = Math.Max(8, (int)Math.Ceiling(occupiedArea / (double)baseLayout.Columns));
        Document.Rows = Math.Clamp(Document.Rows, minimumRows, 40);
        var layout = baseLayout with { Rows = Document.Rows };
        Document.RowHeight = Math.Clamp(Document.RowHeight, 64, 132);
        var occupied = new HashSet<(int Column, int Row)>();

        foreach (var widget in Document.Widgets)
        {
            widget.Title = string.IsNullOrWhiteSpace(widget.Title)
                ? InboxDashboardCatalog.Widgets.First(item => item.Kind == widget.Kind).Name
                : widget.Title.Trim();
            widget.Accent = widget.Accent is "teal" or "blue" or "violet" or "coral" ? widget.Accent : "teal";
            widget.ColumnSpan = Math.Clamp(widget.ColumnSpan, 1, layout.Columns);
            widget.RowSpan = Math.Clamp(widget.RowSpan, 1, 10);
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

    private static bool Fits(
        InboxDashboardWidget widget,
        HashSet<(int Column, int Row)> occupied,
        InboxLayoutOption layout) =>
        widget.Column + widget.ColumnSpan - 1 <= layout.Columns &&
        widget.Row + widget.RowSpan - 1 <= layout.Rows &&
        Cells(widget).All(cell => !occupied.Contains(cell));

    private static void PlaceInFirstAvailableCell(
        InboxDashboardWidget widget,
        HashSet<(int Column, int Row)> occupied,
        InboxLayoutOption layout)
    {
        for (var row = 1; row <= layout.Rows; row++)
        {
            for (var column = 1; column <= layout.Columns; column++)
            {
                widget.Column = column;
                widget.Row = row;
                if (Fits(widget, occupied, layout))
                {
                    return;
                }
            }
        }

        widget.ColumnSpan = 1;
        widget.RowSpan = 1;
        widget.Column = 1;
        widget.Row = layout.Rows;
    }

    private static IEnumerable<(int Column, int Row)> Cells(InboxDashboardWidget widget)
    {
        for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
        for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
        {
            yield return (column, row);
        }
    }

    private InboxDashboardWidget Find(string id) => Document.Widgets.First(widget => widget.Id == id);
    private static string Serialize(InboxDashboardDocument document) => JsonSerializer.Serialize(document, JsonOptions);
    private static InboxDashboardDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<InboxDashboardDocument>(json, JsonOptions) ?? InboxDashboardCatalog.CreateDefault();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class InboxLayout
{
    public int Id { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public int DocumentVersion { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record InboxLayoutSaveResult(int Revision, DateTimeOffset UpdatedAt);

public sealed class InboxLayoutRepository(IDbContextFactory<AlertDbContext> contextFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<InboxDashboardDocument?> LoadAsync(
        string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ownerKey = NormalizeOwnerKey(ownerKey);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.InboxLayouts.AsNoTracking()
            .Where(item => item.OwnerKey == ownerKey)
            .Select(item => item.DocumentJson)
            .SingleOrDefaultAsync(cancellationToken);

        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InboxDashboardDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<InboxLayoutSaveResult> SaveAsync(
        string ownerKey,
        InboxDashboardDocument document,
        CancellationToken cancellationToken = default)
    {
        ownerKey = NormalizeOwnerKey(ownerKey);
        var now = DateTimeOffset.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.InboxLayouts.SingleOrDefaultAsync(
            item => item.OwnerKey == ownerKey,
            cancellationToken);

        if (entity is null)
        {
            entity = new InboxLayout
            {
                OwnerKey = ownerKey,
                CreatedAt = now,
                Revision = 1
            };
            db.InboxLayouts.Add(entity);
        }
        else
        {
            entity.Revision++;
        }

        entity.Name = string.IsNullOrWhiteSpace(document.Name) ? "Alert operations workspace" : document.Name.Trim();
        entity.DocumentJson = JsonSerializer.Serialize(document, JsonOptions);
        entity.DocumentVersion = document.Version;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new InboxLayoutSaveResult(entity.Revision, entity.UpdatedAt);
    }

    private static string NormalizeOwnerKey(string ownerKey)
    {
        var normalized = string.IsNullOrWhiteSpace(ownerKey) ? "open-access" : ownerKey.Trim();
        if (normalized.Length > 256)
        {
            throw new ArgumentException("The layout owner key cannot exceed 256 characters.", nameof(ownerKey));
        }

        return normalized;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}