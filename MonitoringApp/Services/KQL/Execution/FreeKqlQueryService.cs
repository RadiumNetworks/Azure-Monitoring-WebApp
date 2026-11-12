using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using System.Text.Json.Serialization;

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
public sealed record KqlSchemaColumn(string Name, string Type);
public sealed record KqlWorkspaceTables(string WorkspaceKey, IReadOnlyList<string> TableNames);
[JsonConverter(typeof(JsonStringEnumConverter<KqlSchemaStatus>))]
public enum KqlSchemaStatus { Ready, Empty, Unavailable, Failed }
public sealed record KqlTableSchema(
    string WorkspaceKey, string TableName, long CatalogVersion, KqlSchemaStatus Status,
    IReadOnlyList<KqlSchemaColumn> Columns);
public sealed record KqlTableCatalog(
    IReadOnlyList<string> WorkspaceKeys, IReadOnlyList<string> TableNames, bool IsLoading, bool HasError);

public sealed class FreeKqlQueryService(CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secrets, IFreeKqlQueryClient client)
{
    public const string TableNamesQuery = "search *\n| summarize count() by $table\n| sort by $table";

    public async Task<IReadOnlyList<string>> GetTableNamesAsync(
        IReadOnlyCollection<string> workspaceKeys, CancellationToken cancellationToken)
    {
        var catalogs = await GetWorkspaceTablesAsync(workspaceKeys, cancellationToken);
        return catalogs.SelectMany(catalog => catalog.TableNames).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<KqlWorkspaceTables>> GetWorkspaceTablesAsync(
        IReadOnlyCollection<string> workspaceKeys, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspaceKeys.Count == 0) return [];
        var workspaces = workspaceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Select(GetWorkspace).ToArray();
        var secret = await secrets.GetClientSecretAsync(cancellationToken);
        var catalogs = new List<KqlWorkspaceTables>();
        foreach (var workspace in workspaces)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await client.QueryAsync(workspace, options.ApplicationClientId, secret, TableNamesQuery, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Error is not null)
                throw new InvalidOperationException($"Log Analytics table discovery failed: {result.Error.Code}: {result.Error.Message}");
            var tables = result.AllTables;
            if (tables.Count == 0)
                throw new InvalidOperationException("Log Analytics table discovery returned no result table.");
            foreach (var table in tables)
            {
                var column = table.Columns.Select((value, index) => (value, index))
                    .SingleOrDefault(item => item.value.Name == "$table");
                if (column.value is null)
                    throw new InvalidOperationException("Log Analytics table discovery returned no $table column.");
                foreach (var row in table.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (row[column.index] is not string name || string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException("Log Analytics table discovery returned an invalid table name.");
                    names.Add(name);
                }
            }
            catalogs.Add(new(workspace.Key, names.Order(StringComparer.Ordinal).ToArray()));
        }
        return catalogs;
    }

    public async Task<IReadOnlyList<KqlSchemaColumn>> GetTableSchemaAsync(
        string workspaceKey, string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = GetWorkspace(workspaceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var secret = await secrets.GetClientSecretAsync(cancellationToken);
        var kql = $"table(@'{tableName.Replace("'", "''", StringComparison.Ordinal)}') | getschema";
        var result = await client.QueryAsync(workspace, options.ApplicationClientId, secret, kql, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Error is not null)
            throw new InvalidOperationException($"Log Analytics schema discovery failed: {result.Error.Code}: {result.Error.Message}");
        if (result.AllTables.Count != 1)
            throw new InvalidOperationException("Log Analytics schema discovery must return one result table.");
        var table = result.AllTables[0];
        var nameColumn = table.Columns.Select((column, index) => (column, index))
            .SingleOrDefault(item => item.column.Name == "ColumnName");
        var typeColumn = table.Columns.Select((column, index) => (column, index))
            .SingleOrDefault(item => item.column.Name == "ColumnType");
        if (nameColumn.column is null || typeColumn.column is null)
            throw new InvalidOperationException("Log Analytics schema discovery returned no ColumnName or ColumnType column.");
        var columns = new List<KqlSchemaColumn>();
        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row[nameColumn.index] is not string name || string.IsNullOrWhiteSpace(name) ||
                row[typeColumn.index] is not string type || string.IsNullOrWhiteSpace(type))
                throw new InvalidOperationException("Log Analytics schema discovery returned an invalid column name or type.");
            columns.Add(new(name, type));
        }
        return columns;
    }

    private CrossTenantLogAnalyticsWorkspaceOptions GetWorkspace(string workspaceKey)
    {
        if (!options.Enabled)
            throw new AnalysisValidationException(AnalysisValidationCode.LiveDisabled, "Live Log Analytics is disabled.");
        return options.Workspaces.SingleOrDefault(item => item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceRequired, "Select a configured Log Analytics workspace.");
    }

    public async Task<FreeKqlResult> ExecuteAsync(string workspaceKey, string kql, CancellationToken cancellationToken)
    {
        var workspace = GetWorkspace(workspaceKey);
        if (string.IsNullOrWhiteSpace(kql))
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.InputRequired, "Enter a KQL query.");
        var secret = await secrets.GetClientSecretAsync(cancellationToken);
        var result = await client.QueryAsync(workspace, options.ApplicationClientId, secret, kql, cancellationToken);
        return KqlResultNormalizer.NormalizeRawTables(result, workspace.DisplayName, cancellationToken);
    }
}
