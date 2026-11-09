using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;

namespace MonitoringApp;

public interface IAnalysisLogAnalyticsQuery
{
    Task<LogsQueryResult> QueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
        string kql, int timeRangeMinutes, CancellationToken cancellationToken);
}

public sealed class AzureAnalysisLogAnalyticsQuery : IAnalysisLogAnalyticsQuery
{
    private readonly LogAnalyticsClientFactory clients;

    public AzureAnalysisLogAnalyticsQuery() : this(new LogAnalyticsClientFactory())
    {
    }

    internal AzureAnalysisLogAnalyticsQuery(LogAnalyticsClientFactory clients) => this.clients = clients;

    public async Task<LogsQueryResult> QueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
        string kql, int timeRangeMinutes, CancellationToken cancellationToken)
    {
        var credential = clients.CreateCredential(workspace, applicationClientId, secret);
        var response = await clients.CreateClient(credential).QueryWorkspaceAsync(
            workspace.WorkspaceId, kql,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(timeRangeMinutes)),
            cancellationToken: cancellationToken);
        return response.Value;
    }
}
