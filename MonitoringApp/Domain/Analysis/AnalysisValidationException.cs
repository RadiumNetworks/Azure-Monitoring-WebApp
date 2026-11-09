namespace MonitoringApp;

public enum AnalysisValidationCode
{
    DashboardNameInvalid,
    DashboardMissing,
    KeepDashboard,
    SystemsRequired,
    DistinctSystemsRequired,
    TimeRangeInvalid,
    LiveDisabled,
    QueryConfigurationInvalid,
    WorkspaceRequired,
    WorkspaceUnavailable,
    WorkspaceNotEnabled,
    WorkspaceRouteMismatch,
    DomainUnavailable,
    DomainSystemsMissing,
    WorkspaceSystemsMissing,
    FiltersNoMatch,
    QueryNotValidated,
    IntervalInvalid,
    GroupingInvalid,
    DashboardTimeRangeInvalid,
    DashboardIntervalInvalid,
    RegistryWorkspaceRequired,
    RegistryWorkspaceUnavailable,
    RegistryKeyExists,
    RegistryPublishRequired,
    RegistryPublicationMissing,
    CategoryRequired,
    CategoryPathInvalid,
    CategoryNameInvalid,
    CategoryExists,
    CategoryMissing,
    CategoryCycle,
    CategoryCollision,
    CategoryNotEmpty,
    CategoryQueryMissing,
    RegistryQueryMissing
}

public sealed class AnalysisValidationException(
    AnalysisValidationCode code, string message, params object[] arguments) : InvalidOperationException(message)
{
    public AnalysisValidationCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
}
