using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardTests
{
    [Fact]
    public void LegacyJsonDefaultsToFixedSelectionAndModeChangesAreUndoableWithoutFlatteningRoles()
    {
        var collection = AnalysisDashboardCollection.Deserialize(
            """{"systems":[{"subscriptionId":"sub","computer":"FIXED"}]}""");
        var state = new AnalysisDashboardState();
        state.Load(collection.OpenActive());
        Assert.Equal(AnalysisDashboardSelectionMode.FixedSystems, state.Document.SelectionMode);
        Assert.Empty(state.Document.InventoryRoles);

        state.SetSystemSelection(AnalysisDashboardSelectionMode.InventoryRoles,
            [" domaincontrollers ", "DOMAIN CONTROLLER", " File Server "]);
        Assert.Equal(["Domain Controller", "File Server"], state.Document.InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(state.Document.Systems).Computer);
        state.Undo();
        Assert.Equal(AnalysisDashboardSelectionMode.FixedSystems, state.Document.SelectionMode);
        Assert.Empty(state.Document.InventoryRoles);
        state.Redo();
        Assert.Equal(AnalysisDashboardSelectionMode.InventoryRoles, state.Document.SelectionMode);
        state.SetSystemSelection(AnalysisDashboardSelectionMode.FixedSystems, state.Document.InventoryRoles);
        Assert.Equal(["Domain Controller", "File Server"], state.Document.InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(state.Document.Systems).Computer);
    }

    [Theory]
    [InlineData("""{"selectionMode":"Unknown"}""")]
    [InlineData("""{"selectionMode":99}""")]
    [InlineData("""{"selectionMode":null}""")]
    [InlineData("""{"selectionMode":"InventoryRoles","inventoryRoles":[]}""")]
    [InlineData("""{"selectionMode":"InventoryRoles","inventoryRoles":[" "]}""")]
    [InlineData("""{"inventoryRoles":null}""")]
    [InlineData("""{"inventoryRoles":[null]}""")]
    [InlineData("""{"systems":null}""")]
    [InlineData("""{"systems":[null]}""")]
    public void InvalidSavedSelectionsAreExplicitErrors(string json)
    {
        var exception = Record.Exception(() => AnalysisDashboardCollection.Deserialize(json));
        Assert.True(exception is System.Text.Json.JsonException or AnalysisValidationException, exception?.ToString());
    }

    [Fact]
    public void InvalidSelectionChangesAndLoadsPreserveCurrentStateAndUndoHistory()
    {
        var state = new AnalysisDashboardState();
        Assert.Equal(AnalysisValidationCode.DashboardRolesRequired,
            Assert.Throws<AnalysisValidationException>(() =>
                state.SetSystemSelection(AnalysisDashboardSelectionMode.InventoryRoles, [])).Code);
        Assert.False(state.CanUndo);
        state.SetSystemSelection(AnalysisDashboardSelectionMode.InventoryRoles, ["Domain Controller"]);
        state.Undo();
        var original = state.Document;
        Assert.True(state.CanRedo);
        Assert.Throws<AnalysisValidationException>(() => state.SetSystemSelection((AnalysisDashboardSelectionMode)99, ["Other"]));
        Assert.Throws<AnalysisValidationException>(() => state.Load(new() { SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles }));
        Assert.Same(original, state.Document);
        Assert.True(state.CanRedo);
        Assert.False(state.CanUndo);
        state.Redo();
        Assert.Equal(["Domain Controller"], state.Document.InventoryRoles);
    }

    [Fact]
    public void ExistingViewsMustNotUseInactiveFixedSystemsBeforeRoleUiIsImplemented()
    {
        var document = new AnalysisDashboardDocument
        {
            SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles,
            InventoryRoles = ["Domain Controller"],
            Systems = [new() { SubscriptionId = "sub", Computer = "INACTIVE-FIXED" }]
        };
        Assert.Equal(AnalysisValidationCode.DashboardSelectionNotAvailable,
            Assert.Throws<AnalysisValidationException>(() => document.ValidateSelection(allowInventoryRoles: false)).Code);
        document.ValidateSelection();
    }

    [Theory]
    [InlineData(11, 11)]
    [InlineData(100, 100)]
    [InlineData(105, 100)]
    public void PreservesUpToOneHundredSelectedSystemsAcrossPersistence(int requested, int expected)
    {
        var state = new AnalysisDashboardState();
        state.UpdateWorkspace(document => document.Systems = Enumerable.Range(1, requested)
            .Select(index => new AnalysisDashboardSystem { SubscriptionId = "subscription", Computer = $"DC{index}" }).ToList());
        var restored = new AnalysisDashboardState();
        restored.Load(System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!);
        Assert.Equal(expected, restored.Document.Systems.Count);
        Assert.Equal(expected, restored.Document.Systems.DistinctBy(system => system.Computer).Count());
    }

    [Fact]
    public void PersonalLibraryRemovalPersistsIsUndoableAndLeavesWidgetsAndOtherUsersAlone()
    {
        var state = new AnalysisDashboardState();
        var otherUser = new AnalysisDashboardState();
        var queryId = Guid.NewGuid();
        state.AddWidget(queryId, "CPU", 60);
        state.HideQuery(queryId);
        Assert.Contains(queryId, state.Document.HiddenQueryIds);
        Assert.Empty(otherUser.Document.HiddenQueryIds);
        Assert.Equal(queryId, Assert.Single(state.Document.Widgets).QueryDefinitionId);
        state.Undo();
        Assert.Empty(state.Document.HiddenQueryIds);
        state.Redo();
        Assert.Contains(queryId, state.Document.HiddenQueryIds);
        state.HideQuery(queryId);
        Assert.Single(state.Document.HiddenQueryIds);
        var restored = new AnalysisDashboardState();
        restored.Load(System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!);
        Assert.Contains(queryId, restored.Document.HiddenQueryIds);
        restored.RestoreQuery(queryId);
        Assert.Empty(restored.Document.HiddenQueryIds);
        Assert.Single(restored.Document.Widgets);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>("{}")!.HiddenQueryIds);
    }

    [Fact]
    public void ResizesEntireGridAndPreservesWidgetsAcrossUndoRedoAndSerialization()
    {
        var state = new AnalysisDashboardState();
        state.AddWidget(Guid.NewGuid(), "CPU", 60, 3, 8);
        state.AddWidget(Guid.NewGuid(), "RAM", 60, 1, 1);
        state.ResizeGrid(2, 8, 120);

        Assert.Equal(2, state.Document.Columns);
        Assert.Equal(8, state.Document.Rows);
        Assert.Equal(120, state.Document.RowHeight);
        Assert.Equal(2, state.Document.Widgets.Count);
        AssertNoOverlap(state.Document);
        Assert.All(state.Document.Widgets, widget =>
        {
            Assert.InRange(widget.Column + widget.ColumnSpan - 1, 1, 2);
            Assert.InRange(widget.Row + widget.RowSpan - 1, 1, state.Document.Rows);
        });
        state.Undo();
        Assert.Equal(4, state.Document.Columns);
        Assert.Equal(88, state.Document.RowHeight);
        state.Redo();
        Assert.Equal(2, state.Document.Columns);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!;
        Assert.Equal(120, restored.RowHeight);
        Assert.Equal(8, restored.Rows);
        Assert.Equal(2, restored.Columns);
    }

    [Fact]
    public void GridRowsExpandForExistingWidgetsAndDimensionsStayWithinLimits()
    {
        var state = new AnalysisDashboardState();
        for (var index = 0; index < 12; index++)
            state.AddWidget(Guid.NewGuid(), $"Query {index}", 60);
        state.ResizeGrid(2, 8, 1);
        Assert.Equal(96, state.Document.Widgets.Sum(widget => widget.ColumnSpan * widget.RowSpan));
        Assert.True(state.Document.Rows >= 48);
        Assert.Equal(64, state.Document.RowHeight);
        AssertNoOverlap(state.Document);
        state.ResizeGrid(100, 500, 500);
        Assert.Equal(6, state.Document.Columns);
        Assert.Equal(120, state.Document.Rows);
        Assert.Equal(132, state.Document.RowHeight);
        AssertNoOverlap(state.Document);
    }

    [Fact]
    public void AutoRefreshIsOptInAndPersistsWithThirtySecondInterval()
    {
        var document = System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>("{}")!;
        Assert.False(document.AutoRefreshEnabled);
        var state = new AnalysisDashboardState();
        state.Load(document);
        state.UpdateWorkspace(item =>
        {
            item.AutoRefreshEnabled = true;
            item.RefreshSeconds = 30;
        });
        var restored = System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!;
        Assert.True(restored.AutoRefreshEnabled);
        Assert.Equal(30, restored.RefreshSeconds);
        state.Undo();
        Assert.False(state.Document.AutoRefreshEnabled);
        state.Redo();
        Assert.True(state.Document.AutoRefreshEnabled);
        state.UpdateWorkspace(item => item.AutoRefreshEnabled = false);
        Assert.False(state.Document.AutoRefreshEnabled);
    }

    [Fact]
    public void NormalizesSystemsRefreshAndWidgetsToDashboardLimits()
    {
        var document = new AnalysisDashboardDocument
        {
            RefreshSeconds = 15,
            Systems = Enumerable.Range(1, 14).Select(index => new AnalysisDashboardSystem
            {
                SubscriptionId = Guid.NewGuid().ToString(),
                Computer = $"SERVER{index:00}"
            }).ToList(),
            Widgets = Enumerable.Range(1, 15).Select(index => new AnalysisDashboardWidget
            {
                QueryDefinitionId = Guid.NewGuid(),
                Title = $"Widget {index}",
                Column = 1,
                Row = 1
            }).ToList()
        };
        var state = new AnalysisDashboardState();

        state.Load(document);

        Assert.Equal(60, state.Document.RefreshSeconds);
        Assert.Equal(14, state.Document.Systems.Count);
        Assert.Equal(12, state.Document.Widgets.Count);
        AssertNoOverlap(state.Document);
    }

    [Fact]
    public void SupportsMoveResizeUndoAndRedo()
    {
        var state = new AnalysisDashboardState();
        var widget = state.AddWidget(Guid.NewGuid(), "CPU", 60);

        state.ResizeWidget(widget.Id, 4, 6);
        Assert.Equal(4, state.Document.Widgets.Single().ColumnSpan);
        state.Undo();
        Assert.Equal(2, state.Document.Widgets.Single().ColumnSpan);
        state.Redo();
        Assert.Equal(4, state.Document.Widgets.Single().ColumnSpan);

        state.MoveWidget(widget.Id, 1, 2);
        Assert.Equal(2, state.Document.Widgets.Single().Row);
    }

    [Fact]
    public void SharedGridEnginePreservesInboxLayoutBehavior()
    {
        var state = new InboxDashboardState();

        state.MoveWidget(
            state.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Results).Id,
            1,
            1);

        var occupied = new HashSet<(int Column, int Row)>();
        Assert.All(state.Document.Widgets, widget =>
        {
            for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
            for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
                Assert.True(occupied.Add((column, row)));
        });
    }

    [Fact]
    public void ConfiguresDashboardPersistenceModel()
    {
        using var context = new AlertDbContext(
            new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
                .Options);
        var dashboard = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(AnalysisDashboard)));
        var template = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(AnalysisDashboardTemplate)));

        Assert.Contains(dashboard.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(AnalysisDashboard.OwnerKey)]));
        Assert.True(dashboard.FindProperty(nameof(AnalysisDashboard.RowVersion))?.IsConcurrencyToken);
        Assert.Contains(template.GetIndexes(), index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AnalysisDashboardTemplate.Name), nameof(AnalysisDashboardTemplate.Version)]));
    }

    [Fact]
    public void PlacesDroppedQueriesAtRequestedCellsAndMovesWithoutOverlap()
    {
        var state = new AnalysisDashboardState();
        var first = state.AddWidget(Guid.NewGuid(), "CPU", 60, 3, 4);
        var second = state.AddWidget(Guid.NewGuid(), "RAM", 60, 3, 4);

        Assert.Equal(3, second.Column);
        Assert.Equal(4, second.Row);
        AssertNoOverlap(state.Document);
        state.MoveWidget(first.Id, 2, 8);
        Assert.Equal(2, first.Column);
        Assert.Equal(8, first.Row);
        AssertNoOverlap(state.Document);
        state.Undo();
        AssertNoOverlap(state.Document);
        state.Redo();
        Assert.Equal(2, state.Document.Widgets.Single(widget => widget.Id == first.Id).Column);
        Assert.Equal(8, state.Document.Widgets.Single(widget => widget.Id == first.Id).Row);

        var restored = new AnalysisDashboardState();
        restored.Load(System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!);
        Assert.Equal(2, restored.Document.Widgets.Single(widget => widget.Id == first.Id).Column);
        Assert.Equal(8, restored.Document.Widgets.Single(widget => widget.Id == first.Id).Row);
        AssertNoOverlap(restored.Document);
    }

    [Fact]
    public void PreservesPanelSizesAcrossSerializationAndUndoRedo()
    {
        var state = new AnalysisDashboardState();
        state.UpdateWorkspace(document =>
        {
            document.SystemsPanel.Width = 640;
            document.SystemsPanel.Height = 320;
            document.QueryLibraryPanel.Width = 480;
            document.QueryLibraryPanel.Height = 240;
        });
        var restored = new AnalysisDashboardState();
        restored.Load(System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>(
            System.Text.Json.JsonSerializer.Serialize(state.Document))!);

        Assert.Equal(640, restored.Document.SystemsPanel.Width);
        Assert.Equal(320, restored.Document.SystemsPanel.Height);
        Assert.Equal(480, restored.Document.QueryLibraryPanel.Width);
        Assert.Equal(240, restored.Document.QueryLibraryPanel.Height);
        state.Undo();
        Assert.Equal(0, state.Document.SystemsPanel.Width);
        state.Redo();
        Assert.Equal(640, state.Document.SystemsPanel.Width);
    }

    [Fact]
    public void OlderDashboardDocumentsUseDefaultPanelSizesAndNormalizeInvalidDimensions()
    {
        var document = System.Text.Json.JsonSerializer.Deserialize<AnalysisDashboardDocument>("{}")!;
        Assert.Equal(0, document.SystemsPanel.Width);
        Assert.Equal(280, document.SystemsPanel.Height);
        var state = new AnalysisDashboardState();
        state.Load(document);
        state.UpdateWorkspace(item =>
        {
            item.SystemsPanel.Width = 20;
            item.SystemsPanel.Height = 10000;
            item.QueryLibraryPanel.Width = -5;
            item.QueryLibraryPanel.Height = 1;
        });

        Assert.Equal(280, state.Document.SystemsPanel.Width);
        Assert.Equal(900, state.Document.SystemsPanel.Height);
        Assert.Equal(0, state.Document.QueryLibraryPanel.Width);
        Assert.Equal(160, state.Document.QueryLibraryPanel.Height);
    }

    private static void AssertNoOverlap(AnalysisDashboardDocument document)
    {
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var widget in document.Widgets)
        {
            for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
            for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
                Assert.True(occupied.Add((column, row)), $"Cell {column}/{row} is occupied more than once.");
        }
    }
}
