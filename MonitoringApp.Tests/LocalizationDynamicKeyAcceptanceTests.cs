using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class LocalizationDynamicKeyAcceptanceTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<object[]> DirectEnumFamilies =>
    [
        [typeof(AnalysisQueryPublicationCode), "Analysis.Publication."],
        [typeof(QueryTemplateValidationCode), "QueryTemplate.Validation."],
        [typeof(AnalysisQueryValidationCode), "AnalysisQuery.Validation."],
        [typeof(AnalysisValidationCode), "Analysis.Validation."],
        [typeof(AgentAnalysisExecutionDisabledCode), "AgentAnalysis.Disabled."],
        [typeof(ScheduledQueryDataCode), "ScheduledQueries.Data."],
        [typeof(ScheduledQueryValidationCode), "ScheduledQueries.Validation."],
        [typeof(AgentAnalysisScopeDeniedCode), "Prompt.Job."],
        [typeof(AgentAnalysisJobValidationCode), "Prompt.Job."],
        [typeof(IncidentAnalysisSnapshotCode), "Prompt.Snapshot."],
        [typeof(AgentAnalysisReviewValidationCode), "Prompt.Review."],
        [typeof(AgentAnalysisResultCode), "Prompt.Result."]
    ];

    [Theory]
    [MemberData(nameof(DirectEnumFamilies))]
    public void EveryDirectEnumKeyParticipatesInEnglishStartupValidation(Type codeType, string prefix)
    {
        Assert.True(codeType.IsEnum);
        var names = Enum.GetNames(codeType);
        Assert.NotEmpty(names);
        foreach (var name in names)
            RequireEnglishStartupKey(prefix + name);
        UiResources.ValidateEnglish();
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void DynamicChoicesUseRegisteredKeysWithoutRewritingTechnicalOptions(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        var timeZones = AlertTimeZones.Options.Select(option => (option.Id, option.Label, option.TimeZone)).ToArray();
        var statuses = RcaStatuses.All.ToArray();
        var display = new InboxTargetOutputDisplayOptions
        {
            CountColors =
            [
                new() { MinimumCount = 1, Color = "green" },
                new() { MinimumCount = 2, Color = "yellow" },
                new() { MinimumCount = 3, Color = "orange" },
                new() { MinimumCount = 4, Color = "red" },
                new() { MinimumCount = 5, Color = "gray" }
            ]
        };
        Assert.Empty(display.Validate());
        var originalDisplay = JsonSerializer.Serialize(display);
        var page = new Home();
        typeof(Home).GetProperty("Text", Flags)!.SetValue(page, text);
        foreach (var option in AlertTimeZones.Options)
        {
            var key = $"Inbox.TimeZone.{option.Id}";
            RequireEnglishStartupKey(key);
            var label = Assert.IsType<string>(typeof(Home).GetMethod("TimeZoneLabel", Flags)!.Invoke(page, [option]));
            Assert.Equal(text[key], label);
            Assert.NotEqual(key, label);
            if (culture == "en-US") Assert.Equal(option.Label, label);
        }
        foreach (var status in RcaStatuses.All)
        {
            var key = $"Inbox.RcaStatus.{status}";
            RequireEnglishStartupKey(key);
            Assert.NotEqual(key, text[key]);
        }
        var legend = display.Legend();
        Assert.DoesNotContain(legend, entry => entry.Color == InboxTargetColor.MissingHeartbeat);
        Assert.Equal(6, legend.Select(entry => entry.Color).Distinct().Count());
        foreach (var entry in legend)
        {
            var key = $"Inbox.TargetColor.{entry.Color}";
            RequireEnglishStartupKey(key);
            Assert.NotEqual(key, text[key]);
        }
        Assert.Equal(timeZones, AlertTimeZones.Options.Select(option => (option.Id, option.Label, option.TimeZone)));
        Assert.Equal(statuses, RcaStatuses.All);
        Assert.Equal(originalDisplay, JsonSerializer.Serialize(display));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> ScopeCases =>
        from culture in new[] { "en-US", "de-DE" }
        from scope in new[] { IncidentAnalysisScopeType.Incident, IncidentAnalysisScopeType.Target }
        from count in new[] { 0, 1, 2 }
        select new object[] { culture, scope, count };

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void PersistableScopeValuesRemainNeutralAndOriginal(string culture, IncidentAnalysisScopeType scope, int count)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var source = TestCaseLoader.Load<AlertPromptTestCases>("alert-agent-prompt.json").DefaultAlert;
        var now = DateTimeOffset.UtcNow;
        var alerts = Enumerable.Range(0, count).Select(index => TestAlertFactory.FromPrompt(source) with
        {
            ReceivedAt = now.AddSeconds(-index),
            AlertId = $"Event<{index}> & original"
        }).ToArray();
        var original = JsonSerializer.Serialize(alerts);
        var page = new Prompt();
        typeof(Prompt).GetField("alerts", Flags)!.SetValue(page, alerts);
        typeof(Prompt).GetField("scope", Flags)!.SetValue(page, "all");
        typeof(Prompt).GetField("analysisScopeType", Flags)!.SetValue(page, scope.ToString());
        var selected = Assert.IsType<HashSet<Guid>>(typeof(Prompt).GetField("selectedAlertIds", Flags)!.GetValue(page));
        selected.UnionWith(alerts.Select(alert => alert.Id));
        var expected = scope == IncidentAnalysisScopeType.Target
            ? alerts.FirstOrDefault()?.TargetName ?? "No target selected"
            : count == 1 ? "Event<0> & original" : $"{count} selected alert events";

        var value = Assert.IsType<string>(typeof(Prompt).GetMethod("ScopeValue", Flags)!.Invoke(page, null));

        Assert.Equal(expected, value);
        Assert.Equal(original, JsonSerializer.Serialize(alerts));
        Assert.Equal(alerts.Select(alert => alert.Id).Order(), selected.Order());
        Assert.Equal(scope.ToString(), typeof(Prompt).GetField("analysisScopeType", Flags)!.GetValue(page));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static void RequireEnglishStartupKey(string key)
    {
        Assert.Contains(key, UiResources.RequiredKeys);
        var english = UiResources.GetEnglish(key);
        Assert.False(string.IsNullOrWhiteSpace(english), key);
        Assert.NotEqual(key, english);
    }
}
