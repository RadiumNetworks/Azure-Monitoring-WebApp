using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryExportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExportsSavedDraftsIncludingDisabledQueriesAndRoundTripsThroughAdLoader()
    {
        await using var fixture = await RegistryFixture.CreateAsync();
        var gpo = Template("ad-gpo-statistics");
        var visualization = AnalysisVisualization.Parse(gpo.VisualizationJson);
        visualization.StatTitleFontSize = 12;
        visualization.StatValueFontSize = 18;
        visualization.ValueMappings.Add(new(null, "No data", "gray"));
        gpo.VisualizationJson = AnalysisVisualization.Serialize(visualization);
        gpo.MinimumRole = SqlAuthenticationRoles.Admin;
        gpo.Name = "Saved draft name";
        var saved = await fixture.Registry.SaveDraftAsync(gpo, "export-admin");
        var disabled = await fixture.Registry.SaveDraftAsync(Template("ad-fsmo-roles"), "export-admin");
        await fixture.Registry.DisableAsync(disabled.Id, "export-admin");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.SingleAsync(item => item.Id == saved.Id);
            var snapshot = AnalysisQueryRegistry.CreateSnapshot(query) with { Name = "Older published name" };
            var version = new AnalysisQueryVersion
            {
                Id = Guid.NewGuid(), QueryDefinitionId = query.Id, Version = 1,
                SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions), PublishedBy = "publication-admin"
            };
            context.AnalysisQueryVersions.Add(version);
            query.PublishedVersionId = version.Id;
            await context.SaveChangesAsync();
        }
        saved.Name = "Unsaved editor name";

        var bytes = await fixture.Exporter.ExportAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(["ad-fsmo-roles.json", "ad-gpo-statistics.json"], archive.Entries.Select(entry => entry.FullName));
        var directory = Directory.CreateTempSubdirectory("query-export-roundtrip-").FullName;
        try
        {
            archive.ExtractToDirectory(directory);
            var restored = ActiveDirectoryAnalysisQueries.Create(directory);
            var originals = await fixture.Registry.ListAsync();
            Assert.Equal(originals.Count, restored.Count);
            foreach (var query in restored)
            {
                var original = originals.Single(item => item.Key == query.Key);
                Assert.True(JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(AnalysisQueryRegistry.CreateSnapshot(original), JsonOptions),
                    JsonSerializer.SerializeToNode(AnalysisQueryRegistry.CreateSnapshot(query), JsonOptions)));
                Assert.Equal(Guid.Empty, query.Id);
                Assert.Empty(query.Workspaces);
                Assert.Empty(query.Versions);
                Assert.Equal(AnalysisQueryStatuses.Draft, query.Status);
            }
            var restoredGpo = restored.Single(query => query.Key == "ad-gpo-statistics");
            Assert.Equal("Saved draft name", restoredGpo.Name);
            Assert.Equal(AnalysisQueryScopes.Workspace, restoredGpo.Scope);
            var options = AnalysisVisualization.Parse(restoredGpo.VisualizationJson);
            Assert.Equal(12, options.StatTitleFontSize);
            Assert.Equal(18, options.StatValueFontSize);
            Assert.Contains(options.ValueMappings, mapping => mapping.Value is null && mapping.Text == "No data");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        using var reader = new StreamReader(archive.GetEntry("ad-gpo-statistics.json")!.Open());
        var json = await reader.ReadToEndAsync();
        Assert.Contains("\n  \"key\":", json.Replace("\r\n", "\n"));
        Assert.DoesNotContain("export-admin", json);
        Assert.DoesNotContain("publication-admin", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["key", "name", "description", "category", "kql", "visualizationType", "unit",
                "defaultTimeRangeMinutes", "maxTimeRangeMinutes", "defaultBinMinutes", "minimumRole", "visualization", "scope"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task ExportIsReadOnlyAndPreservesIncompleteSavedKqlDrafts()
    {
        await using var fixture = await RegistryFixture.CreateAsync();
        var query = Template("ad-gpo-statistics");
        query.Kql = "Unfinished KQL draft\n// Gr\u00f6\u00dfe and \u6771\u4eac";
        await fixture.Registry.SaveDraftAsync(query, "admin");
        var before = Assert.Single(await fixture.Registry.ListAsync());
        var bytes = await fixture.Exporter.ExportAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(Assert.Single(archive.Entries).Open());
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal(query.Kql, json.RootElement.GetProperty("kql").GetString());
        var after = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Empty(after.Versions);
    }

    [Fact]
    public async Task ZipEntryNamesAreSafeUniqueAndKeepOriginalKeysInTheirJson()
    {
        await using var fixture = await RegistryFixture.CreateAsync();
        string[] keys = ["../CON", "CON", "a/b", "a?b", "ad-normal-key", "a_b"];
        foreach (var key in keys)
        {
            var query = Template("ad-services-overview");
            query.Key = key;
            await fixture.Registry.SaveDraftAsync(query, "admin");
        }
        using var archive = new ZipArchive(new MemoryStream(await fixture.Exporter.ExportAsync()), ZipArchiveMode.Read);
        Assert.Equal(keys.Length, archive.Entries.Count);
        Assert.Equal(keys.Length, archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(archive.Entries, entry => entry.FullName == "ad-normal-key.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "query-con.json");
        var exportedKeys = new List<string>();
        foreach (var entry in archive.Entries)
        {
            Assert.DoesNotContain("/", entry.FullName);
            Assert.DoesNotContain("\\", entry.FullName);
            Assert.DoesNotContain("..", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
            exportedKeys.Add(json.RootElement.GetProperty("key").GetString()!);
        }
        Assert.Equal(keys.Select(key => key.ToLowerInvariant()).Order(), exportedKeys.Order());
    }

    [Fact]
    public async Task EmptyRegistryInvalidSavedOptionsAndCancellationNeverReturnPartialArchives()
    {
        await using var fixture = await RegistryFixture.CreateAsync();
        Assert.Contains("No saved queries", (await Assert.ThrowsAsync<AnalysisQueryExportException>(
            () => fixture.Exporter.ExportAsync())).Message);
        var saved = await fixture.Registry.SaveDraftAsync(Template("ad-gpo-statistics"), "admin");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.SingleAsync(item => item.Id == saved.Id);
            query.VisualizationJson = "{";
            await context.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<AnalysisQueryExportException>(() => fixture.Exporter.ExportAsync());
        Assert.Contains(saved.Key, error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Exporter.ExportAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(SqlAuthenticationRoles.Operator, HttpStatusCode.Forbidden)]
    [InlineData(SqlAuthenticationRoles.Reader, HttpStatusCode.Forbidden)]
    [InlineData(SqlAuthenticationRoles.Admin, HttpStatusCode.OK)]
    public async Task ExportEndpointRequiresAdminAndReturnsAnAttachment(string? role, HttpStatusCode expected)
    {
        var exporter = new StubExporter();
        await using var endpoint = await EndpointFixture.CreateAsync(exporter);
        if (role is not null)
            endpoint.Client.DefaultRequestHeaders.Add("X-Test-Role", role);
        using var response = await endpoint.Client.GetAsync("/api/admin/analysis-queries/export");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(role == SqlAuthenticationRoles.Admin ? 1 : 0, exporter.Calls);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Contains("AnalysisQueries-", response.Content.Headers.ContentDisposition.ToString());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(new byte[] { 80, 75, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
            Assert.True(exporter.SawCancellationToken);
        }
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Conflict)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task FailedDownloadsReturnProblemsAndDoNotDiscloseStorageErrors(bool storageError, HttpStatusCode expected)
    {
        var exporter = new StubExporter
        {
            Error = storageError ? new InvalidOperationException("Server=private;Password=secret")
                : new AnalysisQueryExportException("No saved queries are available to export.")
        };
        await using var endpoint = await EndpointFixture.CreateAsync(exporter);
        endpoint.Client.DefaultRequestHeaders.Add("X-Test-Role", SqlAuthenticationRoles.Admin);
        using var response = await endpoint.Client.GetAsync("/api/admin/analysis-queries/export");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Null(response.Content.Headers.ContentDisposition);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password", json);
        Assert.DoesNotContain("private", json);
        if (!storageError)
            Assert.Contains("No saved queries", json);
    }

    private static AnalysisQueryDefinition Template(string key)
    {
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == key);
        query.Workspaces.Add(new() { WorkspaceKey = "test" });
        return query;
    }

    private sealed class RegistryFixture(SqliteConnection connection, ContextFactory factory, AnalysisQueryRegistry registry) : IAsyncDisposable
    {
        public ContextFactory Factory { get; } = factory;
        public AnalysisQueryRegistry Registry { get; } = registry;
        public AnalysisQueryExportService Exporter { get; } = new(registry);

        public static async Task<RegistryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new ContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            string[] names = ["AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions"];
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            var options = new CrossTenantLogAnalyticsOptions { Workspaces = [new() { Key = "test" }] };
            var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
            return new(connection, factory, new AnalysisQueryRegistry(factory, options, runner));
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Export must not execute Log Analytics queries.");
    }

    private sealed class StubExporter : IAnalysisQueryExportService
    {
        public int Calls { get; private set; }
        public bool SawCancellationToken { get; private set; }
        public Exception? Error { get; init; }
        public Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            SawCancellationToken = cancellationToken.CanBeCanceled;
            return Error is null ? Task.FromResult(new byte[] { 80, 75, 3, 4 }) : Task.FromException<byte[]>(Error);
        }
    }

    private sealed class EndpointFixture(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public static async Task<EndpointFixture> CreateAsync(IAnalysisQueryExportService exporter)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(exporter);
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("AdminAccess",
                policy => policy.RequireAuthenticatedUser().RequireRole(SqlAuthenticationRoles.Admin)));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAnalysisQueryExport();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.Name, "test-admin"), new(ClaimTypes.Role, role)], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
