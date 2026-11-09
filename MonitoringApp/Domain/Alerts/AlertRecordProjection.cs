namespace MonitoringApp;

internal static class AlertRecordProjection
{
    public static AlertRecord ResolveDisplayIdentity(
        this QueryResultPresenter presenter,
        AlertRecord alert,
        DateTimeOffset? queryResultRefreshedAt = null) =>
        alert with
        {
            DisplayIdentity = presenter.ResolveIdentity(alert),
            QueryResultRefreshedAt = queryResultRefreshedAt
        };
}
