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
    RegistryQueryMissing,
    DashboardSelectionInvalid,
    DashboardRolesRequired,
    DashboardSystemsLimitExceeded,
    DashboardSelectionNotAvailable,
    DashboardInventoryRoutingInvalid
}

public sealed class AnalysisValidationException : InvalidOperationException
{
    public AnalysisValidationException(AnalysisValidationCode code, string message, params object[] arguments) : base(message)
    {
        Code = code;
        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    public AnalysisValidationException(AnalysisValidationCode code, string message, object[] arguments, Exception innerException) : base(message, innerException)
    {
        Code = code;
        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    public AnalysisValidationCode Code { get; }
    public IReadOnlyList<object> Arguments { get; }
}
