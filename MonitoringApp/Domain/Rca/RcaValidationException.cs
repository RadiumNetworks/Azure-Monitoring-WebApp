namespace MonitoringApp;

public enum RcaValidationCode
{
    NameInvalid,
    SelectionsInvalid,
    CaseMissing,
    Changed,
    Closed,
    AlertUnavailable,
    AssignmentChanged,
    ReassignmentRequired,
    StatusInvalid,
    LengthExceeded,
    AssigneeInvalid
}

public sealed class RcaValidationException(
    RcaValidationCode code, string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public RcaValidationCode Code { get; } = code;
}
