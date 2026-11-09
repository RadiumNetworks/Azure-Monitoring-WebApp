using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;

namespace MonitoringApp;

internal class LogAnalyticsClientFactory
{
    internal virtual TokenCredential CreateCredential(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret) =>
        new ClientSecretCredential(workspace.TenantId, applicationClientId, secret);

    internal virtual LogsQueryClient CreateClient(TokenCredential credential) => new(credential);
}
