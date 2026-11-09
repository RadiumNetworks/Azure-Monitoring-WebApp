namespace MonitoringApp;

public sealed record InboxCategoryGroup(
    string Name,
    string Tone,
    bool Collapsed,
    IReadOnlyList<AlertRecord> Alerts,
    bool IsOutage = false,
    bool IsSuppressed = false);

public sealed record InboxDisplayItem(
    InboxCategoryGroup? Category,
    AlertRecord? Alert,
    bool IsUncategorizedHeader);

public static class InboxCategories
{
    public static IReadOnlyList<InboxCategoryGroup> Build(
        IReadOnlyList<AlertCategoryGroup> categories,
        IReadOnlyList<AlertRecord> orderedAlerts)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(orderedAlerts);

        return categories
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var alertIds = group.SelectMany(category => category.Alerts)
                    .Select(alert => alert.Id)
                    .ToHashSet();
                return new InboxCategoryGroup(
                    first.Name,
                    first.Tone,
                    first.Collapsed,
                    orderedAlerts.Where(alert => alertIds.Contains(alert.Id)).ToArray(),
                    group.Any(category => category.AnalysisClassification == AlertRuleAnalysisClassifications.SystemOutage),
                    group.All(category => category.AnalysisClassification == AlertRuleAnalysisClassifications.Suppressed));
            })
            .OrderBy(group => group.IsOutage ? 0 : group.IsSuppressed ? 2 : 1)
            .ToArray();
    }

    public static IReadOnlyList<InboxDisplayItem> BuildItems(
        AlertCategorizationResult categorization,
        IReadOnlyList<AlertRecord> orderedAlerts,
        InboxCategoryState state)
    {
        ArgumentNullException.ThrowIfNull(categorization);
        ArgumentNullException.ThrowIfNull(state);
        var groups = Build(categorization.Categories, orderedAlerts);
        var items = new List<InboxDisplayItem>();

        foreach (var group in groups.Where(group => !group.IsSuppressed))
        {
            AddGroup(group);
        }
        if (groups.Count > 0 && categorization.Uncategorized.Count > 0)
        {
            items.Add(new InboxDisplayItem(null, null, true));
        }
        items.AddRange(categorization.Uncategorized.Select(alert => new InboxDisplayItem(null, alert, false)));
        foreach (var group in groups.Where(group => group.IsSuppressed))
        {
            AddGroup(group);
        }
        return items;

        void AddGroup(InboxCategoryGroup group)
        {
            items.Add(new InboxDisplayItem(group, null, false));
            if (!state.IsCollapsed(group))
            {
                items.AddRange(group.Alerts.Select(alert => new InboxDisplayItem(null, alert, false)));
            }
        }
    }
}

public sealed class InboxCategoryState
{
    private readonly Dictionary<string, bool> collapsed = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCollapsed(InboxCategoryGroup category) =>
        collapsed.TryGetValue(category.Name, out var value) ? value : category.Collapsed;

    public void Toggle(InboxCategoryGroup category) =>
        collapsed[category.Name] = !IsCollapsed(category);
}
