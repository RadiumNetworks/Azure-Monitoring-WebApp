using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MonitoringApp.Tests;

internal sealed class ScimTestHost : IAsyncDisposable, IDbContextFactory<AlertDbContext>
{
    private readonly SqliteConnection? connection;
    private readonly DbContextOptions<AlertDbContext> contextOptions;
    public ScimOptions Options { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public HttpClient Client { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public int ContextsCreated { get; private set; }

    private ScimTestHost(SqliteConnection? connection, DbContextOptions<AlertDbContext>? options)
    {
        this.connection = connection;
        contextOptions = options ?? new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection!).Options;
        Options = new()
        {
            Enabled = true, ConnectionId = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            BaseUrl = "https://provisioning.example.test/scim/v2",
            TokenHashes = [Hash(Token)]
        };
    }

    public sealed class AuditTimestampModelCustomizer(Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
        : Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder builder, DbContext context)
        {
            base.Customize(builder, context);
            builder.Entity<LogbookEntry>().Property(entry => entry.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            builder.Entity<AgentAnalysisReviewEvent>().Property(entry => entry.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public AlertDbContext CreateDbContext() { ContextsCreated++; return new(contextOptions); }

    public static async Task<ScimTestHost> CreateAsync(bool enabled = true, bool https = true, string authentication = "sql",
        DbContextOptions<AlertDbContext>? databaseOptions = null, bool orderedAuditTimestamps = false)
    {
        SqliteConnection? connection = null;
        if (databaseOptions is null)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            if (orderedAuditTimestamps)
                databaseOptions = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                    .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, AuditTimestampModelCustomizer>().Options;
        }
        var host = new ScimTestHost(connection, databaseOptions);
        await using (var context = host.CreateDbContext())
        {
            if (context.Database.IsSqlServer())
                await context.Database.EnsureCreatedAsync();
            else
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
        }
        host.ContextsCreated = 0;
        host.Options.Enabled = enabled;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development, ApplicationName = typeof(ScimApi).Assembly.GetName().Name
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration["WEBSITE_SITE_NAME"] = "scim-isolated-test";
        builder.Configuration["WEBSITE_AUTH_ENABLED"] = "true";
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(host);
        builder.Services.AddScoped<IProvisioningUserStore, ProvisioningUserStore>();
        builder.Services.AddScoped<IProvisioningGroupStore, ProvisioningGroupStore>();
        builder.Services.AddScimServices(host.Options, new() { Enabled = true });
        builder.Services.AddAuthentication(ApplicationAuthorization.SqlCookieScheme)
            .AddCookie(ApplicationAuthorization.SqlCookieScheme)
            .AddScheme<AuthenticationSchemeOptions, AppServiceAuthenticationHandler>(AppServiceAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => ApplicationAuthorization.Configure(options, new() { Type = authentication }));
        builder.Services.AddOpenApi();
        host.App = builder.Build();
        host.App.Use(async (http, next) =>
        {
            if (https) http.Request.Scheme = "https";
            if (http.Request.Headers.ContainsKey("X-Test-Interactive"))
                http.User = new(new ClaimsIdentity([new(ClaimTypes.Name, "interactive-admin"), new(ClaimTypes.Role, "Admin")],
                    ApplicationAuthorization.SqlCookieScheme));
            await next();
        });
        host.App.UseAuthentication();
        host.App.UseAuthorization();
        host.App.MapScimApi();
        host.App.MapOpenApi().AllowAnonymous();
        await host.App.StartAsync();
        var addresses = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        host.Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(addresses.Addresses.Single())
        };
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
    }
}
