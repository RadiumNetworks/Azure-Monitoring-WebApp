namespace MonitoringApp;

public static class KqlServiceCollectionExtensions
{
    public static IServiceCollection AddKqlServices(
        this IServiceCollection services, CrossTenantLogAnalyticsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<TargetWorkspaceResolver>();
        services.AddSingleton<ILogAnalyticsSecretProvider, KeyVaultLogAnalyticsSecretProvider>();
        services.AddSingleton<ILogAnalyticsWorkspaceProbe, AzureLogAnalyticsWorkspaceProbe>();
        services.AddSingleton<LogAnalyticsConnectionDiagnostics>();
        services.AddSingleton<IAnalysisLogAnalyticsQuery, AzureAnalysisLogAnalyticsQuery>();
        services.AddSingleton<AnalysisQueryRunner>();
        services.AddSingleton<IFreeKqlQueryClient, AzureFreeKqlQueryClient>();
        services.AddSingleton<FreeKqlQueryService>();
        services.AddSingleton<LockoutQueryService>();
        return services;
    }
}
