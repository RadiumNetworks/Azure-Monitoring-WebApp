using System.Text.RegularExpressions;

namespace MonitoringApp;

public static partial class AnalysisQueryValidator
{
    [GeneratedRegex(@"(?im)^\s*\.|workspace\s*\(|cluster\s*\(")]
    private static partial Regex UnsafeKqlPattern();

    public static IReadOnlyList<string> ValidateDefinition(AnalysisQueryDefinition query) =>
        DefinitionIssues(query).Select(issue => issue.Message).ToList();

    internal static IReadOnlyList<AnalysisQueryValidationIssue> DefinitionIssues(AnalysisQueryDefinition query)
    {
        var errors = new List<AnalysisQueryValidationIssue>();
        if (string.IsNullOrWhiteSpace(query.Key) || query.Key.Trim().Length > 128)
            errors.Add(new(AnalysisQueryValidationCode.KeyInvalid, "Key is required and may contain at most 128 characters."));
        if (string.IsNullOrWhiteSpace(query.Name) || query.Name.Trim().Length > 256)
            errors.Add(new(AnalysisQueryValidationCode.NameInvalid, "Name is required and may contain at most 256 characters."));
        try { AnalysisQueryCategories.Normalize(query.Category); }
        catch (InvalidOperationException exception) { errors.Add(new(AnalysisQueryValidationCode.CategoryInvalid, exception.Message)); }
        if (query.DefaultTimeRangeMinutes is < 5 or > 43200)
            errors.Add(new(AnalysisQueryValidationCode.DefaultTimeRangeInvalid, "Default time range must be between 5 and 43200 minutes."));
        if (query.MaxTimeRangeMinutes < query.DefaultTimeRangeMinutes || query.MaxTimeRangeMinutes > 43200)
            errors.Add(new(AnalysisQueryValidationCode.MaxTimeRangeInvalid, "Maximum time range must include the default and may not exceed 43200 minutes."));
        if (query.DefaultBinMinutes is < 1 or > 1440)
            errors.Add(new(AnalysisQueryValidationCode.BinInvalid, "Bin size must be between 1 and 1440 minutes."));
        if (!AnalysisQueryVisualizationTypes.All.Contains(query.VisualizationType))
            errors.Add(new(AnalysisQueryValidationCode.VisualizationTypeInvalid, "Select a supported visualization type."));
        if (!AnalysisQueryScopes.All.Contains(query.Scope))
            errors.Add(new(AnalysisQueryValidationCode.ScopeInvalid, "Select a supported query scope (SelectedSystems or Workspace)."));
        if (query.MinimumRole is not (SqlAuthenticationRoles.Operator or SqlAuthenticationRoles.Admin))
            errors.Add(new(AnalysisQueryValidationCode.MinimumRoleInvalid, "Minimum role must be Operator or Admin."));
        try
        {
            AnalysisVisualization.Parse(query.VisualizationJson);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(new(AnalysisQueryValidationCode.VisualizationOptionsInvalid, exception.Message));
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateKql(
        string kql,
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart,
        string scope = AnalysisQueryScopes.SelectedSystems,
        string workspaceTimeParameter = "_TimePeriod") =>
        KqlIssues(kql, visualizationType, scope, workspaceTimeParameter).Select(issue => issue.Message).ToList();

    internal static IReadOnlyList<AnalysisQueryValidationIssue> KqlIssues(
        string kql,
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart,
        string scope = AnalysisQueryScopes.SelectedSystems,
        string workspaceTimeParameter = "_TimePeriod")
    {
        var errors = new List<AnalysisQueryValidationIssue>();
        if (string.IsNullOrWhiteSpace(kql))
            errors.Add(new(AnalysisQueryValidationCode.KqlRequired, "KQL is required."));
        if (!AnalysisQueryScopes.All.Contains(scope))
            errors.Add(new(AnalysisQueryValidationCode.ScopeInvalid, "Select a supported query scope (SelectedSystems or Workspace)."));
        if (scope == AnalysisQueryScopes.SelectedSystems && !kql.Contains("_SelectedSystems", StringComparison.Ordinal))
            errors.Add(new(AnalysisQueryValidationCode.SelectedSystemsRequired, "KQL must use _SelectedSystems."));
        if (scope == AnalysisQueryScopes.Workspace && !kql.Contains(workspaceTimeParameter, StringComparison.Ordinal))
            errors.Add(new(AnalysisQueryValidationCode.WorkspaceTimeRequired, $"Workspace-wide KQL must use {workspaceTimeParameter}.", workspaceTimeParameter));
        if (visualizationType is AnalysisQueryVisualizationTypes.LineChart or AnalysisQueryVisualizationTypes.StatusHistory && !kql.Contains("_BinSize", StringComparison.Ordinal))
            errors.Add(new(AnalysisQueryValidationCode.BinParameterRequired, "KQL must use _BinSize."));
        if (UnsafeKqlPattern().IsMatch(kql))
            errors.Add(new(AnalysisQueryValidationCode.UnsafeKql, "Management commands and explicit workspace or cluster references are not allowed."));
        return errors;
    }

    internal static void RequireValidDefinition(AnalysisQueryDefinition query)
    {
        var issues = DefinitionIssues(query);
        if (issues.Count > 0) throw new AnalysisQueryValidationException(issues);
    }

    internal static void RequireValidQuery(AnalysisQueryDefinition query)
    {
        var issues = DefinitionIssues(query)
            .Concat(KqlIssues(query.Kql, query.VisualizationType, query.Scope)).ToArray();
        if (issues.Length > 0) throw new AnalysisQueryValidationException(issues);
    }

    internal static IReadOnlyList<string> ValidateScheduledKql(string kql, string scope)
    {
        var errors = ValidateKql(kql, AnalysisQueryVisualizationTypes.Stat,
            scope, workspaceTimeParameter: "_WindowStart").ToList();
        if (scope == AnalysisQueryScopes.Workspace && kql.Contains("_SelectedSystems", StringComparison.Ordinal))
            errors.Add("Workspace-wide KQL must not depend on _SelectedSystems.");
        if (!kql.Contains("_WindowStart", StringComparison.Ordinal) || !kql.Contains("_WindowEnd", StringComparison.Ordinal))
            errors.Add("KQL must use _WindowStart and _WindowEnd to restrict the evaluation window.");
        if (kql.Length > 65536)
            errors.Add("KQL may contain at most 65536 characters.");
        return errors;
    }

    internal static void ValidateExecutionLimits(
        AnalysisQueryDefinition definition, int systemCount, int requestedMinutes)
    {
        var errors = ValidateKql(definition.Kql, definition.VisualizationType, definition.Scope);
        if (errors.Count > 0)
            throw new AnalysisValidationException(AnalysisValidationCode.QueryConfigurationInvalid, string.Join(" ", errors));
        if (systemCount is < 1 or > 100)
            throw new AnalysisValidationException(AnalysisValidationCode.SystemsRequired, "Select between 1 and 100 systems.");
        if (requestedMinutes < 5 || requestedMinutes > definition.MaxTimeRangeMinutes)
            throw new AnalysisValidationException(AnalysisValidationCode.TimeRangeInvalid,
                $"Time range must be between 5 and {definition.MaxTimeRangeMinutes} minutes.", definition.MaxTimeRangeMinutes);
    }
}
