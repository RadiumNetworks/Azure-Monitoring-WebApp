using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class LocalizationPilotTests
{
    [Fact]
    public void PilotResourcesHaveCompleteEnglishAndGermanKeysAndCompatibleParameters()
    {
        var manager = new ResourceManager("MonitoringApp.Resources.UiResources", typeof(UiResources).Assembly);
        var english = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var german = manager.GetResourceSet(CultureInfo.GetCultureInfo("de-DE"), true, false)!;
        var keys = english.Cast<DictionaryEntry>().Select(item => (string)item.Key).Order().ToArray();
        Assert.Equal(keys, german.Cast<DictionaryEntry>().Select(item => (string)item.Key).Order());
        foreach (var key in keys)
        {
            var source = english.GetString(key)!;
            var translation = german.GetString(key)!;
            Assert.False(string.IsNullOrWhiteSpace(source), key);
            Assert.False(string.IsNullOrWhiteSpace(translation), key);
            Assert.Equal(LocalizationResourceAcceptanceTests.ParameterIndexes(source),
                LocalizationResourceAcceptanceTests.ParameterIndexes(translation));
            foreach (var (culture, text) in new[] { ("en-US", source), ("de-DE", translation) })
            {
                object[] arguments = [1234, "custom-value", "linked-value", "claim-name", "claim-value", "original-role", 15];
                if (key.StartsWith("AnalysisStatus.Refreshed", StringComparison.Ordinal))
                {
                    arguments[0] = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);
                    arguments[1] = 1234;
                }
                _ = string.Format(CultureInfo.GetCultureInfo(culture), text, arguments);
            }
        }

        var paths = new[]
        {
            @"Components\Layout\MainLayout.razor", @"Components\Layout\LoginLayout.razor",
            @"Components\Pages\Login.razor", @"Components\Pages\AccessDenied.razor",
            @"Components\Pages\AdminRules.razor", @"Components\Pages\AdminUsers.razor",
            @"Components\Pages\AdminLogAnalytics.razor", @"Components\Pages\AdminAlertAuthentication.razor",
            @"Components\Pages\AdminSettings.razor", @"Components\DatabaseResetPanel.razor",
            @"Components\LegacyAlertResolutionPanel.razor", @"Components\Pages\Graph.razor",
            @"Components\GraphSiteMap.razor", @"Components\Pages\Home.razor", @"Components\InboxWidgetActions.razor",
            @"Components\AnalysisDashboardTabs.razor", @"Components\Pages\Analysis.razor",
            @"Components\AnalysisQueryCategoryTree.razor", @"Components\AnalysisGridControls.razor",
            @"Components\AnalysisExecutionSelections.razor", @"Components\AnalysisDashboardExecutionControls.razor",
            @"Components\PublishedAnalysisDashboardGrid.razor", @"Presentation\Analysis\AnalysisSelectionLabels.cs",
            @"Components\PublishedAnalysisDashboardView.razor", @"Components\Pages\Queries.razor",
            @"Components\Pages\AdminAnalysisQueries.razor", @"Components\Pages\AdminScheduledQueries.razor",
            @"Components\QueryJsonImport.razor", @"Components\Pages\Logbook.razor",
            @"Components\Pages\Rca.razor", @"Components\RcaCaseSummary.razor", @"Components\Pages\Prompt.razor",
            @"Components\AnalysisResultView.razor", @"Components\AnalysisTableView.razor",
            @"Components\AnalysisStatView.razor", @"Components\AnalysisSummaryView.razor",
            @"Components\AnalysisStatusHistoryView.razor", @"Components\DiagnosticStepView.razor",
            @"Components\Pages\Error.razor", @"Components\Pages\AdminInventory.razor",
            @"Components\QueryResultRows.razor"
        };
        foreach (var path in paths)
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "MonitoringApp", path));
            foreach (Match match in Regex.Matches(source,
                "\"(?<key>(?:Brand|Navigation|Authentication|AccessDenied|Common|Rules|AdminUsers|LogAnalyticsAdmin|IngestionAdmin|AdminSettings|DatabaseReset|LegacyAlerts|Graph|GraphMap|Inbox|Widgets|Filters|AnalysisDashboards|Analysis|AnalysisCategories|AnalysisGrid|AnalysisSelections|AnalysisExecution|AnalysisPublications|AnalysisStatus|AnalysisResults|QueryResults|QueryExamples|AdminAnalysisQueries|AdminScheduledQueries|QueryImport|Logbook|Rca|Prompt|ErrorPage|Diagnostics|AdminInventory)\\.[A-Za-z0-9.]+)\""))
            {
                var key = match.Groups["key"].Value;
                if (key.EndsWith('.')) continue;
                if (keys.Contains(key)) Assert.False(string.IsNullOrWhiteSpace(UiResources.GetEnglish(key)));
                else
                {
                    Assert.Contains(key + ".One", keys);
                    Assert.Contains(key + ".Other", keys);
                }
            }
        }
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 0)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 2)]
    public async Task AnalysisRefreshStatusPreservesUniversalUtcShapeAndWholeCountMessage(string culture, int count)
    {
        using var selection = new UiSelection(culture);
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.Create();
        var utc = new DateTimeOffset(2026, 11, 6, 14, 5, 9, TimeSpan.Zero);
        var prefix = culture == "de-DE" ? "Dashboard aktualisiert" : "Dashboard refreshed";
        Assert.Equal($"{prefix} · 2026-11-06 14:05:09Z", fixture.Text.Format("AnalysisStatus.Refreshed", utc));
        Assert.Equal(culture == "de-DE"
            ? $"{prefix} · 2026-11-06 14:05:09Z · {count} eindeutige " + (count == 1 ? "Anfrage" : "Anfragen")
            : $"{prefix} · 2026-11-06 14:05:09Z · {count} unique request(s)",
            fixture.Text.Format(count == 1 ? "AnalysisStatus.RefreshedRequests.One" : "AnalysisStatus.RefreshedRequests.Other", utc, count));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0, "0 configured rules", "Exactly 0 rows", "0 minutes")]
    [InlineData("en-US", 1, "1 configured rule", "Exactly 1 row", "1 minute")]
    [InlineData("en-US", 2, "2 configured rules", "Exactly 2 rows", "2 minutes")]
    [InlineData("de-DE", 0, "0 konfigurierte Regeln", "Genau 0 Zeilen", "0 Minuten")]
    [InlineData("de-DE", 1, "1 konfigurierte Regel", "Genau 1 Zeile", "1 Minute")]
    [InlineData("de-DE", 2, "2 konfigurierte Regeln", "Genau 2 Zeilen", "2 Minuten")]
    [InlineData("de-DE", 1234, "1.234 konfigurierte Regeln", "Genau 1.234 Zeilen", "1.234 Minuten")]
    public async Task WholePluralMessagesUseDisplayCultureWithoutChangingFormattingCulture(
        string culture, int count, string rules, string rows, string minutes)
    {
        using var selection = new UiSelection(culture);
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.Create();
        Assert.Equal(rules, fixture.Text.Plural("Rules.Count", count));
        Assert.Equal(rows, fixture.Text.Plural("Rules.Condition.RowCountEquals", count));
        Assert.Equal(minutes, fixture.Text.Plural("Rules.DurationMinutes", count));
        Assert.Equal(culture == "de-DE" ? "Mehr als 12 % der DCs in der Domäne haben Alarme"
            : "More than 12% of DCs in domain have alerts", fixture.Text.Format("Rules.Condition.DomainPercentage", 12));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", "sql")]
    [InlineData("de-DE", "sql")]
    [InlineData("en-US", "entra")]
    [InlineData("de-DE", "entra")]
    [InlineData("en-US", "open")]
    [InlineData("de-DE", "open")]
    public async Task AuthenticationPagesTranslateWithoutChangingFormOrEntraContracts(string culture, string mode)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(mode);
        var html = await fixture.Render<Login>();
        Assert.Contains(WebUtility.HtmlEncode(fixture.Text["Authentication.SignIn"]), html);
        if (mode == "sql")
        {
            Assert.Contains("action=\"/auth/login\"", html);
            Assert.Contains("name=\"Username\"", html);
            Assert.Contains("name=\"Password\"", html);
            Assert.Contains("name=\"ReturnUrl\"", html);
            Assert.Contains("<AntiforgeryToken", File.ReadAllText(
                Path.Combine(RepositoryRoot(), @"MonitoringApp\Components\Pages\Login.razor")));
            Assert.Contains(fixture.Text["Authentication.InvalidCredentials"], WebUtility.HtmlDecode(html));
        }
        else if (mode == "entra")
        {
            Assert.Contains("/.auth/login/aad?post_login_redirect_uri=%2Fadmin%2Frules%3Ftab%3Dexample", html);
            Assert.Contains("Reader, Operator", WebUtility.HtmlDecode(html));
            Assert.DoesNotContain("name=\"Password\"", html);
        }
        else Assert.Contains(fixture.Text["Authentication.OpenModeDescription"], WebUtility.HtmlDecode(html));

        var denied = WebUtility.HtmlDecode(await fixture.Render<AccessDenied>());
        Assert.Contains(fixture.Text["AccessDenied.Title"], denied);
        Assert.Contains(fixture.Text["AccessDenied.Description"], denied);
        Assert.Equal(mode == "entra", denied.Contains("/.auth/logout?", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> RuleCases =>
        new[] { "en-US", "de-DE" }.SelectMany(culture => new[]
        {
            AlertRuleTypes.Categorization, AlertRuleTypes.Suppression, AlertRuleTypes.InventoryStatus,
            AlertRuleTypes.InventoryHeartbeatFreshness, AlertRuleTypes.ReplicationInventoryWarning,
            AlertRuleTypes.InventoryRoleAssignment
        }.Select(type => new object[] { culture, type }));

    [Theory]
    [MemberData(nameof(RuleCases))]
    public async Task RuleEditorAndDeleteDialogPreserveTechnicalAndEncodedUserValues(string culture, string type)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var rule = Rule(type);
        rule.Name = "<script>Custom & original</script>";
        var original = JsonSerializer.Serialize(rule);
        await fixture.RenderRules();
        await fixture.Invoke("BeginEdit", rule);
        var editor = fixture.Html;
        Assert.Contains("value=\"Categorization\"", editor);
        Assert.Contains("value=\"ReplicationInventoryWarning\"", editor);
        Assert.Contains("value=\"InventoryHeartbeatFreshness\"", editor);
        Assert.Contains(fixture.Text["Rules.Editor.Name"], WebUtility.HtmlDecode(editor));
        if (type == AlertRuleTypes.Categorization)
        {
            Assert.Contains("value=\"info\"", editor);
            Assert.Contains("value=\"failure\"", editor);
            Assert.Contains("value=\"SystemOutage\"", editor);
            Assert.Contains("value=\"RowCountEquals\"", editor);
        }
        if (type == AlertRuleTypes.ReplicationInventoryWarning)
            foreach (var technical in new[] { "SourceDSA", "DestDSA", "Replication", "Sev2", "rowCount = 0" })
                Assert.Contains(technical, WebUtility.HtmlDecode(editor));

        await fixture.Invoke("BeginDelete", rule);
        var html = fixture.Html;
        Assert.Contains("<strong>&lt;script&gt;Custom &amp; original&lt;/script&gt;</strong>", html);
        Assert.DoesNotContain("<script>", html);
        var expectedKey = type switch
        {
            AlertRuleTypes.Suppression => "Rules.Delete.SuppressionImpact",
            AlertRuleTypes.InventoryStatus => "Rules.Delete.InventoryStatusImpact",
            AlertRuleTypes.ReplicationInventoryWarning => "Rules.Delete.ReplicationWarningImpact",
            AlertRuleTypes.InventoryRoleAssignment => "Rules.Delete.RetiredImpact",
            AlertRuleTypes.InventoryHeartbeatFreshness => "Rules.Delete.FreshnessImpact.Other",
            _ => "Rules.Delete.CategoryImpact"
        };
        Assert.Contains(fixture.Text.Format(expectedKey, type == AlertRuleTypes.InventoryHeartbeatFreshness
            ? InventoryHeartbeatFreshnessRule.DefaultMinutes : rule.CategoryName), WebUtility.HtmlDecode(html));
        Assert.Equal(original, JsonSerializer.Serialize(rule));
        Assert.Empty(await fixture.Store.GetAllAlertRulesAsync());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task KnownCodesHaveLocalizedMessagesAndUnknownDetailsAreNeverDisplayed(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderRules();
        foreach (var code in Enum.GetValues<AlertRuleErrorCode>())
        {
            await fixture.Invoke("ShowRuleError", new AlertRuleException(code, "PRIVATE_PROVIDER_DETAIL",
                arguments: ["original-name"]));
            Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
            Assert.DoesNotContain("Rules.Validation.", fixture.Html);
            Assert.DoesNotContain("Rules.Errors.", fixture.Html);
        }
        fixture.Factory.FailCreation = true;
        await fixture.Invoke("LoadRulesAsync");
        Assert.Contains(fixture.Text["Rules.Errors.LoadFailed"], WebUtility.HtmlDecode(fixture.Html));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
        await fixture.Invoke("BeginEdit", Rule(AlertRuleTypes.Categorization));
        await fixture.Invoke("SaveAsync");
        Assert.Contains(fixture.Text["Rules.Errors.ChangeUnconfirmed"], WebUtility.HtmlDecode(fixture.Html));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
        await fixture.Invoke("BeginDelete", Rule(AlertRuleTypes.Categorization));
        await fixture.Invoke("DeleteAsync");
        Assert.Contains(fixture.Text["Rules.Errors.ChangeUnconfirmed"], WebUtility.HtmlDecode(fixture.Html));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
    }

    [Theory]
    [InlineData("en-US", "Create")]
    [InlineData("de-DE", "Create")]
    [InlineData("en-US", "Update")]
    [InlineData("de-DE", "Update")]
    [InlineData("en-US", "Delete")]
    [InlineData("de-DE", "Delete")]
    public async Task SuccessfulMutationsShowLocalizedConfirmationAndPersistOriginalValues(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var rule = Rule(AlertRuleTypes.Categorization);
        rule.Name = "Original <name> & value";
        if (operation != "Create") rule = await fixture.Store.SaveAlertRuleAsync(rule);
        await fixture.RenderRules();
        if (operation == "Create")
        {
            await fixture.Invoke("BeginCreate");
            await fixture.UpdateEditor(editing =>
            {
                editing.Name = rule.Name;
                editing.CategoryName = rule.CategoryName;
                editing.QueryResultType = rule.QueryResultType;
                editing.ConditionType = rule.ConditionType;
                editing.Threshold = rule.Threshold;
            });
        }
        else await fixture.Invoke(operation == "Delete" ? "BeginDelete" : "BeginEdit", rule);
        await fixture.Invoke(operation == "Delete" ? "DeleteAsync" : "SaveAsync");
        Assert.Contains(fixture.Text.Format($"Rules.{operation}.Success", rule.Name), WebUtility.HtmlDecode(fixture.Html));
        Assert.Contains("admin-message-success", fixture.Html);
        var stored = await fixture.Store.GetAllAlertRulesAsync();
        if (operation == "Delete") Assert.Empty(stored);
        else
        {
            var actual = Assert.Single(stored);
            Assert.Equal(rule.Name, actual.Name);
            Assert.Equal(rule.CategoryName, actual.CategoryName);
            Assert.Equal("DCPort", actual.QueryResultType);
            Assert.Equal(AlertRuleConditionTypes.RowCountEquals, actual.ConditionType);
            Assert.Equal(1, actual.Threshold);
        }
    }

    [Theory]
    [InlineData(AlertRuleErrorCode.Name)]
    [InlineData(AlertRuleErrorCode.Type)]
    [InlineData(AlertRuleErrorCode.HeartbeatFreshness)]
    [InlineData(AlertRuleErrorCode.RetiredRoleAssignment)]
    [InlineData(AlertRuleErrorCode.InventoryRole)]
    [InlineData(AlertRuleErrorCode.InventoryRoleMatch)]
    [InlineData(AlertRuleErrorCode.AnalysisClassification)]
    [InlineData(AlertRuleErrorCode.CategoryName)]
    [InlineData(AlertRuleErrorCode.RequiredMatch)]
    [InlineData(AlertRuleErrorCode.Priority)]
    [InlineData(AlertRuleErrorCode.MatchLength)]
    [InlineData(AlertRuleErrorCode.AnyMatchingAlert)]
    [InlineData(AlertRuleErrorCode.RowCountThreshold)]
    [InlineData(AlertRuleErrorCode.FailedItemsRequired)]
    [InlineData(AlertRuleErrorCode.FailedItemsLength)]
    [InlineData(AlertRuleErrorCode.DomainConditionType)]
    [InlineData(AlertRuleErrorCode.DomainPercentage)]
    [InlineData(AlertRuleErrorCode.SiteConditionType)]
    [InlineData(AlertRuleErrorCode.ConditionType)]
    [InlineData(AlertRuleErrorCode.Tone)]
    public async Task ValidationCodesAreCultureIndependentAndNeverMutateInputsOrWrite(AlertRuleErrorCode expected)
    {
        await using var fixture = await Fixture.Create();
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            using var selection = new UiSelection(culture);
            var rule = Rule(AlertRuleTypes.Categorization);
            switch (expected)
            {
                case AlertRuleErrorCode.Name: rule.Name = " "; break;
                case AlertRuleErrorCode.Type: rule.RuleType = "Original unknown type"; break;
                case AlertRuleErrorCode.HeartbeatFreshness:
                    rule.RuleType = AlertRuleTypes.InventoryHeartbeatFreshness; rule.Threshold = 0; break;
                case AlertRuleErrorCode.RetiredRoleAssignment: rule.RuleType = AlertRuleTypes.InventoryRoleAssignment; break;
                case AlertRuleErrorCode.InventoryRole:
                    rule.RuleType = AlertRuleTypes.InventoryRoleAssignment; rule.Enabled = false; rule.InventoryRole = ""; break;
                case AlertRuleErrorCode.InventoryRoleMatch:
                    rule.RuleType = AlertRuleTypes.InventoryRoleAssignment; rule.Enabled = false; rule.QueryResultType = ""; break;
                case AlertRuleErrorCode.AnalysisClassification: rule.AnalysisClassification = "Original unknown value"; break;
                case AlertRuleErrorCode.CategoryName: rule.CategoryName = ""; break;
                case AlertRuleErrorCode.RequiredMatch:
                    rule.RuleType = AlertRuleTypes.Suppression; rule.QueryResultType = ""; break;
                case AlertRuleErrorCode.Priority: rule.Priority = -1; break;
                case AlertRuleErrorCode.MatchLength: rule.QueryResultType = new string('x', 129); break;
                case AlertRuleErrorCode.AnyMatchingAlert: rule.ConditionType = AlertRuleConditionTypes.AnyMatchingAlert; break;
                case AlertRuleErrorCode.RowCountThreshold: rule.Threshold = -1; break;
                case AlertRuleErrorCode.FailedItemsRequired: rule.ConditionType = AlertRuleConditionTypes.OnlyFailedItems; break;
                case AlertRuleErrorCode.FailedItemsLength:
                    rule.ConditionType = AlertRuleConditionTypes.OnlyFailedItems; rule.FailedItemName = new string('x', 257); break;
                case AlertRuleErrorCode.DomainConditionType:
                    rule.RuleType = AlertRuleTypes.Suppression; rule.ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain; break;
                case AlertRuleErrorCode.DomainPercentage:
                    rule.ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain; rule.Threshold = 101; break;
                case AlertRuleErrorCode.SiteConditionType:
                    rule.RuleType = AlertRuleTypes.Suppression; rule.ConditionType = AlertRuleConditionTypes.AllDomainControllersInSite; break;
                case AlertRuleErrorCode.ConditionType: rule.ConditionType = "Original unknown condition"; break;
                case AlertRuleErrorCode.Tone: rule.Tone = "Original unknown tone"; break;
                default: throw new ArgumentOutOfRangeException(nameof(expected), expected, null);
            }
            var original = JsonSerializer.Serialize(rule);
            var error = await Assert.ThrowsAsync<AlertRuleException>(() => fixture.Store.SaveAlertRuleAsync(rule));
            Assert.Equal(expected, error.Code);
            Assert.Equal(original, JsonSerializer.Serialize(rule));
            Assert.Equal(0, fixture.Interceptor.Saves);
            Assert.Empty(await fixture.Store.GetAllAlertRulesAsync());
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task CommittedMutationWithFailedNotificationShowsSpecificWarningWithoutProviderDetails(string culture, bool deleting)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var rule = await fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization));
        await fixture.RenderRules();
        fixture.Store.Changed += () => throw new InvalidOperationException("PRIVATE_SUBSCRIBER_DETAIL");
        await fixture.Invoke(deleting ? "BeginDelete" : "BeginEdit", rule);
        await fixture.Invoke(deleting ? "DeleteAsync" : "SaveAsync");
        Assert.Contains(fixture.Text[deleting ? "Rules.Errors.DeletedNotificationFailed" : "Rules.Errors.SavedNotificationFailed"],
            WebUtility.HtmlDecode(fixture.Html));
        Assert.DoesNotContain("PRIVATE_SUBSCRIBER_DETAIL", fixture.Html);
        Assert.DoesNotContain("admin-message-success", fixture.Html);
        Assert.DoesNotContain(fixture.Text["Rules.Errors.ChangeUnconfirmed"], WebUtility.HtmlDecode(fixture.Html));
        Assert.Equal(deleting ? 0 : 1, (await fixture.Store.GetAllAlertRulesAsync()).Count);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ConfirmedMutationWithFailedRefreshIsNotReportedAsUnknownOrCompleteSuccess(string culture, bool deleting)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var rule = await fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization));
        await fixture.RenderRules();
        fixture.Store.Changed += () => fixture.Factory.FailCreation = true;
        await fixture.Invoke(deleting ? "BeginDelete" : "BeginEdit", rule);
        await fixture.Invoke(deleting ? "DeleteAsync" : "SaveAsync");
        var html = WebUtility.HtmlDecode(fixture.Html);
        Assert.Contains(fixture.Text[deleting ? "Rules.Errors.DeletedRefreshFailed" : "Rules.Errors.SavedRefreshFailed"], html);
        Assert.DoesNotContain("admin-message-success", html);
        Assert.DoesNotContain(fixture.Text["Rules.Errors.ChangeUnconfirmed"], html);
        fixture.Factory.FailCreation = false;
        Assert.Equal(deleting ? 0 : 1, (await fixture.Store.GetAllAlertRulesAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailureIsTypedOnlyAfterMutationWasConfirmed(bool deleting)
    {
        await using var fixture = await Fixture.Create();
        var rule = await fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization));
        fixture.Store.Changed += () => throw new InvalidOperationException("PRIVATE_SUBSCRIBER_DETAIL");
        var error = deleting
            ? await Assert.ThrowsAsync<AlertRuleException>(() => fixture.Store.DeleteAlertRuleAsync(rule.Id))
            : await Assert.ThrowsAsync<AlertRuleException>(() => fixture.Store.SaveAlertRuleAsync(rule));
        Assert.Equal(deleting ? AlertRuleErrorCode.DeletedNotificationFailed : AlertRuleErrorCode.SavedNotificationFailed, error.Code);
        Assert.Equal("PRIVATE_SUBSCRIBER_DETAIL", error.InnerException!.Message);
        Assert.Equal(deleting ? 0 : 1, (await fixture.Store.GetAllAlertRulesAsync()).Count);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task WriteOrSynchronizationFailureNeverClaimsRollbackOrRetries(int save, bool afterCommit)
    {
        await using var fixture = await Fixture.Create();
        fixture.Interceptor.FailSave = save;
        fixture.Interceptor.AfterCommit = afterCommit;
        var error = await Assert.ThrowsAsync<AlertRuleException>(() =>
            fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization)));
        Assert.Equal(AlertRuleErrorCode.ChangeUnconfirmed, error.Code);
        Assert.Equal(save, fixture.Interceptor.Saves);
        Assert.Equal(save == 1 && !afterCommit ? 0 : 1, (await fixture.Store.GetAllAlertRulesAsync()).Count);
    }

    [Fact]
    public async Task DuplicateNameHasStableCodeAndOriginalParameterWithoutASecondWrite()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization));
        var saves = fixture.Interceptor.Saves;
        var error = await Assert.ThrowsAsync<AlertRuleException>(() =>
            fixture.Store.SaveAlertRuleAsync(Rule(AlertRuleTypes.Categorization)));
        Assert.Equal(AlertRuleErrorCode.DuplicateName, error.Code);
        Assert.Equal(first.Name, Assert.Single(error.Arguments));
        Assert.Equal(saves, fixture.Interceptor.Saves);
    }

    private static AlertRule Rule(string type) => new()
    {
        Id = Guid.NewGuid(), Name = "Original custom rule", CategoryName = "Original category",
        RuleType = type, ConditionType = AlertRuleConditionTypes.RowCountEquals, Threshold = 1,
        QueryResultType = "DCPort", InventoryRole = "Original role",
        Enabled = type != AlertRuleTypes.InventoryRoleAssignment
    };

    private static string RepositoryRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class CapturingActivator(IServiceProvider services) : IComponentActivator
    {
        public AdminRules? Rules { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is AdminRules rules) Rules = rules;
            return component;
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/",
            "http://localhost/login?error=invalid&returnUrl=%2Fadmin%2Frules%3Ftab%3Dexample");
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported test render mode: {renderMode.GetType().Name}");

        public async Task<HtmlRootComponent> RenderComponentAsync<T>(ParameterView parameters) where T : IComponent
        {
            var result = BeginRenderingComponent(typeof(T), parameters);
            await result.QuiescenceTask;
            return result;
        }
    }

    private sealed class Fixture(
        SqliteConnection connection, Factory factory, CommitInterceptor interceptor,
        AlertStore store, WebApplication app, AsyncServiceScope scope, StaticPageRenderer renderer) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public CommitInterceptor Interceptor { get; } = interceptor;
        public AlertStore Store { get; } = store;
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        private HtmlRootComponent root;
        private AdminRules component = default!;
        public string Html => renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString()).GetAwaiter().GetResult();

        public async Task<string> Render<T>(Dictionary<string, object?>? parameters = null) where T : IComponent =>
            await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString());

        public async Task RenderRules() => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = await renderer.RenderComponentAsync<AdminRules>(ParameterView.Empty);
            component = scope.ServiceProvider.GetRequiredService<CapturingActivator>().Rules!;
        });

        public async Task Invoke(string method, params object[] arguments) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AdminRules).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        });

        public Task UpdateEditor(Action<AlertRule> update) => renderer.Dispatcher.InvokeAsync(() =>
            update((AlertRule)typeof(AdminRules).GetField("editingRule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!));

        public static async Task<Fixture> Create(string mode = "sql")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new CommitInterceptor();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlite(connection).AddInterceptors(interceptor).Options);
            await using (var context = factory.CreateDbContext())
            {
                var tables = new[] { "AlertRules", "Alerts", "ParsedAlerts" };
                var statements = context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(statement => tables.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal)) ||
                        statement.Contains(" ON \"AlertRules\" ", StringComparison.Ordinal));
                foreach (var statement in statements)
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            }
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var store = new AlertStore(factory, new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
                presenter, new AlertRuleEvaluator(presenter), NullLogger<AlertStore>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            builder.Services.AddScoped<CapturingActivator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<CapturingActivator>());
            builder.Services.AddSingleton(new ApplicationAuthenticationOptions { Type = mode });
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            var renderer = new StaticPageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
            return new Fixture(connection, factory, interceptor, store, app, scope, renderer);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public bool FailCreation { get; set; }
        public AlertDbContext CreateDbContext() => FailCreation
            ? throw new InvalidOperationException("PRIVATE_PROVIDER_DETAIL") : new(options);
    }

    private sealed class CommitInterceptor : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public int FailSave { get; set; }
        public bool AfterCommit { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Saves == FailSave && !AfterCommit) throw new DbUpdateException("PRIVATE_PROVIDER_DETAIL");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Saves == FailSave && AfterCommit) throw new DbUpdateException("PRIVATE_PROVIDER_DETAIL");
            return ValueTask.FromResult(result);
        }
    }
}
