namespace MonitoringApp;

public enum AnalysisQueryValidationCode
{
    KeyInvalid,
    NameInvalid,
    CategoryInvalid,
    DefaultTimeRangeInvalid,
    MaxTimeRangeInvalid,
    BinInvalid,
    VisualizationTypeInvalid,
    ScopeInvalid,
    MinimumRoleInvalid,
    VisualizationOptionsInvalid,
    KqlRequired,
    SelectedSystemsRequired,
    WorkspaceTimeRequired,
    BinParameterRequired,
    UnsafeKql
}

public sealed record AnalysisQueryValidationIssue(
    AnalysisQueryValidationCode Code, string Message, string? Parameter = null);

public sealed class AnalysisQueryValidationException(IReadOnlyList<AnalysisQueryValidationIssue> issues)
    : InvalidOperationException(string.Join(" ", issues.Select(issue => issue.Message)))
{
    public IReadOnlyList<AnalysisQueryValidationIssue> Issues { get; } = Array.AsReadOnly(issues.ToArray());
}
