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
    [InlineData(SqlAuthenticationRoles.Reader, false, false)]
    [InlineData(SqlAuthenticationRoles.Operator, true, false)]
    [InlineData(SqlAuthenticationRoles.Admin, true, true)]
    public async Task GroupsOverviewAndAnalysisWithoutChangingRoleRestrictedNavigation(
        string role, bool configurationVisible, bool adminVisible)
    {
        var collection = new ServiceCollection().AddLogging().AddAuthorization(options =>
        {
            options.AddPolicy("OperatorAccess", policy => policy.RequireRole(
                SqlAuthenticationRoles.Operator, SqlAuthenticationRoles.Admin));
            options.AddPolicy("AdminAccess", policy => policy.RequireRole(SqlAuthenticationRoles.Admin));
        });
        collection.AddCascadingAuthenticationState();
        await using var services = collection
            .AddSingleton(new ApplicationAuthenticationOptions { Type = ApplicationAuthenticationOptions.Open })
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<AuthenticationStateProvider>(new TestAuthenticationState(role))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString());

        var groups = Regex.Matches(html,
            """class="navigation-group-toggle"[^>]*>(?<title>[^<]+)</button>\s*<div class="navigation-submenu">(?<links>.*?)</div>""",
            RegexOptions.Singleline).ToDictionary(
                match => match.Groups["title"].Value,
                match => match.Groups["links"].Value);
        var overview = groups["Overview"];
        Assert.Equal(new[] { "Inbox", "Graph", "Dashboards" },
            Regex.Matches(overview, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
        Assert.Contains("href=\"/\"", overview);
        Assert.Contains("href=\"/graph\"", overview);
        Assert.Contains("href=\"/analysis\"", overview);

        var analyze = groups["Analyze"];
        Assert.Equal(new[] { "Queries", "Logbook", "Root cause analysis", "Analysis workspace" },
            Regex.Matches(analyze, "<a\\b[^>]*>([^<]+)</a>").Select(match => match.Groups[1].Value));
        Assert.Equal(configurationVisible, groups.ContainsKey("Configuration"));
        Assert.Equal(adminVisible, groups.ContainsKey("Admin"));
        Assert.DoesNotContain("Live analysis", html);
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
