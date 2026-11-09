using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class EFCoreArchitectureTests
{
    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    private static bool IsCoreSource(string source) => Path.GetFullPath(source).StartsWith(
        Path.Combine(ProjectDirectory(), "Services", "EFCore") + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [Fact]
    public void ProductionPersistenceDependenciesRemainInsideDatabaseCore()
    {
        var violations = PersistenceArchitecture.FindViolations(typeof(AlertStore).Assembly, IsCoreSource);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SqlSourcesRemainInsideDatabaseCore()
    {
        var files = SourceFiles(new DirectoryInfo(ProjectDirectory())).Where(file => file.Extension.Equals(".sql", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.True(IsCoreSource(file.FullName), $"SQL source outside Services/EFCore: {file.FullName}"));
    }

    [Theory]
    [InlineData(typeof(DirectSql))]
    [InlineData(typeof(FullyQualifiedSql))]
    [InlineData(typeof(ContextParameter))]
    [InlineData(typeof(ContextField))]
    [InlineData(typeof(ContextConstraint<>))]
    [InlineData(typeof(ContextInterface))]
    [InlineData(typeof(ContextFactoryResolution))]
    [InlineData(typeof(ObjectTypedSql))]
    [InlineData(typeof(AsyncSql))]
    [InlineData(typeof(EfAttribute))]
    [InlineData(typeof(InheritedFactory))]
    [InlineData(typeof(EfExtension))]
    [InlineData(typeof(AdoParameter))]
    [InlineData(typeof(LambdaSql))]
    public void GuardRejectsDatabaseDependenciesOutsideCore(Type type)
    {
        var types = NestedTypes(type).ToArray();
        var violations = PersistenceArchitecture.FindViolations(typeof(EFCoreArchitectureTests).Assembly, _ => false, types);
        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains(type.FullName!, StringComparison.Ordinal));
    }

    [Fact]
    public void GuardAllowsTheSameDatabaseCodeInsideCore()
    {
        var violations = PersistenceArchitecture.FindViolations(
            typeof(EFCoreArchitectureTests).Assembly, _ => true, [typeof(DirectSql), typeof(ContextParameter)]);
        Assert.Empty(violations);
    }

    [Fact]
    public void GuardAllowsServiceCallsRegistrationAndExistingExceptionContracts()
    {
        var violations = PersistenceArchitecture.FindViolations(
            typeof(EFCoreArchitectureTests).Assembly, _ => false, NestedTypes(typeof(AllowedServiceCalls)).ToArray());
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(typeof(IDbContextFactory<AlertDbContext>), true)]
    [InlineData(typeof(AlertDbContext[]), true)]
    [InlineData(typeof(Func<AlertDbContext>), true)]
    [InlineData(typeof(System.Data.IDbConnection), true)]
    [InlineData(typeof(SqlConnectionStringBuilder), true)]
    [InlineData(typeof(DbUpdateConcurrencyException), false)]
    [InlineData(typeof(SqlException), false)]
    [InlineData(typeof(AlertStore), false)]
    [InlineData(typeof(DatabaseConfigurationStatus), false)]
    [InlineData(typeof(IReadOnlyList<AlertRecord>), false)]
    public void GuardDistinguishesPersistenceTypesFromServiceAndErrorContracts(Type type, bool forbidden) =>
        Assert.Equal(forbidden, PersistenceArchitecture.IsDatabaseType(type));

    [Fact]
    public void CorePathRequiresTheActualDirectoryBoundary()
    {
        Assert.True(IsCoreSource(Path.Combine(ProjectDirectory(), "Services", "EFCore", "Queries", "Example.cs")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "Services", "EFCoreCopy", "Example.cs")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "Components", "Example.razor")));
        Assert.False(IsCoreSource(Path.Combine(ProjectDirectory(), "..", "OtherApp", "Services", "EFCore", "Example.cs")));
    }

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
            yield return file;
        foreach (var child in directory.EnumerateDirectories().Where(child =>
            child.Name is not ("bin" or "obj" or "publish")))
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Architecture source scan cannot follow directory links: {child.FullName}");
            foreach (var file in SourceFiles(child))
                yield return file;
        }
    }

    private static IEnumerable<Type> NestedTypes(Type type) => new[] { type }.Concat(
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(NestedTypes));

    private sealed class DirectSql
    {
        public void Open() => new SqlConnection().Open();
    }

    private sealed class FullyQualifiedSql
    {
        public object Create() => new global::Microsoft.Data.SqlClient.SqlCommand();
    }

    private sealed class ContextParameter
    {
        public Task Save(AlertDbContext context) => context.SaveChangesAsync();
    }

    private sealed class ContextField
    {
        public IDbContextFactory<AlertDbContext>? Factory = null;
    }

    private sealed class ContextConstraint<T> where T : DbContext
    {
        public string Name => typeof(T).Name;
    }

    private interface ContextInterface
    {
        AlertDbContext Create();
    }

    private interface InheritedFactory : IDbContextFactory<AlertDbContext>;

    private sealed class EfExtension
    {
        public Task<List<AlertRecord>> Read(IQueryable<AlertRecord> query) =>
            query.AsNoTracking().ToListAsync();
    }

    private sealed class AdoParameter
    {
        public void Open(System.Data.IDbConnection connection) => connection.Open();
    }

    private sealed class LambdaSql
    {
        public Func<object> Factory() => () => new SqlConnection();
    }

    private sealed class ContextFactoryResolution
    {
        public object Resolve(IServiceProvider services) =>
            services.GetRequiredService<IDbContextFactory<AlertDbContext>>();
    }

    private sealed class ObjectTypedSql
    {
        public object Create() => new SqlConnection();
    }

    private sealed class AsyncSql
    {
        public async Task Open()
        {
            await Task.Yield();
            await using var connection = new SqlConnection();
            await connection.OpenAsync();
        }
    }

    [Keyless]
    private sealed class EfAttribute;

    private sealed class AllowedServiceCalls
    {
        public IServiceCollection Register(IServiceCollection services, DatabaseConfigurationStatus configuration) =>
            services.AddAlertDatabase(configuration);

        public int ErrorNumber(SqlException exception) => exception.Errors.Count > 0 ? exception.Errors[0].Number : exception.Number;

        public async Task<IReadOnlyList<AlertRecord>> Read(AlertStore store)
        {
            try
            {
                return await store.GetAllAsync();
            }
            catch (Exception exception) when (exception is SqlException or DbUpdateConcurrencyException)
            {
                throw;
            }
        }
    }
}
