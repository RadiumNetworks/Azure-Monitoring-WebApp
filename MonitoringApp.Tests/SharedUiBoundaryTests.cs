using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class SharedUiBoundaryTests
{
    [Theory]
    [InlineData("en-US", "ChangeTimeRange", "2147483648", "InvalidTimeRange")]
    [InlineData("de-DE", "ChangeTimeRange", "2147483648", "InvalidTimeRange")]
    [InlineData("en-US", "ChangeTimeRange", "43200", "InvalidTimeRange")]
    [InlineData("de-DE", "ChangeTimeRange", "43200", "InvalidTimeRange")]
    [InlineData("en-US", "ChangeBin", "private <interval>", "InvalidInterval")]
    [InlineData("de-DE", "ChangeBin", "private <interval>", "InvalidInterval")]
    [InlineData("en-US", "ChangeBin", "7", "InvalidInterval")]
    [InlineData("de-DE", "ChangeBin", "7", "InvalidInterval")]
    [InlineData("en-US", "ChangeGrouping", "private <group>", "InvalidGrouping")]
    [InlineData("de-DE", "ChangeGrouping", "private <group>", "InvalidGrouping")]
    public async Task InvalidSelectionsKeepBoundValuesAndExposeWholeSafeErrors(
        string culture, string method, string value, string key)
    {
        using var selection = new CultureScope(culture);
        var calls = new List<string>();
        await using var view = await View<AnalysisExecutionSelections>.Create(SelectionParameters(calls));
        await view.Invoke(method, new ChangeEventArgs { Value = value });
        Assert.Empty(calls);
        Assert.Contains(view.Text["AnalysisSelections." + key], view.Decoded);
        Assert.DoesNotContain("private", view.Decoded);
        Assert.Equal(1440, view.Component.TimeRangeMinutes);
        Assert.Equal(15, view.Component.BinMinutes);
        Assert.Equal("SourceDSA", view.Component.GroupBy);
        await view.Invoke(method, new ChangeEventArgs
        {
            Value = method == "ChangeTimeRange" ? "60" : method == "ChangeBin" ? "15" : "NC"
        });
        Assert.Equal(2, calls.Count);
        Assert.Equal("changed", calls[1]);
        Assert.DoesNotContain("role=\"alert\"", view.Html);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CurrentCustomSelectionsRemainAvailableAndCallbackOrderIsUnchanged(string culture)
    {
        using var selection = new CultureScope(culture);
        var calls = new List<string>();
        var parameters = SelectionParameters(calls);
        parameters["TimeRangeMinutes"] = 90;
        parameters["BinMinutes"] = 7;
        parameters["MaxTimeRangeMinutes"] = 120;
        await using var view = await View<AnalysisExecutionSelections>.Create(parameters);
        await view.Invoke("ChangeTimeRange", new ChangeEventArgs { Value = "90" });
        await view.Invoke("ChangeBin", new ChangeEventArgs { Value = "7" });
        await view.Invoke("ChangeGrouping", new ChangeEventArgs { Value = "SourceDSA" });
        Assert.Equal(new[] { "time:90", "changed", "bin:7", "changed", "group:SourceDSA", "changed" }, calls);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DisabledAndHiddenControlsDoNotForwardStaleCallbacks(string culture)
    {
        using var selection = new CultureScope(culture);
        var calls = new List<string>();
        var parameters = SelectionParameters(calls);
        parameters["Disabled"] = true;
        await using var selections = await View<AnalysisExecutionSelections>.Create(parameters);
        await selections.Invoke("ChangeTimeRange", new ChangeEventArgs { Value = "60" });
        await selections.Invoke("ChangeBin", new ChangeEventArgs { Value = "15" });
        await selections.Invoke("ChangeGrouping", new ChangeEventArgs { Value = "NC" });
        parameters["Disabled"] = false;
        parameters["ShowGrouping"] = false;
        await selections.Apply(parameters);
        await selections.Invoke("ChangeGrouping", new ChangeEventArgs { Value = "NC" });
        await using var controls = await View<AnalysisDashboardExecutionControls>.Create(new()
        {
            ["Disabled"] = true,
            ["Changed"] = EventCallback.Factory.Create<(int?, int?)>(calls, _ => calls.Add("override"))
        });
        await controls.Invoke("ChangeTimeRangeAsync", (object?)null);
        await controls.Invoke("ChangeBinAsync", 15);
        var document = new AnalysisDashboardDocument();
        var original = JsonSerializer.Serialize(document);
        await using var grid = await View<AnalysisGridControls>.Create(new()
        {
            ["Document"] = document, ["Disabled"] = true,
            ["SizeChanged"] = EventCallback.Factory.Create<(int, int, int)>(calls, _ => calls.Add("grid"))
        });
        await grid.Invoke("ChangeColumns", new ChangeEventArgs { Value = "6" });
        await using var import = await View<QueryJsonImport>.Create(new()
        {
            ["Busy"] = true, ["OnImport"] = EventCallback.Factory.Create<string>(calls, _ => calls.Add("import"))
        });
        await import.Invoke("ImportAsync");
        Assert.Empty(calls);
        Assert.Equal(original, JsonSerializer.Serialize(document));
    }

    [Theory]
    [InlineData("en-US", "disabled")]
    [InlineData("de-DE", "disabled")]
    [InlineData("en-US", "read-only")]
    [InlineData("de-DE", "read-only")]
    [InlineData("en-US", "single")]
    [InlineData("de-DE", "single")]
    [InlineData("en-US", "changed")]
    [InlineData("de-DE", "changed")]
    public async Task DashboardManagementRequiresLiveEditPermissionAndTheConfirmedTarget(string culture, string mode)
    {
        using var selection = new CultureScope(culture);
        var collection = AnalysisDashboardCollection.Create();
        var first = collection.ActiveDashboardId;
        if (mode != "single") collection.Add("Original second dashboard");
        var calls = new List<string>();
        await using var view = await View<AnalysisDashboardTabs>.Create(new()
        {
            ["Collection"] = collection, ["Disabled"] = mode == "disabled", ["EditMode"] = mode != "read-only",
            ["Created"] = EventCallback.Factory.Create<string>(calls, _ => calls.Add("create")),
            ["Renamed"] = EventCallback.Factory.Create<string>(calls, _ => calls.Add("rename")),
            ["Deleted"] = EventCallback.Factory.Create(calls, () => calls.Add("delete"))
        });
        if (mode is "disabled" or "read-only")
        {
            await view.Invoke("CreateAsync");
            await view.Invoke("RenameAsync");
        }
        await view.Invoke("ConfirmDelete");
        if (mode == "changed") collection.Select(first);
        var original = collection.Serialize();
        await view.Invoke("DeleteAsync");
        Assert.Empty(calls);
        Assert.Equal(original, collection.Serialize());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AConfirmedCurrentDashboardStillDeletesExactlyOnce(string culture)
    {
        using var selection = new CultureScope(culture);
        var collection = AnalysisDashboardCollection.Create();
        collection.Add("Original <custom> name");
        var deletes = 0;
        await using var view = await View<AnalysisDashboardTabs>.Create(new()
        {
            ["Collection"] = collection,
            ["Deleted"] = EventCallback.Factory.Create(collection, () => { deletes++; collection.DeleteActive(); })
        });
        await view.Invoke("ConfirmDelete");
        Assert.Contains("Original &lt;custom&gt; name", view.Html);
        await view.Invoke("DeleteAsync");
        await view.Invoke("DeleteAsync");
        Assert.Equal(1, deletes);
        Assert.Single(collection.Dashboards);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UnknownDiagnosticStatusIsNotDisplayedAsASuccessOrRawEnumValue(string culture)
    {
        using var selection = new CultureScope(culture);
        var step = new DiagnosticStep("Original <name>", (DiagnosticStatus)12345, 0, "Original <technical> detail");
        var original = JsonSerializer.Serialize(step);
        await using var view = await View<DiagnosticStepView>.Create(new() { ["Step"] = step });
        Assert.DoesNotContain(">12345</span>", view.Html);
        Assert.Contains(view.Text["Diagnostics.Status.Unknown"], view.Decoded);
        Assert.Contains("admin-diagnostic-unknown", view.Html);
        Assert.Contains("Original &lt;technical&gt; detail", view.Html);
        Assert.Equal(original, JsonSerializer.Serialize(step));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LayoutDisplayNameTracksTheLatestReaderAndClearsAfterSignOut(string culture)
    {
        using var selection = new CultureScope(culture);
        var layout = new MonitoringApp.Components.Layout.MainLayout();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = layout.GetType();
        type.GetProperty("CurrentUserIdentity", flags)!.SetValue(layout, new CurrentUserIdentity(new HttpContextAccessor()));
        Task SetReader(Task<AuthenticationState> reader)
        {
            type.GetProperty("AuthenticationStateTask", flags)!.SetValue(layout, reader);
            return (Task)type.GetMethod("OnParametersSetAsync", flags)!.Invoke(layout, null)!;
        }
        static AuthenticationState Reader(string name, string role) => new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "test")));
        type.GetProperty("AuthenticationStateTask", flags)!.SetValue(layout, Task.FromResult(Reader("Original admin", "Admin")));
        await (Task)type.GetMethod("OnInitializedAsync", flags)!.Invoke(layout, null)!;
        await (Task)type.GetMethod("OnParametersSetAsync", flags)!.Invoke(layout, null)!;
        Assert.Equal("Original admin", type.GetField("currentUser", flags)!.GetValue(layout));
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = SetReader(pending.Task);
        Assert.Null(type.GetField("currentUser", flags)!.GetValue(layout));
        await SetReader(Task.FromResult(Reader("Original operator", "Operator")));
        pending.SetResult(Reader("Old delayed admin", "Admin"));
        await oldRead;
        Assert.Equal("Original operator", type.GetField("currentUser", flags)!.GetValue(layout));
        await SetReader(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        Assert.Null(type.GetField("currentUser", flags)!.GetValue(layout));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ReplacedTablesIgnorePaginationClicksFromRemovedResults(string culture, bool rawFirst)
    {
        using var selection = new CultureScope(culture);
        AnalysisQueryTable[] tables =
        [
            new("Original workspace", [new("Value", "string")], [["Original value"]]),
            new("Removed workspace", [new("Value", "string")],
                Enumerable.Range(0, 51).Select(index => new string?[] { index.ToString(CultureInfo.InvariantCulture) }).ToArray())
        ];
        await using var formatted = await View<AnalysisTableView>.Create(new() { ["Tables"] = tables });
        await using var raw = await View<RawQueryTableView>.Create(new() { ["Tables"] = tables });
        Assert.Contains("Removed workspace", formatted.Decoded);
        Assert.Contains("Removed workspace", raw.Decoded);
        await formatted.Invoke("ChangePage", 1, 1);
        await raw.Invoke("ChangePage", 1, 1);
        await formatted.Apply(new() { ["Tables"] = Array.Empty<AnalysisQueryTable>() });
        await raw.Apply(new() { ["Tables"] = Array.Empty<AnalysisQueryTable>() });
        if (rawFirst) await raw.Invoke("ChangePage", 1, 1);
        await formatted.Invoke("ChangePage", 1, 1);
        if (!rawFirst) await raw.Invoke("ChangePage", 1, 1);
        Assert.DoesNotContain("Removed workspace", formatted.Decoded);
        Assert.DoesNotContain("Removed workspace", raw.Decoded);
        var replacement = tables.ToArray();
        await formatted.Apply(new() { ["Tables"] = replacement });
        await raw.Apply(new() { ["Tables"] = replacement });
        await formatted.Invoke("ChangePageForTables", tables, 1, 1);
        await raw.Invoke("ChangePageForTables", tables, 1, 1);
        var firstPage = raw.Text.Format("AnalysisResults.Rows", 1, 50, 51);
        Assert.Contains(firstPage, formatted.Decoded);
        Assert.Contains(firstPage, raw.Decoded);
        await formatted.Invoke("ChangePageForTables", replacement, 1, 1);
        await raw.Invoke("ChangePageForTables", replacement, 1, 1);
        var lastPage = raw.Text.Format("AnalysisResults.Rows", 51, 51, 51);
        Assert.Contains(lastPage, formatted.Decoded);
        Assert.Contains(lastPage, raw.Decoded);
    }

    private static Dictionary<string, object?> SelectionParameters(List<string> calls) => new()
    {
        ["MaxTimeRangeMinutes"] = 60, ["ShowGrouping"] = true,
        ["TimeRangeMinutesChanged"] = EventCallback.Factory.Create<int>(calls, value => calls.Add($"time:{value}")),
        ["BinMinutesChanged"] = EventCallback.Factory.Create<int>(calls, value => calls.Add($"bin:{value}")),
        ["GroupByChanged"] = EventCallback.Factory.Create<string>(calls, value => calls.Add($"group:{value}")),
        ["Changed"] = EventCallback.Factory.Create(calls, () => calls.Add("changed"))
    };

    private sealed class View<T>(ServiceProvider services, HtmlRenderer renderer, Capture<T> capture,
        HtmlRootComponent root) : IAsyncDisposable where T : ComponentBase
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public T Component => capture.Component!;
        public UiText Text => services.GetRequiredService<UiText>();
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public Task Apply(Dictionary<string, object?> parameters) => renderer.Dispatcher.InvokeAsync(() =>
            Component.SetParametersAsync(ParameterView.FromDictionary(parameters)));
        public Task Invoke(string method, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(T).GetMethod(method, Flags)!.Invoke(Component, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Component, null);
        });
        public static async Task<View<T>> Create(Dictionary<string, object?> parameters)
        {
            var capture = new Capture<T>();
            var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(capture).BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<T>(
                ParameterView.FromDictionary(parameters)));
            return new(services, renderer, capture, root);
        }
        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class Capture<T> : IComponentActivator where T : ComponentBase
    {
        public T? Component { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is T value) Component = value;
            return component;
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo ui = CultureInfo.CurrentUICulture;
        private readonly CultureInfo format = CultureInfo.CurrentCulture;
        public CultureScope(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = format;
        }
    }
}
