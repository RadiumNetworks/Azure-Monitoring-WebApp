using System.Reflection;
using System.Runtime.CompilerServices;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QueryClientAlias = Azure.Monitor.Query.Logs.LogsQueryClient;

namespace MonitoringApp.Tests;

public sealed class KqlArchitectureTests
{
    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    private static bool IsCoreSource(string source) => Path.GetFullPath(source).StartsWith(
        Path.Combine(ProjectDirectory(), "Services", "KQL") + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsRegistrationSource(string source) => Path.GetFullPath(source).Equals(
        Path.Combine(ProjectDirectory(), "Services", "KQL", "DependencyInjection.cs"),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [Fact]
    public void ProductionLogAnalyticsDependenciesRemainInsideKqlCore()
    {
        var violations = PersistenceArchitecture.FindViolations(typeof(AnalysisQueryRunner).Assembly,
            IsCoreSource, isForbiddenType: KqlArchitecture.IsSdkType);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionTypedKqlRegistrationsRemainInsideTheCentralEntryPoint()
    {
        var entryPointCalls = 0;
        var violations = PersistenceArchitecture.FindViolations(typeof(AnalysisQueryRunner).Assembly,
            IsRegistrationSource, isForbiddenType: _ => false,
            isForbiddenReference: member =>
            {
                if (member is MethodInfo method &&
                    method.DeclaringType == typeof(KqlServiceCollectionExtensions) &&
                    method.Name == nameof(KqlServiceCollectionExtensions.AddKqlServices))
                    entryPointCalls++;
                return KqlArchitecture.IsDirectRegistration(member);
            });
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Equal(1, entryPointCalls);
        Assert.Empty(KqlArchitecture.RegistrationViolations(
            new ServiceCollection().AddKqlServices(new CrossTenantLogAnalyticsOptions())));
    }

    [Theory]
    [InlineData(typeof(DirectSdk))]
    [InlineData(typeof(FullyQualifiedSdk))]
    [InlineData(typeof(AliasedSdk))]
    [InlineData(typeof(ObjectTypedSdk))]
    [InlineData(typeof(AsyncSdk))]
    [InlineData(typeof(LambdaSdk))]
    [InlineData(typeof(SdkField))]
    [InlineData(typeof(SdkConstraint<>))]
    [InlineData(typeof(SdkInterface))]
    [InlineData(typeof(SdkBase))]
    [InlineData(typeof(RazorMappedSdk))]
    [InlineData(typeof(KqlSdkContractFixture))]
    public void GuardRejectsSdkDependenciesOutsideCore(Type type)
    {
        var violations = PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => false, NestedTypes(type).ToArray(), KqlArchitecture.IsSdkType);
        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains(type.FullName!, StringComparison.Ordinal));
    }

    internal interface KqlSdkContractFixture
    {
        LogsQueryResult Read();
    }

    [Theory]
    [InlineData(typeof(EfInKql))]
    [InlineData(typeof(AsyncEfInKql))]
    public void ExistingDatabaseGuardRejectsPersistenceInsideKql(Type type)
    {
        var violations = PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => false, NestedTypes(type).ToArray());
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void GuardAllowsSdkDependenciesInCoreAndNonQueryAzureFeaturesElsewhere()
    {
        Assert.Empty(PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => true, NestedTypes(typeof(AsyncSdk)).ToArray(), KqlArchitecture.IsSdkType));
        Assert.Empty(PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => false, NestedTypes(typeof(AllowedCalls)).ToArray(), KqlArchitecture.IsSdkType));
    }

    [Theory]
    [InlineData(typeof(LogsQueryClient), true)]
    [InlineData(typeof(LogsQueryResult), true)]
    [InlineData(typeof(LogsTable[]), true)]
    [InlineData(typeof(Func<Task<LogsQueryResult>>), true)]
    [InlineData(typeof(SecretClient), false)]
    [InlineData(typeof(ClientSecretCredential), false)]
    [InlineData(typeof(TokenCredential), false)]
    [InlineData(typeof(AnalysisQueryRunner), false)]
    [InlineData(typeof(AnalysisQueryRunResult), false)]
    public void PolicyDistinguishesTheQuerySdkFromOtherAzureAndApplicationTypes(Type type, bool forbidden) =>
        Assert.Equal(forbidden, KqlArchitecture.IsSdkType(type));

    [Fact]
    public void GuardUsesRazorSourceMappingAndRecognizesByRefTypes()
    {
        Assert.True(KqlArchitecture.IsSdkType(typeof(LogsQueryClient).MakeByRefType()));
        var violations = PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            IsCoreSource, [typeof(RazorMappedSdk)], KqlArchitecture.IsSdkType);
        Assert.Contains(violations, violation => violation.Contains("KqlArchitectureViolation.razor", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreAndRegistrationPathsRequireTheExactBoundary()
    {
        Assert.True(IsCoreSource(Path.Combine(ProjectDirectory(), "Services", "KQL", "Execution", "Example.cs")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "Services", "KQLCopy", "Example.cs")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "Components", "Example.razor")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "..", "OtherApp", "Services", "KQL", "Example.cs")));
        Assert.True(IsRegistrationSource(Path.Combine(ProjectDirectory(), "Services", "KQL", "DependencyInjection.cs")));
        Assert.False(IsRegistrationSource(Path.Combine(ProjectDirectory(), "Services", "KQL", "Other.cs")));
    }

    [Theory]
    [InlineData("central")]
    [InlineData("generic")]
    [InlineData("type")]
    public void GuardRejectsDuplicateRegistrationsIncludingRepeatedCentralRegistration(string kind)
    {
        var services = new ServiceCollection().AddKqlServices(new CrossTenantLogAnalyticsOptions());
        if (kind == "central")
            services.AddKqlServices(new CrossTenantLogAnalyticsOptions());
        else if (kind == "type")
            services.AddSingleton(typeof(IAnalysisLogAnalyticsQuery), typeof(AzureAnalysisLogAnalyticsQuery));
        else
            services.AddSingleton<IAnalysisLogAnalyticsQuery, AzureAnalysisLogAnalyticsQuery>();
        Assert.NotEmpty(KqlArchitecture.RegistrationViolations(services));
    }

    [Fact]
    public void GuardRejectsMissingAndChangedLifetimes()
    {
        Assert.Equal(7, KqlArchitecture.RegistrationViolations(new ServiceCollection()).Count);
        var services = new ServiceCollection().AddKqlServices(new CrossTenantLogAnalyticsOptions());
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(AnalysisQueryRunner)));
        services.AddScoped<AnalysisQueryRunner>();
        Assert.Contains("expected Singleton", Assert.Single(KqlArchitecture.RegistrationViolations(services)));
    }

    [Fact]
    public void GuardRejectsDirectRegistrationsOutsideTheEntryPoint()
    {
        var violations = PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => false, [typeof(DirectRegistration)], _ => false, KqlArchitecture.IsDirectRegistration);
        Assert.Equal(3, violations.Count);
        Assert.Empty(PersistenceArchitecture.FindViolations(typeof(KqlArchitectureTests).Assembly,
            _ => true, [typeof(DirectRegistration)], _ => false, KqlArchitecture.IsDirectRegistration));
    }

    private static IEnumerable<Type> NestedTypes(Type type) => new[] { type }.Concat(
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(NestedTypes));

    private sealed class DirectSdk
    {
        public LogsQueryClient Create(TokenCredential credential) => new(credential);
    }

    private sealed class FullyQualifiedSdk
    {
        public object Create(TokenCredential credential) => new global::Azure.Monitor.Query.Logs.LogsQueryClient(credential);
    }

    private sealed class AliasedSdk
    {
        public object Create(TokenCredential credential) => new QueryClientAlias(credential);
    }

    private sealed class ObjectTypedSdk
    {
        public object Create(TokenCredential credential) => new LogsQueryClient(credential);
    }

    private sealed class RazorMappedSdk
    {
#line 1 "KqlArchitectureViolation.razor"
        public object Create(TokenCredential credential) => new LogsQueryClient(credential);
#line default
    }

    private sealed class AsyncSdk
    {
        public async Task<object> Create(TokenCredential credential)
        {
            await Task.Yield();
            return new LogsQueryClient(credential);
        }
    }

    private sealed class LambdaSdk
    {
        public Func<object> Factory(TokenCredential credential) => () => new LogsQueryClient(credential);
    }

    private sealed class SdkField
    {
        public LogsQueryClient? Client = null;
    }

    private sealed class SdkConstraint<T> where T : LogsQueryClient;
    private interface SdkInterface { LogsQueryResult Read(); }
    private sealed class SdkBase : List<LogsQueryClient>;

    private sealed class EfInKql { public Task Save(AlertDbContext context) => context.SaveChangesAsync(); }

    private sealed class AsyncEfInKql
    {
        public async Task Save(AlertDbContext context)
        {
            await Task.Yield();
            await context.SaveChangesAsync();
        }
    }

    private sealed class DirectRegistration
    {
        public IServiceCollection Register(IServiceCollection services) =>
            services.AddSingleton<IAnalysisLogAnalyticsQuery, AzureAnalysisLogAnalyticsQuery>();
        public IServiceCollection Scoped(IServiceCollection services) => services.AddScoped<AnalysisQueryRunner>();
        public void TryRegister(IServiceCollection services) =>
            services.TryAddSingleton<IAnalysisLogAnalyticsQuery, AzureAnalysisLogAnalyticsQuery>();
    }

    private sealed class AllowedCalls
    {
        public IServiceCollection Register(IServiceCollection services, CrossTenantLogAnalyticsOptions options) =>
            services.AddKqlServices(options);
        public AnalysisQueryRunner Runner(AnalysisQueryRunner runner) => runner;
        public object Vault(Uri uri, TokenCredential credential) => new SecretClient(uri, credential);
        public object Credential(string tenant, string client, string secret) => new ClientSecretCredential(tenant, client, secret);
    }
}
