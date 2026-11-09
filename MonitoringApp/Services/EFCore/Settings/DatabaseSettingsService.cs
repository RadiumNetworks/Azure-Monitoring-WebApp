using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class DatabaseSettingsService(
    IDbContextFactory<AlertDbContext> factory,
    CrossTenantLogAnalyticsOptions logAnalyticsOptions)
{
    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await context.Settings.AsNoTracking()
            .Where(setting => DatabaseSettingsLoader.RequiredNames.Contains(setting.Name))
            .ToDictionaryAsync(setting => setting.Name, setting => setting.JsonValue,
                StringComparer.OrdinalIgnoreCase, cancellationToken);
        DatabaseSettingsLoader.EnsureRequired(settings.Keys);
        return settings;
    }

    public async Task<IReadOnlyDictionary<string, string>> SaveAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        var submitted = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var unknown = submitted.Keys.Except(DatabaseSettingsLoader.RequiredNames, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0)
            throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.UnknownSettings,
                $"Unknown database settings: {string.Join(", ", unknown)}.");
        var parsed = DatabaseSettingsLoader.ParseRequired(submitted);
        EnsureValid(DatabaseSettingsLoader.Authentication, parsed.Authentication.Validate());
        EnsureValid(DatabaseSettingsLoader.AlertHistory, parsed.AlertHistory.Validate());
        EnsureValid(DatabaseSettingsLoader.AlertGraph, parsed.AlertGraph.Validate());
        EnsureValid(DatabaseSettingsLoader.AlertSeverityDisplay, parsed.AlertSeverityDisplay.Validate());
        EnsureValid(DatabaseSettingsLoader.InboxTargetOutputDisplay, parsed.InboxTargetOutputDisplay.Validate());
        EnsureValid(DatabaseSettingsLoader.RcaDisplay, parsed.RcaDisplay.Validate());
        EnsureValid(DatabaseSettingsLoader.DashboardFilters, parsed.DashboardFilters.Validate(logAnalyticsOptions));
        EnsureValid(DatabaseSettingsLoader.AgentAnalysisContext, parsed.AgentAnalysisContext.Validate());
        EnsureValid(DatabaseSettingsLoader.AgentAnalysisExecution, parsed.AgentAnalysisExecution.Validate());

        await DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            if (parsed.Authentication.IsSql && !await context.AuthenticationUsers.AnyAsync(cancellationToken))
                throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.SqlUserRequired,
                    "Create at least one authentication user before enabling SQL authentication.");
            var settings = await context.Settings
                .Where(setting => DatabaseSettingsLoader.RequiredNames.Contains(setting.Name))
                .ToDictionaryAsync(setting => setting.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
            DatabaseSettingsLoader.EnsureRequired(settings.Keys);
            foreach (var name in DatabaseSettingsLoader.RequiredNames)
                settings[name].JsonValue = submitted[name];
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken, IsolationLevel.Serializable);
        return submitted;
    }

    private static void EnsureValid(string name, IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
            throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.InvalidValues,
                $"Invalid setting '{name}': {string.Join(" ", errors)}", name);
    }
}
