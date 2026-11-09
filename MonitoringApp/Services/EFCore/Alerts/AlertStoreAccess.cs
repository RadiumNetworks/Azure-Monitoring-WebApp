namespace MonitoringApp;

internal static class AlertStoreAccess
{
    public static void Require(DatabaseConfigurationStatus configuration, string? errorPrefix = null)
    {
        if (!configuration.IsValid)
            throw new InvalidOperationException(errorPrefix is null ? configuration.Error : errorPrefix + configuration.Error);
    }
}
