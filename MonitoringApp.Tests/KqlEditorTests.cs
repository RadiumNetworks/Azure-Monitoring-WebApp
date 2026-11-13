using System.Globalization;
using System.Net;
using System.Reflection;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.KQL;

namespace MonitoringApp.Tests;

public sealed class KqlEditorTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RendersLocalizedSharedMarkupAndPreservesInputConstraints(string culture)
    {
        var original = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            const string query = "Perf\n| where Name == '<own> & \"quoted\"'\n| take 10";
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<KqlEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(KqlEditor.Value)] = query,
                    [nameof(KqlEditor.Rows)] = 15,
                    [nameof(KqlEditor.MaxLength)] = 65536,
                    [nameof(KqlEditor.Required)] = true,
                    [nameof(KqlEditor.Disabled)] = true,
                    [nameof(KqlEditor.ReadOnly)] = true,
                    [nameof(KqlEditor.Class)] = "analysis-kql-editor"
                }))).ToHtmlString());
            Assert.Contains(query, WebUtility.HtmlDecode(html));
            Assert.Contains("data-kql-editor", html);
            Assert.Contains("data-kql-version=\"1\"", html);
            Assert.Contains("analysis-kql-editor", html);
            var textarea = Regex.Match(html, "<textarea\\b[^>]*>").Value;
            Assert.Contains("rows=\"15\"", textarea);
            Assert.Contains("maxlength=\"65536\"", textarea);
            foreach (var attribute in new[] { "required", "disabled", "readonly", "spellcheck=\"false\"", "autocomplete=\"off\"" })
                Assert.Contains(attribute, textarea);
            Assert.Contains("aria-hidden=\"true\"", html);
            Assert.Contains("role=\"tooltip\" hidden", html);
            var text = services.GetRequiredService<UiText>();
            Assert.Contains(text["KqlEditor.CompletionHelp"], WebUtility.HtmlDecode(html));
            Assert.Contains(text["KqlEditor.CompletionFailed"], WebUtility.HtmlDecode(html));
            Assert.Contains("data-kql-hints", html);
            Assert.Contains("aria-live=\"polite\"", html);
            Assert.Contains("aria-atomic=\"true\"", html);
            Assert.Contains(text["KqlEditor.Hints.Failed"], WebUtility.HtmlDecode(html));
            Assert.Contains(text["KqlEditor.Keywords.Title"], WebUtility.HtmlDecode(html));
            Assert.Contains(text["KqlEditor.Keywords.Help"], WebUtility.HtmlDecode(html));
            Assert.Contains(text["KqlEditor.Keywords.NoInfo"], WebUtility.HtmlDecode(html));
            foreach (var (attribute, key) in new[]
            {
                ("syntax", "Syntax"), ("details", "Details"), ("example", "Example")
            })
                Assert.Contains($"data-kql-keyword-{attribute}=\"{WebUtility.HtmlEncode(text[$"KqlEditor.Keywords.{key}"])}\"", html);
            Assert.Contains("data-kql-keyword-list", html);
            Assert.Contains("aria-expanded=\"false\"", html);
            var keywordPanelId = Regex.Match(html, "aria-controls=\"([^\"]+)\"").Groups[1].Value;
            Assert.StartsWith("kql-keyword-panel-", keywordPanelId);
            Assert.Contains($"id=\"{keywordPanelId}\"", html);
            var messagesJson = WebUtility.HtmlDecode(Regex.Match(html, "data-kql-diagnostic-messages=\"([^\"]+)\"").Groups[1].Value);
            using var messages = JsonDocument.Parse(messagesJson);
            Assert.Equal(text["KqlEditor.Hints.UnexpectedCloser"], messages.RootElement.GetProperty("UnexpectedCloser").GetString());
            Assert.Equal(text["KqlEditor.Hints.UnknownColumn"], messages.RootElement.GetProperty("UnknownColumn").GetString());
            Assert.Equal(text["KqlEditor.Hints.Limited"], messages.RootElement.GetProperty("Limited").GetString());
            Assert.DoesNotContain("aria-invalid", textarea);
            var helpId = Regex.Match(textarea, "aria-describedby=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(helpId);
            Assert.Contains($"id=\"{helpId}\"", html);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public async Task KeepsIndependentHelpIdsAndRevisionsForServerLoadedDrafts()
    {
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var first = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.Value)] = "Perf | take 10" })));
        var component = Assert.IsType<KqlEditor>(activator.Editor);
        var initial = await renderer.Dispatcher.InvokeAsync(first.ToHtmlString);
        var helpId = Regex.Match(initial, "aria-describedby=\"([^\"]+)\"").Groups[1].Value;
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(KqlEditor.Value)] = "Event | summarize count()" })));
        var changed = await renderer.Dispatcher.InvokeAsync(first.ToHtmlString);
        var keywordPanelId = Regex.Match(initial, "aria-controls=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains($"id=\"{keywordPanelId}\"", changed);
        Assert.Contains("data-kql-version=\"2\"", changed);
        Assert.Contains("Event | summarize count()", changed);
        Assert.Contains($"aria-describedby=\"{helpId}\"", changed);
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(KqlEditor.Disabled)] = true })));
        Assert.Contains("data-kql-version=\"2\"", await renderer.Dispatcher.InvokeAsync(first.ToHtmlString));
        var second = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>());
        var secondHtml = await renderer.Dispatcher.InvokeAsync(second.ToHtmlString);
        Assert.DoesNotContain($"aria-describedby=\"{helpId}\"", secondHtml);
        Assert.DoesNotContain($"id=\"{keywordPanelId}\"", secondHtml);
        Assert.DoesNotContain("maxlength=", secondHtml);
        var textarea = Regex.Match(secondHtml, "<textarea\\b[^>]*>").Value;
        Assert.DoesNotContain("disabled", textarea);
        Assert.DoesNotContain("required", textarea);
    }

    [Theory]
    [InlineData("Perf\n| summarize count() by Computer")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ChangeCallbackKeepsLocalTextUntilUpdatedParametersWithoutMutatingTheInputParameter(string? changed)
    {
        var activator = new CapturingActivator();
        var changes = new List<string>();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "original",
                [nameof(KqlEditor.ValueChanged)] = EventCallback.Factory.Create<string>(changes, changes.Add)
            })));
        var component = Assert.IsType<KqlEditor>(activator.Editor);
        await renderer.Dispatcher.InvokeAsync(async () => await (Task)typeof(KqlEditor)
            .GetMethod("ChangeValueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, [changed])!);
        Assert.Equal(changed ?? string.Empty, Assert.Single(changes));
        Assert.Equal("original", component.Value);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, []));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Equal(changed ?? string.Empty, WebUtility.HtmlDecode(
            Regex.Match(html, "<textarea\\b[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value));
    }

    [Fact]
    public async Task SchemasUseTheFirstSelectedWorkspaceAndAreCachedUntilItsSelectionChanges()
    {
        var client = new DiscoveryClient { Handler = (_, _) => Task.FromResult(DiscoveryResult("Shared")) };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "original",
                [nameof(KqlEditor.WorkspaceKeys)] = new[] { "b", "a" }
            })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        var version = CatalogVersion(editor);
        var info = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Shared", version)));
        Assert.Equal("Shared", info.TableName);
        Assert.Equal("b", info.WorkspaceKey);
        Assert.Equal(version, info.CatalogVersion);
        Assert.Equal(KqlSchemaStatus.Ready, info.Status);
        Assert.Equal([new("Counter", "int"), new("Computer", "string"), new KqlSchemaColumn("Details", "dynamic")], info.Columns);
        Assert.Equal("b", client.Workspaces.Last());
        Assert.Equal("table(@'Shared') | getschema", client.Queries.Last());
        Assert.Same(info, await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Shared", version)));
        Assert.Equal(3, client.Queries.Count);
        await SetWorkspacesAsync(renderer, editor, "a", "b");
        await AfterRenderAsync(renderer, editor);
        Assert.Equal(3, client.Queries.Count);
        await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Shared", CatalogVersion(editor)));
        Assert.Equal("a", client.Workspaces.Last());
        Assert.Equal(4, client.Queries.Count);
        var unavailable = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() =>
            editor.GetTableSchemaAsync("Unknown", CatalogVersion(editor))));
        Assert.Equal(KqlSchemaStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Columns);
        Assert.Null(await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Shared", version)));
        Assert.Equal(4, client.Queries.Count);
        Assert.Equal("original", editor.Value);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SchemaFailuresAreLocalizedAndCanBeRetriedWithoutLosingTheDraft(string culture)
    {
        using var displayCulture = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new DiscoveryClient();
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "original",
                [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" }
            })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        client.Failure = new InvalidOperationException("private schema provider detail");
        var info = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        var text = services.GetRequiredService<UiText>();
        Assert.Equal(KqlSchemaStatus.Failed, info.Status);
        Assert.Empty(info.Columns);
        Assert.DoesNotContain("private schema provider detail", JsonSerializer.Serialize(info));
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("data-kql-schema-error", html);
        Assert.Contains(text["KqlEditor.TablesRetry"], html);
        Assert.DoesNotContain("private schema provider detail", html);
        Assert.Equal(info, await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal(2, client.Queries.Count);
        client.Failure = null;
        await renderer.Dispatcher.InvokeAsync(() => typeof(KqlEditor)
            .GetMethod("RetryTables", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, []));
        await AfterRenderAsync(renderer, editor);
        info = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal(KqlSchemaStatus.Ready, info.Status);
        Assert.Contains(new KqlSchemaColumn("Computer", "string"), info.Columns);
        Assert.Equal(4, client.Queries.Count);
        Assert.Equal("original", editor.Value);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task StructuredSourceAndMultipleWorkspacePolicyAreVisibleWithoutChangingTheDraft(string culture, bool multiple)
    {
        using var displayCulture = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new DiscoveryClient();
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "original",
                [nameof(KqlEditor.WorkspaceKeys)] = multiple ? new[] { "b", "a" } : new[] { "b" }
            })));
        await AfterRenderAsync(renderer, activator.Editor!);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("data-kql-schema-workspace=\"b\"", html);
        Assert.Contains($"data-kql-workspace-count=\"{(multiple ? 2 : 1)}\"", html);
        var text = services.GetRequiredService<UiText>();
        Assert.Contains(text["KqlEditor.SchemaTitle"], html);
        Assert.Contains(text["KqlEditor.SchemaEmpty"], html);
        Assert.Contains(text["KqlEditor.SchemaUnavailable"], html);
        Assert.Equal(multiple, html.Contains("data-kql-multiple-workspaces", StringComparison.Ordinal));
        Assert.Equal(multiple, html.Contains(text["KqlEditor.MultipleWorkspaces"], StringComparison.Ordinal));
        Assert.Equal("original", activator.Editor!.Value);
    }

    [Fact]
    public async Task TablesOnlyObservedInAnotherWorkspaceNeverUseThatSchemaAsTheFirstWorkspaceSchema()
    {
        var client = new DiscoveryClient();
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "b", "a" } })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        var unavailable = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() =>
            editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal("b", unavailable.WorkspaceKey);
        Assert.Equal(KqlSchemaStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Columns);
        Assert.Equal(2, client.Queries.Count);
        await SetWorkspacesAsync(renderer, editor, "a", "b");
        var available = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() =>
            editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal("a", available.WorkspaceKey);
        Assert.Equal(KqlSchemaStatus.Ready, available.Status);
        Assert.Equal(3, client.Queries.Count);
        await SetWorkspacesAsync(renderer, editor, "A", "b");
        Assert.Same(available, await renderer.Dispatcher.InvokeAsync(() =>
            editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal(3, client.Queries.Count);
    }

    [Fact]
    public async Task EmptySchemaIsDistinctFromUnavailableAndFailedAndSerializesWithSourceAndStatus()
    {
        var client = new DiscoveryClient
        {
            SchemaHandler = (_, _) => Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"string"}],"rows":[]}]}"""))!)
        };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" } })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        var info = Assert.IsType<KqlTableSchema>(await renderer.Dispatcher.InvokeAsync(() =>
            editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal(KqlSchemaStatus.Empty, info.Status);
        Assert.Empty(info.Columns);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("Empty", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("a", json.RootElement.GetProperty("workspaceKey").GetString());
        Assert.Equal("Table_a", json.RootElement.GetProperty("tableName").GetString());
        Assert.Equal(CatalogVersion(editor), json.RootElement.GetProperty("catalogVersion").GetInt64());
        Assert.Equal(0, json.RootElement.GetProperty("columns").GetArrayLength());
        Assert.Same(info, await renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", CatalogVersion(editor))));
        Assert.Equal(2, client.Queries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSchemaRequestsAreDeduplicatedAndLateResultsAreCancelled(bool dispose)
    {
        var pending = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DiscoveryClient
        {
            SchemaHandler = (_, _) => { started.SetResult(); return pending.Task; }
        };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" } })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        var version = CatalogVersion(editor);
        var first = renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", version));
        await started.Task;
        var second = renderer.Dispatcher.InvokeAsync(() => editor.GetTableSchemaAsync("Table_a", version));
        Assert.Equal(2, client.Queries.Count);
        if (dispose) await renderer.Dispatcher.InvokeAsync(editor.Dispose);
        else await SetWorkspacesAsync(renderer, editor);
        Assert.True(client.Tokens.Last().IsCancellationRequested);
        pending.SetResult(SchemaResult());
        Assert.Null(await first);
        Assert.Null(await second);
    }

    private static long CatalogVersion(KqlEditor editor) => (long)typeof(KqlEditor)
        .GetField("catalogVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    [Fact]
    public async Task DiscoveryRunsOnlyAfterInteractiveRenderAndOnlyWhenTheWorkspaceSetChanges()
    {
        var client = new DiscoveryClient();
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "my query",
                [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a", "b", "A" }
            })));
        var editor = activator.Editor!;
        Assert.Empty(client.Workspaces);
        await AfterRenderAsync(renderer, editor);
        Assert.Equal(["a", "b"], client.Workspaces);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("data-kql-tables=\"[\"Table_a\",\"Table_b\"]\"", html);
        await SetWorkspacesAsync(renderer, editor, "B", "A");
        await renderer.Dispatcher.InvokeAsync(() => editor.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(KqlEditor.Value)] = "changed query" })));
        await AfterRenderAsync(renderer, editor);
        Assert.Equal(2, client.Workspaces.Count);
        await SetWorkspacesAsync(renderer, editor, "b");
        Assert.Contains("data-kql-tables=\"[]\"", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        await AfterRenderAsync(renderer, editor);
        Assert.Equal(["a", "b", "b"], client.Workspaces);
        await SetWorkspacesAsync(renderer, editor);
        await AfterRenderAsync(renderer, editor);
        Assert.Equal(3, client.Workspaces.Count);
        Assert.Contains("data-kql-tables=\"[]\"", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Equal("changed query", editor.Value);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DiscoveryFailuresAreVisibleAndRetryKeepsTheDraft(string culture)
    {
        using var displayCulture = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new DiscoveryClient { Failure = new InvalidOperationException("private provider detail") };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KqlEditor.Value)] = "original",
                [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" }
            })));
        var editor = activator.Editor!;
        await AfterRenderAsync(renderer, editor);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        var text = services.GetRequiredService<UiText>();
        Assert.Contains(text["KqlEditor.TablesFailed"], html);
        Assert.Contains(text["KqlEditor.TablesRetry"], html);
        Assert.DoesNotContain("private provider detail", html);
        client.Failure = null;
        await renderer.Dispatcher.InvokeAsync(() => typeof(KqlEditor)
            .GetMethod("RetryTables", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, []));
        await AfterRenderAsync(renderer, editor);
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.DoesNotContain(text["KqlEditor.TablesFailed"], html);
        Assert.Contains(text.Format("KqlEditor.TablesLoaded", 1), html);
        Assert.Equal(2, client.Workspaces.Count);
        Assert.Equal("original", editor.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingOrClearingWorkspacesDiscardsLateResults(bool clear)
    {
        var pending = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DiscoveryClient
        {
            Handler = (workspace, _) =>
            {
                if (workspace.Key != "a") return Task.FromResult(DiscoveryResult("NewTable"));
                started.SetResult();
                return pending.Task;
            }
        };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" } })));
        var editor = activator.Editor!;
        var first = AfterRenderAsync(renderer, editor);
        await started.Task;
        await SetWorkspacesAsync(renderer, editor, clear ? [] : ["b"]);
        Assert.True(client.Tokens[0].IsCancellationRequested);
        await AfterRenderAsync(renderer, editor);
        pending.SetResult(DiscoveryResult("OldTable"));
        await first;
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.DoesNotContain("OldTable", html);
        Assert.Contains(clear ? "data-kql-tables=\"[]\"" : "NewTable", html);
    }

    [Fact]
    public async Task CancelledRequestFinishingBeforeTheNextRenderDoesNotClearTheNewLoadingState()
    {
        var pending = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DiscoveryClient
        {
            Handler = (workspace, _) =>
            {
                if (workspace.Key != "a") return Task.FromResult(DiscoveryResult("NewTable"));
                started.SetResult();
                return pending.Task;
            }
        };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" } })));
        var editor = activator.Editor!;
        var first = AfterRenderAsync(renderer, editor);
        await started.Task;
        await SetWorkspacesAsync(renderer, editor, "b");
        pending.SetResult(DiscoveryResult("OldTable"));
        await first;
        await SetWorkspacesAsync(renderer, editor, "b");
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains(services.GetRequiredService<UiText>()["KqlEditor.TablesLoading"], html);
        await AfterRenderAsync(renderer, editor);
        Assert.Contains("NewTable", WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)));
    }

    [Fact]
    public async Task DisposalCancelsAnInFlightDiscovery()
    {
        var pending = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DiscoveryClient { Handler = (_, _) => { started.SetResult(); return pending.Task; } };
        var activator = new CapturingActivator();
        await using var services = DiscoveryServices(client, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<KqlEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = new[] { "a" } })));
        var editor = activator.Editor!;
        var request = AfterRenderAsync(renderer, editor);
        await started.Task;
        await renderer.Dispatcher.InvokeAsync(editor.Dispose);
        Assert.True(client.Tokens[0].IsCancellationRequested);
        pending.SetResult(DiscoveryResult("Ignored"));
        await request;
    }

    private static ServiceProvider DiscoveryServices(DiscoveryClient client, CapturingActivator activator) =>
        new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, KqlEditorJavaScript>()
            .AddSingleton(new FreeKqlQueryService(new CrossTenantLogAnalyticsOptions
            {
                Enabled = true,
                Workspaces = [new() { Key = "a" }, new() { Key = "b" }]
            }, new DiscoverySecret(), client))
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();

    private static Task SetWorkspacesAsync(HtmlRenderer renderer, KqlEditor editor, params string[] keys) =>
        renderer.Dispatcher.InvokeAsync(() => editor.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(KqlEditor.WorkspaceKeys)] = keys })));

    private static Task AfterRenderAsync(HtmlRenderer renderer, KqlEditor editor) =>
        renderer.Dispatcher.InvokeAsync(() => (Task)typeof(KqlEditor)
            .GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false])!);

    private static LogsQueryResult DiscoveryResult(string name) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[] { new { name = "$table", type = "string" } }, rows = new[] { new[] { name } } } }
        })))!;

    private static LogsQueryResult SchemaResult() =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
            """{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"string"}],"rows":[["Counter","int"],["Computer","string"],["Details","dynamic"]]}]}"""))!;

    private sealed class DiscoverySecret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken token) => Task.FromResult("test-secret");
    }

    private sealed class DiscoveryClient : IFreeKqlQueryClient
    {
        public List<string> Workspaces { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<string> Queries { get; } = [];
        public Exception? Failure { get; set; }
        public Func<CrossTenantLogAnalyticsWorkspaceOptions, CancellationToken, Task<LogsQueryResult>>? Handler { get; init; }
        public Func<CrossTenantLogAnalyticsWorkspaceOptions, CancellationToken, Task<LogsQueryResult>>? SchemaHandler { get; init; }

        public Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId, string secret, string kql, CancellationToken token)
        {
            Workspaces.Add(workspace.Key);
            Tokens.Add(token);
            Queries.Add(kql);
            return Failure is not null ? Task.FromException<LogsQueryResult>(Failure)
                : kql == FreeKqlQueryService.TableNamesQuery
                    ? Handler?.Invoke(workspace, token) ?? Task.FromResult(DiscoveryResult("Table_" + workspace.Key))
                    : SchemaHandler?.Invoke(workspace, token) ?? Task.FromResult(SchemaResult());
        }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public KqlEditor? Editor { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is KqlEditor editor) Editor = editor;
            return component;
        }
    }
}
