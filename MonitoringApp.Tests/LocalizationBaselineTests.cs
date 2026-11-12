using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Layout;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class LocalizationBaselineTests
{
    private static readonly BaselineCases Cases =
        TestCaseLoader.Load<BaselineCases>("localization-baseline.json");
    private static readonly AlertPromptTestCases PromptCases =
        TestCaseLoader.Load<AlertPromptTestCases>("alert-agent-prompt.json");
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    private static readonly IReadOnlyDictionary<string, JsonObject> DiagnosticPayloads =
        TestCaseLoader.Load<JsonArray>("correlated-alerts.json")
            .Select(node => node?.AsObject() ?? throw new InvalidOperationException("Alert payload must be an object."))
            .ToDictionary(TestAlertFactory.GetAlertId, StringComparer.Ordinal);

    public static IEnumerable<object[]> UserInterfaceCases =>
        Cases.UserInterface.Select(testCase => new object[] { testCase.Id });

    public static IEnumerable<object[]> PromptScenarios =>
        from testCase in Cases.Prompts
        from culture in new[] { "en-US", "de-DE" }
        select new object[] { testCase.Scenario, culture };

    [Theory]
    [MemberData(nameof(UserInterfaceCases))]
    public async Task PreservesEnglishRenderedText(string id)
    {
        using var culture = new CultureScope("en-US");
        var testCase = Assert.Single(Cases.UserInterface, item => item.Id == id);
        var authentication = new ApplicationAuthenticationOptions { Type = testCase.Mode };
        await using var navigationStore = await MainNavigationTests.NavigationStore.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication));
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddUiLocalization();
        builder.Services.AddSingleton(authentication);
        builder.Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<AlertDbContext>>(navigationStore);
        builder.Services.AddScoped<AnalysisDashboardPublishing>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentUserIdentity>();
        builder.Services.AddScoped<NavigationManager, BaselineNavigation>();
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => new BaselineAuthentication(testCase.Role));
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var component = testCase.Component switch
        {
            "Navigation" => typeof(MainLayout),
            "Login" => typeof(Login),
            "AccessDenied" => typeof(AccessDenied),
            _ => throw new InvalidOperationException($"Unknown baseline component: {testCase.Component}")
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync(component)).ToHtmlString());
        // The additive AP2 selector is tested independently; retain the frozen existing page-text baseline.
        html = Regex.Replace(html, @"<section\b[^>]*\bdata-language-selector\b.*?</section>", "", RegexOptions.Singleline);
        if (testCase.Component == "Navigation")
        {
            // Direct publication links are covered independently; retain the frozen surrounding navigation baseline.
            html = Regex.Replace(html, @"<details class=""navigation-dashboard-group"">.*?</details>",
                "<a href=\"/analysis\">Dashboards</a>", RegexOptions.Singleline);
            Assert.Equal(authentication.IsOpen || testCase.Role is SqlAuthenticationRoles.Operator or SqlAuthenticationRoles.Admin,
                html.Contains("href=\"/kql\"", StringComparison.Ordinal));
            html = Regex.Replace(html, @"<a\b[^>]*href=""/kql""[^>]*>.*?</a>", "", RegexOptions.Singleline);
        }
        var text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ")), @"\s+", " ").Trim();
        Assert.Equal(testCase.Text, text);
    }

    [Theory]
    [MemberData(nameof(PromptScenarios))]
    public void PreservesCompleteEnglishPrompt(string scenario, string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var expected = Assert.Single(Cases.Prompts, item => item.Scenario == scenario);
        var prompt = BuildPrompt(scenario).Replace("\r\n", "\n", StringComparison.Ordinal);
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        Assert.True(expected.Sha256 == actual, $"Prompt baseline '{scenario}' SHA256: {actual}");
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void TechnicalContractsRemainInvariant(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        Assert.Equal("2026-11-05T14:30", AlertTimeZones.FormatWallClock(new DateTime(2026, 11, 5, 14, 30, 0)));
        Assert.Equal(new DateTime(2026, 11, 5, 14, 30, 0), AlertTimeZones.ParseWallClock("2026-11-05T14:30"));
        Assert.Equal(new[] { "Reader", "Operator", "Admin" }, SqlAuthenticationRoles.All);
        Assert.Equal("incident-analysis-result-v1", AgentAnalysisResultContract.CurrentVersion);
        Assert.Equal("incident-analysis-v4", AlertAgentPrompt.SnapshotPromptVersion);
        foreach (var baseline in Cases.Prompts.Where(item => item.Scenario.StartsWith("incident-", StringComparison.Ordinal)))
        {
            var prompt = BuildPrompt(baseline.Scenario).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(baseline.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))));
        }
    }

    private static string BuildPrompt(string scenario) => scenario switch
    {
        "legacy-default" => AlertAgentPrompt.Build(
            [TestAlertFactory.FromPrompt(PromptCases.DefaultAlert)], PromptCases.GeneratedAt,
            new AlertAgentPromptOptions(), Presenter),
        "legacy-excluded" => AlertAgentPrompt.Build(
            [TestAlertFactory.FromPrompt(PromptCases.DefaultAlert)], PromptCases.GeneratedAt,
            PromptCases.ExcludedOptions),
        "legacy-diagnostics" => AlertAgentPrompt.Build(
            DiagnosticAlerts(), PromptCases.GeneratedAt, new AlertAgentPromptOptions(), Presenter),
        "incident-minimal" => AlertAgentPrompt.Build(new IncidentAnalysisSnapshot(
            PromptCases.GeneratedAt,
            new IncidentAnalysisScope(IncidentAnalysisScopeType.Incident, "baseline-incident"),
            PromptCases.GeneratedAt.AddDays(-7), PromptCases.GeneratedAt, 100, 120_000,
            [], [], [], [],
            new IncidentGraphContext(AlertGraphLayer.Subscription, AlertGraphLayer.Domain, AlertGraphLayer.Target, "baseline", []),
            [], [], [], ["No telemetry in this deterministic baseline fixture."], [], [])),
        "incident-evidence" => AlertAgentPrompt.Build(BuildEvidenceSnapshot()),
        _ => throw new InvalidOperationException($"Unknown prompt baseline scenario: {scenario}")
    };

    private static AlertRecord[] DiagnosticAlerts() => PromptCases.DiagnosticAlertIds
        .Select((id, index) => TestAlertFactory.FromCommonPayload(DiagnosticPayloads[id]) with
        {
            Id = new Guid(index + 1, 0, 0, new byte[8])
        }).ToArray();

    private static IncidentAnalysisSnapshot BuildEvidenceSnapshot()
    {
        var alerts = DiagnosticAlerts();
        var generatedAt = alerts.Max(alert => alert.ReceivedAt).AddMinutes(5);
        return new IncidentAnalysisSnapshotBuilder(Presenter, new AlertRuleEvaluator(Presenter)).Build(
            alerts, alerts,
            alerts.Select(alert => new AlertGraphRecord(
                alert.Id, alert.ReceivedAt, alert.AlertId, alert.MonitorCondition, string.Empty, alert.Name,
                alert.SubscriptionId, alert.ResourceGroup, alert.TargetName, alert.SiteName, "corp.example.test", "Domain Controller")),
            [], [], generatedAt,
            new IncidentAnalysisScope(IncidentAnalysisScopeType.Incident, "baseline-diagnostic-incident"),
            AlertGraphLayer.Subscription, AlertGraphLayer.Domain, AlertGraphLayer.Target);
    }

    private sealed class BaselineCases
    {
        public IReadOnlyList<UserInterfaceCase> UserInterface { get; init; } = [];
        public IReadOnlyList<PromptCase> Prompts { get; init; } = [];
    }

    private sealed class UserInterfaceCase
    {
        public string Id { get; init; } = string.Empty;
        public string Component { get; init; } = string.Empty;
        public string Mode { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
    }

    private sealed class PromptCase
    {
        public string Scenario { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class BaselineNavigation : NavigationManager
    {
        public BaselineNavigation() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class BaselineAuthentication(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(role.Length == 0
                ? new ClaimsIdentity()
                : new ClaimsIdentity([new Claim(ClaimTypes.Name, "baseline-user"), new Claim(ClaimTypes.Role, role)], "baseline"))));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
