using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class SettingsLocalizationTests
{
    private static string GraphWithDefault(string json)
    {
        var graph = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        graph["SLocations"] = true;
        return Canonical(graph.ToJsonString());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SLocationsSavePreservesQuotedFalseAndRejectsInvalidValuesWithoutWriting(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render();
        var json = fixture.Get<string>(fixture.Page, "alertGraphJson");
        var graph = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        graph["SLocations"] = "false";
        await fixture.Set(fixture.Page, "alertGraphJson", graph.ToJsonString());
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Contains(fixture.Text["AdminSettings.Saved"], fixture.Decoded);
        var saved = await fixture.Service.LoadAsync();
        Assert.False(DatabaseSettingsLoader.ParseRequired(saved).AlertGraph.SLocations);
        await fixture.Invoke(fixture.Page, "LoadAsync");
        using var editor = JsonDocument.Parse(fixture.Get<string>(fixture.Page, "alertGraphJson"));
        Assert.Equal("false", editor.RootElement.GetProperty("SLocations").GetString());
        graph["SLocations"] = "invalid";
        await fixture.Set(fixture.Page, "alertGraphJson", graph.ToJsonString());
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Contains(fixture.Text.Format("AdminSettings.Validation.JsonInvalid", fixture.Text["AdminSettings.AlertGraph"]), fixture.Decoded);
        Assert.Null(fixture.Get<string?>(fixture.Page, "statusMessage"));
        Assert.Equal(saved.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("{\"SiteLocations\":[],\"Unknown\":{\"Keep\":7}}", true)]
    [InlineData("{\"SiteLocations\":[],\"SLocations\":false}", false)]
    [InlineData("{\"SiteLocations\":[],\"slocations\":\"false\"}", false)]
    public void GraphEditorExposesMissingSLocationsAndPreservesExistingSettings(string json, bool enabled)
    {
        var format = typeof(AdminSettings).GetMethod("FormatGraphJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        var formatted = Assert.IsType<string>(format.Invoke(null, [json]));
        using var original = JsonDocument.Parse(json);
        using var updated = JsonDocument.Parse(formatted);
        foreach (var property in original.RootElement.EnumerateObject())
            Assert.Equal(JsonSerializer.Serialize(property.Value), JsonSerializer.Serialize(updated.RootElement.GetProperty(property.Name)));
        Assert.Equal(enabled, DatabaseSettingsLoader.Deserialize<AlertGraphOptions>(DatabaseSettingsLoader.AlertGraph, formatted).SLocations);
        if (!original.RootElement.EnumerateObject().Any(property => property.Name.Equals("SLocations", StringComparison.OrdinalIgnoreCase)))
            Assert.True(updated.RootElement.GetProperty("SLocations").GetBoolean());
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    [InlineData("en-US", 3)]
    [InlineData("de-DE", 3)]
    [InlineData("en-US", 4)]
    [InlineData("de-DE", 4)]
    [InlineData("en-US", 5)]
    [InlineData("de-DE", 5)]
    [InlineData("en-US", 6)]
    [InlineData("de-DE", 6)]
    [InlineData("en-US", 7)]
    [InlineData("de-DE", 7)]
    [InlineData("en-US", 8)]
    [InlineData("de-DE", 8)]
    [InlineData("en-US", 9)]
    [InlineData("de-DE", 9)]
    public async Task ActiveDisabledStateUsesWholeMessagesAndDoesNotPersistMetadata(string culture, int scenario)
    {
        using var selection = new UiSelection(culture);
        var state = AgentAnalysisOrchestratorTests.CreateDisabledState(scenario);
        await using var fixture = await Fixture.Create(state);
        var before = await fixture.Service.LoadAsync();
        await fixture.Render();
        Assert.Contains(fixture.Text[$"AgentAnalysis.Disabled.{state.DisabledCode}"], fixture.Decoded);
        Assert.DoesNotContain("Hidden:Endpoint", fixture.Html);
        Assert.DoesNotContain("Hidden:Credential", fixture.Html);
        Assert.False(fixture.Active.IsEnabled);
        Assert.Equal(before.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
        foreach (var unknown in new[]
        {
            state with { DisabledCode = null, DisabledReason = "SECRET <legacy>" },
            state with { DisabledCode = (AgentAnalysisExecutionDisabledCode)999, DisabledReason = "SECRET <unknown>" }
        })
            Assert.Equal(fixture.Text["AgentAnalysis.Disabled.Unknown"],
                AgentAnalysisErrorPresentation.DisabledReason(unknown, fixture.Text));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SettingsAndEmbeddedPanelsTranslateWithoutChangingJsonOrTechnicalValues(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.Service.LoadAsync();
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await fixture.Render();
        foreach (var key in new[] { "AdminSettings.Title", "AdminSettings.RestartTitle",
            "AdminSettings.ActiveHandoff", "AdminSettings.SelectedHandoff",
            "DatabaseReset.Title", "DatabaseReset.BackupWarning", "LegacyAlerts.Title" })
            Assert.Contains(fixture.Text[key], fixture.Decoded);
        foreach (var value in new[] { "open", "sql", "entra", "CopilotPrompt", "TeamsWorkflow", "Api" })
            Assert.Contains($"value=\"{value}\"", fixture.Html);
        foreach (var value in new[] { "SiteLocations", "DisplayName", "Latitude", "Longitude", "Aliases",
            "CountColors", "MinimumCount", "Color", "SuppressedColor", "CriticalOutageAlwaysRed",
            "BoldOutageNames", "ShowDomain", "OpenUnassignedColor", "ClosedColor", "OtherColor",
            "Workspace", "Domain", "Site", "Role", "null", "green", "yellow", "orange", "red", "gray" })
            Assert.Contains(value, fixture.Decoded);
        Assert.Equal(6, Regex.Matches(fixture.Html, "<textarea\\b").Count);
        var editor = fixture.Get<string>(fixture.Page, "alertGraphJson");
        Assert.Equal(GraphWithDefault(original[DatabaseSettingsLoader.AlertGraph]), Canonical(editor));
        Assert.Equal("Original <site> & label", JsonDocument.Parse(editor).RootElement
            .GetProperty("SiteLocations")[0].GetProperty("DisplayName").GetString());
        Assert.Equal(original.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", "TeamsWorkflow", false)]
    [InlineData("de-DE", "TeamsWorkflow", false)]
    [InlineData("en-US", "Api", false)]
    [InlineData("de-DE", "Api", false)]
    [InlineData("en-US", "TeamsWorkflow", true)]
    [InlineData("de-DE", "TeamsWorkflow", true)]
    [InlineData("en-US", "Api", true)]
    [InlineData("de-DE", "Api", true)]
    public async Task UnregisteredSavedProfileCanBeRetainedWithoutEnablingExecutionOrExposingConfiguration(
        string culture, string mode, bool populated)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        if (populated)
        {
            fixture.Profiles.TeamsWorkflows["registered-teams"] = new()
                { Approved = true, EndpointConfigurationKey = "Hidden:Endpoint" };
            fixture.Profiles.Apis["registered-api"] = new()
            {
                Approved = false, Provider = "Original <Provider> & label", Deployment = "hidden-deployment",
                ApiVersion = "hidden-version", Authentication = "hidden-authentication",
                Region = "hidden-region", EndpointConfigurationKey = "Hidden:Endpoint",
                CredentialConfigurationKey = "Hidden:Credential"
            };
        }
        await fixture.Render();
        var modeIsTeams = mode == AgentAnalysisExecutionModes.TeamsWorkflow;
        await fixture.Set(fixture.Page, "agentAnalysisMode", mode);
        await fixture.Set(fixture.Page, modeIsTeams ? "teamsWorkflowProfile" : "apiProfile", "Original.Profile");
        var selectedState = AgentAnalysisExecutionState.Create(new AgentAnalysisExecutionOptions
        {
            Mode = mode, TeamsWorkflowProfile = modeIsTeams ? "Original.Profile" : null,
            ApiProfile = modeIsTeams ? null : "Original.Profile"
        }, fixture.Profiles);
        Assert.Contains(fixture.Text[$"AgentAnalysis.Disabled.{selectedState.DisabledCode}"], fixture.Decoded);
        Assert.Contains("value=\"Original.Profile\"", fixture.Html);
        Assert.Contains(fixture.Text.Format("AdminSettings.UnregisteredProfile", "Original.Profile"), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("AdminSettings.ActiveState", "CopilotPrompt"), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("AdminSettings.SelectedState", mode), fixture.Decoded);
        Assert.Contains(fixture.Text[modeIsTeams ? "AdminSettings.TeamsProfileHint" : "AdminSettings.ApiProfileHint"], fixture.Decoded);
        if (!populated)
            Assert.Contains(fixture.Text[modeIsTeams ? "AdminSettings.NoTeamsProfiles" : "AdminSettings.NoApiProfiles"], fixture.Decoded);
        else
        {
            var profileLabel = modeIsTeams
                ? fixture.Text.Format("AdminSettings.TeamsProfileOption", "registered-teams", fixture.Text["AdminSettings.Approved"])
                : fixture.Text.Format("AdminSettings.ApiProfileOption", "registered-api", "Original <Provider> & label", fixture.Text["AdminSettings.NotApproved"]);
            Assert.Contains(profileLabel, fixture.Decoded);
            if (!modeIsTeams)
                Assert.Contains("Original &lt;Provider&gt; &amp; label", fixture.Html);
        }
        foreach (var hidden in new[] { "Hidden:Endpoint", "Hidden:Credential", "hidden-deployment",
            "hidden-version", "hidden-authentication", "hidden-region" })
            Assert.DoesNotContain(hidden, fixture.Html);
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Contains(fixture.Text["AdminSettings.Saved"], fixture.Decoded);
        var stored = DatabaseSettingsLoader.ParseRequired(await fixture.Service.LoadAsync()).AgentAnalysisExecution;
        Assert.Equal(mode, stored.Mode);
        Assert.Equal("Original.Profile", modeIsTeams ? stored.TeamsWorkflowProfile : stored.ApiProfile);
        Assert.False(AgentAnalysisExecutionState.Create(stored, fixture.Profiles).IsEnabled);
        Assert.Equal("CopilotPrompt", fixture.Active.Mode);
        Assert.True(fixture.Active.IsEnabled);
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.Equal("Original.Profile", fixture.Get<string>(fixture.Page, modeIsTeams ? "teamsWorkflowProfile" : "apiProfile"));
        Assert.Contains("value=\"Original.Profile\"", fixture.Html);
        if (populated)
        {
            await fixture.Set(fixture.Page, modeIsTeams ? "teamsWorkflowProfile" : "apiProfile",
                modeIsTeams ? "registered-teams" : "registered-api");
            await fixture.Invoke(fixture.Page, "SaveAsync");
            var registered = DatabaseSettingsLoader.ParseRequired(await fixture.Service.LoadAsync()).AgentAnalysisExecution;
            Assert.False(AgentAnalysisExecutionState.Create(registered, fixture.Profiles).IsEnabled);
            Assert.Equal("CopilotPrompt", fixture.Active.Mode);
            Assert.True(fixture.Active.IsEnabled);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task JsonRoundTripAndResetChangesPreserveOriginalLabelsNumbersAndUnrelatedSettings(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.Service.LoadAsync();
        await fixture.Render();
        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":14}""");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(14, DatabaseSettingsLoader.ParseRequired(await fixture.Service.LoadAsync()).AlertHistory.Days);
        Assert.Contains(fixture.Text["AdminSettings.Saved"], fixture.Decoded);
        var saved = await fixture.Service.LoadAsync();
        foreach (var name in new[] { DatabaseSettingsLoader.AlertGraph, DatabaseSettingsLoader.AlertSeverityDisplay,
            DatabaseSettingsLoader.InboxTargetOutputDisplay, DatabaseSettingsLoader.RcaDisplay, DatabaseSettingsLoader.DashboardFilters })
            Assert.Equal(name == DatabaseSettingsLoader.AlertGraph ? GraphWithDefault(original[name]) : Canonical(original[name]), Canonical(saved[name]));
        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":21}""");
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.Equal(14, JsonDocument.Parse(fixture.Get<string>(fixture.Page, "alertHistoryJson")).RootElement.GetProperty("Days").GetInt32());
        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":0}""");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Contains(fixture.Text.Format("AdminSettings.Validation.InvalidValues", fixture.Text["AdminSettings.AlertHistory"]), fixture.Decoded);
        Assert.Null(fixture.Get<string?>(fixture.Page, "statusMessage"));
        Assert.Equal(saved.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
        await using var context = fixture.Database.CreateDbContext();
        Assert.Equal("original-unrelated", (await context.Settings.SingleAsync(setting => setting.Name == "Unrelated")).JsonValue);
        Assert.Empty(await context.AuthenticationUsers.ToArrayAsync());
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task MaintenanceConfirmationTokensRemainExactAndDialogsNeverWrite(string culture, bool legacy)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.Service.LoadAsync();
        await fixture.Render();
        ComponentBase panel = legacy ? fixture.Legacy : fixture.Reset;
        var token = legacy ? "RESOLVE" : "RESET";
        var buttonKey = legacy ? "LegacyAlerts.Confirm" : "DatabaseReset.Confirm";
        await fixture.Set(panel, "showConfirmation", true);
        if (legacy)
        {
            await fixture.Set(panel, "candidateCount", 1);
            await fixture.Set(panel, "previewRequired", false);
            Assert.Contains(fixture.Text.Plural("LegacyAlerts.ConfirmTitle", 1), fixture.Decoded);
            Assert.Contains("Resolved", fixture.Decoded);
        }
        else
        {
            Assert.Contains("<strong>original-&lt;database&gt;</strong>", fixture.Html);
            Assert.Contains("<strong>original-&lt;server&gt;</strong>", fixture.Html);
        }
        Assert.Contains(token, fixture.Decoded);
        foreach (var value in new[] { "", token.ToLowerInvariant(), " " + token, token + " ", token })
        {
            await fixture.Set(panel, "confirmation", value);
            var button = Regex.Match(fixture.Decoded,
                $@"<button\b(?<attrs>[^>]*)>\s*{Regex.Escape(fixture.Text[buttonKey])}\s*</button>");
            Assert.True(button.Success);
            Assert.Equal(value != token, button.Groups["attrs"].Value.Contains("disabled", StringComparison.Ordinal));
        }
        await fixture.Set(panel, legacy ? "busy" : "isResetting", true);
        var busyKey = legacy ? "LegacyAlerts.Resolving" : "DatabaseReset.Resetting";
        Assert.Matches($@"<button\b[^>]*disabled[^>]*>\s*{Regex.Escape(fixture.Text[busyKey])}\s*</button>", fixture.Decoded);
        Assert.Equal(original.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("en-US", 0, "0 legacy alerts", "0 default alert rules")]
    [InlineData("en-US", 1, "1 legacy alert", "1 default alert rule")]
    [InlineData("en-US", 2, "2 legacy alerts", "2 default alert rules")]
    [InlineData("de-DE", 0, "0 Altalarme", "0 Standard-Alarmregeln")]
    [InlineData("de-DE", 1, "1 Altalarm", "1 Standard-Alarmregel")]
    [InlineData("de-DE", 2, "2 Altalarme", "2 Standard-Alarmregeln")]
    [InlineData("de-DE", 1234, "1.234 Altalarme", "1.234 Standard-Alarmregeln")]
    public async Task MaintenanceWholeMessagesHaveCorrectPluralAndDisplayNumbers(
        string culture, int count, string alerts, string rules)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        Assert.Contains(alerts, fixture.Text.Plural("LegacyAlerts.ConfirmTitle", count));
        Assert.Contains(alerts, fixture.Text.Plural("LegacyAlerts.Completed", count));
        Assert.Contains(rules, fixture.Text.Plural("DatabaseReset.Completed", count));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SettingsErrorsKeepDraftsAndRequireExplicitRecovery(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render();
        var original = await fixture.Service.LoadAsync();
        foreach (var item in new[]
        {
            ("""{"Days":0}""", "AdminSettings.Validation.InvalidValues", true),
            ("null", "AdminSettings.Validation.JsonNull", true),
            ("""{"Days":"wrong"}""", "AdminSettings.Validation.JsonInvalid", true),
            ("{", "AdminSettings.InvalidJson", false)
        })
        {
            await fixture.Set(fixture.Page, "alertHistoryJson", item.Item1);
            await fixture.Invoke(fixture.Page, "SaveAsync");
            var expected = item.Item3
                ? fixture.Text.Format(item.Item2, fixture.Text["AdminSettings.AlertHistory"])
                : fixture.Text[item.Item2];
            Assert.Equal(expected, fixture.Get<string>(fixture.Page, "errorMessage"));
            Assert.Contains(expected, fixture.Decoded);
            Assert.Null(fixture.Get<string?>(fixture.Page, "statusMessage"));
            Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));
            Assert.Equal(item.Item1, fixture.Get<string>(fixture.Page, "alertHistoryJson"));
            Assert.Equal(original.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
            Assert.Equal(LogLevel.Warning, fixture.Logger.Entries.Last().Level);
        }

        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":14}""");
        await fixture.Set(fixture.Page, "authenticationType", "sql");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(fixture.Text["AdminSettings.Validation.SqlUserRequired"], fixture.Get<string>(fixture.Page, "errorMessage"));
        Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));
        await fixture.Set(fixture.Page, "authenticationType", "open");

        foreach (var code in new[] { DatabaseSettingsValidationCode.UnknownSettings, DatabaseSettingsValidationCode.MissingSettings })
        {
            var error = new DatabaseSettingsValidationException(code, "private-validation-diagnostic");
            fixture.Factory.BeforeNext = () => throw error;
            await fixture.Invoke(fixture.Page, "SaveAsync");
            Assert.Equal(fixture.Text[$"AdminSettings.Validation.{code}"], fixture.Get<string>(fixture.Page, "errorMessage"));
            Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));
            Assert.Equal(LogLevel.Warning, fixture.Logger.Entries.Last().Level);
            Assert.Same(error, fixture.Logger.Entries.Last().Error);
            Assert.DoesNotContain("private-validation-diagnostic", fixture.Decoded);
        }

        var draft = new[] { "authenticationType", "alertHistoryJson", "alertGraphJson", "alertSeverityDisplayJson",
            "inboxTargetOutputDisplayJson", "rcaDisplayJson", "dashboardFiltersJson", "correlateByDomain",
            "correlateByRole", "correlateBySite", "correlateByResourceGroup", "agentAnalysisMode",
            "teamsWorkflowProfile", "apiProfile" }.ToDictionary(field => field, field => fixture.Get<object>(fixture.Page, field));
        // A late parse failure must not publish the earlier fields from the same reload.
        await using (var context = fixture.Database.CreateDbContext())
        {
            (await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.Authentication)).JsonValue = """{"Type":"entra"}""";
            (await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.AgentAnalysisExecution)).JsonValue = "{";
            await context.SaveChangesAsync();
        }
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.Equal("open", fixture.Get<string>(fixture.Page, "authenticationType"));
        Assert.Equal("""{"Days":14}""", fixture.Get<string>(fixture.Page, "alertHistoryJson"));
        foreach (var field in draft)
            Assert.Equal(field.Value, fixture.Get<object>(fixture.Page, field.Key));
        Assert.Equal(fixture.Text.Format("AdminSettings.Validation.JsonInvalid", fixture.Text["AdminSettings.AgentMode"]),
            fixture.Get<string>(fixture.Page, "errorMessage"));
        Assert.True(fixture.Get<bool>(fixture.Page, "reloadRequired"));
        Assert.Equal(LogLevel.Error, fixture.Logger.Entries.Last().Level);

        await using (var context = fixture.Database.CreateDbContext())
        {
            (await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.AgentAnalysisExecution)).JsonValue =
                original[DatabaseSettingsLoader.AgentAnalysisExecution];
            await context.SaveChangesAsync();
        }
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.Equal("entra", fixture.Get<string>(fixture.Page, "authenticationType"));
        Assert.Equal(7, JsonDocument.Parse(fixture.Get<string>(fixture.Page, "alertHistoryJson")).RootElement.GetProperty("Days").GetInt32());
        Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));

        foreach (var error in new Exception[]
        {
            new IOException("private-provider-diagnostic"),
            new JsonException("private-provider-diagnostic"),
            new InvalidOperationException("Invalid setting 'AlertHistory': private-provider-diagnostic"),
            new DatabaseSettingsValidationException((DatabaseSettingsValidationCode)999, "private-provider-diagnostic"),
            new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.InvalidValues, "private-provider-diagnostic", "unknown-setting"),
            new InvalidOperationException("private-provider-diagnostic",
                new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.InvalidValues, "wrapped", DatabaseSettingsLoader.AlertHistory))
        })
        {
            await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":14}""");
            fixture.Factory.BeforeNext = () => throw error;
            await fixture.Invoke(fixture.Page, "SaveAsync");
            Assert.Equal(fixture.Text["AdminSettings.SaveUnconfirmed"], fixture.Get<string>(fixture.Page, "errorMessage"));
            Assert.True(fixture.Get<bool>(fixture.Page, "reloadRequired"));
            Assert.DoesNotContain("private-provider-diagnostic", fixture.Decoded);
            Assert.Equal(LogLevel.Error, fixture.Logger.Entries.Last().Level);
            Assert.Same(error, fixture.Logger.Entries.Last().Error);
            var calls = fixture.Factory.Calls;
            await fixture.Invoke(fixture.Page, "SaveAsync");
            Assert.Equal(calls, fixture.Factory.Calls);
            Assert.Equal(fixture.Text["AdminSettings.ReloadRequired"], fixture.Get<string>(fixture.Page, "errorMessage"));
            fixture.Factory.BeforeNext = () => throw new IOException("recovery-read-diagnostic");
            await fixture.Invoke(fixture.Page, "LoadAsync");
            Assert.True(fixture.Get<bool>(fixture.Page, "reloadRequired"));
            Assert.Equal("""{"Days":14}""", fixture.Get<string>(fixture.Page, "alertHistoryJson"));
            Assert.Equal(fixture.Text["AdminSettings.LoadFailed"], fixture.Get<string>(fixture.Page, "errorMessage"));
            await fixture.Invoke(fixture.Page, "LoadAsync");
            Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));
            Assert.Null(fixture.Get<string?>(fixture.Page, "errorMessage"));
        }

        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":14}""");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(fixture.Text["AdminSettings.Saved"], fixture.Get<string>(fixture.Page, "statusMessage"));
        Assert.Equal(14, DatabaseSettingsLoader.ParseRequired(await fixture.Service.LoadAsync()).AlertHistory.Days);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SettingsInitialFailureAndBusyHandlersCannotWrite(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        fixture.Factory.BeforeNext = () => throw new IOException("private-initial-load");
        await fixture.Render();
        Assert.False(fixture.Get<bool>(fixture.Page, "hasLoaded"));
        Assert.DoesNotContain("settings-form", fixture.Html);
        Assert.Contains(fixture.Text["AdminSettings.LoadFailed"], fixture.Decoded);
        Assert.DoesNotContain("private-initial-load", fixture.Decoded);
        var calls = fixture.Factory.Calls;
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(calls, fixture.Factory.Calls);
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.True(fixture.Get<bool>(fixture.Page, "hasLoaded"));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeNextAsync = async () =>
        {
            started.SetResult();
            await release.Task;
        };
        var load = fixture.Invoke(fixture.Page, "LoadAsync");
        await started.Task;
        calls = fixture.Factory.Calls;
        await fixture.Invoke(fixture.Page, "LoadAsync");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(calls, fixture.Factory.Calls);
        Assert.True(fixture.Get<bool>(fixture.Page, "isLoading"));
        release.SetResult();
        await load;
        Assert.False(fixture.Get<bool>(fixture.Page, "isLoading"));
        Assert.Null(fixture.Get<string?>(fixture.Page, "statusMessage"));

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeNextAsync = async () =>
        {
            saveStarted.SetResult();
            await saveRelease.Task;
        };
        var save = fixture.Invoke(fixture.Page, "SaveAsync");
        await saveStarted.Task;
        calls = fixture.Factory.Calls;
        await fixture.Invoke(fixture.Page, "LoadAsync");
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.True(fixture.Get<bool>(fixture.Page, "isSaving"));
        Assert.Equal(calls, fixture.Factory.Calls);
        Assert.Matches("<form\\b[^>]*\\binert\\b", fixture.Html);
        saveRelease.SetResult();
        await save;
        Assert.False(fixture.Get<bool>(fixture.Page, "isSaving"));
        Assert.Equal(fixture.Text["AdminSettings.Saved"], fixture.Get<string>(fixture.Page, "statusMessage"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SettingsPostCommitFailureDoesNotRepeatConfirmedDatabaseWrite(string culture)
    {
        using var selection = new UiSelection(culture);
        var commit = new SettingsCommitFault();
        await using var fixture = await Fixture.Create(interceptor: commit);
        await fixture.Render();
        await fixture.Set(fixture.Page, "alertHistoryJson", """{"Days":14}""");
        var commits = commit.Commits;
        commit.Fail = true;
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(commits + 1, commit.Commits);
        Assert.True(fixture.Get<bool>(fixture.Page, "reloadRequired"));
        Assert.Null(fixture.Get<string?>(fixture.Page, "statusMessage"));
        Assert.Equal(fixture.Text["AdminSettings.SaveUnconfirmed"], fixture.Get<string>(fixture.Page, "errorMessage"));
        Assert.Equal("""{"Days":14}""", fixture.Get<string>(fixture.Page, "alertHistoryJson"));
        Assert.Equal(14, DatabaseSettingsLoader.ParseRequired(await fixture.Service.LoadAsync()).AlertHistory.Days);
        await fixture.Invoke(fixture.Page, "SaveAsync");
        Assert.Equal(commits + 1, commit.Commits);
        Assert.DoesNotContain("private-post-commit", fixture.Decoded);
        await fixture.Invoke(fixture.Page, "LoadAsync");
        Assert.False(fixture.Get<bool>(fixture.Page, "reloadRequired"));
        Assert.Equal(14, JsonDocument.Parse(fixture.Get<string>(fixture.Page, "alertHistoryJson")).RootElement.GetProperty("Days").GetInt32());
    }

    private sealed class SettingsCommitFault : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public bool Fail { get; set; }
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            if (Fail) throw new IOException("private-post-commit");
            return Task.CompletedTask;
        }
    }

    private sealed class SettingsFactory(DatabaseTransactionsTests.TestDatabase database) : IDbContextFactory<AlertDbContext>
    {
        public Action? BeforeNext { get; set; }
        public Func<Task>? BeforeNextAsync { get; set; }
        public int Calls { get; private set; }
        public AlertDbContext CreateDbContext() => database.CreateDbContext();
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            var asyncHook = BeforeNextAsync;
            BeforeNextAsync = null;
            if (asyncHook is not null) await asyncHook();
            var hook = BeforeNext;
            BeforeNext = null;
            hook?.Invoke();
            return await database.CreateDbContextAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task MaintenanceRealValidationKeepsNeutralContractsAndSafeUi(string culture, bool legacy)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true, openAuthentication: false);
        await fixture.Render();
        if (legacy) await AddMaintenanceAlert(fixture, "original alert", "");
        var panel = legacy ? (ComponentBase)fixture.Legacy : fixture.Reset;
        var logs = legacy ? fixture.LegacyLogger.Entries : fixture.ResetLogger.Entries;
        var errorField = legacy ? "error" : "errorMessage";
        if (legacy) await fixture.Invoke(panel, "PreviewAsync");
        else await fixture.Invoke(panel, "BeginReset");
        foreach (var token in new[] { "", legacy ? "resolve" : "reset", legacy ? " RESOLVE" : " RESET",
            legacy ? "RESOLVE " : "RESET " })
        {
            await fixture.Set(panel, "confirmation", token);
            await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
            Assert.Equal(fixture.Text[legacy ? "LegacyAlerts.ConfirmationInvalid" : "DatabaseReset.ConfirmationInvalid"],
                fixture.Get<string>(panel, errorField));
            Assert.Equal(LogLevel.Warning, logs.Last().Level);
            Assert.IsType<MaintenanceValidationException>(logs.Last().Error);
            Assert.Equal("", fixture.Get<string>(panel, "confirmation"));
        }
        fixture.Authentication.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Operator")], "test"));
        await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
        await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
        Assert.Equal(fixture.Text["Maintenance.AdminRequired"], fixture.Get<string>(panel, errorField));
        Assert.IsType<MaintenanceAccessDeniedException>(logs.Last().Error);
        await using var context = fixture.Database.CreateDbContext();
        Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        Assert.Empty(await context.AlertRules.ToArrayAsync());

        await using var unconfigured = await Fixture.Create(configured: false);
        await unconfigured.Render();
        panel = legacy ? unconfigured.Legacy : unconfigured.Reset;
        if (legacy)
        {
            await unconfigured.Invoke(panel, "PreviewAsync");
            Assert.True(unconfigured.Get<bool>(panel, "previewRequired"));
        }
        else
        {
            await unconfigured.Invoke(panel, "BeginReset");
            await unconfigured.Set(panel, "confirmation", "RESET");
            await unconfigured.Invoke(panel, "ResetAsync");
        }
        Assert.Equal(unconfigured.Text["Maintenance.DatabaseUnavailable"], unconfigured.Get<string>(panel, errorField));
        var exception = (legacy ? unconfigured.LegacyLogger.Entries : unconfigured.ResetLogger.Entries).Last().Error;
        var validation = Assert.IsType<MaintenanceValidationException>(exception);
        Assert.Equal("The database is not configured. Check the application settings.", validation.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(validation);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LegacyPreviewAndUnknownWriteRequireRecoveryWithoutFalseEmptyOrRepeat(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true);
        await fixture.Render();
        var id = await AddMaintenanceAlert(fixture, "original <alert>", "");
        await fixture.Invoke(fixture.Legacy, "PreviewAsync");
        Assert.Equal(1, fixture.Get<int>(fixture.Legacy, "candidateCount"));
        Assert.True(fixture.Get<bool>(fixture.Legacy, "showConfirmation"));
        Assert.False(fixture.Get<bool>(fixture.Legacy, "previewRequired"));

        fixture.Factory.BeforeNext = () => throw new IOException("private-preview-diagnostic");
        await fixture.Invoke(fixture.Legacy, "PreviewAsync");
        Assert.Equal(1, fixture.Get<int>(fixture.Legacy, "candidateCount"));
        Assert.False(fixture.Get<bool>(fixture.Legacy, "showConfirmation"));
        Assert.True(fixture.Get<bool>(fixture.Legacy, "previewRequired"));
        Assert.Contains(fixture.Text["LegacyAlerts.PreviewFailed"], fixture.Decoded);
        Assert.Null(fixture.Get<string?>(fixture.Legacy, "message"));
        Assert.DoesNotContain(fixture.Text["LegacyAlerts.NoCandidates"], fixture.Decoded);
        Assert.DoesNotContain("private-preview-diagnostic", fixture.Decoded);
        var calls = fixture.Factory.Calls;
        await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
        await fixture.Invoke(fixture.Legacy, "ResolveAsync");
        Assert.Equal(calls, fixture.Factory.Calls);
        await fixture.Invoke(fixture.Legacy, "PreviewAsync");

        foreach (var fault in new Exception[]
        {
            new IOException("private-write-diagnostic"),
            new InvalidOperationException("Type RESOLVE exactly to confirm legacy alert resolution."),
            new UnauthorizedAccessException("private-write-diagnostic"),
            new MaintenanceValidationException((MaintenanceValidationCode)999, "private-write-diagnostic"),
            new MaintenanceValidationException(MaintenanceValidationCode.CommentsTooLong, "private-write-diagnostic"),
            new InvalidOperationException("private-write-diagnostic",
                new MaintenanceValidationException(MaintenanceValidationCode.ConfirmationInvalid, "wrapped"))
        })
        {
            fixture.Factory.BeforeNext = () => throw fault;
            await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
            await fixture.Invoke(fixture.Legacy, "ResolveAsync");
            Assert.Equal(fixture.Text["LegacyAlerts.Unconfirmed"], fixture.Get<string>(fixture.Legacy, "error"));
            Assert.True(fixture.Get<bool>(fixture.Legacy, "previewRequired"));
            Assert.Null(fixture.Get<string?>(fixture.Legacy, "message"));
            Assert.Same(fault, fixture.LegacyLogger.Entries.Last().Error);
            Assert.Equal(LogLevel.Error, fixture.LegacyLogger.Entries.Last().Level);
            Assert.DoesNotContain("private-write-diagnostic", fixture.Decoded);
            calls = fixture.Factory.Calls;
            await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
            await fixture.Invoke(fixture.Legacy, "ResolveAsync");
            Assert.Equal(calls, fixture.Factory.Calls);
            fixture.Factory.BeforeNext = () => throw new IOException("private-recovery-diagnostic");
            await fixture.Invoke(fixture.Legacy, "PreviewAsync");
            Assert.True(fixture.Get<bool>(fixture.Legacy, "previewRequired"));
            await fixture.Invoke(fixture.Legacy, "PreviewAsync");
            Assert.False(fixture.Get<bool>(fixture.Legacy, "previewRequired"));
        }
        await using (var context = fixture.Database.CreateDbContext())
        {
            Assert.Equal("", (await context.Alerts.SingleAsync(alert => alert.Id == id)).Comments);
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        }
        await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
        await fixture.Invoke(fixture.Legacy, "ResolveAsync");
        Assert.Equal(fixture.Text.Plural("LegacyAlerts.Completed", 1), fixture.Get<string>(fixture.Legacy, "message"));
        await fixture.Invoke(fixture.Legacy, "PreviewAsync");
        Assert.Equal(0, fixture.Get<int>(fixture.Legacy, "candidateCount"));
        Assert.Equal(fixture.Text["LegacyAlerts.NoCandidates"], fixture.Get<string>(fixture.Legacy, "message"));
        await using var verify = fixture.Database.CreateDbContext();
        Assert.Equal("Resolved", (await verify.Alerts.SingleAsync()).Comments);
        Assert.Single(await verify.LogbookEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MaintenancePostCommitFailureRetainsUnconfirmedOutcomeAndRequiresDeliberateRecovery(string culture)
    {
        using var selection = new UiSelection(culture);
        foreach (var legacy in new[] { true, false })
        {
            var commit = new SettingsCommitFault();
            await using var fixture = await Fixture.Create(interceptor: commit, operationalTables: true);
            await fixture.Render();
            await AddMaintenanceAlert(fixture, "original alert", "");
            var original = await fixture.Service.LoadAsync();
            var panel = legacy ? (ComponentBase)fixture.Legacy : fixture.Reset;
            if (legacy) await fixture.Invoke(panel, "PreviewAsync");
            else await fixture.Invoke(panel, "BeginReset");
            await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
            var commits = commit.Commits;
            commit.Fail = true;
            await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
            Assert.Equal(commits + 1, commit.Commits);
            Assert.Equal(fixture.Text[legacy ? "LegacyAlerts.Unconfirmed" : "DatabaseReset.Unconfirmed"],
                fixture.Get<string>(panel, legacy ? "error" : "errorMessage"));
            Assert.Null(fixture.Get<string?>(panel, legacy ? "message" : "statusMessage"));
            Assert.False(fixture.Get<bool>(panel, "showConfirmation"));
            Assert.Equal("", fixture.Get<string>(panel, "confirmation"));
            Assert.True(fixture.Get<bool>(panel, legacy ? "previewRequired" : "resetReviewRequired"));
            await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
            await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
            Assert.Equal(commits + 1, commit.Commits);
            Assert.DoesNotContain("private-post-commit", fixture.Decoded);
            Assert.Equal(original.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
            await using (var context = fixture.Database.CreateDbContext())
            {
                if (legacy)
                {
                    Assert.Equal("Resolved", (await context.Alerts.SingleAsync()).Comments);
                    Assert.Single(await context.LogbookEntries.ToArrayAsync());
                }
                else
                {
                    Assert.Empty(await context.Alerts.ToArrayAsync());
                    Assert.Empty(await context.LogbookEntries.ToArrayAsync());
                    Assert.Equal(10, await context.AlertRules.CountAsync());
                }
            }
            if (legacy)
            {
                await fixture.Invoke(panel, "PreviewAsync");
                Assert.Equal(fixture.Text["LegacyAlerts.NoCandidates"], fixture.Get<string>(panel, "message"));
                Assert.False(fixture.Get<bool>(panel, "showConfirmation"));
                var calls = fixture.Factory.Calls;
                await fixture.Set(panel, "confirmation", "RESOLVE");
                await fixture.Invoke(panel, "ResolveAsync");
                Assert.Equal(calls, fixture.Factory.Calls);
            }
            else
            {
                await fixture.Invoke(panel, "BeginReset");
                Assert.Contains(fixture.Text["DatabaseReset.ReviewAcknowledgement"], fixture.Decoded);
                Assert.False(fixture.Get<bool>(panel, "databaseReviewed"));
                await fixture.Set(panel, "confirmation", "RESET");
                await fixture.Invoke(panel, "ResetAsync");
                Assert.Equal(commits + 1, commit.Commits);
                await fixture.Set(panel, "databaseReviewed", true);
                commit.Fail = false;
                await fixture.Invoke(panel, "ResetAsync");
                Assert.Equal(commits + 2, commit.Commits);
                Assert.Equal(fixture.Text.Plural("DatabaseReset.Completed", 10), fixture.Get<string>(panel, "statusMessage"));
                Assert.False(fixture.Get<bool>(panel, "resetReviewRequired"));
            }
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task MaintenanceBusyGuardsDoNotReplaceConfirmationOrRepeatWrites(string culture, bool legacy)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true);
        await fixture.Render();
        await AddMaintenanceAlert(fixture, "original alert", "");
        var panel = legacy ? (ComponentBase)fixture.Legacy : fixture.Reset;
        if (legacy) await fixture.Invoke(panel, "PreviewAsync");
        else await fixture.Invoke(panel, "BeginReset");
        await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeNextAsync = async () => { started.SetResult(); await release.Task; };
        var action = fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
        await started.Task;
        var calls = fixture.Factory.Calls;
        await fixture.Invoke(panel, legacy ? "PreviewAsync" : "BeginReset");
        await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
        await fixture.Invoke(panel, legacy ? "Cancel" : "CancelReset");
        Assert.Equal(calls, fixture.Factory.Calls);
        Assert.Equal(legacy ? "RESOLVE" : "RESET", fixture.Get<string>(panel, "confirmation"));
        Assert.True(fixture.Get<bool>(panel, "showConfirmation"));
        release.SetResult();
        await action;
        Assert.False(fixture.Get<bool>(panel, legacy ? "busy" : "isResetting"));
        Assert.Null(fixture.Get<string?>(panel, legacy ? "error" : "errorMessage"));
        Assert.Equal(fixture.Text.Plural(legacy ? "LegacyAlerts.Completed" : "DatabaseReset.Completed", legacy ? 1 : 10),
            fixture.Get<string>(panel, legacy ? "message" : "statusMessage"));
        if (legacy)
        {
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Factory.BeforeNextAsync = async () => { started.SetResult(); await release.Task; };
            var preview = fixture.Invoke(panel, "PreviewAsync");
            await started.Task;
            calls = fixture.Factory.Calls;
            await fixture.Invoke(panel, "PreviewAsync");
            await fixture.Invoke(panel, "ResolveAsync");
            await fixture.Invoke(panel, "Cancel");
            Assert.Equal(calls, fixture.Factory.Calls);
            Assert.True(fixture.Get<bool>(panel, "busy"));
            release.SetResult();
            await preview;
            Assert.False(fixture.Get<bool>(panel, "busy"));
            Assert.Equal(fixture.Text["LegacyAlerts.NoCandidates"], fixture.Get<string>(panel, "message"));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LegacyCommentLimitIsLocalizedWithoutTruncationOrPartialWrites(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true);
        await fixture.Render();
        var id = await AddMaintenanceAlert(fixture, "original alert", new string('x', 3992));
        await fixture.Invoke(fixture.Legacy, "PreviewAsync");
        await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
        await fixture.Invoke(fixture.Legacy, "ResolveAsync");
        var expected = fixture.Text.Format("LegacyAlerts.CommentsTooLong", id);
        Assert.Equal(expected, fixture.Get<string>(fixture.Legacy, "error"));
        Assert.Contains(expected, fixture.Decoded);
        Assert.Equal(LogLevel.Warning, fixture.LegacyLogger.Entries.Last().Level);
        Assert.False(fixture.Get<bool>(fixture.Legacy, "previewRequired"));
        await using (var context = fixture.Database.CreateDbContext())
        {
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
            Assert.Equal(new string('x', 3992), (await context.Alerts.SingleAsync()).Comments);
            await context.Alerts.Where(alert => alert.Id == id).ExecuteUpdateAsync(
                setters => setters.SetProperty(alert => alert.Comments, new string('x', 3991)));
        }
        await fixture.Set(fixture.Legacy, "confirmation", "RESOLVE");
        await fixture.Invoke(fixture.Legacy, "ResolveAsync");
        await using var verify = fixture.Database.CreateDbContext();
        Assert.Equal(new string('x', 3991) + "\nResolved", (await verify.Alerts.SingleAsync()).Comments);
        Assert.Single(await verify.LogbookEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task MaintenanceDatabaseRollbackIsSafeInActualPanels(string culture, bool legacy)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true);
        await fixture.Render();
        await AddMaintenanceAlert(fixture, "original alert", "original comment");
        var settings = await fixture.Service.LoadAsync();
        var panel = legacy ? (ComponentBase)fixture.Legacy : fixture.Reset;
        if (legacy) await fixture.Invoke(panel, "PreviewAsync");
        else await fixture.Invoke(panel, "BeginReset");
        await using (var context = fixture.Database.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(legacy
                ? "CREATE TRIGGER FailMaintenance BEFORE INSERT ON LogbookEntries BEGIN SELECT RAISE(ABORT, 'private-maintenance-sql'); END;"
                : "CREATE TRIGGER FailMaintenance BEFORE INSERT ON AlertRules BEGIN SELECT RAISE(ABORT, 'private-maintenance-sql'); END;");
        await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
        await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
        Assert.Equal(fixture.Text[legacy ? "LegacyAlerts.Unconfirmed" : "DatabaseReset.Unconfirmed"],
            fixture.Get<string>(panel, legacy ? "error" : "errorMessage"));
        Assert.Null(fixture.Get<string?>(panel, legacy ? "message" : "statusMessage"));
        Assert.DoesNotContain("private-maintenance-sql", fixture.Decoded);
        var logged = (legacy ? fixture.LegacyLogger.Entries : fixture.ResetLogger.Entries).Last();
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Contains("private-maintenance-sql", logged.Error!.ToString());
        await using (var context = fixture.Database.CreateDbContext())
        {
            Assert.Equal("original comment", (await context.Alerts.SingleAsync()).Comments);
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
            Assert.Empty(await context.ParsedAlerts.ToArrayAsync());
            Assert.Empty(await context.AlertRules.ToArrayAsync());
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailMaintenance;");
        }
        Assert.Equal(settings.OrderBy(pair => pair.Key), (await fixture.Service.LoadAsync()).OrderBy(pair => pair.Key));
        if (legacy)
        {
            await fixture.Invoke(panel, "PreviewAsync");
            Assert.Equal(1, fixture.Get<int>(panel, "candidateCount"));
        }
        else
        {
            await fixture.Invoke(panel, "BeginReset");
            await fixture.Set(panel, "databaseReviewed", true);
        }
        await fixture.Set(panel, "confirmation", legacy ? "RESOLVE" : "RESET");
        await fixture.Invoke(panel, legacy ? "ResolveAsync" : "ResetAsync");
        Assert.Equal(fixture.Text.Plural(legacy ? "LegacyAlerts.Completed" : "DatabaseReset.Completed", legacy ? 1 : 10),
            fixture.Get<string>(panel, legacy ? "message" : "statusMessage"));
        Assert.Null(fixture.Get<string?>(panel, legacy ? "error" : "errorMessage"));
    }

    private static async Task<Guid> AddMaintenanceAlert(Fixture fixture, string name, string comments)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var context = fixture.Database.CreateDbContext();
        context.Alerts.Add(new AlertRecord(id, now, id.ToString(), name, "Sev1", "New", "Log", "Fired",
            "DC01", "rg", "sub", now, "", "", comments, "{}"));
        await context.SaveChangesAsync();
        return id;
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ResetUnknownFailuresRequireReviewAndNeverExposeDiagnostics(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(operationalTables: true);
        await fixture.Render();
        await AddMaintenanceAlert(fixture, "original alert", "");
        foreach (var fault in new Exception[]
        {
            new IOException("private-reset-diagnostic"),
            new InvalidOperationException("Type RESET exactly to confirm the permanent database reset."),
            new UnauthorizedAccessException("private-reset-diagnostic"),
            new MaintenanceValidationException((MaintenanceValidationCode)999, "private-reset-diagnostic"),
            new MaintenanceValidationException(MaintenanceValidationCode.CommentsTooLong, "private-reset-diagnostic", Guid.NewGuid()),
            new InvalidOperationException("private-reset-diagnostic",
                new MaintenanceValidationException(MaintenanceValidationCode.ConfirmationInvalid, "wrapped"))
        })
        {
            await fixture.Invoke(fixture.Reset, "BeginReset");
            if (fixture.Get<bool>(fixture.Reset, "resetReviewRequired"))
                await fixture.Set(fixture.Reset, "databaseReviewed", true);
            await fixture.Set(fixture.Reset, "confirmation", "RESET");
            fixture.Factory.BeforeNext = () => throw fault;
            await fixture.Invoke(fixture.Reset, "ResetAsync");
            Assert.Equal(fixture.Text["DatabaseReset.Unconfirmed"], fixture.Get<string>(fixture.Reset, "errorMessage"));
            Assert.True(fixture.Get<bool>(fixture.Reset, "resetReviewRequired"));
            Assert.False(fixture.Get<bool>(fixture.Reset, "databaseReviewed"));
            Assert.False(fixture.Get<bool>(fixture.Reset, "showConfirmation"));
            Assert.Null(fixture.Get<string?>(fixture.Reset, "statusMessage"));
            Assert.Equal(LogLevel.Error, fixture.ResetLogger.Entries.Last().Level);
            Assert.Same(fault, fixture.ResetLogger.Entries.Last().Error);
            Assert.DoesNotContain("private-reset-diagnostic", fixture.Decoded);
            var calls = fixture.Factory.Calls;
            await fixture.Set(fixture.Reset, "confirmation", "RESET");
            await fixture.Invoke(fixture.Reset, "ResetAsync");
            Assert.Equal(calls, fixture.Factory.Calls);
            await fixture.Invoke(fixture.Reset, "BeginReset");
            await fixture.Set(fixture.Reset, "confirmation", "RESET");
            await fixture.Invoke(fixture.Reset, "ResetAsync");
            Assert.Equal(calls, fixture.Factory.Calls);
            await fixture.Set(fixture.Reset, "databaseReviewed", true);
            await fixture.Invoke(fixture.Reset, "CancelReset");
            Assert.True(fixture.Get<bool>(fixture.Reset, "resetReviewRequired"));
            Assert.False(fixture.Get<bool>(fixture.Reset, "databaseReviewed"));
            await fixture.Invoke(fixture.Reset, "BeginReset");
            Assert.False(fixture.Get<bool>(fixture.Reset, "databaseReviewed"));
            await using var context = fixture.Database.CreateDbContext();
            Assert.Single(await context.Alerts.ToArrayAsync());
            Assert.Empty(await context.AlertRules.ToArrayAsync());
        }
    }

    private sealed class MaintenanceLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    private sealed class SettingsLogger : ILogger<AdminSettings>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    private static string Canonical(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin/settings");
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-administrator"), new Claim(ClaimTypes.Role, "Admin")], "test"));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(User));
    }

    private sealed class Activator(IServiceProvider services) : IComponentActivator
    {
        public AdminSettings Page { get; private set; } = default!;
        public DatabaseResetPanel Reset { get; private set; } = default!;
        public LegacyAlertResolutionPanel Legacy { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is AdminSettings page) Page = page;
            if (component is DatabaseResetPanel reset) Reset = reset;
            if (component is LegacyAlertResolutionPanel legacy) Legacy = legacy;
            return component;
        }
    }

    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type)
                : throw new NotSupportedException($"Unsupported test render mode: {mode.GetType().Name}");
    }

    private sealed class Fixture(DatabaseTransactionsTests.TestDatabase database, WebApplication app,
        AsyncServiceScope scope, Renderer renderer) : IAsyncDisposable
    {
        public DatabaseTransactionsTests.TestDatabase Database { get; } = database;
        public DatabaseSettingsService Service => scope.ServiceProvider.GetRequiredService<DatabaseSettingsService>();
        public SettingsFactory Factory => scope.ServiceProvider.GetRequiredService<SettingsFactory>();
        public SettingsLogger Logger => scope.ServiceProvider.GetRequiredService<SettingsLogger>();
        public TestAuthentication Authentication => scope.ServiceProvider.GetRequiredService<TestAuthentication>();
        public MaintenanceLogger<DatabaseResetPanel> ResetLogger => scope.ServiceProvider.GetRequiredService<MaintenanceLogger<DatabaseResetPanel>>();
        public MaintenanceLogger<LegacyAlertResolutionPanel> LegacyLogger => scope.ServiceProvider.GetRequiredService<MaintenanceLogger<LegacyAlertResolutionPanel>>();
        public AgentAnalysisProfilesOptions Profiles => scope.ServiceProvider.GetRequiredService<AgentAnalysisProfilesOptions>();
        public AgentAnalysisExecutionState Active => scope.ServiceProvider.GetRequiredService<AgentAnalysisExecutionState>();
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        private Activator Components => scope.ServiceProvider.GetRequiredService<Activator>();
        public AdminSettings Page => Components.Page;
        public DatabaseResetPanel Reset => Components.Reset;
        public LegacyAlertResolutionPanel Legacy => Components.Legacy;
        private HtmlRootComponent root;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public T Get<T>(ComponentBase component, string field) =>
            (T)component.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        public Task Set(ComponentBase component, string field, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            component.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
            Changed(component);
        });
        public Task Invoke(ComponentBase component, string method) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
            if (result is Task task) await task;
            Changed(component);
        });
        private static void Changed(ComponentBase component) =>
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        public async Task Render() => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = renderer.BeginRenderingComponent(typeof(AdminSettings), ParameterView.Empty);
            await root.QuiescenceTask;
        });

        public static async Task<Fixture> Create(AgentAnalysisExecutionState? active = null,
            Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null, bool operationalTables = false,
            bool openAuthentication = true, bool configured = true)
        {
            var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync(interceptor: interceptor,
                operationalTables: operationalTables);
            var cases = TestCaseLoader.Load<DatabaseSettingsTestCases>("database-settings.json");
            var graph = cases.AlertGraph;
            graph["SiteLocations"] = JsonSerializer.SerializeToNode(new AlertGraphSiteLocationOptions[]
                { new() { DisplayName = "Original <site> & label", Latitude = 50.125, Longitude = 8.625, Aliases = ["Original.Site"] } });
            var values = new Dictionary<string, string>
            {
                [DatabaseSettingsLoader.Authentication] = """{"Type":"open"}""",
                [DatabaseSettingsLoader.AlertHistory] = """{"Days":7}""",
                [DatabaseSettingsLoader.AlertGraph] = graph.ToJsonString(),
                [DatabaseSettingsLoader.AlertSeverityDisplay] = cases.SeverityDisplay.ToJsonString(),
                [DatabaseSettingsLoader.InboxTargetOutputDisplay] = JsonSerializer.Serialize(new InboxTargetOutputDisplayOptions()),
                [DatabaseSettingsLoader.RcaDisplay] = JsonSerializer.Serialize(new RcaDisplayOptions()),
                [DatabaseSettingsLoader.DashboardFilters] = "{}",
                [DatabaseSettingsLoader.AgentAnalysisContext] = JsonSerializer.Serialize(new AgentAnalysisContextOptions()),
                [DatabaseSettingsLoader.AgentAnalysisExecution] = JsonSerializer.Serialize(new AgentAnalysisExecutionOptions())
            };
            await using (var context = database.CreateDbContext())
            {
                context.Settings.AddRange(values.Select(pair => new DatabaseSetting { Name = pair.Key, JsonValue = pair.Value }));
                context.Settings.Add(new() { Name = "Unrelated", JsonValue = "original-unrelated" });
                await context.SaveChangesAsync();
            }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            builder.Services.AddSingleton(new SettingsFactory(database));
            builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(services => services.GetRequiredService<SettingsFactory>());
            builder.Services.AddSingleton<SettingsLogger>();
            builder.Services.AddSingleton<ILogger<AdminSettings>>(services => services.GetRequiredService<SettingsLogger>());
            builder.Services.AddSingleton<MaintenanceLogger<DatabaseResetPanel>>();
            builder.Services.AddSingleton<ILogger<DatabaseResetPanel>>(services => services.GetRequiredService<MaintenanceLogger<DatabaseResetPanel>>());
            builder.Services.AddSingleton<MaintenanceLogger<LegacyAlertResolutionPanel>>();
            builder.Services.AddSingleton<ILogger<LegacyAlertResolutionPanel>>(services => services.GetRequiredService<MaintenanceLogger<LegacyAlertResolutionPanel>>());
            builder.Services.AddSingleton(new CrossTenantLogAnalyticsOptions());
            builder.Services.AddScoped<DatabaseSettingsService>();
            builder.Services.AddSingleton(new AgentAnalysisProfilesOptions());
            builder.Services.AddSingleton(active ?? AgentAnalysisExecutionState.Create(new AgentAnalysisExecutionOptions()));
            var authentication = new ApplicationAuthenticationOptions { Type = openAuthentication ? "open" : "sql" };
            builder.Services.AddSingleton(authentication);
            builder.Services.AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication));
            builder.Services.AddSingleton<TestAuthentication>();
            builder.Services.AddSingleton<AuthenticationStateProvider>(services => services.GetRequiredService<TestAuthentication>());
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<CurrentUserIdentity>();
            builder.Services.AddSingleton(new DatabaseConfigurationStatus(configured, null, new SqlConnectionStringBuilder
                { DataSource = "original-<server>", InitialCatalog = "original-<database>" }));
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            builder.Services.AddSingleton(presenter);
            builder.Services.AddSingleton(new AlertRuleEvaluator(presenter));
            builder.Services.AddSingleton<AlertStore>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<DatabaseResetService>();
            builder.Services.AddScoped<LegacyAlertResolutionService>();
            builder.Services.AddScoped<Activator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Activator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            return new Fixture(database, app, scope,
                new Renderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()));
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
