using System.Text.Json;

namespace MonitoringApp;

// One instance belongs to one authorized refresh, never to the DI container.
internal sealed class AnalysisDashboardQueryBatch(
    Func<AnalysisQueryRequest, CancellationToken, Task<AnalysisQueryResponse>> execute,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<RequestKey, Task<AnalysisQueryResponse>> requests = [];
    internal int Count => requests.Count;

    internal Task<AnalysisQueryResponse> ExecuteAsync(AnalysisQueryDefinition query, AnalysisQueryRequest request)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Id != request.QueryDefinitionId)
            throw new InvalidOperationException("The widget request does not match the available query.");
        var snapshot = request with { Systems = Array.AsReadOnly(request.Systems.ToArray()) };
        var key = new RequestKey(snapshot.QueryDefinitionId, query.Version, query.PublishedVersionId,
            snapshot.TimeRangeMinutes, snapshot.BinMinutes ?? query.DefaultBinMinutes, snapshot.GroupBy,
            snapshot.WorkspaceKey, snapshot.Domain, snapshot.Site, snapshot.Role,
            JsonSerializer.Serialize(snapshot.Systems));
        if (!requests.TryGetValue(key, out var task))
        {
            task = ExecuteCoreAsync(snapshot);
            requests.Add(key, task);
        }
        return task;
    }

    private async Task<AnalysisQueryResponse> ExecuteCoreAsync(AnalysisQueryRequest request) =>
        await execute(request, cancellationToken);

    private sealed record RequestKey(
        Guid QueryId, int Version, Guid? PublishedVersionId, int Minutes, int BinMinutes,
        string GroupBy, string? Workspace, string? Domain, string? Site, string? Role, string Systems);
}
