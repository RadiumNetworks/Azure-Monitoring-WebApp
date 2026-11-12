using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Layout;

namespace MonitoringApp.Tests;

public sealed class MainNavigationTests
{
    [Theory]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Reader, false, false)]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Operator, true, false)]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Admin, true, true)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Reader, false, false)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Operator, true, false)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Admin, true, true)]
    [InlineData(ApplicationAuthenticationOptions.Open, "", true, true)]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Reader, false, false, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Operator, true, false, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Admin, true, true, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Reader, false, false, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Operator, true, false, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Admin, true, true, "de-DE")]
    [InlineData(ApplicationAuthenticationOptions.Open, "", true, true, "de-DE")]
    public async Task GroupsOverviewAndAnalysisWithoutChangingRoleRestrictedNavigation(
        string mode, string role, bool configurationVisible, bool adminVisible, string culture = "en-US")
    {
        using var selection = new UiSelection(culture);
        await using var store = await NavigationStore.CreateAsync(seed: true);
        var authentication = new ApplicationAuthenticationOptions { Type = mode };
        var collection = new ServiceCollection().AddLogging().AddAuthorization(options =>
            ApplicationAuthorization.Configure(options, authentication));
        collection.AddCascadingAuthenticationState();
        collection.AddUiLocalization();
        await using var services = collection
            .AddSingleton(authentication)
            .AddSingleton<IDbContextFactory<AlertDbContext>>(store)
            .AddSingleton<AnalysisDashboardPublishing>()
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<AuthenticationStateProvider>(new TestAuthenticationState(role))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString());

        html = WebUtility.HtmlDecode(html);
        var german = culture == "de-DE";
        var groups = Regex.Matches(html,
            """class="navigation-group-toggle"[^>]*>(?<title>[^<]+)</button>\s*<div class="navigation-submenu">(?<links>.*?)</div>""",
            RegexOptions.Singleline).ToDictionary(
                match => match.Groups["title"].Value,
                match => match.Groups["links"].Value);
        var overview = groups[german ? "Übersicht" : "Overview"];
        Assert.Equal(configurationVisible
            ? new[] { german ? "Posteingang" : "Inbox", "Graph", german ? "Meine Dashboards" : "My dashboards", "Published dashboard" }
            : new[] { german ? "Posteingang" : "Inbox", "Graph", german ? "Meine Dashboards" : "My dashboards" },
            Regex.Matches(overview, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
        Assert.Contains("<summary>Dashboards</summary>", overview);
        Assert.Contains("class=\"navigation-dashboard-links\"", overview);
        Assert.Equal(configurationVisible, overview.Contains($"/analysis/published/{NavigationStore.PublishedId:D}"));
        Assert.DoesNotContain("Private dashboard", html);
        Assert.DoesNotContain("original-owner@example.com", html);
        Assert.Contains("href=\"/\"", overview);
        Assert.Contains("href=\"/graph\"", overview);
        Assert.Contains("href=\"/analysis\"", overview);
        var support = groups["Support"];
        Assert.Contains("href=\"/support/lockouts\"", support);
        Assert.Equal(new[] { "Lockouts" },
            Regex.Matches(support, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
        Assert.Equal(new[] { german ? "Übersicht" : "Overview", "Support" }, groups.Keys.Take(2));

        var analyze = groups[german ? "Analysieren" : "Analyze"];
        Assert.Equal(german
            ? new[] { "Abfragen", "Logbuch", "Ursachenanalyse", "Analysearbeitsbereich" }
            : new[] { "Queries", "Logbook", "Root cause analysis", "Analysis workspace" },
            Regex.Matches(analyze, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
        Assert.Equal(configurationVisible, groups.ContainsKey(german ? "Konfiguration" : "Configuration"));
        Assert.Equal(adminVisible, groups.ContainsKey("Admin"));
        Assert.DoesNotContain("Live analysis", html);
        Assert.Equal(authentication.IsSql, html.Contains("action=\"/auth/logout\"", StringComparison.Ordinal));
        Assert.Equal(authentication.IsEntra, html.Contains("/.auth/logout?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MenuLoadFailureIsLocalizedAndDoesNotHideTheExistingDashboardLink(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var store = await NavigationStore.CreateAsync();
        store.FailReads = true;
        var authentication = new ApplicationAuthenticationOptions { Type = ApplicationAuthenticationOptions.Sql };
        var collection = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication))
            .AddSingleton(authentication)
            .AddSingleton<IDbContextFactory<AlertDbContext>>(store)
            .AddSingleton<AnalysisDashboardPublishing>()
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<AuthenticationStateProvider>(new TestAuthenticationState(SqlAuthenticationRoles.Operator));
        collection.AddCascadingAuthenticationState();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString()));
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(services.GetRequiredService<UiText>()["Navigation.DashboardListFailed"], html);
        Assert.Contains("href=\"/analysis\"", html);
        Assert.DoesNotContain("Sensitive database failure", html);
    }

    internal sealed class NavigationStore(SqliteConnection connection, DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>, IAsyncDisposable
    {
        public static Guid PublishedId { get; } = Guid.Parse("e1111111-1111-1111-1111-111111111111");
        public bool FailReads { get; set; }
        public AlertDbContext CreateDbContext() => FailReads
            ? throw new InvalidOperationException("Sensitive database failure")
            : new(options);

        public static async Task<NavigationStore> CreateAsync(bool seed = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var store = new NavigationStore(connection, new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlite(connection).Options);
            await using var context = store.CreateDbContext();
            var statement = context.Database.GenerateCreateScript().Split(';')
                .Single(statement => statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal));
            await context.Database.ExecuteSqlRawAsync(statement
                .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            if (seed)
            {
                context.AnalysisDashboardTemplates.AddRange(
                    new() { Id = PublishedId, Name = "Published dashboard", Published = true, Version = 1, UpdatedBy = "original-owner@example.com" },
                    new() { Id = Guid.NewGuid(), Name = "Private dashboard", Published = false, Version = 1, UpdatedBy = "original-owner@example.com" });
                await context.SaveChangesAsync();
            }
            return store;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class TestAuthenticationState(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role)], "test"))));
    }
}
