namespace MonitoringApp;

internal static class AnalysisQueryWriteRules
{
    public static void AssignCategory(AnalysisQueryDefinition query, string category, string actor)
    {
        query.Category = AnalysisQueryCategories.Normalize(category);
        UpdateAudit(query, actor);
    }

    public static void UpdateAudit(AnalysisQueryDefinition query, string actor, DateTimeOffset? updatedAt = null)
    {
        query.UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        query.UpdatedBy = actor.Trim();
    }
}
