namespace MonitoringApp;

public enum DatabaseSettingsValidationCode
{
    UnknownSettings,
    MissingSettings,
    JsonNull,
    JsonInvalid,
    InvalidValues,
    SqlUserRequired
}

public sealed class DatabaseSettingsValidationException(
    DatabaseSettingsValidationCode code,
    string message,
    string? settingName = null,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public DatabaseSettingsValidationCode Code { get; } = code;
    public string? SettingName { get; } = settingName;
}
