using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public sealed record AnalysisDashboardViewFilters
{
    public string WorkspaceKey { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Site { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
}

public sealed record AnalysisDashboardSetup
{
    public required string Format { get; init; }
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required int RowHeight { get; init; }
    public int RefreshSeconds { get; init; } = 60;
    public bool RefreshPaused { get; init; }
    public bool AutoRefreshEnabled { get; init; }
    public int? TimeRangeMinutesOverride { get; init; }
    public int? BinMinutesOverride { get; init; }
    public AnalysisDashboardPanelLayout SystemsPanel { get; init; } = new();
    public AnalysisDashboardPanelLayout QueryLibraryPanel { get; init; } = new();
    public AnalysisDashboardViewFilters Filters { get; init; } = new();
    public AnalysisDashboardSelectionMode SelectionMode { get; init; }
    public List<AnalysisDashboardSystem> Systems { get; init; } = [];
    public List<string> InventoryRoles { get; init; } = [];
    public required List<AnalysisDashboardSetupWidget> Widgets { get; init; }
}

public sealed record AnalysisDashboardSetupWidget
{
    public required string QueryKey { get; init; }
    public required string Title { get; init; }
    public string Accent { get; init; } = "teal";
    public int TimeRangeMinutes { get; init; } = 1440;
    public int? BinMinutes { get; init; }
    public string GroupBy { get; init; } = "SourceDSA";
    public required int Column { get; init; }
    public required int Row { get; init; }
    public required int ColumnSpan { get; init; }
    public required int RowSpan { get; init; }
}

public sealed record AnalysisDashboardSetupPreview(
    IReadOnlyList<string> UnavailableQueries,
    IReadOnlyList<AnalysisDashboardSystem> MissingSystems,
    IReadOnlyList<string> EmptyRoles,
    bool WorkspaceUnavailable)
{
    public bool CanImport => !WorkspaceUnavailable && UnavailableQueries.Count == 0;
    public bool HasInventoryWarnings => MissingSystems.Count > 0 || EmptyRoles.Count > 0;
}

public enum AnalysisDashboardSetupError
{
    Required, TooLarge, InvalidJson, UnsupportedVersion, InvalidSettings,
    QueryUnavailable, DependenciesUnavailable
}

public sealed class AnalysisDashboardSetupException(AnalysisDashboardSetupError code, Exception? innerException = null)
    : Exception($"Dashboard setup: {code}.", innerException)
{
    public AnalysisDashboardSetupError Code { get; } = code;
}

public static class AnalysisDashboardSetupJson
{
    public const string Format = "alert-console.dashboard-setup";
    public const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static string Export(AnalysisDashboardDocument document, AnalysisDashboardViewFilters filters,
        IReadOnlyList<AnalysisQueryDefinition> queries)
    {
        var widgets = document.Widgets.Select(widget =>
        {
            var query = queries.SingleOrDefault(query => query.Id == widget.QueryDefinitionId)
                ?? throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.QueryUnavailable);
            return new AnalysisDashboardSetupWidget
            {
                QueryKey = query.Key, Title = widget.Title, Accent = widget.Accent,
                TimeRangeMinutes = widget.TimeRangeMinutes, BinMinutes = widget.BinMinutes, GroupBy = widget.GroupBy,
                Column = widget.Column, Row = widget.Row, ColumnSpan = widget.ColumnSpan, RowSpan = widget.RowSpan
            };
        }).ToList();
        var setup = new AnalysisDashboardSetup
        {
            Format = Format, Version = 1, Name = document.Name,
            Columns = document.Columns, Rows = document.Rows, RowHeight = document.RowHeight,
            RefreshSeconds = document.RefreshSeconds, RefreshPaused = document.RefreshPaused,
            AutoRefreshEnabled = document.AutoRefreshEnabled,
            TimeRangeMinutesOverride = document.TimeRangeMinutesOverride, BinMinutesOverride = document.BinMinutesOverride,
            SystemsPanel = document.SystemsPanel, QueryLibraryPanel = document.QueryLibraryPanel,
            Filters = filters, SelectionMode = document.SelectionMode, Systems = document.Systems,
            InventoryRoles = document.InventoryRoles, Widgets = widgets
        };
        Validate(setup);
        var json = JsonSerializer.Serialize(setup, JsonOptions);
        RequireSize(json);
        return json;
    }

    public static AnalysisDashboardSetup Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.Required);
        RequireSize(json);
        AnalysisDashboardSetup setup;
        try
        {
            using var parsed = JsonDocument.Parse(json, new() { MaxDepth = JsonOptions.MaxDepth });
            RequireUniqueProperties(parsed.RootElement);
            setup = JsonSerializer.Deserialize<AnalysisDashboardSetup>(json, JsonOptions)
                ?? throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidJson);
        }
        catch (JsonException exception)
        {
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidJson, exception);
        }
        Validate(setup);
        return setup;
    }

    public static AnalysisDashboardSetupPreview Preview(AnalysisDashboardSetup setup, string? workspaceKey,
        IReadOnlyList<AnalysisQueryDefinition> queries, IReadOnlyList<ComputerInventoryEntry> inventory,
        TargetWorkspaceResolver resolver)
    {
        Validate(setup);
        var workspaceUnavailable = workspaceKey is null ||
            workspaceKey.Length > 0 && !resolver.IsConfiguredWorkspace(workspaceKey);
        var missing = setup.SelectionMode == AnalysisDashboardSelectionMode.FixedSystems
            ? AnalysisDashboardInventorySelection.Resolve(setup.Systems, inventory).MissingSystems : [];
        IReadOnlyList<string> emptyRoles = setup.SelectionMode == AnalysisDashboardSelectionMode.InventoryRoles
            ? setup.InventoryRoles.Where(role => !inventory.Any(system => string.Equals(
                InventoryRoleRuleMatcher.NormalizeRole(system.Role?.Trim()),
                InventoryRoleRuleMatcher.NormalizeRole(role.Trim()), StringComparison.OrdinalIgnoreCase))).ToArray() : [];
        var requiredWorkspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!workspaceUnavailable)
        {
            var selection = AnalysisDashboardInventorySelection.Resolve(
                SelectionDocument(setup), inventory, resolver, workspaceKey);
            if (setup.SelectionMode == AnalysisDashboardSelectionMode.InventoryRoles)
            {
                var selected = selection.Systems.Select(system => SubscriptionComputerKey.Create(system.SubscriptionId, system.Computer))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                emptyRoles = setup.InventoryRoles.Where(role => !inventory.Any(system =>
                    selected.Contains(SubscriptionComputerKey.Create(system.SubscriptionId, system.Computer)) &&
                    string.Equals(InventoryRoleRuleMatcher.NormalizeRole(system.Role?.Trim()),
                        InventoryRoleRuleMatcher.NormalizeRole(role.Trim()), StringComparison.OrdinalIgnoreCase))).ToArray();
            }
            if (workspaceKey!.Length > 0)
                requiredWorkspaces.Add(workspaceKey);
            foreach (var system in selection.Systems)
            {
                var entry = inventory.First(item => SubscriptionComputerKey.Create(item.SubscriptionId, item.Computer)
                    .Equals(SubscriptionComputerKey.Create(system.SubscriptionId, system.Computer), StringComparison.OrdinalIgnoreCase));
                requiredWorkspaces.Add(resolver.Resolve(entry.SubscriptionId, entry.Domain).Workspace.Key);
            }
        }
        var unavailable = setup.Widgets.Select(widget => widget.QueryKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key =>
            {
                var query = queries.SingleOrDefault(query => query.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                return query is null || !query.Enabled || query.PublishedVersionId is null ||
                    !query.Workspaces.Any(assignment => assignment.Enabled && resolver.IsConfiguredWorkspace(assignment.WorkspaceKey)) ||
                    requiredWorkspaces.Any(workspace => !query.Workspaces.Any(assignment => assignment.Enabled &&
                        assignment.WorkspaceKey.Equals(workspace, StringComparison.OrdinalIgnoreCase)));
            }).ToArray();
        return new(unavailable, missing, emptyRoles, workspaceUnavailable);
    }

    public static AnalysisDashboardDocument CreateDocument(AnalysisDashboardSetup setup, string? workspaceKey,
        IReadOnlyList<AnalysisQueryDefinition> queries, IReadOnlyList<ComputerInventoryEntry> inventory,
        TargetWorkspaceResolver resolver, bool acknowledgeInventoryWarnings)
    {
        var preview = Preview(setup, workspaceKey, queries, inventory, resolver);
        if (!preview.CanImport || preview.HasInventoryWarnings && !acknowledgeInventoryWarnings)
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.DependenciesUnavailable);
        var document = SelectionDocument(setup);
        document.Name = setup.Name.Trim();
        document.Columns = setup.Columns;
        document.Rows = setup.Rows;
        document.RowHeight = setup.RowHeight;
        document.RefreshSeconds = setup.RefreshSeconds;
        document.RefreshPaused = setup.RefreshPaused;
        document.AutoRefreshEnabled = setup.AutoRefreshEnabled;
        document.TimeRangeMinutesOverride = setup.TimeRangeMinutesOverride;
        document.BinMinutesOverride = setup.BinMinutesOverride;
        document.SystemsPanel = new() { Width = setup.SystemsPanel.Width, Height = setup.SystemsPanel.Height };
        document.QueryLibraryPanel = new() { Width = setup.QueryLibraryPanel.Width, Height = setup.QueryLibraryPanel.Height };
        document.ViewFilters = setup.Filters with { WorkspaceKey = workspaceKey! };
        document.Widgets = setup.Widgets.Select(widget => new AnalysisDashboardWidget
        {
            QueryDefinitionId = queries.Single(query => query.Key.Equals(widget.QueryKey, StringComparison.OrdinalIgnoreCase)).Id,
            Title = widget.Title, Accent = widget.Accent, TimeRangeMinutes = widget.TimeRangeMinutes,
            BinMinutes = widget.BinMinutes, GroupBy = widget.GroupBy, Column = widget.Column,
            Row = widget.Row, ColumnSpan = widget.ColumnSpan, RowSpan = widget.RowSpan
        }).ToList();
        return document;
    }

    private static AnalysisDashboardDocument SelectionDocument(AnalysisDashboardSetup setup) => new()
    {
        SelectionMode = setup.SelectionMode,
        Systems = setup.Systems.Select(system => new AnalysisDashboardSystem
            { SubscriptionId = system.SubscriptionId, Computer = system.Computer }).ToList(),
        InventoryRoles = setup.InventoryRoles.ToList()
    };

    private static void Validate(AnalysisDashboardSetup setup)
    {
        if (setup.Format != Format || setup.Version != 1)
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.UnsupportedVersion);
        if (!TextValid(setup.Name, 256) || setup.Columns is < 2 or > 6 || setup.Rows is < 8 or > 120 ||
            setup.RowHeight is < 64 or > 132 || setup.RefreshSeconds is not (30 or 60 or 120 or 300 or 600 or 900) ||
            !PanelValid(setup.SystemsPanel) || !PanelValid(setup.QueryLibraryPanel) ||
            setup.Filters is null || !OptionalTextValid(setup.Filters.WorkspaceKey, 256) ||
            !OptionalTextValid(setup.Filters.Domain, 256) || !OptionalTextValid(setup.Filters.Site, 256) ||
            !OptionalTextValid(setup.Filters.Role, 256) ||
            setup.Systems is null || setup.Systems.Count > AnalysisDashboardDocument.MaximumSystems ||
            setup.Systems.Any(system => system is null || !Guid.TryParse(system.SubscriptionId, out _) ||
                !TextValid(system.Computer, 256)) ||
            setup.Systems.DistinctBy(system => SubscriptionComputerKey.Create(system.SubscriptionId, system.Computer),
                StringComparer.OrdinalIgnoreCase).Count() != setup.Systems.Count ||
            setup.InventoryRoles is null || setup.InventoryRoles.Count > 100 ||
            setup.InventoryRoles.Any(role => !TextValid(role, 256)) ||
            setup.Widgets is null || setup.Widgets.Count > 12)
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidSettings);
        try
        {
            SelectionDocument(setup).ValidateSelection();
            AnalysisDashboardDocument.ValidateExecutionOverrides(setup.TimeRangeMinutesOverride, setup.BinMinutesOverride);
        }
        catch (AnalysisValidationException exception)
        {
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidSettings, exception);
        }
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var widget in setup.Widgets)
        {
            if (widget is null || !TextValid(widget.QueryKey, 128) ||
                !TextValid(widget.Title, 256) || widget.Accent is not ("teal" or "blue" or "violet" or "coral") ||
                widget.TimeRangeMinutes is < 5 or > 43200 || widget.BinMinutes is < 1 or > 1440 ||
                widget.GroupBy is null || !AnalysisQuerySelections.Groupings.ContainsKey(widget.GroupBy) ||
                widget.ColumnSpan < 1 || widget.ColumnSpan > setup.Columns || widget.RowSpan is < 1 or > 10 ||
                widget.Column < 1 || widget.Column > setup.Columns - widget.ColumnSpan + 1 ||
                widget.Row < 1 || widget.Row > setup.Rows - widget.RowSpan + 1)
                throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidSettings);
            for (var column = widget.Column; column < widget.Column + widget.ColumnSpan; column++)
            for (var row = widget.Row; row < widget.Row + widget.RowSpan; row++)
                if (!occupied.Add((column, row)))
                    throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidSettings);
        }
    }

    private static bool TextValid(string? text, int maximum) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
    private static bool OptionalTextValid(string? text, int maximum) =>
        text is not null && text.Length <= maximum && !text.Any(char.IsControl);
    private static bool PanelValid(AnalysisDashboardPanelLayout? panel) => panel is not null &&
        (panel.Width == 0 || panel.Width is >= 280 and <= 2400) && panel.Height is >= 160 and <= 900;

    private static void RequireSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.TooLarge);
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new AnalysisDashboardSetupException(AnalysisDashboardSetupError.InvalidJson);
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                RequireUniqueProperties(item);
    }
}
