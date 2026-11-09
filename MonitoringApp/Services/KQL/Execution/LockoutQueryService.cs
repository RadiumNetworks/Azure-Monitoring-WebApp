namespace MonitoringApp;

public sealed record LockoutUsers(IReadOnlyList<string> Names, bool Limited);

public sealed class LockoutQueryService(CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secrets, IAnalysisLogAnalyticsQuery client)
{
    public static IReadOnlyList<int> TimeRanges { get; } = [60, 240, 720, 1440];

    public async Task<LockoutUsers> GetUsersAsync(string workspaceKey, int timeRangeMinutes, CancellationToken cancellationToken)
    {
        var workspace = Validate(workspaceKey, timeRangeMinutes);
        var result = await ExecuteAsync(workspace, LockoutQueryTemplates.Users(), timeRangeMinutes, cancellationToken);
        if (result.Tables.Count != 1 || result.Tables[0].Columns.Count != 1 ||
            result.Tables[0].Rows.Any(row => row.Count != 1))
            throw new InvalidOperationException("The lockout users query must return one table with one column.");
        var names = result.Tables[0].Rows.Select(row => row[0])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(names, result.Limited);
    }

    public Task<FreeKqlResult> SearchAsync(string workspaceKey, int timeRangeMinutes, string user, CancellationToken cancellationToken)
    {
        var workspace = Validate(workspaceKey, timeRangeMinutes);
        return ExecuteAsync(workspace, LockoutQueryTemplates.Events(user), timeRangeMinutes, cancellationToken);
    }

    private CrossTenantLogAnalyticsWorkspaceOptions Validate(string workspaceKey, int timeRangeMinutes)
    {
        if (!options.Enabled)
            throw new AnalysisValidationException(AnalysisValidationCode.LiveDisabled, "Live Log Analytics is disabled.");
        var workspace = options.Workspaces.SingleOrDefault(item => item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceRequired, "Select a configured Log Analytics workspace.");
        if (!TimeRanges.Contains(timeRangeMinutes))
            throw new InvalidOperationException("Select a lockout time range of 1, 4, 12 or 24 hours.");
        return workspace;
    }

    private async Task<FreeKqlResult> ExecuteAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql, int timeRangeMinutes, CancellationToken cancellationToken)
    {
        var secret = await secrets.GetClientSecretAsync(cancellationToken);
        var result = await client.QueryAsync(workspace, options.ApplicationClientId, secret, kql, timeRangeMinutes, cancellationToken);
        return KqlResultNormalizer.NormalizeRawTables(result, workspace.DisplayName, cancellationToken);
    }
}
