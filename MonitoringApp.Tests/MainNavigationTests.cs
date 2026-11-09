using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
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
        var authentication = new ApplicationAuthenticationOptions { Type = mode };
        var collection = new ServiceCollection().AddLogging().AddAuthorization(options =>
            ApplicationAuthorization.Configure(options, authentication));
        collection.AddCascadingAuthenticationState();
        collection.AddUiLocalization();
        await using var services = collection
            .AddSingleton(authentication)
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
        Assert.Equal(new[] { german ? "Posteingang" : "Inbox", "Graph", "Dashboards" },
            Regex.Matches(overview, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
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
