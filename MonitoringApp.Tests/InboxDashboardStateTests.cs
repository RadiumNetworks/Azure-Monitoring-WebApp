using MonitoringApp;

namespace MonitoringApp.Tests;

public sealed class InboxDashboardStateTests
{
    [Fact]
    public void DefaultWorkspaceContainsEveryInboxSectionWithoutOverlap()
    {
        var state = new InboxDashboardState();

        Assert.Equal(Enum.GetValues<InboxWidgetKind>().Length, state.Document.Widgets.Count);
        Assert.Equal(
            Enum.GetValues<InboxWidgetKind>().Order(),
            state.Document.Widgets.Select(widget => widget.Kind).Order());
        AssertNoOverlap(state.Document);
        Assert.Equal(3, state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Filters).RowSpan);
    }

    [Fact]
    public void LoadingVersionOneWorkspaceAddsOneFilterRow()
    {
        var document = InboxDashboardCatalog.CreateDefault();
        document.Version = 1;
        var filters = document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Filters);
        filters.RowSpan = 2;
        var state = new InboxDashboardState();

        state.Load(document);

        Assert.Equal(3, state.Document.Version);
        Assert.Equal(3, state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Filters).RowSpan);
        AssertNoOverlap(state.Document);
    }

    [Theory]
    [InlineData(4, 15)]
    [InlineData(24, 24)]
    [InlineData(50, 40)]
    public void RowCountIsClampedToSupportedRange(int requestedRows, int expectedRows)
    {
        var state = new InboxDashboardState();

        state.UpdateWorkspace(document => document.Rows = requestedRows);

        Assert.Equal(expectedRows, state.Document.Rows);
        AssertNoOverlap(state.Document);
    }

    [Fact]
    public void MovingWidgetKeepsRequestedWidgetAtDropTargetAndReflowsCollisions()
    {
        var state = new InboxDashboardState();
        var results = state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Results);

        state.MoveWidget(results.Id, 1, 1);

        Assert.Equal(1, results.Column);
        Assert.Equal(1, results.Row);
        AssertNoOverlap(state.Document);
    }

    [Fact]
    public void LayoutChangeClampsWidgetsToNewGrid()
    {
        var state = new InboxDashboardState();

        state.ChangeLayout(InboxLayoutMode.Focus);

        var layout = InboxDashboardCatalog.GetLayout(InboxLayoutMode.Focus);
        Assert.All(state.Document.Widgets, widget =>
        {
            Assert.InRange(widget.Column, 1, layout.Columns);
            Assert.InRange(widget.Column + widget.ColumnSpan - 1, 1, layout.Columns);
            Assert.InRange(widget.Row + widget.RowSpan - 1, 1, layout.Rows);
        });
        AssertNoOverlap(state.Document);
    }

    [Fact]
    public void UndoAndRedoRestoreWidgetChanges()
    {
        var state = new InboxDashboardState();
        var filters = state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Filters);
        var originalTitle = filters.Title;

        state.UpdateWidget(filters.Id, widget => widget.Title = "My filters");
        Assert.Equal("My filters", state.Document.Widgets.Single(widget => widget.Id == filters.Id).Title);

        state.Undo();
        Assert.Equal(originalTitle, state.Document.Widgets.Single(widget => widget.Id == filters.Id).Title);

        state.Redo();
        Assert.Equal("My filters", state.Document.Widgets.Single(widget => widget.Id == filters.Id).Title);
    }

    [Fact]
    public void RemovedSectionCanBeAddedAgain()
    {
        var state = new InboxDashboardState();
        var heatmap = state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Heatmap);

        state.RemoveWidget(heatmap.Id);
        var restored = state.AddWidget(InboxWidgetKind.Heatmap);

        Assert.Equal(InboxWidgetKind.Heatmap, restored.Kind);
        Assert.Single(state.Document.Widgets, widget => widget.Kind == InboxWidgetKind.Heatmap);
        AssertNoOverlap(state.Document);
    }

    private static void AssertNoOverlap(InboxDashboardDocument document)
    {
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var widget in document.Widgets)
        {
            for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
            for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
            {
                Assert.True(occupied.Add((column, row)), $"Cell {column}/{row} is occupied more than once.");
            }
        }
    }
}
