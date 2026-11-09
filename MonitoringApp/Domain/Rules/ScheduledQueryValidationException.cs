namespace MonitoringApp;

public enum ScheduledQueryValidationCode
{
    LiveDisabled,
    WorkspaceSelectionInvalid,
    WorkspaceUnavailable,
    IdentityInvalid,
    ScopeInvalid,
    WorkspaceFiltersInvalid,
    FiltersInvalid,
    TimingInvalid,
    ConditionInvalid,
    KqlInvalid,
    ScopeChangeNotAllowed,
    NameExists,
    DeleteNotAllowed
}

public sealed class ScheduledQueryValidationException(
    ScheduledQueryValidationCode code, string message) : InvalidOperationException(message)
{
    public ScheduledQueryValidationCode Code { get; } = code;
}
