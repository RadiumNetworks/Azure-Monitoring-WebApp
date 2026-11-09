using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddAlertDatabase(
        this IServiceCollection services, DatabaseConfigurationStatus configuration)
    {
        var connectionString = configuration.RequireConnectionString();
        services.AddSingleton(configuration);
        services.AddSingleton<SqlCommandFailureLoggingInterceptor>();
        services.AddSingleton<SqlConnectionFailureLoggingInterceptor>();
        services.AddDbContextFactory<AlertDbContext>((provider, options) =>
            DatabaseOptions.ConfigureSqlServer(options, connectionString)
                .AddInterceptors(
                    provider.GetRequiredService<SqlCommandFailureLoggingInterceptor>(),
                    provider.GetRequiredService<SqlConnectionFailureLoggingInterceptor>()));
        services.AddHostedService<DatabaseStartupCheck>();
        services.AddSingleton<DatabaseSettingsService>();
        services.AddSingleton<AnalysisQueryRegistry>();
        services.AddSingleton<AnalysisQueryCategoryService>();
        services.AddSingleton<AnalysisQueryExecutionService>();
        services.AddScoped<InboxLayoutRepository>();
        services.AddScoped<AnalysisDashboardRepository>();
        services.AddScoped<AnalysisDashboardPublishing>();
        services.AddSingleton<AlertStore>();
        services.AddScoped<RcaService>();
        services.AddSingleton<RcaReconciliationService>();
        services.AddSingleton<ScheduledQueryService>();
        services.AddSingleton<AgentAnalysisJobStore>();
        services.AddSingleton<AgentAnalysisReviewStore>();
        services.AddSingleton<SqlAuthenticationService>();
        services.AddSingleton<LogbookStore>();
        services.AddScoped<DatabaseResetService>();
        services.AddScoped<LegacyAlertResolutionService>();
        return services;
    }
}
