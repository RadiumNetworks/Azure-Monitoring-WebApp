using System.Reflection;
using Azure.Monitor.Query.Logs;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

internal static class KqlArchitecture
{
    private static readonly Type[] ServiceTypes =
    [
        typeof(CrossTenantLogAnalyticsOptions), typeof(TargetWorkspaceResolver),
        typeof(ILogAnalyticsSecretProvider), typeof(ILogAnalyticsWorkspaceProbe),
        typeof(LogAnalyticsConnectionDiagnostics), typeof(IAnalysisLogAnalyticsQuery),
        typeof(AnalysisQueryRunner)
    ];

    internal static bool IsSdkType(Type type) => PersistenceArchitecture.ContainsType(type,
        candidate => candidate.Assembly == typeof(LogsQueryClient).Assembly ||
            typeof(LogsQueryClient).IsAssignableFrom(candidate));

    internal static bool IsDirectRegistration(MemberInfo member) =>
        member is MethodInfo method &&
        method.DeclaringType?.Namespace is { } space &&
        (space == typeof(ServiceCollectionServiceExtensions).Namespace ||
            space.StartsWith(typeof(ServiceCollectionServiceExtensions).Namespace + ".", StringComparison.Ordinal)) &&
        (method.Name.StartsWith("Add", StringComparison.Ordinal) ||
            method.Name.StartsWith("TryAdd", StringComparison.Ordinal)) &&
        method.GetGenericArguments().Any(ServiceTypes.Contains);

    internal static IReadOnlyList<string> RegistrationViolations(IServiceCollection services) =>
        ServiceTypes.SelectMany(type =>
        {
            var registrations = services.Where(descriptor => descriptor.ServiceType == type).ToArray();
            return registrations.Length != 1
                ? new[] { $"{type.Name}: expected one registration, found {registrations.Length}." }
                : registrations[0].Lifetime != ServiceLifetime.Singleton
                    ? [$"{type.Name}: expected Singleton, found {registrations[0].Lifetime}."]
                    : Array.Empty<string>();
        }).ToArray();
}
