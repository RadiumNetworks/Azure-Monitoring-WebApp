using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;

namespace MonitoringApp;

public interface IFreeKqlQueryClient
{
    Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string applicationClientId, string secret, string kql, CancellationToken cancellationToken);
}

public sealed class AzureFreeKqlQueryClient : IFreeKqlQueryClient
{
    private readonly LogAnalyticsClientFactory clients;
    public AzureFreeKqlQueryClient() : this(new LogAnalyticsClientFactory()) { }
    internal AzureFreeKqlQueryClient(LogAnalyticsClientFactory clients) => this.clients = clients;

    public async Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string applicationClientId, string secret, string kql, CancellationToken cancellationToken)
    {
        var credential = clients.CreateCredential(workspace, applicationClientId, secret);
        var response = await clients.CreateClient(credential).QueryWorkspaceAsync(
            workspace.WorkspaceId, kql, LogsQueryTimeRange.All, cancellationToken: cancellationToken);
        return response.Value;
    }
}

public sealed record FreeKqlResult(IReadOnlyList<AnalysisQueryTable> Tables, bool Limited);

public sealed class FreeKqlQueryService(CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secrets, IFreeKqlQueryClient client)
{
    public async Task<FreeKqlResult> ExecuteAsync(string workspaceKey, string kql, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            throw new AnalysisValidationException(AnalysisValidationCode.LiveDisabled, "Live Log Analytics is disabled.");
        var workspace = options.Workspaces.SingleOrDefault(item => item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceRequired, "Select a configured Log Analytics workspace.");
        if (string.IsNullOrWhiteSpace(kql))
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.InputRequired, "Enter a KQL query.");
        var secret = await secrets.GetClientSecretAsync(cancellationToken);
        var result = await client.QueryAsync(workspace, options.ApplicationClientId, secret, kql, cancellationToken);
        return KqlResultNormalizer.NormalizeRawTables(result, workspace.DisplayName, cancellationToken);
    }
}
