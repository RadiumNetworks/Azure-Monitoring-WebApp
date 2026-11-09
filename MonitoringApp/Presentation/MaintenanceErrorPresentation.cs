namespace MonitoringApp;

public static class MaintenanceErrorPresentation
{
    public static string? ValidationMessage(Exception exception, bool legacy, UiText text) => exception switch
    {
        MaintenanceAccessDeniedException => text["Maintenance.AdminRequired"],
        MaintenanceValidationException { Code: MaintenanceValidationCode.ConfirmationInvalid } =>
            text[legacy ? "LegacyAlerts.ConfirmationInvalid" : "DatabaseReset.ConfirmationInvalid"],
        MaintenanceValidationException { Code: MaintenanceValidationCode.DatabaseUnavailable } =>
            text["Maintenance.DatabaseUnavailable"],
        MaintenanceValidationException { Code: MaintenanceValidationCode.CommentsTooLong, AlertId: { } id }
            when legacy => text.Format("LegacyAlerts.CommentsTooLong", id),
        _ => null
    };
}
